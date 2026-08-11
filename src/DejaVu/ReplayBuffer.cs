using System.Globalization;
using ScreenRecorderLib;

namespace DejaVu;

/// <summary>
/// The rolling buffer: the primary display is recorded in fixed-length hardware-encoded
/// segments in a scratch folder, old segments are pruned as they age out, and "save"
/// stitches the segments covering the configured window into one MP4 without re-encoding.
///
/// ponytail: segments run strictly one at a time — ScreenRecorderLib hard-crashes when two
/// recorders overlap — so each seam drops the finalize+startup latency (~1 s per minute).
/// The upgrade path is a single recorder writing fragmented MP4 to a ring stream
/// (VideoEncoderOptions.IsFragmentedMp4Enabled).
/// </summary>
internal sealed class ReplayBuffer : IDisposable
{
    private const string SegmentPrefix = "seg_";
    private const string TimeFormat = "yyyyMMdd_HHmmssfff";

    private readonly AppConfig config;
    private readonly int segmentSeconds;
    private readonly object gate = new();
    private readonly System.Threading.Timer cycleTimer;

    private Recorder? current;
    private ManualResetEventSlim? currentDone;
    private bool running;
    private IntPtr windowTarget;
    private bool sourceFallback;
    private int consecutiveFailures;


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
    public string? RecoverCrashedSession()
    {
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
                    return output;
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
            sourceFallback = false;
            consecutiveFailures = 0;
            StartSegment();
        }
    }

    public void Stop()
    {
        Recorder? old;
        ManualResetEventSlim? done;
        lock (gate)
        {
            if (!running)
            {
                return;
            }

            running = false;
            cycleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            old = current;
            done = currentDone;
            current = null;
            currentDone = null;
        }

        old?.Stop();
        done?.Wait(TimeSpan.FromSeconds(10));
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
        Recorder? old;
        lock (gate)
        {
            if (!running)
            {
                return;
            }

            old = current;
        }

        // The completion handler chains the next segment once this one has finalized.
        old?.Stop();
    }

    /// <summary>Caller must hold <see cref="gate"/>.</summary>
    private void StartSegment()
    {
        var path = Path.Combine(
            AppInfo.BufferDirectory,
            $"{SegmentPrefix}{DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture)}.mp4");

        var done = new ManualResetEventSlim(false);
        var recorder = Recorder.CreateRecorder(BuildOptions());

        recorder.OnRecordingComplete += (_, _) =>
        {
            done.Set();
            // Read before dispose. Zero frames across a whole segment means the source
            // silently delivered nothing (seen with some multi-adapter display setups) —
            // a static screen still yields its initial frames, so this does not misfire
            // on an idle desktop.
            bool healthy = recorder.CurrentFrameNumber > 0;
            Prune();
            Task.Run(recorder.Dispose);
            lock (gate)
            {
                // An empty segment is not fatal: a locked or sleeping screen legitimately
                // yields no frames, and the chain must keep cycling so recording resumes
                // the moment the desktop is back. It might also be a dead capture source
                // (some multi-adapter setups), so future segments switch to the main
                // display — reported once, never silently.
                if (!healthy)
                {
                    TryDelete(path);
                    if (!sourceFallback)
                    {
                        sourceFallback = true;
                        Failed?.Invoke("Capture produced no frames; watching the main display until it recovers.");
                    }
                }
                else
                {
                    consecutiveFailures = 0;
                }

                // Chain the next segment, unless Stop() got here first or a newer
                // recorder already took over.
                if (running && ReferenceEquals(current, recorder))
                {
                    StartSegment();
                }
            }
        };

        recorder.OnRecordingFailed += (_, e) =>
        {
            done.Set();
            TryDelete(path);
            Task.Run(recorder.Dispose);
            lock (gate)
            {
                // Self-heal instead of silently stopping: fall back to the main display
                // and keep the chain alive. Only repeated failure on the fallback source
                // gives up, and even that is reported.
                EscalateFallback(e.Error);
                if (running && ReferenceEquals(current, recorder))
                {
                    StartSegment();
                }
            }
        };

        recorder.Record(path);
        current = recorder;
        currentDone = done;
        cycleTimer.Change(TimeSpan.FromSeconds(segmentSeconds), Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// What the next segment records. Resolved per segment, so "auto" follows the active
    /// window across monitors with at most one segment of lag, and a dead window target
    /// falls back to the configured display instead of erroring forever.
    /// ponytail: per-segment granularity means a monitor switch shows up one seam late;
    /// live source swapping via GetDynamicOptionsBuilder is the upgrade path.
    /// </summary>
    private RecordingSourceBase ResolveSource()
    {
        if (sourceFallback)
        {
            return DisplayRecordingSource.MainMonitor;
        }

        if (windowTarget != IntPtr.Zero)
        {
            if (Native.IsWindow(windowTarget))
            {
                return new WindowRecordingSource(windowTarget);
            }

            windowTarget = IntPtr.Zero;
        }

        string? device = config.CaptureTarget == "auto"
            ? Native.ForegroundDisplayDevice()
            : config.CaptureTarget;

        try
        {
            if (!string.IsNullOrEmpty(device)
                && Recorder.GetDisplays().Any(d => d.DeviceName == device))
            {
                return new DisplayRecordingSource(device);
            }
        }
        catch
        {
            // Enumeration hiccup; the main monitor always exists.
        }

        return DisplayRecordingSource.MainMonitor;
    }

    private RecorderOptions BuildOptions() => new()
    {
        SourceOptions = new SourceOptions
        {
            RecordingSources = new List<RecordingSourceBase> { ResolveSource() },
        },
        VideoEncoderOptions = new VideoEncoderOptions
        {
            Quality = config.QualityValue,
            Framerate = config.Fps,
            IsHardwareEncodingEnabled = true,
            IsLowLatencyEnabled = true,
            // Fragmented output: a crash or power cut mid-segment still leaves every
            // finished fragment playable. Save() remuxes to a normal moov MP4.
            IsFragmentedMp4Enabled = true,
            Encoder = new H264VideoEncoder
            {
                BitrateMode = H264BitrateControlMode.Quality,
                EncoderProfile = H264Profile.Main,
            },
        },
        AudioOptions = new AudioOptions
        {
            IsAudioEnabled = config.SystemAudio,
            IsOutputDeviceEnabled = true,
            IsInputDeviceEnabled = false,
        },
        MouseOptions = new MouseOptions
        {
            IsMousePointerEnabled = true,
        },
    };

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
