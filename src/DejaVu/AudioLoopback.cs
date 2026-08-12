using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DejaVu;

/// <summary>
/// System-audio capture that can leave one app out of the mix. With an exclude PID it
/// activates Windows' process-loopback virtual device (Win10 2004+) in
/// exclude-target-process-tree mode — everything except that app's tree (Discord and its
/// voice children, typically). With PID 0 it captures the default render endpoint whole.
/// PCM is encoded to AAC on the fly through a Media Foundation sink writer, one file per
/// buffer segment; silence is synthesized whenever the system renders nothing, because
/// loopback delivers no packets then and the AAC timeline must stay continuous.
/// </summary>
internal sealed class AudioLoopback : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int BitsPerSample = 16;
    private const int BlockAlign = Channels * BitsPerSample / 8;
    private const int BytesPerSecond = SampleRate * BlockAlign;

    private const int AUDCLNT_SHAREMODE_SHARED = 0;
    private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    private const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    private readonly IAudioClient client;
    private readonly IAudioCaptureClient capture;
    private readonly Mf.IMFSinkWriter writer;
    private readonly int streamIndex;
    private readonly Thread pump;
    private readonly Stopwatch clock = new();

    private long writtenFrames;
    private volatile bool stopping;
    private bool finalized;

    /// <summary>Null when audio capture is unavailable; the caller records silent video.</summary>
    public static AudioLoopback? TryStart(string outputPath, int excludePid)
    {
        try
        {
            return new AudioLoopback(outputPath, excludePid);
        }
        catch (Exception ex)
        {
            // The reason must reach the log: a silent audio failure in the field is
            // undiagnosable from "my clips have no sound".
            AppLog.Write("audio unavailable: " + ex.Message);
            return null;
        }
    }

    private AudioLoopback(string outputPath, int excludePid)
    {
        Mf.EnsureStarted();

        Mf.Check(Mf.MFCreateSinkWriterFromURL(outputPath, IntPtr.Zero, IntPtr.Zero, out writer));
        var aac = Mf.CreateAudioType(Mf.AudioFormat_AAC, SampleRate, Channels, BitsPerSample, 24000, 0);
        var pcm = Mf.CreateAudioType(Mf.AudioFormat_PCM, SampleRate, Channels, BitsPerSample, BytesPerSecond, BlockAlign);
        try
        {
            Mf.Check(writer.AddStream(aac, out streamIndex));
            Mf.Check(writer.SetInputMediaType(streamIndex, pcm, IntPtr.Zero));
            Mf.Check(writer.BeginWriting());
        }
        finally
        {
            Marshal.ReleaseComObject(aac);
            Marshal.ReleaseComObject(pcm);
        }

        client = excludePid > 0 ? ActivateProcessLoopback(excludePid) : ActivateEndpointLoopback();

        var format = Marshal.AllocHGlobal(18);
        try
        {
            // WAVEFORMATEX: PCM, stereo, 48 kHz, 16-bit, no extra bytes.
            Marshal.WriteInt16(format, 0, 1);
            Marshal.WriteInt16(format, 2, Channels);
            Marshal.WriteInt32(format, 4, SampleRate);
            Marshal.WriteInt32(format, 8, BytesPerSecond);
            Marshal.WriteInt16(format, 12, BlockAlign);
            Marshal.WriteInt16(format, 14, BitsPerSample);
            Marshal.WriteInt16(format, 16, 0);

            uint flags = AUDCLNT_STREAMFLAGS_LOOPBACK;
            if (excludePid == 0)
            {
                // The endpoint path must accept our fixed format; the process-loopback
                // virtual device takes the requested format as-is.
                flags |= AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
            }

            Mf.Check(client.Initialize(
                AUDCLNT_SHAREMODE_SHARED, flags, 2_000_000, 0, format, IntPtr.Zero));
        }
        finally
        {
            Marshal.FreeHGlobal(format);
        }

        var captureIid = IID_IAudioCaptureClient;
        Mf.Check(client.GetService(ref captureIid, out var capturePtr));
        capture = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(capturePtr);
        Marshal.Release(capturePtr);

        Mf.Check(client.Start());
        clock.Start();

        pump = new Thread(Pump) { IsBackground = true, Name = "DejaVu audio pump" };
        pump.Start();
    }

    private void Pump()
    {
        while (!stopping)
        {
            Thread.Sleep(10);
            Drain();

            // Loopback goes quiet when nothing renders; keep the timeline continuous.
            // Lag up to ~20 ms is packet jitter, anything beyond becomes silence.
            long expected = (long)(clock.Elapsed.TotalSeconds * SampleRate);
            long deficit = expected - writtenFrames - SampleRate / 50;
            if (deficit > 0)
            {
                WriteFrames(IntPtr.Zero, (int)Math.Min(deficit, SampleRate));
            }
        }
    }

    private void Drain()
    {
        while (capture.GetNextPacketSize(out uint packet) >= 0 && packet > 0)
        {
            if (capture.GetBuffer(out var data, out uint frames, out uint flags, out _, out _) < 0)
            {
                return;
            }

            bool silent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
            WriteFrames(silent ? IntPtr.Zero : data, (int)frames);
            capture.ReleaseBuffer(frames);
        }
    }

    private void WriteFrames(IntPtr data, int frames)
    {
        if (frames <= 0)
        {
            return;
        }

        Mf.WritePcm(
            writer, streamIndex, data, frames * BlockAlign,
            writtenFrames * 10_000_000L / SampleRate,
            frames * 10_000_000L / SampleRate);
        writtenFrames += frames;
    }

    public void Dispose()
    {
        if (finalized)
        {
            return;
        }

        finalized = true;
        stopping = true;
        pump.Join(2000);

        try
        {
            client.Stop();
            Drain();
            Mf.Check(writer.Finalize_());
        }
        catch
        {
            // A torn-down device mid-capture; the file holds whatever was written.
        }
        finally
        {
            Marshal.ReleaseComObject(capture);
            Marshal.ReleaseComObject(client);
            Marshal.ReleaseComObject(writer);
        }
    }

    // ---- activation ----

    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    private const string VirtualLoopbackDevice = @"VAD\Process_Loopback";
    private const int ActivationTypeProcessLoopback = 1;
    private const int LoopbackModeExcludeTree = 1;
    private const ushort VT_BLOB = 65;

    private static IAudioClient ActivateProcessLoopback(int excludePid)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS { type, { TargetProcessId, ProcessLoopbackMode } }
        var paramsBlob = Marshal.AllocHGlobal(12);
        try
        {
            Marshal.WriteInt32(paramsBlob, 0, ActivationTypeProcessLoopback);
            Marshal.WriteInt32(paramsBlob, 4, excludePid);
            Marshal.WriteInt32(paramsBlob, 8, LoopbackModeExcludeTree);

            var propvariant = new PROPVARIANT { vt = VT_BLOB, blobSize = 12, blobData = paramsBlob };
            var handler = new ActivateHandler();
            var iid = IID_IAudioClient;
            Mf.Check(ActivateAudioInterfaceAsync(
                VirtualLoopbackDevice, ref iid, ref propvariant, handler, out var operation));

            if (!handler.Done.Wait(5000))
            {
                throw new TimeoutException("Audio interface activation timed out.");
            }

            try
            {
                Mf.Check(operation.GetActivateResult(out int hrActivate, out var activated));
                Mf.Check(hrActivate);
                return (IAudioClient)activated;
            }
            finally
            {
                Marshal.ReleaseComObject(operation);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(paramsBlob);
        }
    }

    private static IAudioClient ActivateEndpointLoopback()
    {
        var enumeratorType = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)
            ?? throw new InvalidOperationException("MMDeviceEnumerator unavailable.");
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;
        try
        {
            Mf.Check(enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 0 /* eConsole */, out var device));
            try
            {
                var iid = IID_IAudioClient;
                Mf.Check(device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var clientPtr));
                var client = (IAudioClient)Marshal.GetObjectForIUnknown(clientPtr);
                Marshal.Release(clientPtr);
                return client;
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    [DllImport("mmdevapi.dll", CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(
        string deviceInterfacePath, ref Guid riid, ref PROPVARIANT activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint blobSize;
        [FieldOffset(16)] public IntPtr blobData;
    }

    private sealed class ActivateHandler : IActivateAudioInterfaceCompletionHandler
    {
        public ManualResetEventSlim Done { get; } = new(false);

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            Done.Set();
            return 0;
        }
    }

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(
            out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(
            int shareMode, uint streamFlags, long bufferDuration, long periodicity,
            IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, out IntPtr service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(
            out IntPtr data, out uint frames, out uint flags,
            out long devicePosition, out long qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice(string id, out IntPtr device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, out IntPtr instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out uint state);
    }
}
