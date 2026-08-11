// Assertions over the parts of DejaVu that do not need a live desktop: hotkey
// round-tripping, segment-name parsing and window selection.
//
//   dotnet run --project tests/DejaVu.Tests
//
// With "smoke" as the first argument it additionally records the real screen in short
// segments for ~10 seconds, saves a replay through the full concat path, and asserts the
// output exists. Needs an interactive desktop, so it is not part of the default run.
//
// Exit code is 0 when everything passes.

using DejaVu;

int failed = 0, passed = 0;

void Check(string name, bool ok, string? detail = null)
{
    if (ok) { passed++; return; }
    failed++;
    Console.WriteLine($"FAIL  {name}{(detail is null ? "" : "  -> " + detail)}");
}

void Eq(string name, object? actual, object? expected) =>
    Check(name, Equals(actual, expected), $"expected <{expected}>, got <{actual}>");

// HotkeyBinding
Eq("default save renders", HotkeyBinding.DefaultSave.ToString(), "Alt+F10");
Check("default save parses back",
    HotkeyBinding.TryParse("Alt+F10", out var parsed) && parsed == HotkeyBinding.DefaultSave);
Check("parse is case/space tolerant",
    HotkeyBinding.TryParse(" alt + f10 ", out var sloppy) && sloppy == HotkeyBinding.DefaultSave);
Check("modifier-only rejected", !HotkeyBinding.TryParse("Ctrl+Shift", out _));

// Segment name round trip
var t = new DateTime(2026, 8, 11, 14, 30, 52, 123);
var name = $"seg_{t:yyyyMMdd_HHmmssfff}.mp4";
Check("segment name parses", ReplayBuffer.TryParseSegmentStart(name, out var start) && start == t);
Check("foreign file rejected", !ReplayBuffer.TryParseSegmentStart("other_20260811_143052123.mp4", out _));

// Window selection: 60 s segments, 5 min window, "now" at 10:00:00
var now = new DateTime(2026, 8, 11, 10, 0, 0);
DateTime Min(int m) => now.AddMinutes(-m);
var segments = new (string Path, DateTime Start)[]
{
    ("a", Min(8)),                    // aged out entirely
    ("b", Min(6)),                    // ends exactly at the −5:00 window edge: zero overlap, dropped
    ("p", now.AddSeconds(-330)),      // straddles the edge: kept
    ("c", Min(4)),
    ("d", Min(2)),
    ("e", Min(0)),                    // in-flight partial
};
var picked = ReplayBuffer.SelectSegments(segments, now, TimeSpan.FromMinutes(5), 60);
Eq("selection contents", string.Join(",", picked), "p,c,d,e");

var unordered = new[] { segments[4], segments[3], segments[5] };
Eq("selection is oldest-first",
    string.Join(",", ReplayBuffer.SelectSegments(unordered, now, TimeSpan.FromMinutes(5), 60)), "c,d,e");

Eq("empty buffer selects nothing",
    ReplayBuffer.SelectSegments([], now, TimeSpan.FromMinutes(5), 60).Count, 0);

// Clip naming
Eq("exe name sanitised", SourceApp.Sanitise("Overwatch 2.exe"), "overwatch-2");
Eq("path stripped", SourceApp.Sanitise(@"C:\Games\RocketLeague.exe"), "rocketleague");
Eq("empty name falls back", SourceApp.Sanitise("  "), SourceApp.Unknown);

// Folder cap: newest clips are kept, oldest past the cap are doomed, oldest first
var gb = 1L << 30;
var clips = new (string Path, long Size, DateTime Written)[]
{
    ("old1", gb, now.AddDays(-3)),
    ("old2", gb, now.AddDays(-2)),
    ("new1", gb, now.AddDays(-1)),
    ("new2", gb, now),
};
Eq("cap keeps newest within budget",
    string.Join(",", ReplayBuffer.SelectClipsOverCap(clips, 2 * gb)), "old1,old2");
