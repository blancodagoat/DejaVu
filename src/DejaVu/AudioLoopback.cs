using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DejaVu;

/// <summary>
/// System-audio capture that can leave one app out of the mix — or keep only one in.
/// With a PID it activates Windows' process-loopback virtual device (Win10 2004+):
/// exclude mode drops that app's tree (Discord and its voice children, typically),
/// include mode captures nothing but it (the captured game, keeping virtual-mixer
/// mic re-renders out). With PID 0 it captures the default render endpoint whole.
/// PCM is encoded to AAC on the fly through a Media Foundation sink writer, one file per
/// buffer segment; silence is synthesized whenever the system renders nothing, because
/// loopback delivers no packets then and the AAC timeline must stay continuous.
/// The default microphone, when opted in, is mixed into that same stream, and so is any
/// extra output device ticked in the tray, each drift-corrected to this stream's clock.
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
    private const uint AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR = 0x4;
    private const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);

#if DEBUG
#pragma warning disable CS0649 // set by the test harness, which compiles this file in
    /// <summary>Debug builds only, for the live device-loss test: the extra device with
    /// this endpoint ID fails its next read with AUDCLNT_E_DEVICE_INVALIDATED, exactly
    /// what an unplug or a Sonar restart does mid-segment. Cleared once it fires.</summary>
    internal static volatile string? DebugInvalidateExtra;

    /// <summary>Debug builds only: the extra device with this endpoint ID reads as
    /// unplugged when a segment opens it, until cleared.</summary>
    internal static volatile string? DebugUnpluggedExtra;

    /// <summary>Debug builds only: app-only capture always takes process loopback, even
    /// when the app's session sits on another device. For probing what that path hears.</summary>
    internal static volatile bool DebugForceProcessLoopback;

    /// <summary>Debug builds only: the running capture acts as if Windows had just made
    /// this endpoint ID the default playback device. Cleared once handled.</summary>
    internal static volatile string? DebugDefaultChanged;
