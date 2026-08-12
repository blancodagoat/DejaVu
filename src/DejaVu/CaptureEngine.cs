using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace DejaVu;

/// <summary>
/// The capture engine: Windows.Graphics.Capture frames go straight into a Media
/// Foundation sink writer with hardware transforms — no third-party code, frames stay on
/// the GPU, and the writer rotates between segment files without stopping capture, so
/// segment seams cost at most one frame. Encodes AV1 when a hardware encoder is
/// registered, H.264 otherwise. Memory is bounded by the sample allocator: when the
/// encoder falls behind, frames are dropped instead of queued.
/// </summary>
internal sealed class CaptureEngine : IDisposable
{
    /// <summary>Probed once: a registered hardware AV1 encoder MFT.</summary>
    public static readonly bool Av1Available = ProbeAv1();

    private readonly int fps;
    private readonly int quality;
    private readonly int width;
    private readonly int height;
    private readonly long frameTicks;

    private readonly IntPtr d3dDevice;
    private readonly IntPtr d3dContext;
    private readonly Mf.IMFDXGIDeviceManager deviceManager;
    private readonly IDirect3DDevice winrtDevice;
    private readonly GraphicsCaptureItem item;
    private readonly Direct3D11CaptureFramePool framePool;
    private readonly GraphicsCaptureSession session;
    private readonly Mf.IMFVideoSampleAllocatorEx allocator;
    private readonly Mf.IMFMediaType inputType;

    private readonly object gate = new();
    private Mf.IMFSinkWriter? writer;
    private int streamIndex;
    private long segmentEpochTicks = -1;
    private long nextDueTicks;
    private int framesInSegment;
    private bool errored;

    public event Action<string>? Error;

    /// <summary>Frames written to the current segment file so far.</summary>
    public int FramesInSegment => framesInSegment;

    public Guid Codec { get; private set; }

    /// <summary>Even-aligned and clamped to the codec's ceiling, aspect preserved.
    /// H.264 encoders cap at 4096 per axis; hardware AV1 goes to 8192.</summary>
    public static (int W, int H) FitEncoder(int w, int h, Guid codec)
    {
        int cap = codec == Mf.VideoFormat_AV1 ? 8192 : 4096;
        if (w > cap || h > cap)
        {
            double scale = Math.Min((double)cap / w, (double)cap / h);
            w = (int)(w * scale);
            h = (int)(h * scale);
        }

        return (Math.Max(2, w & ~1), Math.Max(2, h & ~1));
    }

