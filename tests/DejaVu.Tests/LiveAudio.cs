using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using DejaVu;

/// <summary>
/// Test signals for the live multi-device checks (#11). Each device gets a quiet steady
/// tone plus a Hann-windowed chirp marker at fixed wall-clock instants, so the clip can
/// be checked for presence, clicks, pitch, echo and the offset between devices. Markers
/// are scheduled through each render stream's IAudioClock against QPC: two devices on
/// different crystals then still emit their markers at the same real instants, and any
/// drift measured in the clip is the recorder's, not the renderer's.
/// </summary>
internal sealed class SignalSpec
{
    public double ToneHz { get; init; }
    public double ToneAmp { get; init; }
    public double MarkerAmp { get; init; }
    public bool MarkerUp { get; init; }
    public double PeriodSec { get; init; }
    public double PhaseSec { get; init; }
}

internal static class Markers
{
    public const int Rate = 48000;
    public const double Seconds = 0.1;
    public const double LowHz = 1500;
    public const double HighHz = 5500;

    public static int Length => (int)(Rate * Seconds);

    /// <summary>The analytic marker: real part is what is rendered (times the amplitude).</summary>
    public static Complex[] Template(bool up)
    {
        int n = Length;
        double f0 = up ? LowHz : HighHz;
        double f1 = up ? HighHz : LowHz;
        var t = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            double s = (double)i / Rate;
            double w = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (n - 1)));
            double phase = 2 * Math.PI * (f0 * s + (f1 - f0) * s * s / (2 * Seconds));
            t[i] = Complex.FromPolarCoordinates(w, phase);
        }

        return t;
    }
}

/// <summary>Renders a <see cref="SignalSpec"/> on one output device, shared mode.</summary>
internal sealed class SignalRender : IDisposable
{
    private const int Rate = Markers.Rate;
    private const int Channels = 2;

    private readonly IAudioClient client;
    private IAudioRenderClient render = null!;
    private IAudioClock audioClock = null!;
    private ulong clockFrequency;
    private readonly SignalSpec spec;
    private readonly double[] marker;
    private readonly double t0;
    private readonly Thread thread;
    private uint bufferFrames;
    private volatile bool stopping;
    private long frame;
    private int nextMarker;
    private long nextMarkerFrame = -1;
    private (double Qpc, double Played)? firstClock;
    private (double Qpc, double Played) lastClock;
    private readonly Queue<(double Qpc, double Played)> readings = new();

    /// <summary>RMS scatter, in frames, of the device clock readings around their fit
    /// at the last marker placement: how stale single readings are.</summary>
    public double ClockJitterFrames { get; private set; }

    public string Name { get; }

    /// <summary>Marker index -> stream frame it was placed at.</summary>
    public List<(int K, long Frame)> Placed { get; } = [];

    /// <summary>Wall-clock seconds (QPC) at which marker k is scheduled to play.</summary>
    public double MarkerTime(int k) => t0 + spec.PhaseSec + k * spec.PeriodSec;

    /// <summary>Device ID meaning "the default playback device, following it when it
    /// changes" (automatic stream routing), the way a game's stream behaves.</summary>
    public const string FollowDefault = "follow-default";

    public SignalRender(string deviceId, string name, SignalSpec spec, double t0Seconds)
    {
        Name = name;
        this.spec = spec;
        t0 = t0Seconds;
        var template = Markers.Template(spec.MarkerUp);
        marker = template.Select(c => c.Real).ToArray();

        if (deviceId == FollowDefault)
        {
            client = ActivateFollowingDefault();
        }
        else
        {
            client = ActivateDevice(deviceId);
        }

        InitializeAndStart();
        thread = new Thread(() =>
        {
            while (!stopping)
            {
                Thread.Sleep(5);
                Fill();
            }
        }) { IsBackground = true, Name = "test signal " + name };
        thread.Start();
    }

