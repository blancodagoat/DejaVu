using System.Globalization;

namespace DejaVu;

/// <summary>
/// The rolling buffer on top of <see cref="CaptureEngine"/>: capture runs continuously
/// and the engine rotates between fixed-length segment files without stopping, so a seam
/// costs at most one frame. Old segments are pruned as they age out, and "save" stitches
/// the segments covering the configured window into one MP4 without re-encoding.
///
/// ponytail: audio starts alongside the engine rather than on a shared clock, which
/// lands A/V sync within ~100 ms; frame-accurate alignment would need timestamps from
/// one clock across both pipelines.
/// </summary>
internal sealed class ReplayBuffer : IDisposable
{
    private const string SegmentPrefix = "seg_";
    private const string TimeFormat = "yyyyMMdd_HHmmssfff";

    private readonly AppConfig config;
    private readonly int segmentSeconds;
    private readonly object gate = new();
    private readonly System.Threading.Timer cycleTimer;

    private CaptureEngine? engine;
    private IntPtr engineMonitor;
    private IntPtr engineWindow;
    private string? currentVideoPath;
    private string? currentAudioPath;
    private string? currentSegmentPath;
    private AudioLoopback? currentAudio;
    private bool running;
    private IntPtr windowTarget;
    private bool sourceFallback;
    private int consecutiveFailures;
    private int giveUpStreak;
    private bool manuallyPaused;
    private bool audioWarned;
    private int zeroFrameStreak;
    private string? lastAudioDesc;