    /// <summary>Captures a monitor (window handle zero) or a window (monitor zero).</summary>
    public CaptureEngine(IntPtr monitor, IntPtr window, int fps, int quality)
    {
        this.fps = fps;
        this.quality = quality;
        frameTicks = TimeSpan.TicksPerSecond / fps;
        Codec = Av1Available ? Mf.VideoFormat_AV1 : Mf.VideoFormat_H264;

        Mf.EnsureStarted();

        // One D3D11 device shared by capture and encode, multithread-protected because
        // frame callbacks and the encoder touch it from different threads.
        Mf.Check(D3D11CreateDevice(
            IntPtr.Zero, 1 /* hardware */, IntPtr.Zero, 0x820 /* BGRA | VIDEO_SUPPORT */,
            IntPtr.Zero, 0, 7 /* SDK version */, out d3dDevice, out _, out d3dContext));

        var mtIid = IID_ID3D10Multithread;
        Mf.Check(Marshal.QueryInterface(d3dContext, in mtIid, out var mtPtr));
        var multithread = (ID3D10Multithread)Marshal.GetObjectForIUnknown(mtPtr);
        Marshal.Release(mtPtr);
        multithread.SetMultithreadProtected(true);
        Marshal.ReleaseComObject(multithread);

        Mf.Check(Mf.MFCreateDXGIDeviceManager(out uint resetToken, out deviceManager));
        Mf.Check(deviceManager.ResetDevice(d3dDevice, resetToken));

        var dxgiIid = IID_IDXGIDevice;
        Mf.Check(Marshal.QueryInterface(d3dDevice, in dxgiIid, out var dxgiPtr));
        try
        {
            Mf.Check(CreateDirect3D11DeviceFromDXGIDevice(dxgiPtr, out var inspectable));
            winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            Marshal.Release(inspectable);
        }
        finally
        {
            Marshal.Release(dxgiPtr);
        }

        // The capture item comes from the classic interop factory — WinRT has no public
        // constructor from a raw HMONITOR/HWND.
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var itemIid = IID_IGraphicsCaptureItem;
        var itemPtr = window != IntPtr.Zero
            ? interop.CreateForWindow(window, ref itemIid)
            : interop.CreateForMonitor(monitor, ref itemIid);
        item = GraphicsCaptureItem.FromAbi(itemPtr);
        Marshal.Release(itemPtr);

        width = item.Size.Width;
        height = item.Size.Height;
        if (width < 2 || height < 2)
        {
            throw new InvalidOperationException("The capture target has no visible size (minimized window?).");
        }

        // ARGB32, deliberately: MF maps it to B8G8R8A8 — the exact format of WGC frame
        // textures. RGB32 would map to B8G8R8X8, and CopyResource silently no-ops on a
        // format mismatch, encoding untouched (black) allocator textures. Diagnosed by
        // decode-probing output that "looked" valid by size and duration.
        inputType = CreateVideoType(Mf.VideoFormat_ARGB32, width, height, stride: width * 4);

        // GPU-backed sample pool with hard backpressure: initial 2, max 6 in flight.
        // A slow encoder empties the pool and frames drop — memory stays flat.
        var allocIid = IID_IMFVideoSampleAllocatorEx;
        Mf.Check(Mf.MFCreateVideoSampleAllocatorEx(ref allocIid, out var allocObj));
        allocator = (Mf.IMFVideoSampleAllocatorEx)allocObj;
        Mf.Check(allocator.SetDirectXManager(deviceManager));
        Mf.Check(allocator.InitializeSampleAllocatorEx(2, 6, null, inputType));

        framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        session = framePool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = true;
        try
        {
            session.IsBorderRequired = false;
        }
        catch
        {
            // Needs Win10 21H1+; older builds show the yellow capture border.
        }

        framePool.FrameArrived += OnFrameArrived;
    }

    public void Start(string segmentPath)
    {
        lock (gate)
        {
            writer = CreateWriter(segmentPath);
            framesInSegment = 0;
            segmentEpochTicks = -1;
            errored = false;
        }

        session.StartCapture();
    }

    /// <summary>
    /// Swaps to the next segment file without stopping capture: the new writer is live
    /// before the old one finalizes on a worker thread, so the seam costs one frame at
    /// most.
    /// </summary>
    public void Rotate(string nextSegmentPath, Action<int>? onFinalized = null)
    {
        Mf.IMFSinkWriter? old;
        int frames;
        lock (gate)
        {
            old = writer;
            frames = framesInSegment;
            writer = CreateWriter(nextSegmentPath);
            framesInSegment = 0;
            segmentEpochTicks = -1;
            // One report per segment, not per engine: without the reset, an engine
            // that errored once cycles on mute until something else kills it.
            errored = false;
        }

        if (old is not null)
        {
            Task.Run(() =>
            {
                // Under the gate: Finalize while another thread is inside WriteSample
                // deadlocks in Media Foundation. Frames arriving during the ~200 ms of
                // finalization briefly block on the lock — still no lost seconds.
                lock (gate)
                {
                    Finalize(old);
                }

                onFinalized?.Invoke(frames);
            });
        }
    }

    /// <summary>Stops capture and finalizes the current segment. Blocking.</summary>
    public void Stop()
    {
        lock (gate)
        {
            if (writer is not null)
            {
                Finalize(writer);
                writer = null;
            }
        }
    }

