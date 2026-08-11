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

    public ReplayBuffer(AppConfig config, int segmentSeconds = 60)
    {
        this.config = config;
        this.segmentSeconds = segmentSeconds;
        cycleTimer = new System.Threading.Timer(_ => Cycle());
        Directory.CreateDirectory(AppInfo.BufferDirectory);
        ClearBufferDirectory();
    }

    public bool Running
    {
        get { lock (gate) { return running; } }
    }

    /// <summary>Raised from a background thread when a segment recorder dies.</summary>
    public event Action<string>? Failed;

    public void Start()
    {
        lock (gate)
        {
            if (running)
            {
                return;
            }

            running = true;
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

    /// <summary>Applies changed encoder settings by starting a fresh segment chain.</summary>
    public void Restart()
    {
        Stop();
        Start();
    }

    /// <summary>
    /// Finalizes the in-flight segment, stitches everything inside the window into one MP4
    /// under the save root, and resumes buffering. Blocking — call off the UI thread.
    /// </summary>
    public string Save()
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
            var output = Path.Combine(config.SaveRoot, $"Replay_{now:yyyy-MM-dd_HHmmss}.mp4");

            if (segments.Count == 1)
            {
                File.Copy(segments[0], output, overwrite: true);
            }
            else
            {
                Mp4Concat.Concat(segments, output);
            }

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
            Prune();
            Task.Run(recorder.Dispose);
            lock (gate)
            {
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
            Task.Run(recorder.Dispose);
            Failed?.Invoke(e.Error);
        };

        recorder.Record(path);
        current = recorder;
        currentDone = done;
        cycleTimer.Change(TimeSpan.FromSeconds(segmentSeconds), Timeout.InfiniteTimeSpan);
    }

    private RecorderOptions BuildOptions() => new()
    {
        SourceOptions = new SourceOptions
        {
            RecordingSources = new List<RecordingSourceBase> { DisplayRecordingSource.MainMonitor },
        },
        VideoEncoderOptions = new VideoEncoderOptions
        {
            Bitrate = config.Bitrate,
            Framerate = config.Fps,
            IsHardwareEncodingEnabled = true,
            IsLowLatencyEnabled = true,
            Encoder = new H264VideoEncoder
            {
                BitrateMode = H264BitrateControlMode.CBR,
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