#pragma warning restore CS0649
#endif

    // Mic backlog bounds, in frames: mixing starts once a cushion has built up, and
    // the backlog is trimmed back to it when the mic's clock runs ahead of render's.
    private const int MicPrimeFrames = SampleRate * 3 / 100;
    private const int MicMaxFrames = SampleRate / 5;

    private readonly IAudioClient? client;
    private readonly IAudioCaptureClient? capture;
    private readonly IAudioClient? micClient;
    private readonly IAudioCaptureClient? micCapture;
    private readonly Mf.IMFSinkWriter writer;
    private readonly int streamIndex;
    private readonly Thread pump;
    private readonly Stopwatch clock = new();

    private long writtenFrames;
    private long baseFrames;
    private long synthesizedFrames;
    private volatile bool stopping;
    private bool finalized;
    private bool deviceErrorLogged;
    private bool micErrorLogged;

    // Extra render devices recorded whole and mixed in (#11), each on its own clock.
    private readonly List<ExtraDevice> extras = [];

    // Capture time (QPC seconds) of the next main-stream frame written, from the main
    // packets' own timestamps; NaN when the route gives none. The extras are held a
    // fixed delay behind it.
    private double mainNextTime = double.NaN;

    // Process loopback hears every output device: a default change moves sound the
    // extras already record into the main stream. Set from the notification thread.
    private bool mainHearsEveryDevice;
    private volatile string? defaultChangedTo;
    private DefaultWatcher? watcher;

    // Interleaved mic samples not yet mixed. Pump thread only, like the writer.
    private readonly short[]? micRing;
    private int micHead;
    private int micCount;
    private bool micPrimed;
    private short[] mixScratch = [];

    /// <summary>Null when audio capture is unavailable; the caller records silent video.
    /// With <paramref name="includeOnly"/> the pid's tree is the whole mix instead of the
    /// one part left out — "captured app audio only". <paramref name="system"/> off with
    /// <paramref name="mic"/> on records the microphone alone. <paramref name="extraDevices"/>
    /// are render endpoint IDs recorded whole on top of the system audio; any that are
    /// not plugged in are skipped.</summary>
    public static AudioLoopback? TryStart(
        string outputPath, int pid, bool includeOnly = false, bool system = true, bool mic = false,
        IReadOnlyList<string>? extraDevices = null)
    {
        try
        {
            return new AudioLoopback(outputPath, pid, includeOnly, system, mic, extraDevices ?? []);
        }
        catch (Exception ex)
        {
            // The reason must reach the log: a silent audio failure in the field is
            // undiagnosable from "my clips have no sound".
            AppLog.Write("audio unavailable: " + ex.Message);
            return null;
        }
    }

    /// <summary>How this capture is actually wired, for the log — audio bug reports are
    /// unreadable without knowing which path a session used.</summary>
    public string Route { get; }

    /// <summary>Extra devices actually opened, with each drift corrector's state. For
    /// diagnostics and the live test; read while the pump runs, so values are a snapshot.</summary>
    internal IReadOnlyList<(string Name, double Ratio, int Backlog, long Dropped, long Underruns, bool Dead)> ExtraStats =>
        extras.Select(e => (e.Name, e.Mixer.Ratio, e.Mixer.BufferedFrames, e.Mixer.DroppedFrames, e.Mixer.Underruns, e.Dead)).ToList();

    private AudioLoopback(
        string outputPath, int pid, bool includeOnly, bool system, bool mic, IReadOnlyList<string> extraDevices)
    {
        Mf.EnsureStarted();

        // Device first, writer last: activation is the step that fails on machines with
        // no working endpoint, and creating the sink writer before it leaked one writer
        // plus one locked zero-byte aud_*.mp4 per segment, forever, on those boxes.
        Route = "microphone only";
        try
        {
            if (system)
            {
                // The endpoint the main stream hears, so an extra device that is the
                // same one is not mixed in twice.
                string? mainDeviceId = DefaultRenderId();
                bool vad = false;
                int excludedPid = 0;
                if (pid > 0 && includeOnly)
                {
                    // Per-app routing (SteelSeries Sonar/GG, Windows' per-app output devices)
                    // moves the app's session to its own endpoint, where the process-loopback
                    // virtual device hears NOTHING — measured dead silence against a game
                    // routed by GG. The endpoint hosting the app's session is the truth: on
                    // routed setups its mix IS the app (the mixer already keeps voice and mic
                    // on other endpoints), and only when the app shares the default device is
                    // the include-tree VAD both correct and necessary.
                    var routed = FindSessionDevice(pid);
#if DEBUG
                    if (DebugForceProcessLoopback && routed is { } skipped)
                    {
                        Marshal.ReleaseComObject(skipped.Device);
                        routed = null;
                    }
#endif
                    if (routed is { IsDefault: false } home)
                    {
                        Route = $"app-only via the app's output device (pid {pid})";
                        mainDeviceId = DeviceId(home.Device);
                        client = ActivateDevice(home.Device);
                    }
                    else
                    {
                        Route = routed is null
                            ? $"app-only via process loopback (pid {pid}; no live session found yet)"
                            : $"app-only via process loopback (pid {pid})";
                        vad = true;
                        client = ActivateProcessLoopback(pid, LoopbackModeIncludeTree);
                    }
                }
                else if (pid > 0)
                {
                    Route = $"system mix excluding pid {pid}";
                    vad = true;
                    excludedPid = pid;
                    client = ActivateProcessLoopback(pid, LoopbackModeExcludeTree);
                }
                else
                {
                    Route = "full system mix (default device)";
                    client = ActivateDefaultEndpoint(0 /* eRender */);
                }

                uint flags = AUDCLNT_STREAMFLAGS_LOOPBACK;
                if (!vad)
                {
                    // Endpoint paths must accept our fixed format; the process-loopback
                    // virtual device takes the requested format as-is.
                    flags |= AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
                }

                capture = InitializeCapture(client, flags);
                mainHearsEveryDevice = vad;
                Route += OpenExtras(extraDevices, mainDeviceId, excludedPid);
            }

            if (mic)
            {
                try
                {
                    micClient = ActivateDefaultEndpoint(1 /* eCapture */);
                    micCapture = InitializeCapture(
                        micClient, AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY);
                    micRing = new short[SampleRate / 2 * Channels];
                    if (system)
                    {
                        Route += " + microphone";
                    }
                }
                catch (Exception ex) when (system)
                {
                    // The mic is the extra: a missing one, or Windows' privacy switch
                    // denying desktop apps, must not cost the clip its game audio.
                    AppLog.Write("microphone unavailable: " + ex.Message);
                    if (micClient is not null)
                    {
                        Marshal.ReleaseComObject(micClient);
                        micClient = null;
                    }
                }
            }

            // Same throttling opt-out as every other writer in the app: the sink
            // writer's pacing was caught blocking in a memory dump once already, and
            // the resync path below can burst a second of silence at a time.
            Mf.Check(Mf.MFCreateAttributes(out var attrs, 1));
            var key = Mf.SINK_WRITER_DISABLE_THROTTLING;
            Mf.Check(attrs.SetUINT32(ref key, 1));
            var attrsPtr = Marshal.GetIUnknownForObject(attrs);
            try
            {
                Mf.Check(Mf.MFCreateSinkWriterFromURL(outputPath, IntPtr.Zero, attrsPtr, out writer));
            }
            finally
            {
                Marshal.Release(attrsPtr);
                Marshal.ReleaseComObject(attrs);
            }

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

            if (client is not null)
            {
                Mf.Check(client.Start());
            }

            if (micClient is not null)
            {
                Mf.Check(micClient.Start());
            }

            foreach (var extra in extras)
            {
                int hr = extra.Client.Start();
                if (hr < 0)
                {
                    // An extra never costs the clip its main audio.
                    extra.Dead = true;
                    AppLog.Write($"extra audio device {extra.Name} would not start: 0x{hr:X8}");
                }
            }

            if (mainHearsEveryDevice && extras.Count > 0)
            {
                watcher = DefaultWatcher.TryStart(id => defaultChangedTo = id);
            }
        }
        catch
        {
            ReleaseIfSet(writer);
            ReleaseIfSet(micCapture);
            ReleaseIfSet(micClient);
            ReleaseExtras();
            ReleaseIfSet(capture);
            ReleaseIfSet(client);
            throw;
        }

        clock.Start();

        pump = new Thread(Pump) { IsBackground = true, Name = "DejaVu audio pump" };
        pump.Start();
    }

    private static void ReleaseIfSet(object? com)
    {
        if (com is not null)
        {
            Marshal.ReleaseComObject(com);
        }
    }

    /// <summary>One extra render endpoint, loopback-captured whole and mixed in through
    /// its own drift corrector. Pump thread only after construction.</summary>
    private sealed class ExtraDevice(
        string id, string name, IAudioClient client, IAudioCaptureClient capture, IReadOnlyCollection<string> covers)
    {
        public string Id { get; } = id;

        /// <summary>The output devices whose sound this stream carries.</summary>
        public IReadOnlyCollection<string> Covers { get; } = covers;

        public string Name { get; } = name;
        public IAudioClient Client { get; } = client;
        public IAudioCaptureClient Capture { get; } = capture;
        public DriftMixer Mixer { get; } = new(Channels, SampleRate);

        /// <summary>Unplugged or restarted mid-segment: its calls only fail from here on,
        /// and the next segment looks for it afresh.</summary>
        public bool Dead { get; set; }
    }

    // Extra device IDs already reported as missing, so a ticked device that stays
    // unplugged costs one log line, not one per one-minute segment.
    private static readonly HashSet<string> reportedMissing = new(StringComparer.OrdinalIgnoreCase);

    private static void ReportMissing(string id, string why)
    {
        lock (reportedMissing)
        {
            if (reportedMissing.Add(id))
            {
                AppLog.Write($"extra audio device skipped ({why}): {id}");
            }
        }
    }

    private static void ReportPresent(string id, string name)
    {
        lock (reportedMissing)
        {
            if (reportedMissing.Remove(id))
            {
                AppLog.Write($"extra audio device back: {name}");
            }
        }
    }

    /// <summary>Which ticked devices a segment records, and how (#11).</summary>
    /// <param name="Endpoints">Devices captured whole through their own loopback.</param>
    /// <param name="HeardByMain">Ticked devices the main stream already hears in full.</param>
    /// <param name="ExcludedApp">Add the excluded app's own audio back: it plays on ticked
    /// devices only, where a whole capture of the device would have carried it.</param>
    /// <param name="ExcludedAppCovers">The devices that excluded-app stream stands for.</param>
    internal sealed record ExtraPlan(
        List<string> Endpoints, List<string> HeardByMain, bool ExcludedApp, List<string> ExcludedAppCovers);

    /// <summary>
    /// The default device is always in the main stream, and the main stream's own device
    /// is already heard: neither is mixed twice. On the exclude route the main stream is
    /// process loopback minus the excluded app's tree, and process loopback hears EVERY
    /// output device, not just the default one: a ticked device captured whole on top
    /// would carry everything on it twice. There the one thing a ticked device adds is the
    /// excluded app's own sound on it (Discord on Sonar Chat), and that is added back
    /// only when the app plays on ticked, non-default devices alone, so Keep Discord out
    /// still holds for the default device. <paramref name="excludedAppDevices"/> are the
    /// devices hosting an audio session of the excluded tree.
    /// </summary>
    internal static ExtraPlan PlanExtras(
        IReadOnlyList<string> ticked, string? defaultId, string? mainDeviceId, bool excludeRoute,
        IReadOnlyCollection<string>? excludedAppDevices)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        var candidates = ticked.Where(id => !string.IsNullOrEmpty(id)).Distinct(cmp)
            .Where(id => !cmp.Equals(id, defaultId) && !cmp.Equals(id, mainDeviceId)).ToList();
        if (!excludeRoute)
        {
            return new ExtraPlan(candidates, [], false, []);
        }

        var appDevices = (excludedAppDevices ?? []).Distinct(cmp).ToList();
        bool addBack = appDevices.Count > 0 && appDevices.All(d => candidates.Contains(d, cmp));
        return new ExtraPlan([], candidates, addBack, addBack ? appDevices : []);
    }

    /// <summary>Whether an extra stream standing for <paramref name="covers"/> must fall
    /// silent when <paramref name="newDefault"/> becomes the default playback device: on
    /// routes that hear every device, the main stream now carries that device's sound
    /// too (a game following the default moves there). An endpoint main stream stays on
    /// the device it opened, so nothing is doubled there.</summary>
    internal static bool MuteOnDefaultChange(IReadOnlyCollection<string> covers, string newDefault, bool mainHearsEveryDevice) =>
        mainHearsEveryDevice && covers.Contains(newDefault, StringComparer.OrdinalIgnoreCase);

    /// <summary>Opens what <see cref="PlanExtras"/> decides; returns the route suffix.</summary>
    private string OpenExtras(IReadOnlyList<string> ids, string? mainDeviceId, int excludedPid)
    {
        if (ids.Count == 0)
        {
            return "";
        }

        string? defaultId = DefaultRenderId();
        IMMDeviceEnumerator enumerator;
        try
        {
            enumerator = CreateDeviceEnumerator();
        }
        catch (Exception ex)
        {
            AppLog.Write("extra audio devices unavailable: " + ex.Message);
            return "";
        }

        try
        {
            var plan = PlanExtras(ids, defaultId, mainDeviceId, excludedPid > 0,
                excludedPid > 0 ? SessionDevices(enumerator, ProcessTree(excludedPid)) : null);
            if (excludedPid > 0)
            {
                return OpenExcludedApp(enumerator, plan, excludedPid);
            }

            foreach (var id in plan.Endpoints)
            {
                if (enumerator.GetDevice(id, out var device) < 0 || device is null)
                {
                    ReportMissing(id, "unknown device");
                    continue;
                }

                IAudioClient? extraClient = null;
                try
                {
                    int stateHr = device.GetState(out uint state);
#if DEBUG
                    if (string.Equals(id, DebugUnpluggedExtra, StringComparison.OrdinalIgnoreCase))
                    {
                        state = 8; // DEVICE_STATE_UNPLUGGED
                    }
#endif
                    if (stateHr < 0 || state != 1 /* DEVICE_STATE_ACTIVE */)
                    {
                        ReportMissing(id, "not connected");
                        continue;
                    }

                    if (!IsRender(device))
                    {
                        ReportMissing(id, "not an output device");
                        continue;
                    }

                    string name = FriendlyName(device) ?? id;
                    extraClient = ActivateDevice(device, release: false);
                    var extraCapture = InitializeCapture(extraClient,
                        AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY);
                    extras.Add(new ExtraDevice(id, name, extraClient, extraCapture, [id]));
                    extraClient = null;
                    ReportPresent(id, name);
                }
                catch (Exception ex)
                {
                    ReportMissing(id, ex.Message);
                }
                finally
                {
                    ReleaseIfSet(extraClient);
                    Marshal.ReleaseComObject(device);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }

        return extras.Count == 0 ? "" : " + " + string.Join(" + ", extras.Select(e => e.Name));
    }

    /// <summary>The exclude route: ticked devices are already in the main stream; only
    /// the excluded app's own audio may be added, as an include-tree capture.</summary>
    private string OpenExcludedApp(IMMDeviceEnumerator enumerator, ExtraPlan plan, int pid)
    {
        string Names(IEnumerable<string> ids) => string.Join(", ", ids.Select(id => NameOf(enumerator, id)));
        var suffix = plan.HeardByMain.Count == 0 ? "" : $" (already hears {Names(plan.HeardByMain)})";
        if (!plan.ExcludedApp)
        {
            return suffix;
        }

        IAudioClient? appClient = null;
        try
        {
            appClient = ActivateProcessLoopback(pid, LoopbackModeIncludeTree);
            var appCapture = InitializeCapture(appClient, AUDCLNT_STREAMFLAGS_LOOPBACK);
            string name = $"pid {pid}'s own audio on {Names(plan.ExcludedAppCovers)}";
            extras.Add(new ExtraDevice($"pid {pid}", name, appClient, appCapture, plan.ExcludedAppCovers));
            appClient = null;
            return suffix + " + " + name;
        }
        catch (Exception ex)
        {
            AppLog.Write($"excluded app's own audio unavailable (pid {pid}): {ex.Message}");
            return suffix;
        }
        finally
        {
            ReleaseIfSet(appClient);
        }
    }

    private static string NameOf(IMMDeviceEnumerator enumerator, string id)
    {
        if (enumerator.GetDevice(id, out var device) < 0 || device is null)
        {
            return id;
        }

        try
        {
            return FriendlyName(device) ?? id;
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    private void ReleaseExtras()
    {
        foreach (var extra in extras)
        {
            ReleaseIfSet(extra.Capture);
            ReleaseIfSet(extra.Client);
        }

        extras.Clear();
    }

    /// <summary>Initializes <paramref name="client"/> for our fixed PCM format and
    /// returns its capture service.</summary>
    private static IAudioCaptureClient InitializeCapture(IAudioClient client, uint flags)
    {
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

            Mf.Check(client.Initialize(
                AUDCLNT_SHAREMODE_SHARED, flags, 2_000_000, 0, format, IntPtr.Zero));
        }
        finally
        {
            Marshal.FreeHGlobal(format);
        }

        var captureIid = IID_IAudioCaptureClient;
        Mf.Check(client.GetService(ref captureIid, out var capturePtr));
        var capture = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(capturePtr);
        Marshal.Release(capturePtr);
        return capture;
    }

    private void Pump()
    {
        try
        {
            while (!stopping)
            {
                Thread.Sleep(10);
#if DEBUG
                if (DebugDefaultChanged is { } simulated)
                {
                    DebugDefaultChanged = null;
                    defaultChangedTo = simulated;
                }
#endif
                if (defaultChangedTo is { } newDefault)
                {
                    defaultChangedTo = null;
                    OnDefaultChanged(newDefault);
                }

                // Mic first, so the frames written next have its newest samples to mix.
                if (micCapture is not null)
                {
                    Drain(micCapture, mic: true);
                }

                // Extras likewise queue ahead of the main stream that pulls them.
                foreach (var extra in extras)
                {
                    DrainExtra(extra);
                }

                if (capture is not null)
                {
                    Drain(capture, mic: false);
                }

                // Loopback goes quiet when nothing renders; keep the timeline continuous.
                // Lag up to ~20 ms is packet jitter, anything beyond becomes silence.
                long expected = baseFrames + (long)(clock.Elapsed.TotalSeconds * SampleRate);
                long deficit = expected - writtenFrames - SampleRate / 50;
                if (deficit > 2 * SampleRate)
                {
                    // The thread itself was suspended (sleep/resume, hibernate) — the
                    // per-iteration fill can only ever fall ~16 ms behind on its own.
                    // Filling the whole gap wrote hours of silence at 100× real time
                    // and shoved every later segment's timeline forward. Resync instead.
                    AppLog.Write($"audio resync after a {deficit / SampleRate} s gap");
                    clock.Restart();
                    baseFrames = writtenFrames;
                    continue;
                }

                if (deficit > 0)
                {
                    synthesizedFrames += Math.Min(deficit, SampleRate);
                    WriteFrames(IntPtr.Zero, (int)Math.Min(deficit, SampleRate));
                }
            }
        }
        catch (Exception ex)
        {
            // Disk full, an MFT dying after device loss — the video pump survives its
            // failures and this thread crashing took the whole process with it.
            AppLog.Write("audio pump stopped: " + ex.Message);
        }
    }

    /// <summary>Pump thread: the default playback device changed mid-segment.</summary>
    private void OnDefaultChanged(string newDefault)
    {
        foreach (var extra in extras)
        {
            if (!extra.Mixer.Muted && MuteOnDefaultChange(extra.Covers, newDefault, mainHearsEveryDevice))
            {
                extra.Mixer.Mute();
                AppLog.Write($"extra audio device {extra.Name}: now the default device, already in the system mix; "
                    + "left out until the next segment");
            }
        }
    }

    private void Drain(IAudioCaptureClient source, bool mic)
    {
        while (true)
        {
            int hr = source.GetNextPacketSize(out uint packet);
            if (hr < 0)
            {
                LogDeviceError("GetNextPacketSize", hr, mic);
                return;
            }

            if (packet == 0)
            {
                return;
            }

            hr = source.GetBuffer(out var data, out uint frames, out uint flags, out _, out long qpc);
            if (hr < 0)
            {
                LogDeviceError("GetBuffer", hr, mic);
                return;
            }

            if (!mic && qpc > 0 && (flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) == 0)
            {
                mainNextTime = qpc / 1e7;
            }

            if (frames == 0)
            {
                // A phantom packet (driver reports a size it cannot hand over) would
                // otherwise spin this loop at 100% CPU.
                source.ReleaseBuffer(0);
                return;
            }

            bool silent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
            if (mic)
            {
                QueueMic(silent ? IntPtr.Zero : data, (int)frames);
            }
            else
            {
                WriteFrames(silent ? IntPtr.Zero : data, (int)frames);
            }

            source.ReleaseBuffer(frames);
        }
    }

    /// <summary>Queues an extra device's packets into its drift corrector. A failing
    /// device (unplugged, Sonar restarting) is dropped for the rest of this segment —
    /// the main stream and the buffer carry on — and the next segment retries it.</summary>
    private unsafe void DrainExtra(ExtraDevice extra)
    {
        if (extra.Dead)
        {
            return;
        }

        while (true)
        {
            int hr = extra.Capture.GetNextPacketSize(out uint packet);
            if (hr >= 0 && packet == 0)
            {
                return;
            }

#if DEBUG
            if (string.Equals(extra.Id, DebugInvalidateExtra, StringComparison.OrdinalIgnoreCase))
            {
                DebugInvalidateExtra = null;
                hr = AUDCLNT_E_DEVICE_INVALIDATED;
            }
#endif
            IntPtr data = IntPtr.Zero;
            uint frames = 0, flags = 0;
            long qpc = 0;
            if (hr >= 0)
            {
                hr = extra.Capture.GetBuffer(out data, out frames, out flags, out _, out qpc);
            }

            if (hr < 0)
            {
                extra.Dead = true;
                AppLog.Write($"extra audio device lost: {extra.Name} (0x{hr:X8}); recording carries on without it");
                return;
            }

            if (frames == 0)
            {
                // Phantom packet: see Drain.
                extra.Capture.ReleaseBuffer(0);
                return;
            }

            double time = qpc > 0 && (flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) == 0 ? qpc / 1e7 : double.NaN;
            if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0 || data == IntPtr.Zero)
            {
                extra.Mixer.QueueSilence((int)frames, time);
            }
            else
            {
                extra.Mixer.Queue(new ReadOnlySpan<short>((void*)data, (int)frames * Channels), time);
            }

            extra.Capture.ReleaseBuffer(frames);
        }
    }

    /// <summary>Once per instance: a dead device (unplugged, default switched, driver
    /// restart) fails every call until the next segment builds a fresh capture, and the
    /// old behavior — synthesize silence, say nothing — was undiagnosable in the field.</summary>
    private void LogDeviceError(string call, int hr, bool mic)
    {
        ref bool logged = ref mic ? ref micErrorLogged : ref deviceErrorLogged;
        if (!logged)
        {
            logged = true;
            AppLog.Write($"{(mic ? "microphone" : "audio")} device error: {call} 0x{hr:X8}; silence until the next segment");
        }
    }

    private unsafe void QueueMic(IntPtr data, int frames)
    {
        var ring = micRing!;
        int samples = frames * Channels;
        var source = data == IntPtr.Zero ? default : new ReadOnlySpan<short>((void*)data, samples);
        for (int i = 0; i < samples; i++)
        {
            ring[(micHead + micCount) % ring.Length] = source.IsEmpty ? (short)0 : source[i];
            if (micCount < ring.Length)
            {
                micCount++;
            }
            else
            {
                micHead = (micHead + 1) % ring.Length;
            }
        }

        // ponytail: mic and render clocks drift apart, and trimming the backlog back to
        // the cushion clicks once in a while. Resample the mic if anyone ever hears it.
        if (micCount > MicMaxFrames * Channels)
        {
            int drop = micCount - MicPrimeFrames * Channels;
            micHead = (micHead + drop) % ring.Length;
            micCount -= drop;
        }
    }

    /// <summary>Adds queued mic samples into <paramref name="mix"/>, saturating.</summary>
    private void MixMic(Span<short> mix)
    {
        if (!micPrimed)
        {
            if (micCount < MicPrimeFrames * Channels)
            {
                return;
            }

            micPrimed = true;
        }

        var ring = micRing!;
        int n = Math.Min(mix.Length, micCount);
        for (int i = 0; i < n; i++)
        {
            mix[i] = (short)Math.Clamp(mix[i] + ring[micHead], short.MinValue, short.MaxValue);
            micHead = (micHead + 1) % ring.Length;
        }

        micCount -= n;
        if (n < mix.Length)
        {
            // Underrun: rebuild the cushion instead of stuttering on every packet.
            micPrimed = false;
        }
    }

    private unsafe void WriteFrames(IntPtr data, int frames)
    {
        if (frames <= 0)
        {
            return;
        }

        // Synthesized silence carries on the main stream's timeline from its last packet.
        double time = mainNextTime;
        if (!double.IsNaN(mainNextTime))
        {
            mainNextTime += frames / (double)SampleRate;
        }

        if (micRing is null && extras.Count == 0)
        {
            Emit(data, frames);
            return;
        }

        int samples = frames * Channels;
        if (mixScratch.Length < samples)
        {
            mixScratch = new short[samples];
        }

        var mix = mixScratch.AsSpan(0, samples);
        if (data == IntPtr.Zero)
        {
            mix.Clear();
        }
        else
        {
            new ReadOnlySpan<short>((void*)data, samples).CopyTo(mix);
        }

        if (micRing is not null)
        {
            MixMic(mix);
        }

        // A dead extra is still drained of what it buffered before it went: its mixer
        // just runs dry into silence.
        foreach (var extra in extras)
        {
            extra.Mixer.MixInto(mix, time);
        }

        fixed (short* p = mix)
        {
            Emit((IntPtr)p, frames);
        }
    }

    private void Emit(IntPtr data, int frames)
    {
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
        watcher?.Dispose();
        if (!pump.Join(2000))
        {
            // The pump is wedged inside a call to a dead device; finalizing or
            // releasing the writer under it is an access violation waiting to happen.
            // A bounded, logged leak beats a crash.
            AppLog.Write("audio pump did not stop; abandoning this audio segment");
            return;
        }

        // Without system audio every frame is synthesized by design; nothing to report.
        if (capture is not null && synthesizedFrames > 0 && writtenFrames > SampleRate
            && synthesizedFrames * 10 >= writtenFrames * 9)
        {
            AppLog.Write($"audio was {synthesizedFrames * 100 / writtenFrames}% synthesized silence this segment");
        }

        foreach (var extra in extras)
        {
            // Trimming only happens when the main stream stalled; worth a line when it does.
            if (extra.Mixer.DroppedFrames > 0)
            {
                AppLog.Write($"extra audio device {extra.Name}: {extra.Mixer.DroppedFrames * 1000 / SampleRate} ms trimmed this segment");
            }
#if DEBUG
            AppLog.Write($"debug: extra audio device {extra.Name}: {extra.Mixer.Underruns} underruns, ratio {extra.Mixer.Ratio:F6}, "
                + $"aligned {extra.Mixer.AlignedFrames} frames, timestamps {(double.IsNaN(mainNextTime) ? "none" : "main")}");
#endif

            extra.Client.Stop();  // HRESULT only; a vanished device just reports an error
        }

        try
        {
            client?.Stop();
            micClient?.Stop();
            if (micCapture is not null)
            {
                Drain(micCapture, mic: true);
            }

            if (capture is not null)
            {
                Drain(capture, mic: false);
            }

            Mf.Check(writer.Finalize_());
        }
        catch
        {
            // A torn-down device mid-capture; the file holds whatever was written.
        }
        finally
        {
            ReleaseIfSet(micCapture);
            ReleaseIfSet(micClient);
            ReleaseExtras();
            ReleaseIfSet(capture);
            ReleaseIfSet(client);
            Marshal.ReleaseComObject(writer);
        }
    }

    // ---- activation ----

    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    private const string VirtualLoopbackDevice = @"VAD\Process_Loopback";
    private const int ActivationTypeProcessLoopback = 1;
    private const int LoopbackModeIncludeTree = 0;
    private const int LoopbackModeExcludeTree = 1;
    private const ushort VT_BLOB = 65;

    private static IAudioClient ActivateProcessLoopback(int pid, int mode)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS { type, { TargetProcessId, ProcessLoopbackMode } }
        var paramsBlob = Marshal.AllocHGlobal(12);
        try
        {
            Marshal.WriteInt32(paramsBlob, 0, ActivationTypeProcessLoopback);
            Marshal.WriteInt32(paramsBlob, 4, pid);
            Marshal.WriteInt32(paramsBlob, 8, mode);

            var propvariant = new PROPVARIANT { vt = VT_BLOB, blobSize = 12, blobData = paramsBlob };
            var handler = new ActivateHandler();
            var iid = IID_IAudioClient;
            Mf.Check(ActivateAudioInterfaceAsync(
                VirtualLoopbackDevice, ref iid, ref propvariant, handler, out var operation));

            if (!handler.Done.Wait(5000))
            {
                // The service may still be reading the params blob — leak its 12 bytes
                // deliberately rather than free memory under a live async operation.
                paramsBlob = IntPtr.Zero;
                Marshal.ReleaseComObject(operation);
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
            if (paramsBlob != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(paramsBlob);
            }
        }
    }

    private static IMMDeviceEnumerator CreateDeviceEnumerator()
    {
        var enumeratorType = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)
            ?? throw new InvalidOperationException("MMDeviceEnumerator unavailable.");
        return (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;
    }

    private static IAudioClient ActivateDefaultEndpoint(int dataFlow)
    {
        var enumerator = CreateDeviceEnumerator();
        try
        {
            Mf.Check(enumerator.GetDefaultAudioEndpoint(dataFlow, 0 /* eConsole */, out var device));
            try
            {
                return ActivateDevice(device, release: false);
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

    /// <summary>Audio client for a specific device. Takes ownership of
    /// <paramref name="device"/> unless <paramref name="release"/> is false.</summary>
    private static IAudioClient ActivateDevice(IMMDevice device, bool release = true)
    {
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
            if (release)
            {
                Marshal.ReleaseComObject(device);
            }
        }
    }

    /// <summary>
    /// The active render device hosting an audio session owned by <paramref name="pid"/>,
    /// or null when no live session exists. Virtual mixers assign apps their own
    /// endpoints; the session table is the only place that routing is visible.
    /// </summary>
    private static (IMMDevice Device, bool IsDefault)? FindSessionDevice(int pid)
    {
        var enumerator = CreateDeviceEnumerator();
        try
        {
            string? defaultId = null;
            if (enumerator.GetDefaultAudioEndpoint(0, 0, out var def) >= 0)
            {
                defaultId = DeviceId(def);
                Marshal.ReleaseComObject(def);
            }

            if (enumerator.EnumAudioEndpoints(0 /* eRender */, 1 /* DEVICE_STATE_ACTIVE */, out var devices) < 0)
            {
                return null;
            }

            try
            {
                Mf.Check(devices.GetCount(out int count));
                for (int i = 0; i < count; i++)
                {
                    if (devices.Item(i, out var device) < 0)
                    {
                        continue;
                    }

                    if (HasSessionFor(device, pid))
                    {
                        return (device, DeviceId(device) == defaultId);
                    }

                    Marshal.ReleaseComObject(device);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(devices);
            }

            return null;
        }
        catch
        {
            // Enumeration is best-effort; the caller falls back to process loopback.
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>Diagnostic: every active render device and the pids with sessions on
    /// it. This table is the ground truth per-app routing hides in.</summary>
    internal static string DumpAudioSessions()
    {
        var lines = new List<string>();
        var enumerator = CreateDeviceEnumerator();
        try
        {
            string? defaultId = null;
            if (enumerator.GetDefaultAudioEndpoint(0, 0, out var def) >= 0)
            {
                defaultId = DeviceId(def);
                Marshal.ReleaseComObject(def);
            }

            Mf.Check(enumerator.EnumAudioEndpoints(0, 1, out var devices));
            try
            {
                Mf.Check(devices.GetCount(out int count));
                for (int i = 0; i < count; i++)
                {
                    if (devices.Item(i, out var device) < 0)
                    {
                        continue;
                    }

                    try
                    {
                        var id = DeviceId(device);
                        var pids = new List<string>();
                        var mgrIid = IID_IAudioSessionManager2;
                        if (device.Activate(ref mgrIid, 23, IntPtr.Zero, out var mgrPtr) >= 0)
                        {
                            var manager = (IAudioSessionManager2)Marshal.GetObjectForIUnknown(mgrPtr);
                            Marshal.Release(mgrPtr);
                            try
                            {
                                if (manager.GetSessionEnumerator(out var sessions) >= 0)
                                {
                                    Mf.Check(sessions.GetCount(out int sessionCount));
                                    for (int s = 0; s < sessionCount; s++)
                                    {
                                        if (sessions.GetSession(s, out var session) < 0)
                                        {
                                            continue;
                                        }

                                        if (session is IAudioSessionControl2 details
                                            && details.GetProcessId(out uint owner) >= 0)
                                        {
                                            string name;
                                            try
                                            {
                                                using var p = System.Diagnostics.Process.GetProcessById((int)owner);
                                                name = p.ProcessName;
                                            }
                                            catch
                                            {
                                                name = "?";
                                            }

                                            pids.Add($"{owner}({name})");
                                        }

                                        Marshal.ReleaseComObject(session);
                                    }

                                    Marshal.ReleaseComObject(sessions);
                                }
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(manager);
                            }
                        }

                        lines.Add($"{(id == defaultId ? "DEFAULT " : "        ")}{id?[^12..]}: {string.Join(", ", pids)}");
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(devices);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>An active output device, for the tray's "Audio devices" checklist.</summary>
    internal readonly record struct RenderDevice(string Id, string Name, bool IsDefault);

    /// <summary>Every active render endpoint by friendly name, the default first. Empty
    /// when the audio service cannot be reached.</summary>
    internal static List<RenderDevice> ListRenderDevices()
    {
        var list = new List<RenderDevice>();
        try
        {
            var enumerator = CreateDeviceEnumerator();
            try
            {
                string? defaultId = null;
                if (enumerator.GetDefaultAudioEndpoint(0, 0, out var def) >= 0)
                {
                    defaultId = DeviceId(def);
                    Marshal.ReleaseComObject(def);
                }

                if (enumerator.EnumAudioEndpoints(0 /* eRender */, 1 /* DEVICE_STATE_ACTIVE */, out var devices) < 0)
                {
                    return list;
                }

                try
                {
                    Mf.Check(devices.GetCount(out int count));
                    for (int i = 0; i < count; i++)
                    {
                        if (devices.Item(i, out var device) < 0)
                        {
                            continue;
                        }

                        try
                        {
                            if (DeviceId(device) is { } id)
                            {
                                list.Add(new RenderDevice(id, FriendlyName(device) ?? id, id == defaultId));
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(device);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(devices);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("audio device list unavailable: " + ex.Message);
        }

        return list
            .OrderByDescending(d => d.IsDefault)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? DefaultRenderId()
    {
        try
        {
            var enumerator = CreateDeviceEnumerator();
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 0 /* eConsole */, out var def) < 0)
                {
                    return null;
                }

                try
                {
                    return DeviceId(def);
                }
                finally
                {
                    Marshal.ReleaseComObject(def);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
        catch
        {
            return null;
        }
    }

    private static bool IsRender(IMMDevice device) =>
        device is IMMEndpoint endpoint && endpoint.GetDataFlow(out int flow) >= 0 && flow == 0 /* eRender */;

    // PKEY_Device_FriendlyName: "Speakers (Realtek(R) Audio)", what Sound settings shows.
    private static readonly PROPERTYKEY PKEY_Device_FriendlyName =
        new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };

    private const ushort VT_LPWSTR = 31;

    private static string? FriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(0 /* STGM_READ */, out var storePtr) < 0 || storePtr == IntPtr.Zero)
        {
            return null;
        }

        var store = (IPropertyStore)Marshal.GetObjectForIUnknown(storePtr);
        Marshal.Release(storePtr);
        try
        {
            var key = PKEY_Device_FriendlyName;
            var value = default(PROPVARIANT);
            if (store.GetValue(ref key, ref value) < 0)
            {
                return null;
            }

            try
            {
                return value.vt == VT_LPWSTR && value.pointer != IntPtr.Zero
                    ? Marshal.PtrToStringUni(value.pointer)
                    : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT value);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    [ComImport]
    [Guid("1BE09788-6894-4089-8586-9A2A6C265AC5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMEndpoint
    {
        [PreserveSig] int GetDataFlow(out int dataFlow);
    }

    private static string? DeviceId(IMMDevice device)
    {
        if (device.GetId(out var idPtr) < 0 || idPtr == IntPtr.Zero)
        {
            return null;
        }

        var id = Marshal.PtrToStringUni(idPtr);
        Marshal.FreeCoTaskMem(idPtr);
        return id;
    }

    /// <summary>Active render devices hosting an audio session of any pid in
    /// <paramref name="tree"/>.</summary>
    private static List<string> SessionDevices(IMMDeviceEnumerator enumerator, HashSet<uint> tree)
    {
        var found = new List<string>();
        try
        {
            if (enumerator.EnumAudioEndpoints(0 /* eRender */, 1 /* DEVICE_STATE_ACTIVE */, out var devices) < 0)
            {
                return found;
            }

            try
            {
                Mf.Check(devices.GetCount(out int count));
                for (int i = 0; i < count; i++)
                {
                    if (devices.Item(i, out var device) < 0)
                    {
                        continue;
                    }

                    try
                    {
                        if (HasSessionFor(device, tree.Contains) && DeviceId(device) is { } id)
                        {
                            found.Add(id);
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(devices);
            }
        }
        catch
        {
            // Best effort: no sessions found means the excluded app stays out.
        }

        return found;
    }

    /// <summary><paramref name="root"/> and every process descended from it.</summary>
    private static HashSet<uint> ProcessTree(int root)
    {
        var tree = new HashSet<uint> { (uint)root };
        IntPtr snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1))
        {
            return tree;
        }

        try
        {
            var parents = new List<(uint Pid, uint Parent)>();
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (bool ok = Process32FirstW(snap, ref entry); ok; ok = Process32NextW(snap, ref entry))
            {
                parents.Add((entry.th32ProcessID, entry.th32ParentProcessID));
            }

            for (bool grew = true; grew;)
            {
                grew = false;
                foreach (var (pid, parent) in parents)
                {
                    if (pid != 0 && tree.Contains(parent) && tree.Add(pid))
                    {
                        grew = true;
                    }
                }
            }
        }
        finally
        {
            CloseHandle(snap);
        }

        return tree;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    private static bool HasSessionFor(IMMDevice device, int pid) => HasSessionFor(device, owner => owner == (uint)pid);

    private static bool HasSessionFor(IMMDevice device, Func<uint, bool> owns)
    {
        var mgrIid = IID_IAudioSessionManager2;
        if (device.Activate(ref mgrIid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var mgrPtr) < 0)
        {
            return false;
        }

        var manager = (IAudioSessionManager2)Marshal.GetObjectForIUnknown(mgrPtr);
        Marshal.Release(mgrPtr);
        try
        {
            if (manager.GetSessionEnumerator(out var sessions) < 0)
            {
                return false;
            }

            try
            {
                Mf.Check(sessions.GetCount(out int count));
                for (int i = 0; i < count; i++)
                {
                    if (sessions.GetSession(i, out var session) < 0)
                    {
                        continue;
                    }

                    try
                    {
                        if (session is IAudioSessionControl2 details
                            && details.GetProcessId(out uint owner) >= 0 && owns(owner))
                        {
                            return true;
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(session);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(sessions);
            }

            return false;
        }
        finally
        {
            Marshal.ReleaseComObject(manager);
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
        [FieldOffset(8)] public IntPtr pointer;     // VT_LPWSTR and other pointer types
        [FieldOffset(16)] public IntPtr blobData;
    }

    /// <summary>Tells the owner when the default playback device changes. The callback
    /// runs on a Windows audio thread: it only hands the ID over, never blocks.</summary>
    private sealed class DefaultWatcher : IMMNotificationClient, IDisposable
    {
        private readonly Action<string> changed;
        private IMMDeviceEnumerator? enumerator;

        private DefaultWatcher(Action<string> changed) => this.changed = changed;

        public static DefaultWatcher? TryStart(Action<string> changed)
        {
            var w = new DefaultWatcher(changed);
            try
            {
                w.enumerator = CreateDeviceEnumerator();
                if (w.enumerator.RegisterEndpointNotificationCallback(w) >= 0)
                {
                    return w;
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("default device changes not watched: " + ex.Message);
            }

            w.Release();
            return null;
        }

        public void Dispose()
        {
            if (enumerator is not null)
            {
                enumerator.UnregisterEndpointNotificationCallback(this);
                Release();
            }
        }

        private void Release()
        {
            if (enumerator is not null)
            {
                Marshal.ReleaseComObject(enumerator);
                enumerator = null;
            }
        }

        public int OnDefaultDeviceChanged(int flow, int role, string? id)
        {
            // eRender, eConsole: what Sound settings calls the default playback device.
            if (flow == 0 && role == 0 && id is not null)
            {
                changed(id);
            }

            return 0;
        }

        public int OnDeviceStateChanged(string? id, uint state) => 0;

        public int OnDeviceAdded(string? id) => 0;

        public int OnDeviceRemoved(string? id) => 0;

        public int OnPropertyValueChanged(string? id, PROPERTYKEY key) => 0;
    }

    [ComImport]
    [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string? id, uint state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string? id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string? id);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string? id, PROPERTYKEY key);
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

    private static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // IAudioSessionManager
        [PreserveSig] int _GetAudioSessionControl();
        [PreserveSig] int _GetSimpleAudioVolume();
        // IAudioSessionManager2
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        [PreserveSig] int _RegisterSessionNotification();
        [PreserveSig] int _UnregisterSessionNotification();
        [PreserveSig] int _RegisterDuckNotification();
        [PreserveSig] int _UnregisterDuckNotification();
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl — stubs hold the vtable slots, never called.
        [PreserveSig] int _GetState();
        [PreserveSig] int _GetDisplayName();
        [PreserveSig] int _SetDisplayName();
        [PreserveSig] int _GetIconPath();
        [PreserveSig] int _SetIconPath();
        [PreserveSig] int _GetGroupingParam();
        [PreserveSig] int _SetGroupingParam();
        [PreserveSig] int _RegisterAudioSessionNotification();
        [PreserveSig] int _UnregisterAudioSessionNotification();
        // IAudioSessionControl2
        [PreserveSig] int _GetSessionIdentifier();
        [PreserveSig] int _GetSessionInstanceIdentifier();
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int _IsSystemSoundsSession();
        [PreserveSig] int _SetDuckingPreference();
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