    private static void Finalize(Mf.IMFSinkWriter old)
    {
        try
        {
            old.Finalize_();
        }
        finally
        {
            Marshal.ReleaseComObject(old);
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null)
        {
            return;
        }

        string stage = "start";
        try
        {
            // A resized window (or changed display mode) delivers frames at a new size;
            // CopyResource silently no-ops on mismatched textures and the clip freezes
            // while looking healthy. Surface it through the error path — the restart
            // rebuilds the engine at the new size.
            var contentSize = frame.ContentSize;
            if (contentSize.Width != width || contentSize.Height != height)
            {
                if (!errored)
                {
                    errored = true;
                    Error?.Invoke($"capture size changed to {contentSize.Width}x{contentSize.Height}");
                }

                return;
            }

            long now = frame.SystemRelativeTime.Ticks;
            lock (gate)
            {
                if (writer is null)
                {
                    return;
                }

                if (now < nextDueTicks)
                {
                    return;
                }

                // Steady pacing with catch-up capped at one interval, so a stall does
                // not cause a burst.
                nextDueTicks = Math.Max(nextDueTicks + frameTicks, now - frameTicks);

                if (segmentEpochTicks < 0)
                {
                    segmentEpochTicks = now;
                }

                // Backpressure: an exhausted pool means the encoder is behind — drop.
                stage = "allocate";
                if (allocator.AllocateSample(out var sample) < 0)
                {
                    return;
                }

                try
                {
                    stage = "surface-access";
                    var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
                    var texIid = IID_ID3D11Texture2D;
                    Mf.Check(access.GetInterface(ref texIid, out var sourceTexture));
                    try
                    {
                        stage = "sample-buffer";
                        Mf.Check(sample.GetBufferByIndex(0, out var buffer));
                        try
                        {
                            var dxgi = (Mf.IMFDXGIBuffer)buffer;
                            var resIid = IID_ID3D11Texture2D;
                            stage = "buffer-resource";
                            Mf.Check(dxgi.GetResource(ref resIid, out var destTexture));
                            try
                            {
                                stage = "copy";
                                CopyResource(d3dContext, destTexture, sourceTexture);
                            }
                            finally
                            {
                                Marshal.Release(destTexture);
                            }

                            // DXGI-backed buffers report zero length until told otherwise,
                            // and the writer rejects zero-length samples.
                            Mf.Check(buffer.SetCurrentLength((uint)(width * height * 4)));
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(buffer);
                        }
                    }
                    finally
                    {
                        Marshal.Release(sourceTexture);
                    }

                    stage = "timestamps";
                    Mf.Check(sample.SetSampleTime(now - segmentEpochTicks));
                    Mf.Check(sample.SetSampleDuration(frameTicks));
                    stage = "write";
                    Mf.Check(writer.WriteSample(streamIndex, sample));
                    framesInSegment++;
                }
                finally
                {
                    Marshal.ReleaseComObject(sample);
                }
            }
        }
        catch (Exception ex)
        {
            if (!errored)
            {
                errored = true;
                // Full stack into the log: the field failures here are COM cast/RCW
                // exceptions whose message alone cannot say WHICH call site died.
                AppLog.Write($"engine error detail ({stage}): {ex}");
                Error?.Invoke($"{stage}: {ex.Message}");
            }
        }
    }

    private Mf.IMFSinkWriter CreateWriter(string path)
    {
        try
        {
            return CreateWriterFor(path, Codec);
        }
        catch when (Codec == Mf.VideoFormat_AV1)
        {
            // AV1 MFTs carry their own type limits (frame-size minimums among them).
            // H.264 is the universal fallback — sticky, so every later segment in the
            // buffer matches and the mux never sees mixed codecs.
            AppLog.Write("AV1 encoder rejected the stream; falling back to H264");
            Codec = Mf.VideoFormat_H264;
            return CreateWriterFor(path, Codec);
        }
    }

