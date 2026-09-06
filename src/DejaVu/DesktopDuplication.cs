using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DejaVu;

/// <summary>
/// A frame source that duplicates a display output instead of going through
/// Windows.Graphics.Capture. WGC paints the yellow "something is recording your screen"
/// border, and the property that turns it off — GraphicsCaptureSession.IsBorderRequired —
/// does not exist below Windows 11, so on Windows 10 the border is permanent and there is
/// no API to argue with it (#6). Desktop Duplication predates that indicator and draws
/// nothing.
///
/// Displays only: DXGI duplicates outputs, not windows, so window capture stays on WGC.
/// The duplicated desktop also arrives without a cursor, which is drawn back on with GDI
/// rather than decoded from DXGI's pointer shapes — same picture, a fifth of the code.
///
/// ponytail: one extra full-frame GPU copy per frame versus WGC, because the cursor needs
/// a GDI-compatible surface to be drawn into. A compositing shader would drop it; only
/// worth writing if that copy ever shows up in a profile.
/// </summary>
internal sealed class DesktopDuplication : IDisposable
{
    private const int WaitTimeout = unchecked((int)0x887A0027);   // DXGI_ERROR_WAIT_TIMEOUT
    private const int NotFound = unchecked((int)0x887A0002);      // DXGI_ERROR_NOT_FOUND

    private readonly IntPtr device;
    private readonly IntPtr context;
    private readonly int fps;
    private readonly IDXGIOutput1 output;

    private IntPtr staging;
    private IDXGIOutputDuplication? duplication;
    private volatile bool stopping;
    private Thread? pump;

    // Hotspots come from GetIconInfo, which allocates two bitmaps per call; at 60 fps
    // that is a leak with a schedule. The cursor handle only changes when the shape does.
    private IntPtr hotspotFor;
    private int hotspotX;
    private int hotspotY;
    private bool cursorWarned;

    // Left/top of the duplicated output in virtual-desktop coordinates, which is what
    // turns a screen-space cursor position into a texture-space one.
    private int originX;
    private int originY;

    public int Width { get; }

    public int Height { get; }

    /// <summary>A B8G8R8A8 texture on the caller's device, and the 100 ns tick it was
    /// captured at. Valid only for the duration of the call.</summary>
    public event Action<IntPtr, long>? FrameArrived;

    /// <summary>Raised when this source cannot go on — the output came back at a new
    /// size after a mode change, or the pump hit something it could not recover from.
    /// The engine is built around one size and one live source, so either means rebuild.
    /// </summary>
    public event Action<string>? Failed;

