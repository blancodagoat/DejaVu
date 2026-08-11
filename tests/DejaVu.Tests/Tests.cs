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

// Inspect: decode-probe arbitrary files. Usage: -- inspect <file> [<file>...]
if (args.Length > 1 && args[0] == "inspect")
{
    foreach (var file in args.Skip(1))
    {
        try
        {
            var (frames, luma) = Mf.ProbeVideo(file);
            Console.WriteLine($"{Path.GetFileName(file)}: decoded {frames} frames, mean luma {luma:F1} ({new FileInfo(file).Length / 1024} KB)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{Path.GetFileName(file)}: UNREADABLE — {ex.Message}");
        }
    }

    return 0;
}

// Engine smoke: the new WGC + Media Foundation pipeline. Captures the primary monitor,
// rotates the writer mid-capture (the gapless seam), then remuxes both segments through
// the concat path — which also proves the chosen codec survives the save pipeline.
if (args.Length > 0 && args[0] == "engine")
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(System.Drawing.Point pt, uint flags);

    var monitor = MonitorFromPoint(default, 1 /* PRIMARY */);
    string p1 = Path.Combine(Path.GetTempPath(), "dejavu_eng1.mp4");
    string p2 = Path.Combine(Path.GetTempPath(), "dejavu_eng2.mp4");
    string merged = Path.Combine(Path.GetTempPath(), "dejavu_eng_merged.mp4");

    string? engineError = null;
    int rotatedFrames = 0;
    using (var engine = new CaptureEngine(monitor, IntPtr.Zero, 60, 70))
    {
        engine.Error += e => engineError = e;
        Console.WriteLine($"engine codec: {(engine.Codec == Mf.VideoFormat_AV1 ? "AV1" : "H264")} (AV1 hw: {CaptureEngine.Av1Available})");

        engine.Start(p1);
        Console.WriteLine($"quality mode: {(engine.QualityModeActive ? "constant-quality" : "BITRATE FALLBACK")}");
        Thread.Sleep(3000);
        int liveFrames = engine.FramesInSegment;
        using var rotated = new ManualResetEventSlim(false);
        engine.Rotate(p2, frames => { rotatedFrames = frames; rotated.Set(); });
        Thread.Sleep(3000);
        engine.Stop();
        rotated.Wait(5000);

        Check("engine reported no errors", engineError is null, engineError);
        Check("frames flowed before rotate", liveFrames > 30, $"got {liveFrames}");
        Check("rotate finalized with frames", rotatedFrames > 30, $"got {rotatedFrames}");
    }

    // Size floors are deliberately low: AV1 on a static desktop is startlingly small
    // (a keyframe plus near-empty deltas), and that efficiency is the point.
    Check("segment 1 exists", new FileInfo(p1).Length > 8_000, $"{new FileInfo(p1).Length} bytes");
    Check("segment 2 exists", new FileInfo(p2).Length > 8_000, $"{new FileInfo(p2).Length} bytes");

    try
    {
        Mp4Concat.Concat([p1, p2], merged);
        Check("engine segments remux through save path", new FileInfo(merged).Length > 12_000);
        Console.WriteLine($"engine merged: {merged} ({new FileInfo(merged).Length / 1024} KB)");
    }
    catch (Exception ex)
    {
        Check("engine segments remux through save path", false, ex.Message);
    }
}

// Live audio: prove process-tree exclusion works, using ourselves as the excluded app.
// A tone plays from this process; captured system audio with our tree excluded must be
// silent while an unexcluded capture hears it. Needs an unmuted default output device.
if (args.Length > 0 && args[0] is "smoke" or "audio")
{
    // Exclusion removes OUR audio from the mix but rightly keeps everything else, so the
    // system's ambient level is measured first and all assertions are relative to it.
    double ambient = CaptureLevel(excludePid: 0);

    var toneWav = Path.Combine(Path.GetTempPath(), "dejavu_tone.wav");
    WriteToneWav(toneWav);
    using var player = new System.Media.SoundPlayer(toneWav);
    player.PlayLooping();
    try
    {
        double heard = CaptureLevel(excludePid: 0);
        double tone = heard - ambient;
        if (tone < 0.002)
        {
            Console.WriteLine(
                $"SKIP audio: tone not measurable over ambient (ambient {ambient:F4}, with tone {heard:F4}) — muted or loud system.");
        }
        else
        {
            double excluded = CaptureLevel(excludePid: Environment.ProcessId);
            Check("exclusion strips our tone from the mix", excluded < ambient + tone / 10,
                $"ambient {ambient:F5}, with tone {heard:F5}, excluded {excluded:F5}");
            Console.WriteLine($"audio levels: ambient {ambient:F4}, +tone {heard:F4}, excluded {excluded:F4}");
        }
    }
    finally
    {
        player.Stop();
    }

    static double CaptureLevel(int excludePid)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dejavu_cap_{excludePid}.mp4");
        using (var capture = AudioLoopback.TryStart(path, excludePid))
        {
            if (capture is null)
            {
                return -1;
            }

            Thread.Sleep(2500);
        }

        double level = Mf.MeasureAudioLevel(path);
        File.Delete(path);
        return level;
    }

    static void WriteToneWav(string path)
    {
        const int rate = 44100, seconds = 2;
        var samples = new short[rate * seconds];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 12000);
        }

        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + samples.Length * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples.Length * 2);
        foreach (var s in samples)
        {
            writer.Write(s);
        }
    }
}

// Live smoke: real capture, real concat.
if (args.Length > 0 && args[0] == "smoke")
{
    // A locked or sleeping screen delivers no frames, and every downstream assertion
    // would fail for reasons that have nothing to do with the code. Probe first.
    {
        var probePath = Path.Combine(Path.GetTempPath(), "dejavu_probe.mp4");
        int frames;
        using (var probe = new CaptureEngine(Native.PrimaryMonitor(), IntPtr.Zero, 30, 60))
        {
            probe.Start(probePath);
            Thread.Sleep(2500);
            frames = probe.FramesInSegment;
            probe.Stop();
        }
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
        CaptureTarget = Native.ListDisplays().First().DeviceName,
    };
    Directory.CreateDirectory(config.SaveRoot);

    string? recordError = null;

    // Phase 1: buffer for a while, then vanish without cleanup — a simulated crash.
    // Stop() finalizes segments but only Dispose() clears the buffer directory.
    var crashed = new ReplayBuffer(config, segmentSeconds: 4);
    crashed.Failed += e => recordError = e;
    crashed.RecoverWithTimeout(TimeSpan.FromSeconds(60));  // clean slate from any earlier smoke run
    crashed.Start();
    // Long enough for two full segments even with a cold first-recorder start and the
    // per-seam audio mux.
    Thread.Sleep(14_000);
    crashed.Stop();
    Check("no recorder failures", recordError is null, recordError);
    var crashLeftovers = Directory.GetFiles(AppInfo.BufferDirectory, "seg_*.mp4");
    Check("crash left segments behind", crashLeftovers.Length >= 2,
        $"segments: {crashLeftovers.Length}; all files: {string.Join(", ", Directory.GetFiles(AppInfo.BufferDirectory).Select(Path.GetFileName))}");

    // Phase 2: next launch recovers the crashed session into a clip.
    using (var buffer = new ReplayBuffer(config, segmentSeconds: 4))
    {
        var (recovered, recTimedOut) = buffer.RecoverWithTimeout(TimeSpan.FromSeconds(60));
        Check("crashed session recovered", recovered is not null && !recTimedOut && File.Exists(recovered));
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