    private Mf.IMFSinkWriter CreateWriterFor(string path, Guid codec)
    {
        Mf.Check(Mf.MFCreateAttributes(out var attrs, 4));
        var key = Mf.READWRITE_ENABLE_HARDWARE_TRANSFORMS;
        Mf.Check(attrs.SetUINT32(ref key, 1));
        key = Mf.SINK_WRITER_DISABLE_THROTTLING;
        Mf.Check(attrs.SetUINT32(ref key, 1));
        key = Mf.SINK_WRITER_D3D_MANAGER;
        Mf.Check(attrs.SetUnknown(ref key, deviceManager));
        key = Mf.TRANSCODE_CONTAINERTYPE;
        var container = Mf.TranscodeContainerType_FMPEG4;
        Mf.Check(attrs.SetGUID(ref key, ref container));

        var attrsPtr = Marshal.GetIUnknownForObject(attrs);
        Mf.IMFSinkWriter newWriter;
        try
        {
            Mf.Check(Mf.MFCreateSinkWriterFromURL(path, IntPtr.Zero, attrsPtr, out newWriter));
        }
        finally
        {
            Marshal.Release(attrsPtr);
            Marshal.ReleaseComObject(attrs);
        }

        try
        {
            // Encode size is not capture size: encoders reject odd dimensions
            // (drag-resized windows) and have ceilings (H.264 caps at 4096 — ultrawide
            // and portrait-4K monitors exceed it). Input stays at the exact capture
            // size because CopyResource requires identical textures; when they differ,
            // the sink writer inserts a video processor to scale.
            var (encodeW, encodeH) = FitEncoder(width, height, codec);
            var outputType = CreateVideoType(codec, encodeW, encodeH, stride: 0);
            try
            {
                Mf.Check(newWriter.AddStream(outputType, out streamIndex));
                Mf.Check(newWriter.SetInputMediaType(streamIndex, inputType, IntPtr.Zero));
            }
            finally
            {
                Marshal.ReleaseComObject(outputType);
            }

            QualityModeActive = TrySetQualityMode(newWriter, streamIndex);
            Mf.Check(newWriter.BeginWriting());
            return newWriter;
        }
        catch
        {
            Marshal.ReleaseComObject(newWriter);
            throw;
        }
    }

    /// <summary>True when the encoder accepted constant-quality rate control.</summary>
    public bool QualityModeActive { get; private set; }

    private Mf.IMFMediaType CreateVideoType(Guid subtype, int w, int h, int stride)
    {
        Mf.Check(Mf.MFCreateMediaType(out var type));
        var major = Mf.MT_MAJOR_TYPE;
        var video = Mf.MediaType_Video;
        var sub = Mf.MT_SUBTYPE;
        Mf.Check(type.SetGUID(ref major, ref video));
        Mf.Check(type.SetGUID(ref sub, ref subtype));
        var key = Mf.MT_FRAME_SIZE;
        Mf.Check(type.SetUINT64(ref key, ((ulong)(uint)w << 32) | (uint)h));
        key = Mf.MT_FRAME_RATE;
        Mf.Check(type.SetUINT64(ref key, ((ulong)(uint)fps << 32) | 1));
        key = Mf.MT_INTERLACE_MODE;
        Mf.Check(type.SetUINT32(ref key, 2 /* progressive */));
        if (stride > 0)
        {
            key = Mf.MT_DEFAULT_STRIDE;
            Mf.Check(type.SetUINT32(ref key, (uint)stride));
        }
        else
        {
            // Encoders want a bitrate on the output type even in quality mode — and if
            // quality mode is refused this becomes the actual rate, so scale it with the
            // preset instead of leaving one generous cap.
            key = Mf.MT_AVG_BITRATE;
            Mf.Check(type.SetUINT32(ref key, (uint)(quality switch
            {
                <= 50 => 12_000_000,
                <= 70 => 25_000_000,
                _ => 45_000_000,
            })));
        }

        return type;
    }

    /// <summary>
    /// Best-effort constant-quality rate control on the encoder behind the sink writer.
    /// Encoders that refuse fall back to the bitrate cap on the output type.
    /// </summary>
    private bool TrySetQualityMode(Mf.IMFSinkWriter target, int stream)
    {
        var service = Guid.Empty;
        var iid = IID_ICodecAPI;
        unsafe
        {
            if (target.GetServiceForStream(stream, (IntPtr)(&service), (IntPtr)(&iid), out var ptr) < 0)
            {
                return false;
            }

            var codec = (ICodecAPI)Marshal.GetObjectForIUnknown(ptr);
            Marshal.Release(ptr);
            try
            {
                var mode = CODECAPI_AVEncCommonRateControlMode;
                object modeValue = 3u; // eAVEncCommonRateControlMode_Quality
                codec.SetValue(ref mode, ref modeValue);

                try
                {
                    var q = CODECAPI_AVEncCommonQuality;
                    object qualityValue = (uint)quality;
                    codec.SetValue(ref q, ref qualityValue);
                    return true;
                }
                catch
                {
                    // Intel/AMD accept the mode switch but not the quality knob. Left
                    // half-applied, the encoder runs quality mode at a vendor default
                    // and MT_AVG_BITRATE is ignored — put it back on bitrate control.
                    object cbr = 0u; // eAVEncCommonRateControlMode_CBR
                    codec.SetValue(ref mode, ref cbr);
                    return false;
                }
            }
            catch
            {
                return false; // Bitrate cap it is.
            }
            finally
            {
                Marshal.ReleaseComObject(codec);
            }
        }
    }