    public DesktopDuplication(IntPtr device, IntPtr context, IntPtr monitor, int fps)
    {
        this.device = device;
        this.context = context;
        this.fps = fps;
        output = FindOutput(device, monitor);

        try
        {
            var desc = Describe();
            Width = desc.Width;
            Height = desc.Height;
            if (Width < 2 || Height < 2)
            {
                throw new InvalidOperationException("The display reports no usable size.");
            }

            staging = CreateStaging(device, Width, Height);
            duplication = Duplicate();
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    public void Start()
    {
        pump = new Thread(Pump) { IsBackground = true, Name = "DejaVu duplication" };
        pump.SetApartmentState(ApartmentState.MTA);
        pump.Start();
    }

    /// <summary>
    /// One acquire per frame interval. AcquireNextFrame returns the moment the desktop
    /// changes, so waiting out the remainder of the interval paces the loop instead of
    /// spinning at the monitor's refresh rate — and a desktop that never changes still
    /// yields frames, because the last one is kept and re-sent. A segment of no frames
    /// is how the buffer decides the source is dead.
    /// </summary>
    private void Pump()
    {
        long interval = Stopwatch.Frequency / fps;
        long due = Stopwatch.GetTimestamp();

        while (!stopping)
        {
            // Nothing may escape a background thread: an unhandled exception here would
            // take the process down instead of the one dead capture source. Report it
            // the way an engine error is reported and let the buffer rebuild.
            try
            {
                Step(ref due, interval);
            }
            catch (Exception ex)
            {
                stopping = true;
                AppLog.Write("duplication pump failed: " + ex);
                Failed?.Invoke("duplication: " + ex.Message);
            }
        }
    }

    /// <summary>One pass of the loop, split out so <see cref="Pump"/> is nothing but
    /// the guard around it and every early exit is a plain return.</summary>
    private void Step(ref long due, long interval)
    {
        if (duplication is null)
        {
            // Lost to a mode change, a full-screen transition, or the secure desktop
            // (which a non-elevated process may not duplicate). All of them end.
            if (!TryRecover())
            {
                Thread.Sleep(200);
                return;
            }

            due = Stopwatch.GetTimestamp();
        }

        long remaining = due - Stopwatch.GetTimestamp();
        int wait = remaining <= 0 ? 0 : (int)(remaining * 1000 / Stopwatch.Frequency);
        int hr = duplication!.AcquireNextFrame((uint)wait, out _, out var resource);
        if (hr >= 0)
        {
            try
            {
                CopyDesktop(resource);
            }
            finally
            {
                Marshal.Release(resource);
                duplication.ReleaseFrame();
            }
        }
        else if (hr != WaitTimeout)
        {
            Marshal.ReleaseComObject(duplication);
            duplication = null;
            return;
        }

        // Woken early by a desktop change: take the next one too rather than
        // encoding this one ahead of schedule.
        if (Stopwatch.GetTimestamp() < due)
        {
            return;
        }

        DrawCursor();
        FrameArrived?.Invoke(staging, Now());
        // Catch-up capped at one interval, matching the engine's own pacing: a stall
        // must not turn into a burst.
        due = Math.Max(due + interval, Stopwatch.GetTimestamp() - interval);
    }

    /// <summary>The monotonic clock the engine paces and timestamps against, in 100 ns
    /// ticks. Only differences matter to it, so any steady source will do.</summary>
    private static long Now() =>
        (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));

    private bool TryRecover()
    {
        try
        {
            var desc = Describe();
            if (desc.Width != Width || desc.Height != Height)
            {
                // The same report the WGC path makes for a resized target: the encoder,
                // the allocator and the sample type are all built around one size.
                stopping = true;
                Failed?.Invoke($"capture size changed to {desc.Width}x{desc.Height}");
                return false;
            }

            duplication = Duplicate();
            return true;
        }
        catch
        {
            // Still gone. The caller sleeps and asks again.
            return false;
        }
    }

    private void CopyDesktop(IntPtr resource)
    {
        var texIid = IID_ID3D11Texture2D;
        Mf.Check(Marshal.QueryInterface(resource, in texIid, out var desktop));
        try
        {
            CaptureEngine.CopyResource(context, staging, desktop);
        }
        finally
        {
            Marshal.Release(desktop);
        }
    }

    /// <summary>
    /// The duplicated desktop has no cursor in it. Drawn through the surface's GDI DC
    /// with the live cursor handle, rather than by decoding DXGI's monochrome and masked
    /// pointer shapes: DrawIconEx already knows how to do all of that.
    /// </summary>
    private void DrawCursor()
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        // flags is 0 for a hidden cursor and CURSOR_SUPPRESSED while the session is
        // driven by touch. Neither is painted, which is what WGC does too.
        if (!GetCursorInfo(ref info) || info.flags != CURSOR_SHOWING || info.hCursor == IntPtr.Zero)
        {
            return;
        }

        int x = info.ptScreenPos.X - originX;
        int y = info.ptScreenPos.Y - originY;
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return; // On another display.
        }

        if (info.hCursor != hotspotFor)
        {
            hotspotFor = info.hCursor;
            (hotspotX, hotspotY) = Hotspot(info.hCursor);
        }

        var surfaceIid = IID_IDXGISurface1;
        if (Marshal.QueryInterface(staging, in surfaceIid, out var surfacePtr) < 0)
        {
            return;
        }