    public ReplayBuffer(AppConfig config, int segmentSeconds = 60)
    {
        this.config = config;
        this.segmentSeconds = segmentSeconds;
        cycleTimer = new System.Threading.Timer(_ => Cycle());
        try
        {
            // Leftovers from a previous session are NOT cleared here — they are a crashed
            // session's buffer, and RecoverCrashedSession turns them into a saved clip.
            Directory.CreateDirectory(AppInfo.BufferDirectory);

            // Quarantined buffers exist for diagnosis, not as a permanent archive: on
            // exactly the machines that crash most they accumulate gigabytes forever.
            var quarantine = AppInfo.BufferDirectory + "-quarantine";
            if (Directory.Exists(quarantine))
            {
                foreach (var file in Directory.EnumerateFiles(quarantine))
                {
                    if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-7))
                    {
                        TryDelete(file);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // An unwritable LocalAppData (quota, roaming profile offline) must not kill
            // startup here with no UI — every segment start will report it instead.
            AppLog.Write("buffer directory unavailable: " + ex.Message);
        }
    }

    public bool Running
    {
        get { lock (gate) { return running; } }
    }

    /// <summary>Raised from a background thread when a segment recorder dies.</summary>
    public event Action<string>? Failed;

    /// <summary>
    /// Pins capture to one window for this session (IntPtr.Zero returns to the configured
    /// display/auto target). Not persisted: window handles do not survive restarts.
    /// </summary>
    public void SetWindowTarget(IntPtr handle)
    {
        windowTarget = handle;
        Restart();
    }

    public IntPtr WindowTarget => windowTarget;

    /// <summary>
    /// Call once at startup, before Start(). Segments on disk mean the previous session
    /// ended without a clean exit — a crash or power cut — so whatever survived is
    /// stitched into a clip. The newest segment may be truncated mid-write; if the
    /// stitch fails it is retried without it. The buffer directory ends up empty either way.
    /// </summary>
    /// <summary>
    /// Recovery, but never trusted to return: Media Foundation can hang forever parsing
    /// a file truncated at exactly the wrong byte. On timeout the leftovers are moved to
    /// a quarantine folder (locked ones stay behind for the next launch) so the app
    /// starts buffering instead of sitting dead, and nothing is deleted unseen.
    /// </summary>
    public (string? Clip, bool TimedOut) RecoverWithTimeout(TimeSpan timeout)
    {
        var work = Task.Run(RecoverCrashedSession);
        if (work.Wait(timeout))
        {
            return (work.Result, false);
        }

        var quarantine = AppInfo.BufferDirectory + "-quarantine";
        try
        {
            Directory.CreateDirectory(quarantine);
            foreach (var file in Directory.EnumerateFiles(AppInfo.BufferDirectory))
            {
                try
                {
                    File.Move(file, Path.Combine(quarantine, Path.GetFileName(file)), overwrite: true);
                }
                catch
                {
                    // Held open by the hung reader; the next launch retries it.
                }
            }
        }
        catch
        {
            // Quarantine is best-effort; buffering still starts.
        }

        return (null, true);
    }

    public string? RecoverCrashedSession()
    {
        // Snapshot first: cleanup below is scoped to what existed at entry, so a
        // recovery that outlives its timeout can never delete the segments of the live
        // session that started without it.
        string[] snapshot;
        try
        {
            snapshot = Directory.GetFiles(AppInfo.BufferDirectory);
        }
        catch
        {
            return null;
        }

        // The in-flight segment of a crashed session is still split into its video and
        // audio halves. Join what can be joined so the recovered clip includes it.
        foreach (var video in snapshot.Where(f => Path.GetFileName(f).StartsWith("vid_", StringComparison.Ordinal)))
        {
            var stamp = Path.GetFileNameWithoutExtension(video)["vid_".Length..];
            JoinSegment(
                video,
                Path.Combine(AppInfo.BufferDirectory, $"aud_{stamp}.mp4"),
                Path.Combine(AppInfo.BufferDirectory, $"{SegmentPrefix}{stamp}.mp4"));
        }

        // Only segments that existed at entry or were just joined from entry-time
        // halves are ours to stitch and delete — never anything a live session wrote.
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in snapshot)
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith(SegmentPrefix, StringComparison.Ordinal))
            {
                owned.Add(file);
            }
            else if (name.StartsWith("vid_", StringComparison.Ordinal))
            {
                var stamp = Path.GetFileNameWithoutExtension(name)["vid_".Length..];
                owned.Add(Path.Combine(AppInfo.BufferDirectory, $"{SegmentPrefix}{stamp}.mp4"));
            }
        }

        var leftovers = ListSegments()
            .Where(s => owned.Contains(s.Path))
            .OrderBy(s => s.Start)
            .Select(s => s.Path)
            .ToList();
        if (leftovers.Count == 0)
        {
            return null;
        }

        string output;
        try
        {
            Directory.CreateDirectory(config.SaveRoot);
            output = UniqueClipPath(config.SaveRoot, "recovered");
        }
        catch (Exception ex)
        {
            // Unwritable save root (unplugged drive, protected folder): leave the
            // buffer for the next launch instead of destroying the only copy.
            AppLog.Write("recovery could not reach the save folder: " + ex.Message);
            return null;
        }

        try
        {
            for (int drop = 0; drop <= 1 && leftovers.Count > drop; drop++)
            {
                try
                {
                    Mp4Concat.Concat(leftovers.GetRange(0, leftovers.Count - drop), output);
                    // Concat can "succeed" over truncated inputs yet write a file no
                    // player opens. A recovered clip that does not decode is garbage —
                    // never hand it to the user.
                    if (Mf.ProbeVideo(output, maxSamples: 3).Frames > 0)
                    {
                        return output;
                    }

                    TryDelete(output);
                }
                catch
                {
                    TryDelete(output);
                }
            }

            return null;
        }
        finally
        {
            foreach (var file in snapshot)
            {
                TryDelete(file);
            }

            foreach (var segment in leftovers)
            {
                TryDelete(segment);
            }
        }
    }

    public void Start()
    {
        lock (gate)
        {
            if (running)
            {
                return;
            }

            running = true;
            manuallyPaused = false;
            consecutiveFailures = 0;
            audioWarned = false;

            var (monitor, window) = ResolveTarget();
            try
            {
                engine = new CaptureEngine(monitor, window, config.Fps, config.QualityValue);
                engineMonitor = monitor;
                engineWindow = window;
                engine.Error += OnEngineError;

                BeginSegmentPaths();
                // Inside the guard on purpose: encoder negotiation happens here, not in
                // the ctor, and an unguarded throw out of the startup Task.Run is a
                // silently dead buffer with the red dot still lit.
                engine.Start(currentVideoPath!);
            }
            catch (Exception ex)
            {
                engine?.Dispose();
                engine = null;
                running = false;
                AppLog.Write("capture could not start: " + ex.Message);
                Failed?.Invoke("Capture could not start: " + ex.Message);
                return;
            }

            AppLog.Write($"buffering started: codec {(engine.Codec == Mf.VideoFormat_AV1 ? "AV1" : "H264")}, "
                + $"target {(window != IntPtr.Zero ? "window" : "monitor")}, {config.Fps} fps, quality {config.QualityValue}");

            cycleTimer.Change(TimeSpan.FromSeconds(segmentSeconds), TimeSpan.FromSeconds(segmentSeconds));
        }

        // Off the gate AND off the caller's thread: audio activation can block for
        // seconds on a wedged audio service, Start() runs on the UI thread from the
        // pause toggle, and StartAudio's stale-path check makes the race with a quick
        // Stop harmless.
        Task.Run(StartAudio);
    }

    /// <summary>The user's pause: suppresses the automatic cool-down retry too.</summary>
    public void Pause()
    {
        lock (gate)
        {
            manuallyPaused = true;
        }

        Stop();
    }

    public void Stop()
    {
        CaptureEngine? old;
        AudioLoopback? audio;
        string? vid, aud, seg;
        int frames;
        lock (gate)
        {
            if (!running)
            {
                return;
            }

            running = false;
            cycleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            old = engine;
            engine = null;
            audio = currentAudio;
            currentAudio = null;
            (vid, aud, seg) = (currentVideoPath, currentAudioPath, currentSegmentPath);
            frames = old?.FramesInSegment ?? 0;
        }

        if (old is null)
        {
            return;
        }

        old.Stop();
        audio?.Dispose();
        if (vid is not null)
        {
            if (frames > 0)
            {
                JoinSegment(vid, aud!, seg!);
            }
            else
            {
                TryDelete(vid);
                TryDelete(aud!);
            }
        }

        old.Dispose();
        Prune();
    }

    /// <summary>Caller must hold <see cref="gate"/>.</summary>
    private void BeginSegmentPaths()
    {
        var stamp = DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture);
        currentVideoPath = Path.Combine(AppInfo.BufferDirectory, $"vid_{stamp}.mp4");
        currentAudioPath = Path.Combine(AppInfo.BufferDirectory, $"aud_{stamp}.mp4");
        currentSegmentPath = Path.Combine(AppInfo.BufferDirectory, $"{SegmentPrefix}{stamp}.mp4");
    }

    /// <summary>Call OUTSIDE <see cref="gate"/>: device activation can block for
    /// seconds, and holding the gate through it freezes the tray menu and hotkey saves.</summary>
    private void StartAudio()
    {
        if (!config.SystemAudio)
        {
            return;
        }

        string? path;
        IntPtr window;
        lock (gate)
        {
            if (!running)
            {
                return;
            }

            path = currentAudioPath;
            window = engineWindow;
        }

        // App-only mode needs a single process: the captured window's. Monitor and auto
        // targets fall back to the usual mix-minus-exclusions. Which path runs decides
        // which audio device family is in play, so log it when it changes — an audio bug
        // report is unreadable without knowing which capture the session used.
        AudioLoopback? audio;
        string desc;
        if (config.AppAudioOnly && window != IntPtr.Zero)
        {
            int pid = ResolveAudioPid(window);
            desc = $"app-only (pid {pid})";
            audio = AudioLoopback.TryStart(path!, pid, includeOnly: true);
        }
        else
        {
            int exclude = FindExcludePid();
            desc = exclude > 0 ? $"system mix excluding pid {exclude}" : "full system mix";
            audio = AudioLoopback.TryStart(path!, exclude);
        }

        bool warn;
        lock (gate)
        {
            // A Stop/Restart may have won the race while the device was activating.
            if (!running || currentAudioPath != path)
            {
                audio?.Dispose();
                return;
            }

            currentAudio = audio;
            if (desc != lastAudioDesc)
            {
                lastAudioDesc = desc;
                AppLog.Write("audio: " + desc);
            }

            warn = audio is null && !audioWarned;
            if (warn)
            {
                audioWarned = true;
            }
        }

        if (warn)
        {
            AppLog.Write("audio capture failed to start");
            Failed?.Invoke("Audio capture failed; replays will be silent.");
        }
    }

    /// <summary>The PID whose process tree renders the captured window's audio. UWP
    /// windows belong to ApplicationFrameHost — the real app owns a child window.</summary>
    private static int ResolveAudioPid(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            if (process.ProcessName == "ApplicationFrameHost")
            {
                uint host = pid;
                Native.EnumChildWindows(hwnd, child =>
                {
                    Native.GetWindowThreadProcessId(child, out uint childPid);
                    if (childPid != host && childPid != 0)
                    {
                        pid = childPid;
                        return false;
                    }

                    return true;
                });
            }
        }
        catch
        {
            // Gone or inaccessible; the window's own pid is the best remaining answer.
        }

        return (int)pid;
    }

    private void OnEngineError(string message)
    {
        AppLog.Write("engine error: " + message);
        bool giveUp;
        lock (gate)
        {
            consecutiveFailures++;
            giveUp = consecutiveFailures >= 3;
            if (!sourceFallback && !giveUp)
            {
                sourceFallback = true;
                Failed?.Invoke(message);
            }
        }

        if (!Running)
        {
            return;
        }

        if (!giveUp)
        {
            Task.Run(Restart);
            return;
        }

        // Repeated failures: tear down COMPLETELY (finalize the in-flight segment, stop
        // the audio capture, release the engine — a half-stopped buffer leaks a recorder
        // that runs forever), report once per streak, and retry on a cool-down instead
        // of dying for good. "It silently stopped recording" must never be this app.
        Task.Run(() =>
        {
            Stop();
            bool firstGiveUp;
            lock (gate)
            {
                firstGiveUp = ++giveUpStreak == 1;
                consecutiveFailures = 0;
            }

            AppLog.Write($"buffering paused after repeated failures (streak {giveUpStreak}); retrying in 60 s");
            if (firstGiveUp)
            {
                Failed?.Invoke("Recording keeps failing; retrying every minute. " + message);
            }

            Thread.Sleep(TimeSpan.FromSeconds(60));
            if (!Running && !manuallyPaused)
            {
                Start();
            }
        });
    }

    /// <summary>Applies changed settings by starting a fresh segment chain. A paused
    /// buffer stays paused; the new settings take effect on the next Start.</summary>
    public void Restart()
    {
        bool wasRunning = Running;
        Stop();
        if (wasRunning)
        {
            Start();
        }
    }

    /// <summary>
    /// Finalizes the in-flight segment, stitches everything inside the window into one MP4
    /// under the save root, and resumes buffering. Blocking — call off the UI thread.
    /// The clip is named after the app that was foreground when the hotkey fired.
    /// </summary>
    public string Save(string appName = SourceApp.Unknown)
    {
        bool wasRunning = Running;
        Stop();
        try
        {
            var now = DateTime.Now;
            var segments = SelectSegments(ListSegments(), now, TimeSpan.FromMinutes(config.BufferMinutes), segmentSeconds);
            if (segments.Count == 0)
            {
                throw new InvalidOperationException("Nothing buffered yet.");
            }

            Directory.CreateDirectory(config.SaveRoot);

            // The remux needs roughly the buffer's size again; failing up front with a
            // real message beats a bare 0x80070070 halfway through the write.
            try
            {
                long need = segments.Sum(f => new FileInfo(f).Length);
                var root = Path.GetPathRoot(Path.GetFullPath(config.SaveRoot));
                if (root is not null && new DriveInfo(root).AvailableFreeSpace < need + (64L << 20))
                {
                    throw new InvalidOperationException(
                        $"Not enough free space on {root} — the clip needs about {need >> 20} MB.");
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch
            {
                // UNC roots and exotic mounts have no DriveInfo; let the write decide.
            }

            var output = UniqueClipPath(config.SaveRoot, appName);

            // System.IO handles long paths; the Media Foundation URL APIs do not, and
            // fail with a bare HRESULT after the folder was created just fine.
            if (output.Length >= 248)
            {
                throw new InvalidOperationException(
                    "The save path is too long for Windows' media stack — choose a shorter replays folder.");
            }

            try
            {
                // Always through the remuxer, even for a single segment: the buffer files are
                // fragmented MP4, and this pass is what turns the save into a standard one.
                Mp4Concat.Concat(segments, output);
            }
            catch
            {
                // A partial MP4 is unplayable and would count toward the folder cap.
                TryDelete(output);
                throw;
            }

            CapClipFolder();
            return output;
        }
        finally
        {
            if (wasRunning)
            {
                Start();
            }
        }
    }

    /// <summary>
    /// Rolling cap on the clips folder: oldest clips (ours only, recognised by name shape)
    /// are deleted until the folder fits the configured cap. Off when the cap is 0.
    /// </summary>
    private void CapClipFolder()
    {
        if (config.ClipCapGB <= 0)
        {
            return;
        }

        var clips = Directory.EnumerateFiles(config.SaveRoot, "*.mp4")
            .Where(f => ClipNamePattern.IsMatch(Path.GetFileName(f)))
            .Select(f => new FileInfo(f))
            .Select(f => (f.FullName, f.Length, f.LastWriteTime));

        foreach (var path in SelectClipsOverCap(clips, (long)config.ClipCapGB << 30))
        {
            TryDelete(path);
        }
    }

    /// <summary>The clips exceeding the cap, oldest first, so deleting them frees the folder.</summary>
    public static List<string> SelectClipsOverCap(
        IEnumerable<(string Path, long Size, DateTime Written)> clips, long capBytes)
    {
        var newestFirst = clips.OrderByDescending(c => c.Written).ToList();
        var doomed = new List<string>();
        long kept = 0;

        for (int i = 0; i < newestFirst.Count; i++)
        {
            kept += newestFirst[i].Size;
            // The newest clip is never doomed — deleting what the user just saved to
            // enforce a disk cap would be the wrong trade at any cap.
            if (i > 0 && kept > capBytes)
            {
                doomed.Add(newestFirst[i].Path);
            }
        }

        doomed.Reverse();
        return doomed;
    }

    private static readonly System.Text.RegularExpressions.Regex ClipNamePattern =
        new(@"_\d{4}-\d{2}-\d{2}_\d{6}(_\d+)?\.mp4$");

    /// <summary>
    /// Timestamped clip path that never overwrites: DST fall-back replays the same
    /// wall-clock second, and the old name collided and clobbered the earlier clip.
    /// Invariant format — a Buddhist or Hijri calendar year would break
    /// <see cref="ClipNamePattern"/> and with it the folder cap.
    /// </summary>
    private static string UniqueClipPath(string root, string baseName)
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(root, $"{baseName}_{stamp}.mp4");
        for (int n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(root, $"{baseName}_{stamp}_{n}.mp4");
        }

        return path;
    }

    /// <summary>
    /// The segment files overlapping [now − window, now], oldest first. A segment starting
    /// at S covers up to S + segmentSeconds, so anything starting after
    /// now − window − segmentSeconds can still hold wanted frames.
    /// </summary>
    public static List<string> SelectSegments(
        IEnumerable<(string Path, DateTime Start)> segments,
        DateTime now,
        TimeSpan window,
        int segmentSeconds)
    {
        // The cutoff anchors on min(now, newest segment): after a clock jump the wall
        // clock and the segment stamps live in different time domains, and the LOOSER
        // cutoff keeps the fresh footage from both. Backward jump (DST fall-back):
        // pre-jump segments are "future" but survive the wall-clock cutoff, and the
        // post-jump ones survive it trivially. Forward jump (first NTP sync): the
        // newest-segment cutoff keeps everything real. Normal operation: the two
        // anchors are near-identical and nothing changes.
        var list = segments.ToList();
        var newest = DateTime.MinValue;
        foreach (var s in list)
        {
            if (s.Start > newest)
            {
                newest = s.Start;
            }
        }

        var anchor = newest > DateTime.MinValue && newest < now ? newest : now;

        var cutoff = anchor - window - TimeSpan.FromSeconds(segmentSeconds);
        return list
            .Where(s => s.Start > cutoff)
            .OrderBy(s => s.Start)
            .Select(s => s.Path)
            .ToList();
    }

    public static bool TryParseSegmentStart(string path, out DateTime start)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        start = default;
        return name.StartsWith(SegmentPrefix, StringComparison.Ordinal)
            && DateTime.TryParseExact(
                name[SegmentPrefix.Length..], TimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out start);
    }

    private static List<(string Path, DateTime Start)> ListSegments()
    {
        var result = new List<(string, DateTime)>();
        foreach (var file in Directory.EnumerateFiles(AppInfo.BufferDirectory, SegmentPrefix + "*.mp4"))
        {
            if (TryParseSegmentStart(file, out var start))
            {
                result.Add((file, start));
            }
        }

        return result;
    }

    private void Cycle()
    {
        bool retarget = false;
        bool rotated = false;
        lock (gate)
        {
            if (!running || engine is null)
            {
                return;
            }

            // Auto mode follows the active window across monitors; a target change means
            // a fresh engine rather than a rotation. At most one segment of lag.
            var (monitor, window) = ResolveTarget();
            if (monitor != engineMonitor || window != engineWindow)
            {
                retarget = true;
            }
            else
            {
                var (vid, aud, seg) = (currentVideoPath!, currentAudioPath!, currentSegmentPath!);
                var audio = currentAudio;
                BeginSegmentPaths();
                engine.Rotate(
                    currentVideoPath!,
                    frames => OnSegmentFinalized(vid, aud, seg, audio, frames));
                currentAudio = null;
                rotated = true;
            }
        }

        if (retarget)
        {
            Restart();
        }
        else if (rotated)
        {
            StartAudio();
        }
    }

    /// <summary>
    /// Runs on a worker thread once the rotated-out segment's writer has finalized. The
    /// audio chunk records marginally past the video seam; the mux does not mind a tail.
    /// </summary>
    private void OnSegmentFinalized(
        string videoPath, string audioPath, string segmentPath, AudioLoopback? audio, int frames)
    {
        audio?.Dispose();

        // Zero frames across a whole segment means the source silently delivered
        // nothing — a sleeping or locked screen, or a dead capture source. A static
        // desktop still yields its initial frames, so this does not misfire when idle.
        if (frames > 0)
        {
            JoinSegment(videoPath, audioPath, segmentPath);
            lock (gate)
            {
                consecutiveFailures = 0;
                giveUpStreak = 0;
                zeroFrameStreak = 0;
            }
        }
        else
        {
            TryDelete(videoPath);
            TryDelete(audioPath);
            bool rebuild;
            lock (gate)
            {
                // Keep cycling — recording resumes the moment the desktop is back — but
                // move future capture to the main display in case the source is dead.
                // Reported once, never silently.
                if (!sourceFallback)
                {
                    sourceFallback = true;
                    AppLog.Write("segment had zero frames; falling back to the main display");
                    Failed?.Invoke("Capture produced no frames; watching the main display until it recovers.");
                }

                // A frame pool killed without an error (TDR, driver update, iGPU/dGPU
                // switch, resume) delivers nothing forever, and when the target already
                // IS the fallback display the retarget check never differs — a full
                // rebuild is the only way back. Once per streak: a locked screen also
                // produces empty segments, and a rebuild loop there would balloon-spam
                // "could not start" against the secure desktop.
                rebuild = ++zeroFrameStreak == 2;
            }

            if (rebuild)
            {
                AppLog.Write("two empty segments; rebuilding the capture engine");
                Task.Run(Restart);
            }
        }

        Prune();
    }

    /// <summary>
    /// Turns a finished video (and its audio chunk, when one exists) into the final
    /// segment file. A failed mux degrades to silent video — a replay without audio
    /// beats no replay.
    /// </summary>
    private static void JoinSegment(string videoPath, string audioPath, string segmentPath)
    {
        try
        {
            if (File.Exists(audioPath))
            {
                Mp4Concat.MuxParallel(videoPath, audioPath, segmentPath);
                // The parts are only discarded once the joined file provably decodes;
                // a bad mux degrades to the intact silent video instead.
                if (Mf.ProbeVideo(segmentPath, maxSamples: 1).Frames > 0)
                {
                    TryDelete(videoPath);
                    TryDelete(audioPath);
                    return;
                }

                AppLog.Write($"muxed segment did not decode; keeping silent video ({Path.GetFileName(segmentPath)})");
                TryDelete(segmentPath);
                TryDelete(audioPath);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"segment mux failed: {ex.Message} ({Path.GetFileName(segmentPath)})");
            TryDelete(segmentPath);
            TryDelete(audioPath);
        }

        try
        {
            File.Move(videoPath, segmentPath, overwrite: true);
        }
        catch
        {
            // The video file itself is gone or locked; the segment is lost.
        }
    }

    private int FindExcludePid()
    {
        foreach (var name in config.AudioExclude)
        {
            System.Diagnostics.Process? oldest = null;
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
            {
                try
                {
                    // The earliest-started process is the root of the tree; excluding it
                    // covers the child processes that actually render voice audio.
                    if (oldest is null || process.StartTime < oldest.StartTime)
                    {
                        oldest?.Dispose();
                        oldest = process;
                    }
                    else
                    {
                        process.Dispose();
                    }
                }
                catch
                {
                    process.Dispose();
                }
            }

            if (oldest is not null)
            {
                int pid = oldest.Id;
                oldest.Dispose();
                return pid;
            }
        }

        return 0;
    }

    /// <summary>
    /// What the engine records: a window handle, or the monitor resolved from the
    /// configured target. "auto" is the display hosting the active window; a dead window
    /// target falls back to the configured display instead of erroring forever.
    /// </summary>
    private (IntPtr Monitor, IntPtr Window) ResolveTarget()
    {
        if (sourceFallback)
        {
            return (Native.PrimaryMonitor(), IntPtr.Zero);
        }

        if (windowTarget != IntPtr.Zero)
        {
            if (Native.IsWindow(windowTarget))
            {
                return (IntPtr.Zero, windowTarget);
            }

            windowTarget = IntPtr.Zero;
        }

        string? device = config.CaptureTarget == "auto"
            ? Native.ForegroundDisplayDevice()
            : config.CaptureTarget;

        var monitor = string.IsNullOrEmpty(device) ? IntPtr.Zero : Native.MonitorFromDeviceName(device);
        return (monitor != IntPtr.Zero ? monitor : Native.PrimaryMonitor(), IntPtr.Zero);
    }

    /// <summary>Deletes segments beyond the largest possible window. By count, not
    /// wall clock: a forward clock jump (first NTP sync, dual-boot RTC offset) used to
    /// age out the entire buffer in one sweep.</summary>
    private void Prune()
    {
        int keep = config.BufferMinutes * 60 / segmentSeconds + 2;
        foreach (var (path, _) in ListSegments().OrderByDescending(s => s.Start).Skip(keep))
        {
            TryDelete(path);
        }
    }

    private static void ClearBufferDirectory()
    {
        foreach (var file in Directory.EnumerateFiles(AppInfo.BufferDirectory))
        {
            TryDelete(file);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Still being written or scanned; the next prune gets it.
        }
    }

    public void Dispose()
    {
        Stop();
        cycleTimer.Dispose();
        ClearBufferDirectory();
    }
}