    public void Dispose()
    {
        try
        {
            session.Dispose();
            framePool.Dispose();
        }
        catch
        {
            // WinRT teardown after device loss.
        }

        Stop();
        allocator.UninitializeSampleAllocator();
        Marshal.ReleaseComObject(allocator);
        Marshal.ReleaseComObject(inputType);
        Marshal.ReleaseComObject(deviceManager);
        winrtDevice.Dispose();
        Marshal.Release(d3dContext);
        Marshal.Release(d3dDevice);
    }

    private static bool ProbeAv1()
    {
        try
        {
            Mf.EnsureStarted();
            // Encoder AND decoder: saves are decode-verified, and Win10 has no in-box
            // AV1 decoder even on GPUs that encode it — choosing AV1 there means every
            // save is muxed, fails the probe, and gets deleted.
            return Mf.HasHardwareEncoder(Mf.VideoFormat_AV1) && Mf.HasDecoder(Mf.VideoFormat_AV1);
        }
        catch
        {
            return false;
        }
    }

    // ---- native ----

    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid IID_ID3D10Multithread = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");
    private static readonly Guid IID_IMFVideoSampleAllocatorEx = new("545b3a48-3283-4f62-866f-a62d8f598f9f");
    private static readonly Guid IID_ICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
    private static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid CODECAPI_AVEncCommonQuality = new("fcbf57a3-7ea5-4b0c-9644-69b40c39c391");

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels,
        uint levels, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice, out IntPtr inspectable);

    /// <summary>ID3D11DeviceContext::CopyResource via a direct vtable call — declaring
    /// the 45 preceding methods buys nothing. Slot 47 (after IUnknown 0–2, then
    /// ID3D11DeviceChild's 4, then the 40 context methods preceding it), verified
    /// against d3d11.h.</summary>
    private static unsafe void CopyResource(IntPtr context, IntPtr dest, IntPtr source)
    {
        var vtbl = *(void***)context;
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)vtbl[47])(context, dest, source);
    }

    [ComImport]
    [Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    [ComImport]
    [Guid("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        [PreserveSig] int GetInterface(ref Guid iid, out IntPtr obj);
    }

    [ComImport]
    [Guid("9b7e4e00-342c-4106-a19f-4f2704f689f0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3D10Multithread
    {
        [PreserveSig] void Enter();
        [PreserveSig] void Leave();
        [PreserveSig] int SetMultithreadProtected(bool protect);
        [PreserveSig] int GetMultithreadProtected();
    }

    [ComImport]
    [Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICodecAPI
    {
        [PreserveSig] int IsSupported(ref Guid api);
        [PreserveSig] int IsModifiable(ref Guid api);
        [PreserveSig] int _GetParameterRange();
        [PreserveSig] int _GetParameterValues();
        [PreserveSig] int _GetDefaultValue();
        [PreserveSig] int _GetValue();
        void SetValue(ref Guid api, [MarshalAs(UnmanagedType.Struct)] ref object value);
        [PreserveSig] int _RegisterForEvent();
        [PreserveSig] int _UnregisterForEvent();
        [PreserveSig] int _SetAllDefaults();
        [PreserveSig] int _SetValueWithNotify();
        [PreserveSig] int _SetAllDefaultsWithNotify();
        [PreserveSig] int _GetAllSettings();
        [PreserveSig] int _SetAllSettings();
        [PreserveSig] int _SetAllSettingsWithNotify();
    }
}