        var surface = (IDXGISurface1)Marshal.GetObjectForIUnknown(surfacePtr);
        Marshal.Release(surfacePtr);
        try
        {
            // discard: false — the desktop just copied in is the thing being drawn on.
            int dcHr = surface.GetDC(false, out var hdc);
            if (dcHr < 0)
            {
                // Silence here is a cursor-less recording nobody can explain.
                if (!cursorWarned)
                {
                    cursorWarned = true;
                    AppLog.Write($"cursor overlay unavailable (0x{dcHr:X8}); this display records without a pointer");
                }

                return;
            }

            try
            {
                DrawIconEx(hdc, x - hotspotX, y - hotspotY, info.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
            }
            finally
            {
                // The surface stays unusable by D3D until the DC goes back.
                surface.ReleaseDC(IntPtr.Zero);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(surface);
        }
    }

    private static (int X, int Y) Hotspot(IntPtr cursor)
    {
        if (!GetIconInfo(cursor, out var icon))
        {
            return (0, 0);
        }

        // GetIconInfo hands over two bitmaps every call and they are the caller's to free.
        DeleteObject(icon.hbmMask);
        DeleteObject(icon.hbmColor);
        return ((int)icon.xHotspot, (int)icon.yHotspot);
    }

    private (int Width, int Height) Describe()
    {
        Mf.Check(output.GetDesc(out var desc));
        originX = desc.DesktopLeft;
        originY = desc.DesktopTop;
        return (desc.DesktopRight - desc.DesktopLeft, desc.DesktopBottom - desc.DesktopTop);
    }

    private IDXGIOutputDuplication Duplicate()
    {
        int hr = output.DuplicateOutput(device, out var dupl);
        if (hr < 0)
        {
            // 0x887A0022 (NOT_CURRENTLY_AVAILABLE) means the machine already has the
            // maximum number of duplications open; anything else is a driver saying no.
            throw new InvalidOperationException(
                $"This display cannot be duplicated (0x{hr:X8}) — switch capture back to Windows Graphics Capture.");
        }

        return dupl;
    }

    /// <summary>The output whose HMONITOR matches the target, walked from the adapter the
    /// engine's own device sits on — a duplication is only valid on its own adapter.</summary>
    private static IDXGIOutput1 FindOutput(IntPtr device, IntPtr monitor)
    {
        var dxgiIid = IID_IDXGIDevice;
        Mf.Check(Marshal.QueryInterface(device, in dxgiIid, out var dxgiPtr));
        IDXGIAdapter adapter;
        try
        {
            var dxgi = (IDXGIDevice)Marshal.GetObjectForIUnknown(dxgiPtr);
            try
            {
                Mf.Check(dxgi.GetAdapter(out adapter));
            }
            finally
            {
                Marshal.ReleaseComObject(dxgi);
            }
        }
        finally
        {
            Marshal.Release(dxgiPtr);
        }

        try
        {
            for (uint i = 0; ; i++)
            {
                int hr = adapter.EnumOutputs(i, out var candidate);
                if (hr == NotFound)
                {
                    break;
                }

                Mf.Check(hr);
                bool keep = false;
                try
                {
                    Mf.Check(candidate.GetDesc(out var desc));
                    keep = desc.Monitor == monitor && desc.AttachedToDesktop != 0;
                    if (keep)
                    {
                        return candidate;
                    }
                }
                finally
                {
                    if (!keep)
                    {
                        Marshal.ReleaseComObject(candidate);
                    }
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(adapter);
        }

        // A monitor driven by a second GPU: duplication only works from the adapter that
        // drives it, and the engine's device is already bound to the default one.
        throw new InvalidOperationException(
            "That display is driven by a different GPU than the encoder — switch capture back to Windows Graphics Capture.");
    }

    private static IntPtr CreateStaging(IntPtr device, int width, int height)
    {
        // GDI_COMPATIBLE is what makes IDXGISurface1::GetDC legal, and it comes with
        // conditions: B8G8R8A8_UNORM, render-target bindable, one mip, one slice, no
        // multisampling. All of which the encoder's input format already is.
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = 87 /* DXGI_FORMAT_B8G8R8A8_UNORM */,
            SampleCount = 1,
            SampleQuality = 0,
            Usage = 0 /* D3D11_USAGE_DEFAULT */,
            BindFlags = 0x28 /* RENDER_TARGET | SHADER_RESOURCE */,
            CPUAccessFlags = 0,
            MiscFlags = 0x200 /* D3D11_RESOURCE_MISC_GDI_COMPATIBLE */,
        };

        var d3d = (ID3D11Device)Marshal.GetObjectForIUnknown(device);
        try
        {
            int hr = d3d.CreateTexture2D(ref desc, IntPtr.Zero, out var texture);
            if (hr < 0)
            {
                // Named rather than left as a bare HRESULT: every failure here is the
                // GDI-compatible conditions above going unmet on some driver.
                throw new InvalidOperationException(
                    $"Could not allocate a {width}x{height} GDI-compatible capture texture (0x{hr:X8}).");
            }

            return texture;
        }
        finally
        {
            Marshal.ReleaseComObject(d3d);
        }
    }

    public void Dispose()
    {
        stopping = true;
        // Bounded like every other engine teardown: the pump sits in a DXGI wait at most
        // one frame interval long, but it also blocks on the engine's writer lock, and a
        // wedged encoder holds that forever. Resources are only reclaimed once the pump
        // has provably let go of them — a pump still inside WriteFrame would be copying
        // out of a texture this released. Leaking an abandoned duplication beats that.
        if (pump is null || pump.Join(TimeSpan.FromSeconds(2)))
        {
            Cleanup();
        }
    }

    private void Cleanup()
    {
        if (duplication is not null)
        {
            Marshal.ReleaseComObject(duplication);
            duplication = null;
        }

        Marshal.ReleaseComObject(output);
        if (staging != IntPtr.Zero)
        {
            Marshal.Release(staging);
            staging = IntPtr.Zero;
        }
    }

    // ---- native ----

    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid IID_IDXGISurface1 = new("4ae63092-6327-4c1b-80ae-bfe12ea32b86");

    private const int CURSOR_SHOWING = 0x0001;
    private const uint DI_NORMAL = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width, Height, MipLevels, ArraySize, Format;
        public uint SampleCount, SampleQuality;
        public uint Usage, BindFlags, CPUAccessFlags, MiscFlags;
    }

    /// <summary>DXGI_OUTPUT_DESC, with its RECT flattened so the struct stays blittable
    /// without a nested type nothing else needs.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_OUTPUT_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public int DesktopLeft, DesktopTop, DesktopRight, DesktopBottom;
        public int AttachedToDesktop;
        public uint Rotation;
        public IntPtr Monitor;
    }

