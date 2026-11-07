using System.Runtime.InteropServices;

/// <summary>
/// Plays a quiet sine on ONE specific output device, so the live multi-device test can
/// prove a ticked extra device reaches the clip. SoundPlayer only knows the default
/// device. Kept at -40 dBFS: the owner may be using the PC while this runs.
/// </summary>
internal sealed class ToneRender : IDisposable
{
    private const int Rate = 48000;
    private const int Channels = 2;
    private const double Amplitude = 0.01;  // -40 dBFS

    private readonly IAudioClient client;
    private readonly IAudioRenderClient render;
    private readonly Thread thread;
    private volatile bool stopping;
    private long frame;

    public ToneRender(string deviceId, double hz = 440)
    {
        var type = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!;
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
        try
        {
            Check(enumerator.GetDevice(deviceId, out var device));
            try
            {
                var iid = typeof(IAudioClient).GUID;
                Check(device.Activate(ref iid, 23, IntPtr.Zero, out var clientPtr));
                client = (IAudioClient)Marshal.GetObjectForIUnknown(clientPtr);
                Marshal.Release(clientPtr);
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

        var format = Marshal.AllocHGlobal(18);
        try
        {
            Marshal.WriteInt16(format, 0, 1);
            Marshal.WriteInt16(format, 2, Channels);
            Marshal.WriteInt32(format, 4, Rate);
            Marshal.WriteInt32(format, 8, Rate * 4);
            Marshal.WriteInt16(format, 12, 4);
            Marshal.WriteInt16(format, 14, 16);
            Marshal.WriteInt16(format, 16, 0);
            // AUTOCONVERTPCM | SRC_DEFAULT_QUALITY: the device's mix format is not ours.
            Check(client.Initialize(0, 0x80000000 | 0x08000000, 2_000_000, 0, format, IntPtr.Zero));
        }
        finally
        {
            Marshal.FreeHGlobal(format);
        }

        var renderIid = typeof(IAudioRenderClient).GUID;
        Check(client.GetService(ref renderIid, out var renderPtr));
        render = (IAudioRenderClient)Marshal.GetObjectForIUnknown(renderPtr);
        Marshal.Release(renderPtr);
        Check(client.GetBufferSize(out uint bufferFrames));

        Fill(hz, bufferFrames);
        Check(client.Start());
        thread = new Thread(() =>
        {
            while (!stopping)
            {
                Thread.Sleep(10);
                Fill(hz, bufferFrames);
            }
        }) { IsBackground = true };
        thread.Start();
    }

    private unsafe void Fill(double hz, uint bufferFrames)
    {
        if (client.GetCurrentPadding(out uint padding) < 0)
        {
            return;
        }

        uint frames = bufferFrames - padding;
        if (frames == 0 || render.GetBuffer(frames, out var data) < 0)
        {
            return;
        }

        var samples = new Span<short>((void*)data, (int)frames * Channels);
        for (int i = 0; i < frames; i++, frame++)
        {
            short s = (short)(Math.Sin(2 * Math.PI * hz * frame / Rate) * Amplitude * short.MaxValue);
            samples[2 * i] = s;
            samples[2 * i + 1] = s;
        }

        render.ReleaseBuffer(frames, 0);
    }

    public void Dispose()
    {
        stopping = true;
        thread.Join();
        client.Stop();
        Marshal.ReleaseComObject(render);
        Marshal.ReleaseComObject(client);
    }

    private static void Check(int hr)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, out IntPtr instance);
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
    [Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }
}