Eq("cap off-by-nothing at exact fit",
    ReplayBuffer.SelectClipsOverCap(clips, 4 * gb).Count, 0);
Eq("oversized single clip survives alone",
    ReplayBuffer.SelectClipsOverCap([("only", 3 * gb, now)], gb).Count, 0);

// Live smoke: real capture, real concat.
if (args.Length > 0 && args[0] == "smoke")
{
    // A locked or sleeping screen delivers no frames, and every downstream assertion
    // would fail for reasons that have nothing to do with the code. Probe first.
    using (var probe = ScreenRecorderLib.Recorder.CreateRecorder(new ScreenRecorderLib.RecorderOptions
    {
        SourceOptions = new ScreenRecorderLib.SourceOptions
        {
            RecordingSources = { ScreenRecorderLib.DisplayRecordingSource.MainMonitor },
        },
    }))
    {
        var probePath = Path.Combine(Path.GetTempPath(), "dejavu_probe.mp4");
        probe.Record(probePath);
        Thread.Sleep(3000);
        int frames = probe.CurrentFrameNumber;
        probe.Stop();
        Thread.Sleep(1000);
        File.Delete(probePath);
        if (frames == 0)
        {
            Console.WriteLine("SKIP smoke: the desktop is not delivering frames (screen locked or asleep).");
            Console.WriteLine($"{passed} passed, {failed} failed");
            return failed == 0 ? 0 : 1;
        }
    }

    var config = new AppConfig
    {
        SaveRoot = Path.Combine(Path.GetTempPath(), "DejaVu.SmokeTest"),
        SystemAudio = true,
        // Pinned to the primary display: "auto" would depend on wherever the user's
        // focus happens to be while the test runs.
        CaptureTarget = ScreenRecorderLib.DisplayRecordingSource.MainMonitor.DeviceName,
    };
    Directory.CreateDirectory(config.SaveRoot);

    string? recordError = null;

    // Phase 1: buffer for a while, then vanish without cleanup — a simulated crash.
    // Stop() finalizes segments but only Dispose() clears the buffer directory.
    var crashed = new ReplayBuffer(config, segmentSeconds: 4);
    crashed.Failed += e => recordError = e;
    crashed.RecoverCrashedSession();  // clean slate from any earlier smoke run
    crashed.Start();
    Thread.Sleep(10_000);
    crashed.Stop();
    Check("no recorder failures", recordError is null, recordError);
    var crashLeftovers = Directory.GetFiles(AppInfo.BufferDirectory, "seg_*.mp4");
    Check("crash left segments behind", crashLeftovers.Length >= 2, $"got {crashLeftovers.Length}");

    // Phase 2: next launch recovers the crashed session into a clip.
    using (var buffer = new ReplayBuffer(config, segmentSeconds: 4))
    {
        var recovered = buffer.RecoverCrashedSession();
        Check("crashed session recovered", recovered is not null && File.Exists(recovered));
        Check("buffer empty after recovery",
            Directory.GetFiles(AppInfo.BufferDirectory, "seg_*.mp4").Length == 0);

        // Phase 3: normal buffering and a hotkey save through the concat path.
        buffer.Start();
        Thread.Sleep(10_000);
        var segFiles = Directory.GetFiles(AppInfo.BufferDirectory, "seg_*.mp4");
        Check("multiple segments on disk", segFiles.Length >= 2, $"got {segFiles.Length}");
        long largest = segFiles.Length == 0 ? 0 : segFiles.Max(f => new FileInfo(f).Length);

        var output = buffer.Save("smoketest");
        Check("replay file exists", File.Exists(output));
        Check("replay named for the app", Path.GetFileName(output).StartsWith("smoketest_"));
        Check("replay merges more than one segment",
            new FileInfo(output).Length > largest, $"{new FileInfo(output).Length} <= {largest}");
        Console.WriteLine($"smoke replay: {output} ({new FileInfo(output).Length / 1024} KB)");
    }
}

Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
