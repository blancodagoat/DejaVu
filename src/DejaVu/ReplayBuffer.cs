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
    private bool audioWarned;


    public ReplayBuffer(AppConfig config, int segmentSeconds = 60)
    {
        this.config = config;
        this.segmentSeconds = segmentSeconds;
        cycleTimer = new System.Threading.Timer(_ => Cycle());
        // Leftovers from a previous session are NOT cleared here — they are a crashed
        // session's buffer, and RecoverCrashedSession turns them into a saved clip.
        Directory.CreateDirectory(AppInfo.BufferDirectory);
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
        // The in-flight segment of a crashed session is still split into its video and
        // audio halves. Join what can be joined so the recovered clip includes it.
        foreach (var video in Directory.EnumerateFiles(AppInfo.BufferDirectory, "vid_*.mp4"))
        {
            var stamp = Path.GetFileNameWithoutExtension(video)["vid_".Length..];
            JoinSegment(
                video,
                Path.Combine(AppInfo.BufferDirectory, $"aud_{stamp}.mp4"),
                Path.Combine(AppInfo.BufferDirectory, $"{SegmentPrefix}{stamp}.mp4"));
        }

        var leftovers = ListSegments().OrderBy(s => s.Start).Select(s => s.Path).ToList();
        if (leftovers.Count == 0)
        {
            return null;
        }

        Directory.CreateDirectory(config.SaveRoot);
        var output = Path.Combine(config.SaveRoot, $"recovered_{DateTime.Now:yyyy-MM-dd_HHmmss}.mp4");
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
            ClearBufferDirectory();
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
            consecutiveFailures = 0;
            audioWarned = false;

            var (monitor, window) = ResolveTarget();
            try
            {
                engine = new CaptureEngine(monitor, window, config.Fps, config.QualityValue);
            }
            catch (Exception ex)
            {
                running = false;
                Failed?.Invoke("Capture could not start: " + ex.Message);
                return;
            }

            engineMonitor = monitor;
            engineWindow = window;
            engine.Error += OnEngineError;

            BeginSegmentPaths();
            engine.Start(currentVideoPath!);
            StartAudio();
            cycleTimer.Change(TimeSpan.FromSeconds(segmentSeconds), TimeSpan.FromSeconds(segmentSeconds));
        }
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

    /// <summary>Caller must hold <see cref="gate"/>.</summary>
    private void StartAudio()
    {
        if (!config.SystemAudio)
        {
            return;
        }

        currentAudio = AudioLoopback.TryStart(currentAudioPath!, FindExcludePid());
        if (currentAudio is null && !audioWarned)
        {
            audioWarned = true;
            Failed?.Invoke("Audio capture failed; replays will be silent.");
        }
    }

    private void OnEngineError(string message)
    {
        lock (gate)
        {
            EscalateFallback(message);
        }

        if (Running)
        {
            Task.Run(Restart);
        }
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
            var output = Path.Combine(config.SaveRoot, $"{appName}_{now:yyyy-MM-dd_HHmmss}.mp4");

            // Always through the remuxer, even for a single segment: the buffer files are
            // fragmented MP4, and this pass is what turns the save into a standard one.
            Mp4Concat.Concat(segments, output);

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
        new(@"_\d{4}-\d{2}-\d{2}_\d{6}\.mp4$");

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
        var cutoff = now - window - TimeSpan.FromSeconds(segmentSeconds);
        return segments
            .Where(s => s.Start > cutoff && s.Start <= now)
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
                StartAudio();
            }
        }

        if (retarget)
        {
            Restart();
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
            }
        }
        else
        {
            TryDelete(videoPath);
            TryDelete(audioPath);
            lock (gate)
            {
                // Keep cycling — recording resumes the moment the desktop is back — but
                // move future capture to the main display in case the source is dead.
                // Reported once, never silently.
                if (!sourceFallback)
                {
                    sourceFallback = true;
                    Failed?.Invoke("Capture produced no frames; watching the main display until it recovers.");
                }
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
                TryDelete(videoPath);
                TryDelete(audioPath);
                return;
            }
        }
        catch
        {
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

    /// <summary>
    /// Caller must hold <see cref="gate"/>. Hard recorder errors: first strike swaps
    /// future segments onto the main display; repeated strikes on the fallback stop the
    /// buffer — loudly, via <see cref="Failed"/>, never silently.
    /// </summary>
    private void EscalateFallback(string reason)
    {
        consecutiveFailures++;
        if (!sourceFallback)
        {
            sourceFallback = true;
            Failed?.Invoke(reason);
        }
        else if (consecutiveFailures >= 3)
        {
            running = false;
            cycleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            Failed?.Invoke("Recording keeps failing; buffering stopped. " + reason);
        }
    }

    /// <summary>Deletes segments that have aged out of the largest possible window.</summary>
    private void Prune()
    {
        var cutoff = DateTime.Now
            - TimeSpan.FromMinutes(config.BufferMinutes)
            - TimeSpan.FromSeconds(2 * segmentSeconds);

        foreach (var (path, start) in ListSegments())
        {
            if (start < cutoff)
            {
                TryDelete(path);
            }
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