    /// <summary>DXGI_OUTDUPL_FRAME_INFO. Only its size matters here — the cursor comes
    /// from GetCursorInfo, which reports the shape as well as the position.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTDUPL_FRAME_INFO
    {
        public long LastPresentTime, LastMouseUpdateTime;
        public uint AccumulatedFrames, RectsCoalesced, ProtectedContentMaskedOut;
        public int PointerX, PointerY;
        public uint PointerVisible, TotalMetadataBufferSize, PointerShapeBufferSize;
    }

    [ComImport]
    [Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3D11Device
    {
        [PreserveSig] int _CreateBuffer();
        [PreserveSig] int _CreateTexture1D();
        [PreserveSig] int CreateTexture2D(ref D3D11_TEXTURE2D_DESC desc, IntPtr initialData, out IntPtr texture);
    }

    [ComImport]
    [Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIDevice
    {
        // IDXGIObject
        [PreserveSig] int _SetPrivateData();
        [PreserveSig] int _SetPrivateDataInterface();
        [PreserveSig] int _GetPrivateData();
        [PreserveSig] int _GetParent();
        // IDXGIDevice
        [PreserveSig] int GetAdapter(out IDXGIAdapter adapter);
    }

    [ComImport]
    [Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter
    {
        // IDXGIObject
        [PreserveSig] int _SetPrivateData();
        [PreserveSig] int _SetPrivateDataInterface();
        [PreserveSig] int _GetPrivateData();
        [PreserveSig] int _GetParent();
        // IDXGIAdapter
        [PreserveSig] int EnumOutputs(uint output, out IDXGIOutput1 result);
    }

    [ComImport]
    [Guid("00cddea8-939b-4b83-a340-a685226666cc")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIOutput1
    {
        // IDXGIObject
        [PreserveSig] int _SetPrivateData();
        [PreserveSig] int _SetPrivateDataInterface();
        [PreserveSig] int _GetPrivateData();
        [PreserveSig] int _GetParent();
        // IDXGIOutput
        [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC desc);
        [PreserveSig] int _GetDisplayModeList();
        [PreserveSig] int _FindClosestMatchingMode();
        [PreserveSig] int _WaitForVBlank();
        [PreserveSig] int _TakeOwnership();
        [PreserveSig] int _ReleaseOwnership();
        [PreserveSig] int _GetGammaControlCapabilities();
        [PreserveSig] int _SetGammaControl();
        [PreserveSig] int _GetGammaControl();
        [PreserveSig] int _SetDisplaySurface();
        [PreserveSig] int _GetDisplaySurfaceData();
        [PreserveSig] int _GetFrameStatistics();
        // IDXGIOutput1
        [PreserveSig] int _GetDisplayModeList1();
        [PreserveSig] int _FindClosestMatchingMode1();
        [PreserveSig] int _GetDisplaySurfaceData1();
        [PreserveSig] int DuplicateOutput(IntPtr device, out IDXGIOutputDuplication duplication);
    }

    [ComImport]
    [Guid("191cfac3-a341-470d-b26e-a864f428319c")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIOutputDuplication
    {
        // IDXGIObject
        [PreserveSig] int _SetPrivateData();
        [PreserveSig] int _SetPrivateDataInterface();
        [PreserveSig] int _GetPrivateData();
        [PreserveSig] int _GetParent();
        // IDXGIOutputDuplication
        [PreserveSig] int _GetDesc();
        [PreserveSig] int AcquireNextFrame(
            uint timeoutMs, out DXGI_OUTDUPL_FRAME_INFO info, out IntPtr desktopResource);
        [PreserveSig] int _GetFrameDirtyRects();
        [PreserveSig] int _GetFrameMoveRects();
        [PreserveSig] int _GetFramePointerShape();
        [PreserveSig] int _MapDesktopSurface();
        [PreserveSig] int _UnMapDesktopSurface();
        [PreserveSig] int ReleaseFrame();
    }

    [ComImport]
    [Guid("4ae63092-6327-4c1b-80ae-bfe12ea32b86")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGISurface1
    {
        // IDXGIObject
        [PreserveSig] int _SetPrivateData();
        [PreserveSig] int _SetPrivateDataInterface();
        [PreserveSig] int _GetPrivateData();
        [PreserveSig] int _GetParent();
        // IDXGIDeviceSubObject
        [PreserveSig] int _GetDevice();
        // IDXGISurface
        [PreserveSig] int _GetDesc();
        [PreserveSig] int _Map();
        [PreserveSig] int _Unmap();
        // IDXGISurface1
        [PreserveSig] int GetDC([MarshalAs(UnmanagedType.Bool)] bool discard, out IntPtr hdc);
        [PreserveSig] int ReleaseDC(IntPtr dirtyRect);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CURSORINFO info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(
        IntPtr hdc, int x, int y, IntPtr icon, int cx, int cy,
        uint frame, IntPtr flickerBrush, uint flags);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);
}