    private static IAudioClient ActivateFollowingDefault()
    {
        // DEVINTERFACE_AUDIO_RENDER: the stream moves with the default device.
        var iid = typeof(IAudioClient).GUID;
        var handler = new ActivateHandler();
        Check(ActivateAudioInterfaceAsync("{E6327CAD-DCEC-4949-AE8A-991E976A79D2}", ref iid, IntPtr.Zero, handler, out var op));
        try
        {
            if (!handler.Done.Wait(5000))
            {
                throw new TimeoutException("default render activation timed out");
            }

            Check(op.GetActivateResult(out int hr, out var activated));
            Check(hr);
            return (IAudioClient)activated;
        }
        finally
        {
            Marshal.ReleaseComObject(op);
        }
    }

    private static IAudioClient ActivateDevice(string deviceId)
    {
        IAudioClient client;
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(
            Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
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

        return client;
    }

    private void InitializeAndStart()
    {
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
        var clockIid = typeof(IAudioClock).GUID;
        Check(client.GetService(ref clockIid, out var clockPtr));
        audioClock = (IAudioClock)Marshal.GetObjectForIUnknown(clockPtr);
        Marshal.Release(clockPtr);
        Check(audioClock.GetFrequency(out clockFrequency));
        Check(client.GetBufferSize(out bufferFrames));

        Fill();
        Check(client.Start());
    }

    [DllImport("mmdevapi.dll", CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(
        string deviceInterfacePath, ref Guid riid, IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

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

    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>This device's clock against QPC since the first reading, in ppm.</summary>
    public double ClockPpm
    {
        get
        {
            if (firstClock is not { } a)
            {
                return 0;
            }

            var b = lastClock;
            double wall = b.Qpc - a.Qpc;
            return wall < 1 ? 0 : ((b.Played - a.Played) / wall - 1) * 1e6;
        }
    }

    private unsafe void Fill()
    {
        if (client.GetCurrentPadding(out uint padding) < 0)
        {
            return;
        }

        uint frames = bufferFrames - padding;
        if (frames == 0)
        {
            return;
        }

        // Where the stream is right now, against QPC: frame n plays at
        // qpc + (n - played) / Rate.
        double playedFrames = -1, qpcSeconds = 0;
        if (audioClock.GetPosition(out ulong pos, out ulong qpc) >= 0 && qpc != 0)
        {
            playedFrames = pos * (double)Rate / clockFrequency;
            qpcSeconds = qpc / 1e7;
            if (pos > 0)
            {
                firstClock ??= (qpcSeconds, playedFrames / Rate);
                lastClock = (qpcSeconds, playedFrames / Rate);
                readings.Enqueue((qpcSeconds, playedFrames));
                while (readings.Count > 0 && readings.Peek().Qpc < qpcSeconds - 3)
                {
                    readings.Dequeue();
                }
            }
        }

        // Pin the next marker to a stream frame once it is under a second away and the
        // clock is running; from then on it is fixed.
        if (nextMarkerFrame < 0 && playedFrames > 0 && readings.Count >= 20)
        {
            double due = MarkerTime(nextMarker);
            if (due - qpcSeconds < 1.0)
            {
                // A line through the last 3 s of clock readings: a single reading can be
                // a whole device period stale, the fit is good to a few samples.
                double q0 = readings.Peek().Qpc;
                double sx = 0, sy = 0, sxx = 0, sxy = 0;
                int count = readings.Count;
                foreach (var (q, p) in readings)
                {
                    double xq = q - q0;
                    sx += xq;
                    sy += p;
                    sxx += xq * xq;
                    sxy += xq * p;
                }

                double slope = (count * sxy - sx * sy) / (count * sxx - sx * sx);
                double intercept = (sy - slope * sx) / count;
                double rms = 0;
                foreach (var (q, p) in readings)
                {
                    double r = p - (intercept + slope * (q - q0));
                    rms += r * r;
                }

                ClockJitterFrames = Math.Sqrt(rms / count);
                nextMarkerFrame = (long)Math.Round(intercept + slope * (due - q0));
                if (nextMarkerFrame < frame)
                {
                    // Too late to place exactly; skip it rather than place it wrong.
                    nextMarker++;
                    nextMarkerFrame = -1;
                }
                else
                {
                    Placed.Add((nextMarker, nextMarkerFrame));
                }
            }
        }

        if (render.GetBuffer(frames, out var data) < 0)
        {
            return;
        }

        var samples = new Span<short>((void*)data, (int)frames * Channels);
        for (int i = 0; i < frames; i++, frame++)
        {
            double fade = Math.Min(1.0, frame / (0.05 * Rate));
            double v = spec.ToneAmp * fade * Math.Sin(2 * Math.PI * spec.ToneHz * frame / Rate);
            if (nextMarkerFrame >= 0 && frame >= nextMarkerFrame)
            {
                long at = frame - nextMarkerFrame;
                if (at < marker.Length)
                {
                    v += spec.MarkerAmp * marker[at];
                }

                if (at == marker.Length - 1)
                {
                    nextMarker++;
                    nextMarkerFrame = -1;
                }
            }

            short s = (short)Math.Round(v * short.MaxValue);
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
        Marshal.ReleaseComObject(audioClock);
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

    [ComImport]
    [Guid("CD63314F-3FBA-4a1b-812C-EF96358728E7")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClock
    {
        [PreserveSig] int GetFrequency(out ulong frequency);
        [PreserveSig] int GetPosition(out ulong position, out ulong qpcPosition);
        [PreserveSig] int GetCharacteristics(out uint characteristics);
    }
}

/// <summary>
/// The Windows default playback device, read and switched through IPolicyConfig (what
/// Sound settings uses; no elevation). <see cref="Guard"/> records all three roles and
/// puts them back on Dispose, and leaves a file behind so a killed run can be undone with
/// the "restoredefault" mode.
/// </summary>
internal static class DefaultDevice
{
    public static readonly string RestoreFile = Path.Combine(Path.GetTempPath(), "dejavu_default_device_restore.txt");

    public static string? Get(int role = 0)
    {
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(
            Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(0, role, out var device) < 0)
            {
                return null;
            }

            try
            {
                if (device.GetId(out var idPtr) < 0)
                {
                    return null;
                }

                var id = Marshal.PtrToStringUni(idPtr);
                Marshal.FreeCoTaskMem(idPtr);
                return id;
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

    public static void Set(string id, int role)
    {
        var policy = (IPolicyConfig)Activator.CreateInstance(
            Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"))!)!;
        try
        {
            int hr = policy.SetDefaultEndpoint(id, role);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    }

    /// <summary>Console and multimedia, the pair Sound settings switches together.</summary>
    public static void SetPlayback(string id)
    {
        Set(id, 0);
        Set(id, 1);
    }

    public static Guard Remember() => new();

    /// <summary>Puts back whatever <see cref="RestoreFile"/> holds. True when the file
    /// existed and every role now matches it.</summary>
    public static bool RestoreFromFile()
    {
        if (!File.Exists(RestoreFile))
        {
            return false;
        }

        var ids = File.ReadAllLines(RestoreFile);
        bool ok = true;
        for (int role = 0; role < 3 && role < ids.Length; role++)
        {
            if (ids[role].Length > 0 && Get(role) != ids[role])
            {
                Set(ids[role], role);
            }

            ok &= ids[role].Length == 0 || Get(role) == ids[role];
        }

        if (ok)
        {
            File.Delete(RestoreFile);
        }

        return ok;
    }

    internal sealed class Guard : IDisposable
    {
        public string?[] Original { get; } = [Get(0), Get(1), Get(2)];

        public Guard()
        {
            File.WriteAllLines(RestoreFile, Original.Select(i => i ?? ""));
        }

        public bool Restored { get; private set; }

        public void Dispose()
        {
            for (int attempt = 0; attempt < 5 && !Restored; attempt++)
            {
                try
                {
                    for (int role = 0; role < 3; role++)
                    {
                        if (Original[role] is { } id && Get(role) != id)
                        {
                            Set(id, role);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("      default device restore attempt failed: " + ex.Message);
                }

                Restored = Enumerable.Range(0, 3).All(r => Original[r] is null || Get(r) == Original[r]);
                if (!Restored)
                {
                    Thread.Sleep(500);
                }
            }

            if (Restored)
            {
                File.Delete(RestoreFile);
            }

            Console.WriteLine($"      default playback device restored: {Restored}");
        }
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, out IntPtr instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
        [PreserveSig] int GetId(out IntPtr id);
    }

    // Undocumented but stable since Windows 7; the slots before SetDefaultEndpoint are
    // never called and only hold the vtable layout.
    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr id, IntPtr format);
        [PreserveSig] int GetDeviceFormat(IntPtr id, int defaultFormat, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(IntPtr id);
        [PreserveSig] int SetDeviceFormat(IntPtr id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(IntPtr id, int defaultPeriod, IntPtr period, IntPtr minimum);
        [PreserveSig] int SetProcessingPeriod(IntPtr id, IntPtr period);
        [PreserveSig] int GetShareMode(IntPtr id, IntPtr mode);
        [PreserveSig] int SetShareMode(IntPtr id, IntPtr mode);
        [PreserveSig] int GetPropertyValue(IntPtr id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(IntPtr id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility(IntPtr id, int visible);
    }
}

/// <summary>Decodes a clip's audio track to interleaved 16-bit PCM.</summary>
internal static class ClipAudio
{
    public static (short[] Samples, int Rate, int Channels) Decode(string path)
    {
        Mf.EnsureStarted();
        Mf.Check(Mf.MFCreateSourceReaderFromURL(path, IntPtr.Zero, out var reader));
        try
        {
            Mf.Check(reader.SetStreamSelection(Mf.SOURCE_READER_ALL_STREAMS, false));
            Mf.Check(reader.SetStreamSelection(Mf.SOURCE_READER_FIRST_AUDIO_STREAM, true));
            var pcm = Mf.CreateAudioType(Mf.AudioFormat_PCM, 0, 0, 16, 0, 0);
            try
            {
                Mf.Check(reader.SetCurrentMediaType(Mf.SOURCE_READER_FIRST_AUDIO_STREAM, IntPtr.Zero, pcm));
            }
            finally
            {
                Marshal.ReleaseComObject(pcm);
            }

            var all = new List<short>(1 << 20);
            while (true)
            {
                Mf.Check(reader.ReadSample(
                    Mf.SOURCE_READER_FIRST_AUDIO_STREAM, 0, out _, out uint flags, out _, out IntPtr samplePtr));
                if (samplePtr != IntPtr.Zero)
                {
                    var sample = (Mf.IMFSample)Marshal.GetObjectForIUnknown(samplePtr);
                    Marshal.Release(samplePtr);
                    try
                    {
                        Mf.Check(sample.ConvertToContiguousBuffer(out var buffer));
                        try
                        {
                            Mf.Check(buffer.Lock(out var data, out _, out uint length));
                            unsafe
                            {
                                all.AddRange(new ReadOnlySpan<short>((void*)data, (int)length / 2));
                            }

                            Mf.Check(buffer.Unlock());
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(buffer);
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(sample);
                    }
                }

                if ((flags & Mf.READERF_ENDOFSTREAM) != 0)
                {
                    break;
                }
            }

            // The app writes 48 kHz stereo and nothing else.
            return (all.ToArray(), 48000, 2);
        }
        finally
        {
            Marshal.ReleaseComObject(reader);
        }
    }

    public static float[] Mono(short[] interleaved)
    {
        var mono = new float[interleaved.Length / 2];
        for (int i = 0; i < mono.Length; i++)
        {
            mono[i] = (interleaved[2 * i] + interleaved[2 * i + 1]) * 0.5f;
        }

        return mono;
    }
}

/// <summary>The checks the live tests run over a clip's decoded audio.</summary>
internal static class SignalAnalysis
{
    public const int Rate = Markers.Rate;

    /// <summary>One marker found in the clip: where its strongest copy sits (in samples),
    /// its gain against the rendered amplitude, and the strongest OTHER copy within
    /// ±300 ms relative to it (an echo or a device mixed twice).</summary>
    public readonly record struct Hit(double Position, double Gain, double SecondRatio, double SecondOffsetMs);

    /// <summary>Matched filter over the whole clip for one marker. Peaks of the
    /// envelope above <paramref name="minGain"/> are grouped into events 300 ms wide.</summary>
    public static List<Hit> FindMarkers(float[] mono, bool up, double markerAmp, double minGain = 0.2)
    {
        var template = Markers.Template(up);
        int m = template.Length;
        double norm = template.Sum(c => c.Magnitude * c.Magnitude) / 2 * markerAmp * short.MaxValue;

        const int Fft = 1 << 16;
        int hop = Fft - m;
        var tSpec = new Complex[Fft];
        for (int i = 0; i < m; i++)
        {
            tSpec[i] = template[i];
        }

        Dsp.Fft(tSpec, false);

        var peaks = new List<(double Pos, double Gain)>();
        var buf = new Complex[Fft];
        var env = new double[hop];
        for (int start = 0; start < mono.Length; start += hop)
        {
            Array.Clear(buf);
            for (int i = 0; i < Fft && start + i < mono.Length; i++)
            {
                buf[i] = mono[start + i];
            }

            Dsp.Fft(buf, false);
            for (int i = 0; i < Fft; i++)
            {
                buf[i] *= Complex.Conjugate(tSpec[i]);
            }

            Dsp.Fft(buf, true);
            int valid = Math.Min(hop, mono.Length - start);
            for (int i = 0; i < valid; i++)
            {
                env[i] = buf[i].Magnitude / norm;
            }

            for (int i = 1; i + 1 < valid; i++)
            {
                if (env[i] >= minGain * 0.25 && env[i] >= env[i - 1] && env[i] > env[i + 1])
                {
                    // Parabolic refinement of the envelope peak.
                    double a = env[i - 1], b = env[i], c = env[i + 1];
                    double den = a - 2 * b + c;
                    double d = den == 0 ? 0 : 0.5 * (a - c) / den;
                    peaks.Add((start + i + d, b));
                }
            }
        }

        // Group into events: anything within 300 ms of the strongest peak belongs to it.
        var hits = new List<Hit>();
        var used = new bool[peaks.Count];
        var order = Enumerable.Range(0, peaks.Count).OrderByDescending(i => peaks[i].Gain).ToList();
        foreach (int i in order)
        {
            if (used[i] || peaks[i].Gain < minGain)
            {
                continue;
            }

            used[i] = true;
            double second = 0, secondAt = 0;
            for (int j = 0; j < peaks.Count; j++)
            {
                double dt = Math.Abs(peaks[j].Pos - peaks[i].Pos);
                if (j == i || dt > 0.3 * Rate)
                {
                    continue;
                }

                used[j] = true;
                // The envelope's own main lobe is ~0.5 ms wide; anything past 1.5 ms
                // is a separate copy.
                if (dt > 0.0015 * Rate && peaks[j].Gain > second)
                {
                    second = peaks[j].Gain;
                    secondAt = (peaks[j].Pos - peaks[i].Pos) * 1000.0 / Rate;
                }
            }

            hits.Add(new Hit(peaks[i].Pos, peaks[i].Gain, second / peaks[i].Gain, secondAt));
        }

        return hits.OrderBy(h => h.Position).ToList();
    }

    /// <summary>Clicks: a discontinuity is broadband, while every test signal stays
    /// under 5.5 kHz. Energy above 8 kHz past <paramref name="threshold"/> (in LSB) is a
    /// click. Returns the sample positions found (merged within 10 ms) and the 99.9th
    /// percentile of the high band, the noise floor the threshold is judged against.</summary>
    public static (List<int> Clicks, double Floor, double Max) FindClicks(
        float[] mono, double threshold, int skipStart = 0, int skipEnd = 0)
    {
        var hp = Dsp.HighPass(8000, 127);
        int half = hp.Length / 2;
        var clicks = new List<int>();
        var hist = new int[4096];
        double max = 0;
        long counted = 0;
        for (int n = half + skipStart; n + half + skipEnd < mono.Length; n++)
        {
            double acc = 0;
            int baseIndex = n - half;
            for (int k = 0; k < hp.Length; k++)
            {
                acc += hp[k] * mono[baseIndex + k];
            }

            double v = Math.Abs(acc);
            max = Math.Max(max, v);
            hist[Math.Min(hist.Length - 1, (int)(v * 16))]++;
            counted++;
            if (v > threshold && (clicks.Count == 0 || n - clicks[^1] > Rate / 100))
            {
                clicks.Add(n);
            }
        }

        long target = (long)(counted * 0.999), seen = 0;
        double floor = 0;
        for (int i = 0; i < hist.Length; i++)
        {
            seen += hist[i];
            if (seen >= target)
            {
                floor = i / 16.0;
                break;
            }
        }

        return (clicks, floor, max);
    }

    /// <summary>The tone's frequency in consecutive 100 ms blocks, from the phase advance
    /// of the demodulated tone. Blocks overlapping a marker are left out (NaN).</summary>
    public static double[] TrackTone(float[] mono, double hz, IEnumerable<double>? markerStarts = null)
    {
        int block = Rate / 10;
        int blocks = mono.Length / block;
        var phases = new double[blocks];
        var magnitude = new double[blocks];
        for (int b = 0; b < blocks; b++)
        {
            Complex acc = 0;
            for (int i = 0; i < block; i++)
            {
                int n = b * block + i;
                double ph = -2 * Math.PI * hz * n / Rate;
                acc += mono[n] * new Complex(Math.Cos(ph), Math.Sin(ph));
            }

            phases[b] = acc.Phase;
            magnitude[b] = acc.Magnitude / block * 2;
        }

        var skip = new bool[blocks];
        foreach (var start in markerStarts ?? [])
        {
            int b0 = (int)(start / block) - 1, b1 = (int)((start + Markers.Length) / block) + 1;
            for (int b = Math.Max(0, b0); b <= b1 && b < blocks; b++)
            {
                skip[b] = true;
            }
        }

        var freq = new double[Math.Max(0, blocks - 1)];
        for (int b = 0; b + 1 < blocks; b++)
        {
            if (skip[b] || skip[b + 1] || magnitude[b] < 1 || magnitude[b + 1] < 1)
            {
                freq[b] = double.NaN;
                continue;
            }

            double d = phases[b + 1] - phases[b];
            d = Math.IEEERemainder(d, 2 * Math.PI);
            freq[b] = hz + d / (2 * Math.PI * 0.1);
        }

        return freq;
    }

    /// <summary>Amplitude (in LSB) of the tone at <paramref name="hz"/>, the median over
    /// 100 ms blocks: immune to a slow phase roll from a resampled stream.</summary>
    public static double ToneLevel(float[] mono, double hz)
    {
        int block = Rate / 10;
        var levels = new List<double>();
        for (int b = 0; (b + 1) * block <= mono.Length; b++)
        {
            Complex acc = 0;
            for (int i = 0; i < block; i++)
            {
                int n = b * block + i;
                double ph = -2 * Math.PI * hz * n / Rate;
                acc += mono[n] * new Complex(Math.Cos(ph), Math.Sin(ph));
            }

            levels.Add(acc.Magnitude / block * 2);
        }

        levels.Sort();
        return levels.Count == 0 ? 0 : levels[levels.Count / 2];
    }
}

internal static class Dsp
{
    public static void Fft(Complex[] a, bool inverse)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (a[i], a[j]) = (a[j], a[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            var wl = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (int i = 0; i < n; i += len)
            {
                Complex w = 1;
                for (int k = 0; k < len / 2; k++)
                {
                    var u = a[i + k];
                    var v = a[i + k + len / 2] * w;
                    a[i + k] = u + v;
                    a[i + k + len / 2] = u - v;
                    w *= wl;
                }
            }
        }

        if (inverse)
        {
            for (int i = 0; i < n; i++)
            {
                a[i] /= n;
            }
        }
    }

    /// <summary>Windowed-sinc (Blackman) high-pass, odd length.</summary>
    public static double[] HighPass(double cutoffHz, int taps)
    {
        var h = new double[taps];
        int mid = taps / 2;
        double fc = cutoffHz / Markers.Rate;
        for (int i = 0; i < taps; i++)
        {
            int k = i - mid;
            double sinc = k == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * k) / (Math.PI * k);
            double w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (taps - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (taps - 1));
            h[i] = -sinc * w;
        }

        h[mid] += 1;
        return h;
    }
}
