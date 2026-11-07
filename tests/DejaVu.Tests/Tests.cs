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

// Same pinning the app does in Main: d3d11/dxgi resolve from System32 even when a
// shim copy sits beside the exe. The "probe" child below proves it.
Native.PinSystemDlls();

// Tests must never share the live app's segment ring: two writers prune and mux each
// other's half-written files. Child processes (probe/soakrecord) inherit this.
Environment.SetEnvironmentVariable(
    "DEJAVU_BUFFER_DIR", Path.Combine(Path.GetTempPath(), "DejaVu.TestBuffer"));

int failed = 0, passed = 0;

void Check(string name, bool ok, string? detail = null)
{
    if (ok) { passed++; return; }
    failed++;
    Console.WriteLine($"FAIL  {name}{(detail is null ? "" : "  -> " + detail)}");
}

void Eq(string name, object? actual, object? expected) =>
    Check(name, Equals(actual, expected), $"expected <{expected}>, got <{actual}>");

// Shared by the live modes. Save root away from the user's real Videos folder; capture
// pinned to the first display so results do not depend on where focus happens to be.
AppConfig SoakConfig() => new()
{
    SaveRoot = Path.Combine(Path.GetTempPath(), "DejaVu.Soak"),
    SystemAudio = true,
    CaptureTarget = Native.ListDisplays().First().DeviceName,
};

// A locked or sleeping screen delivers no frames, and every downstream assertion would
// fail for reasons that have nothing to do with the code. Live modes probe first.
bool DesktopDeliversFrames()
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
    return frames > 0;
}

// Live multi-device audio checks on real endpoints (#11): see LiveModes.
if (args.Length > 0 && LiveModes.Modes.Contains(args[0]))
{
    return LiveModes.Run(args);
}

// The tray's Audio devices submenu, read back by OCR and pixel checks (#11).
if (args.Length > 0 && args[0] == "menushot")
{
    return MenuShotMode.Run(args);
}

// Soak recorder victim: buffers until killed from outside. Spawned by "soak", below.
if (args.Length > 0 && args[0] == "soakrecord")
{
    var recConfig = SoakConfig();
    Directory.CreateDirectory(recConfig.SaveRoot);
    var rec = new ReplayBuffer(recConfig, segmentSeconds: 4);
    rec.Start();
    Console.WriteLine("      soakrecord: buffering until killed");
    Thread.Sleep(Timeout.Infinite);
}

// Decoy probe child: exit 0 when capture delivers frames; used by the shadowed-DLL test.
if (args.Length > 0 && args[0] == "probe")
{
    return DesktopDeliversFrames() ? 0 : 3;
}

// Apartment regression: start via ReplayBuffer on an STA thread (exactly what a tray
// menu click does), stop from MTA (the cycle timer). Raw CaptureEngine used this way
// dies with E_NOINTERFACE / "separated RCW" — MF objects don't aggregate the
// free-threaded marshaler — so ReplayBuffer must confine engine work to MTA.
if (args.Length > 0 && args[0] == "apartment")
{
    if (Directory.Exists(AppInfo.BufferDirectory) &&
        Directory.EnumerateFiles(AppInfo.BufferDirectory).Any())
    {
        Console.WriteLine("REFUSING apartment: the buffer directory is in use — stop DejaVu first.");
        return 2;
    }

    var cfg = SoakConfig();
    Directory.CreateDirectory(cfg.SaveRoot);
    using var rb = new ReplayBuffer(cfg, segmentSeconds: 4);
    Exception? staError = null;
    var sta = new Thread(() => { try { rb.Start(); } catch (Exception ex) { staError = ex; } });
    sta.SetApartmentState(ApartmentState.STA);
    sta.Start();
    sta.Join();     // the STA apartment is torn down here — MTA-confined RCWs must survive it
    if (staError is not null)
    {
        Console.WriteLine("apartment isolation FAILED at start: " + staError.Message);
        return 3;
    }

    Thread.Sleep(6000); // spans one 4 s rotation: Rotate + Finalize must also survive
    try
    {
        rb.Stop();
        Console.WriteLine("apartment isolation: OK");
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine("apartment isolation FAILED at stop: " + ex.Message);
        return 3;
    }
}

// Field diagnosis: capture a few seconds via each audio path and report the levels.
// "audiopid <pid>" — include-tree of pid, exclude-tree of pid, and plain endpoint.
if (args.Length > 1 && args[0] == "audiopid" && int.TryParse(args[1], out int targetPid))
{
    double Level(int pid, bool include)
    {
        var capPath = Path.Combine(Path.GetTempPath(), "dejavu_audiopid.mp4");
        using (var cap = AudioLoopback.TryStart(capPath, pid, include))
        {
            if (cap is null)
            {
                return -1;
            }

            Console.WriteLine($"      route ({(include ? "include" : "exclude")} {pid}): {cap.Route}");
            Thread.Sleep(4000);
        }

        double level = Mf.MeasureAudioLevel(capPath);
        File.Delete(capPath);
        return level;
    }

    Console.WriteLine(AudioLoopback.DumpAudioSessions());
    Console.WriteLine($"include pid {targetPid}: {Level(targetPid, true):F4}");
    Console.WriteLine($"exclude pid {targetPid}: {Level(targetPid, false):F4}");
    Console.WriteLine($"endpoint (default device): {Level(0, false):F4}");
    return 0;
}

// Live multi-device audio (#11): a -40 dBFS tone on a second output device must reach
// the clip only when that device is ticked, and a minute of mixing must stay locked.
// "multiaudio [device id or name part]"; defaults to the first non-default device.
if (args.Length > 0 && args[0] == "multiaudio")
{
    var devices = AudioLoopback.ListRenderDevices();
    foreach (var d in devices)
    {
        Console.WriteLine($"      {(d.IsDefault ? "DEFAULT " : "        ")}{d.Name}  {d.Id}");
    }

    Check("output devices listed", devices.Count > 0);
    Check("one default, listed first", devices.Count(d => d.IsDefault) == 1 && devices[0].IsDefault);

    var pick = args.Length > 1
        ? devices.FirstOrDefault(d => !d.IsDefault && (d.Id == args[1] || d.Name.Contains(args[1], StringComparison.OrdinalIgnoreCase)))
        : devices.FirstOrDefault(d => !d.IsDefault);
    if (pick.Id is null)
    {
        Console.WriteLine("SKIP multiaudio: no second output device on this machine.");
    }
    else
    {
        var main = devices[0];
        Console.WriteLine($"      extra device under test: {pick.Name}");

        (double Level, string Route) Capture(string[] extras, int ms)
        {
            var path = Path.Combine(Path.GetTempPath(), "dejavu_multi.mp4");
            string route;
            using (var cap = AudioLoopback.TryStart(path, 0, extraDevices: extras))
            {
                if (cap is null)
                {
                    return (-1, "none");
                }

                route = cap.Route;
                Thread.Sleep(ms);
            }

            double level = Mf.MeasureAudioLevel(path);
            File.Delete(path);
            return (level, route);
        }

        var unknown = Capture(["{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}"], 1000);
        Check("an unknown extra device is skipped, not an error", unknown.Level >= 0, unknown.Route);
        Eq("an unknown extra device leaves the route alone", unknown.Route, "full system mix (default device)");
        var dup = Capture([main.Id], 1000);
        Eq("the default device ticked as an extra is not mixed twice", dup.Route, "full system mix (default device)");

        var quietMain = Capture([], 3000);
        var quietBoth = Capture([pick.Id], 3000);
        Check("route names the extra device", quietBoth.Route.EndsWith(" + " + pick.Name), quietBoth.Route);

        using (new ToneRender(pick.Id))
        {
            Thread.Sleep(300);
            var toneMain = Capture([], 3000);
            var toneBoth = Capture([pick.Id], 3000);
            Console.WriteLine(
                $"      levels: default only {quietMain.Level:F4} -> {toneMain.Level:F4} with tone; "
                + $"with extra {quietBoth.Level:F4} -> {toneBoth.Level:F4} with tone");

            // A -40 dBFS sine averages 0.0064 in this measure.
            Check("the extra device's tone reaches the mix when ticked",
                toneBoth.Level - quietBoth.Level > 0.003, $"{quietBoth.Level:F4} -> {toneBoth.Level:F4}");
            Check("the extra device's tone stays out when not ticked",
                toneMain.Level - quietMain.Level < 0.0015, $"{quietMain.Level:F4} -> {toneMain.Level:F4}");

            // A minute of mixing against the main stream's clock.
            var path = Path.Combine(Path.GetTempPath(), "dejavu_multi_long.mp4");
            using (var cap = AudioLoopback.TryStart(path, 0, extraDevices: [pick.Id]))
            {
                Check("long capture started", cap is not null);
                if (cap is not null)
                {
                    int minBacklog = int.MaxValue, maxBacklog = 0;
                    long settledUnderruns = 0;
                    for (int s = 0; s < 60; s++)
                    {
                        Thread.Sleep(1000);
                        var st = cap.ExtraStats.Single();
                        if (s == 5)
                        {
                            settledUnderruns = st.Underruns;
                        }

                        if (s >= 5)
                        {
                            minBacklog = Math.Min(minBacklog, st.Backlog);
                            maxBacklog = Math.Max(maxBacklog, st.Backlog);
                        }

                        if (s % 15 == 14)
                        {
                            Console.WriteLine(
                                $"      t={s + 1}s ratio {st.Ratio:F6} backlog {st.Backlog} frames, dropped {st.Dropped}, underruns {st.Underruns}, dead {st.Dead}");
                        }
                    }

                    var end = cap.ExtraStats.Single();
                    Check("a minute of mixing never trimmed", end.Dropped == 0, $"dropped {end.Dropped}");
                    Check("the extra device stayed alive", !end.Dead);
                    Check("a steady tone never ran the extra dry once settled", end.Underruns == settledUnderruns,
                        $"underruns {settledUnderruns} -> {end.Underruns}");
                    Check("backlog held near the cushion", maxBacklog < 48000 / 10,
                        $"backlog {minBacklog}..{maxBacklog} frames");
                    Console.WriteLine($"      backlog over the minute: {minBacklog}..{maxBacklog} frames, final ratio {end.Ratio:F6}");
                }
            }

            double longLevel = Mf.MeasureAudioLevel(path);
            Check("the minute-long clip carries the tone", longLevel - quietBoth.Level > 0.003, $"{longLevel:F4}");
            File.Delete(path);
        }
    }

    Console.WriteLine($"{passed} passed, {failed} failed");
    return failed == 0 ? 0 : 1;
}

// Field diagnosis: can Media Foundation open and decode this file on this machine?
if (args.Length > 1 && args[0] == "probefile")
{
    try
    {
        int open = Mf.MFCreateSourceReaderFromURL(args[1], IntPtr.Zero, out var reader);
        Console.WriteLine($"source reader open: 0x{open:X8}");
        if (open >= 0)
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(reader);
            var probe = Mf.ProbeVideo(args[1], maxSamples: 3);
            Console.WriteLine($"decode probe: {probe.Frames} frames");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("probe failed: " + ex.Message);
    }

    return 0;
}

// Crash-kill soak: N cycles of record in a child process -> hard-kill mid-write at a
// random moment -> recover -> decode-probe the recovered clip. This is the automated
// version of "killing the power mid-write still leaves playable files" (minus the OS
// write cache, which software cannot drop). Usage: -- soak [cycles], default 12;
// "soak 500" is an overnight run.
if (args.Length > 0 && args[0] == "soak")
{
    int cycles = args.Length > 1 && int.TryParse(args[1], out var n) ? n : 12;

    // The buffer directory is shared with a real DejaVu install. Anything already in it
    // is a real crashed session's buffer; soaking here would eat the user's clip.
    if (Directory.Exists(AppInfo.BufferDirectory) &&
        Directory.EnumerateFiles(AppInfo.BufferDirectory).Any())
    {
        Console.WriteLine("REFUSING soak: the buffer directory is not empty — launch DejaVu once to recover it first.");
        return 2;
    }

    if (!DesktopDeliversFrames())
    {
        Console.WriteLine("SKIP soak: the desktop is not delivering frames (screen locked or asleep).");
        Console.WriteLine($"{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    var soakConfig = SoakConfig();
    Directory.CreateDirectory(soakConfig.SaveRoot);
    int seed = Environment.TickCount;
    var rand = new Random(seed);
    Console.WriteLine($"soak: {cycles} cycles, seed {seed}");

    for (int i = 0; i < cycles; i++)
    {
        // First cycle kills a near-empty buffer; one late cycle runs long enough for
        // many segment rotations; the rest land anywhere, including mid-rotation.
        int ms = i == 0 ? 3_000
            : cycles >= 6 && i == cycles - 1 ? 120_000
            : rand.Next(6_000, 40_000);

        var child = System.Diagnostics.Process.Start(Environment.ProcessPath!, "soakrecord");
        try
        {
            Thread.Sleep(ms);
        }
        finally
        {
            child.Kill();
            child.WaitForExit();
        }

        int leftovers = Directory.GetFiles(AppInfo.BufferDirectory, "*.mp4").Length;
        Check($"cycle {i + 1} left files to recover", leftovers > 0, "the child never wrote anything");

        string? clip;
        bool timedOut;
        using (var recovery = new ReplayBuffer(soakConfig, segmentSeconds: 4))
        {
            (clip, timedOut) = recovery.RecoverWithTimeout(TimeSpan.FromSeconds(90));
            Check($"cycle {i + 1} recovered a clip (killed at {ms / 1000.0:F0}s, {leftovers} files)",
                clip is not null && !timedOut && File.Exists(clip),
                timedOut ? "recovery timed out" : "no clip produced");
            Check($"cycle {i + 1} buffer clean after recovery",
                Directory.GetFiles(AppInfo.BufferDirectory, "seg_*.mp4").Length == 0);
        }

        if (clip is not null && File.Exists(clip))
        {
            try
            {
                // Full decode, head to tail: the truncated in-flight segment is stitched
                // LAST, so a broken tail is the likeliest failure and a 10-sample head
                // probe would never see it. The assertion is on DURATION, not frame
                // count — WGC delivers frames only when pixels change, so an idle
                // desktop legitimately yields ~2 fps. The floor allows ~2 s of encoder
                // cold start plus the unflushed tail of the in-flight fragment.
                var (frames, luma, duration) = Mf.ProbeVideo(clip, maxSamples: 500_000);
                double floorSec = Math.Max(0.5, ms / 1000.0 - 6);
                Check($"cycle {i + 1} clip covers the window to the tail", duration.TotalSeconds >= floorSec,
                    $"{duration.TotalSeconds:F1}s decoded of a {ms / 1000.0:F1}s buffer, floor {floorSec:F1}s");
                Console.WriteLine(
                    $"      cycle {i + 1}: killed at {ms / 1000.0:F1}s, {leftovers} files -> " +
                    $"{new FileInfo(clip).Length / 1024} KB, {duration.TotalSeconds:F1}s, {frames} frames, luma {luma:F1}");
            }
            catch (Exception ex)
            {
                Check($"cycle {i + 1} clip covers the window to the tail", false, ex.Message);
            }

            File.Delete(clip);
        }
    }
}

// HotkeyBinding
Eq("default save renders", HotkeyBinding.DefaultSave.ToString(), "Alt+F10");
Check("default save parses back",
    HotkeyBinding.TryParse("Alt+F10", out var parsed) && parsed == HotkeyBinding.DefaultSave);
Check("parse is case/space tolerant",
    HotkeyBinding.TryParse(" alt + f10 ", out var sloppy) && sloppy == HotkeyBinding.DefaultSave);
Check("modifier-only rejected", !HotkeyBinding.TryParse("Ctrl+Shift", out _));

// Engine work is bounded: a Media Foundation call that never returns must not take the
// save, the pause toggle or Exit down with it (#4, #5).
var wedged = new SemaphoreSlim(0);
var boundedClock = System.Diagnostics.Stopwatch.StartNew();
ReplayBuffer.OnMta("wedged", () => wedged.Wait(), TimeSpan.FromMilliseconds(300));
Check("wedged engine work is abandoned, not waited on",
    boundedClock.Elapsed < TimeSpan.FromSeconds(3), $"took {boundedClock.ElapsedMilliseconds} ms");
wedged.Release();

boundedClock.Restart();
ReplayBuffer.OnMta("throwing", () => throw new InvalidOperationException("teardown blew up"),
    TimeSpan.FromSeconds(5));
Check("a throwing teardown is logged, not rethrown into the save",
    boundedClock.Elapsed < TimeSpan.FromSeconds(3), $"took {boundedClock.ElapsedMilliseconds} ms");

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

// Clock jumps: after DST fall-back the freshest segments are "future"; after a
// forward NTP jump they all look ancient. Neither may cost the user their footage.
var fellBack = new (string Path, DateTime Start)[]
{
    ("stale", now.AddMinutes(-70)),   // genuinely old
    ("f1", now.AddMinutes(58)),       // written just before the clock fell back
    ("f2", now.AddMinutes(59)),
    ("g", now),                       // first post-jump segment
};
Eq("backward clock jump keeps both time domains",
    string.Join(",", ReplayBuffer.SelectSegments(fellBack, now, TimeSpan.FromMinutes(5), 60)), "g,f1,f2");

var jumpedForward = new (string Path, DateTime Start)[]
{
    ("h1", now.AddMinutes(-125)),
    ("h2", now.AddMinutes(-122)),     // clock then jumped two hours ahead
};
Eq("forward clock jump keeps the real footage",
    string.Join(",", ReplayBuffer.SelectSegments(jumpedForward, now, TimeSpan.FromMinutes(5), 60)), "h1,h2");

// Encoder frame-size fitting: even alignment (drag-resized windows), per-codec
// ceilings (ultrawides exceed H.264's 4096), pass-through for normal sizes.
Eq("odd window size is even-aligned", CaptureEngine.FitEncoder(1281, 999, Mf.VideoFormat_H264), (1280, 998));
Eq("ultrawide clamps to the H264 ceiling", CaptureEngine.FitEncoder(5120, 1440, Mf.VideoFormat_H264), (4096, 1152));
Eq("normal size passes through", CaptureEngine.FitEncoder(2560, 1440, Mf.VideoFormat_H264), (2560, 1440));
Eq("AV1 keeps 5K", CaptureEngine.FitEncoder(5120, 1440, Mf.VideoFormat_AV1), (5120, 1440));

// AV1 gate (#9, #10): the sink writer accepts the first few AV1 samples and only then
// fails with MF_E_SINK_HEADERS_NOT_FOUND, so "failed before its first sample" never
// fired and every rebuilt engine picked AV1 again, forever.
{
    const int headers = unchecked((int)0xC00D4A45);
    const int deviceRemoved = unchecked((int)0x887A0005);

    var gate = new Av1Gate();
    Check("AV1 allowed with hardware and no verdict", gate.Allows(hardwareAvailable: true));
    Check("AV1 never allowed without hardware", !gate.Allows(hardwareAvailable: false));

    Check("headers error a few frames in rejects AV1", gate.OnFailure(headers));
    Check("rejected AV1 is no longer allowed", !gate.Allows(hardwareAvailable: true));
    Check("a second failure does not report again", !gate.OnFailure(headers));
    Check("nor does any later failure", !gate.OnFailure(deviceRemoved));

    var early = new Av1Gate();
    Check("any failure before the first good AV1 segment rejects AV1", early.OnFailure(deviceRemoved));

    var proven = new Av1Gate();
    Check("a fresh session has no verified AV1 segment", !proven.Verified);
    proven.OnSegmentVerified();
    Check("a read-back AV1 segment marks the session verified", proven.Verified);
    Check("device loss after a good AV1 segment keeps AV1", !proven.OnFailure(deviceRemoved));
    Check("AV1 still allowed after a mid-stream device loss", proven.Allows(hardwareAvailable: true));
    Check("headers error rejects AV1 even after a good segment", proven.OnFailure(headers));
    Check("headers error after a good segment disallows AV1", !proven.Allows(hardwareAvailable: true));

    var rejectedFirst = new Av1Gate();
    Check("explicit reject reports once", rejectedFirst.Reject());
    Check("explicit reject is idempotent", !rejectedFirst.Reject());
    rejectedFirst.OnSegmentVerified();
    Check("a verified segment cannot revive rejected AV1", !rejectedFirst.Allows(hardwareAvailable: true));

    var selfTest = new Av1Gate();
    Check("first start claims the self-test", selfTest.TryClaimSelfTest());
    Check("the self-test runs once per session", !selfTest.TryClaimSelfTest());
    var noTestNeeded = new Av1Gate();
    noTestNeeded.Reject();
    Check("no self-test once AV1 is rejected", !noTestNeeded.TryClaimSelfTest());
}

// An undecodable video (the AV1 header failure leaves one behind) must never enter the
// segment ring: Prune keeps segments by count, so a junk segment per restart pushed
// the good footage out. Headless: Media Foundation parses without a desktop or GPU.
{
    var dir = Path.Combine(Path.GetTempPath(), "DejaVu.JoinTest");
    Directory.CreateDirectory(dir);
    string vid = Path.Combine(dir, "vid_x.mp4"), aud = Path.Combine(dir, "aud_x.mp4"), seg = Path.Combine(dir, "seg_x.mp4");
    var junk = new byte[4096];
    new Random(7).NextBytes(junk);

    File.WriteAllBytes(vid, junk);
    File.WriteAllBytes(aud, junk);
    ReplayBuffer.JoinSegment(vid, aud, seg);
    Check("junk video with audio does not become a segment", !File.Exists(seg));
    Check("junk video with audio is removed", !File.Exists(vid) && !File.Exists(aud));

    File.WriteAllBytes(vid, junk);
    ReplayBuffer.JoinSegment(vid, aud, seg);
    Check("junk silent video does not become a segment", !File.Exists(seg));
    Check("junk silent video is removed", !File.Exists(vid));
    Directory.Delete(dir, recursive: true);
}

// Username scrubbing: word-bounded, and short/generic names must not shred the log.
Eq("username scrubbed at word boundaries",
    IssueReport.Scrub("saved by Jax_ok for Jax", @"C:\Users\Jax", "Jax"), "saved by Jax_ok for <user>");
Eq("two-letter username left alone",
    IssueReport.Scrub("PC restarted", @"C:\Users\PC", "PC"), "PC restarted");

// Download-copy suffix stripping ("DejaVu (2).exe" from browser re-downloads).
Eq("copy suffix stripped", SelfTidy.StripCopySuffix("DejaVu (3)"), "DejaVu");
Eq("clean name untouched", SelfTidy.StripCopySuffix("DejaVu"), "DejaVu");
Eq("inner parentheses survive", SelfTidy.StripCopySuffix("My (old) App (2)"), "My (old) App");
Eq("parens without space are not a copy suffix", SelfTidy.StripCopySuffix("App(1)"), "App(1)");

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

// Extra audio devices (#11): mixed in on their own clocks, drift-corrected.
DriftTests.Run(Check);

// The detectors the live multi-device checks judge real clips with, proven on synthetic
// faults first.
SignalAnalysisTests.Run((name, ok, detail) => Check(name, ok, detail));

// Which extra devices a segment records, and how. Process loopback minus Discord hears
// EVERY output device (measured: a marker on Sonar Chat, a headset or a monitor's
// speakers all reach it at full level), so mixing a ticked device on top heard all of
// it twice, 6-16 ms apart. On that route only the excluded app's own audio is missing,
// and only when it plays on ticked devices alone may it be added back.
{
    const string Def = "{default}", Chat = "{chat}", Head = "{headset}", Main = "{main}";
    var plain = AudioLoopback.PlanExtras([Chat, Head, Def, Chat.ToUpperInvariant()], Def, Def, excludeRoute: false, excludedAppDevices: null);
    Eq("endpoint route: ticked devices are recorded, default and duplicates left out",
        string.Join(",", plain.Endpoints), "{chat},{headset}");
    Check("endpoint route: no excluded app stream", !plain.ExcludedApp);
    var routed = AudioLoopback.PlanExtras([Chat, Main], Def, Main, excludeRoute: false, excludedAppDevices: null);
    Eq("app routed to its own device: that device is not mixed twice", string.Join(",", routed.Endpoints), "{chat}");

    var discordOnChat = AudioLoopback.PlanExtras([Chat], Def, Def, excludeRoute: true, excludedAppDevices: [Chat]);
    Check("exclude route: no ticked device is captured whole", discordOnChat.Endpoints.Count == 0);
    Eq("exclude route: ticked devices are already heard", string.Join(",", discordOnChat.HeardByMain), "{chat}");
    Check("exclude route: Discord on the ticked chat device is added back", discordOnChat.ExcludedApp);
    Eq("exclude route: the added stream stands for the chat device", string.Join(",", discordOnChat.ExcludedAppCovers), "{chat}");

    var discordOnDefault = AudioLoopback.PlanExtras([Chat], Def, Def, excludeRoute: true, excludedAppDevices: [Chat, Def]);
    Check("exclude route: Discord also on the default device stays out", !discordOnDefault.ExcludedApp && discordOnDefault.Endpoints.Count == 0);
    var discordElsewhere = AudioLoopback.PlanExtras([Chat], Def, Def, excludeRoute: true, excludedAppDevices: [Head]);
    Check("exclude route: Discord on an unticked device stays out", !discordElsewhere.ExcludedApp);
    var discordSilent = AudioLoopback.PlanExtras([Chat], Def, Def, excludeRoute: true, excludedAppDevices: []);
    Check("exclude route: Discord with no audio session stays out", !discordSilent.ExcludedApp);
    var nothingTicked = AudioLoopback.PlanExtras([], Def, Def, excludeRoute: true, excludedAppDevices: [Chat]);
    Check("exclude route: nothing ticked adds nothing", !nothingTicked.ExcludedApp && nothingTicked.Endpoints.Count == 0);
    var tickedDefault = AudioLoopback.PlanExtras([Def], Def, Def, excludeRoute: true, excludedAppDevices: [Def]);
    Check("exclude route: ticking the default device does not bring Discord back", !tickedDefault.ExcludedApp);

    // The default device changes mid-segment to one an extra stream stands for.
    Check("default moves to a ticked device the main stream hears: muted at once",
        AudioLoopback.MuteOnDefaultChange([Chat], Chat, mainHearsEveryDevice: true));
    Check("default moves to a ticked device, endpoint route: kept (the main stream stays on the old device)",
        !AudioLoopback.MuteOnDefaultChange([Chat], Chat, mainHearsEveryDevice: false));
    Check("default moves to some other device: kept",
        !AudioLoopback.MuteOnDefaultChange([Chat], Head, mainHearsEveryDevice: true));
    Check("device IDs compare case-insensitively",
        AudioLoopback.MuteOnDefaultChange([Chat], Chat.ToUpperInvariant(), mainHearsEveryDevice: true));
}

// Extra audio devices in config.json: a new, optional key. A 1.4 config without it
// loads with every setting intact and is NOT rewritten just to add it.
{
    const string v14 = """
    {
      "bufferMinutes": 15, "quality": "Medium", "fps": 120, "saveHotkey": "Ctrl+F9",
      "saveRoot": "D:\\Clips", "showIndicator": false, "indicatorStyle": "icon",
      "systemAudio": true, "microphone": true, "clipCapGB": 25, "captureTarget": "auto",
      "captureBackend": "duplication", "audioExclude": ["Discord"], "appAudioOnly": false,
      "saveSound": false, "updateNotify": true
    }
    """;
    var old = AppConfig.Parse(v14, out bool oldRewrite);
    Check("1.4 config parses", old is not null);
    Check("1.4 config is not rewritten for the new key", !oldRewrite);
    Eq("1.4 config has no extra devices", old?.ExtraAudioDevices.Length, 0);
    Check("1.4 config keeps its settings",
        old is { BufferMinutes: 15, Quality: Quality.Medium, Fps: 120, Microphone: true, ClipCapGB: 25,
            CaptureBackend: "duplication", AppAudioOnly: false, SaveSound: false, UpdateNotify: true, ShowIndicator: false }
        && old.SaveRoot == @"D:\Clips" && old.AudioExclude.SequenceEqual(["Discord"]));

    var withExtras = AppConfig.Parse(v14.Replace("\"updateNotify\": true",
        "\"updateNotify\": true, \"extraAudioDevices\": "
        + "[\"{0.0.0.00000000}.{chat}\", \"\", null, \"{0.0.0.00000000}.{CHAT}\", \"{0.0.0.00000000}.{aux}\"]"),
        out bool extrasRewrite);
    Check("extra devices parse without a rewrite", withExtras is not null && !extrasRewrite);
    Eq("extra devices: blanks dropped, duplicates folded case-insensitively",
        string.Join("|", withExtras?.ExtraAudioDevices ?? []), "{0.0.0.00000000}.{chat}|{0.0.0.00000000}.{aux}");

    Check("a malformed config is not parsed", AppConfig.Parse("{ not json", out _) is null);

    // Round trip: what Save writes, Parse reads back.
    var roundTrip = AppConfig.Parse(new AppConfig { ExtraAudioDevices = ["{x}", "{y}"] }.ToJson(), out bool rtRewrite);
    Eq("extra devices round-trip", string.Join("|", roundTrip?.ExtraAudioDevices ?? []), "{x}|{y}");
    Check("a saved config needs no rewrite", !rtRewrite);
}

// Update check: pure parsing over canned GitHub releases JSON. The rolling "latest"
// tag and prereleases must be ignored; only a newer stable vX.Y.Z counts.
{
    const string releases = """
    [
      {"tag_name":"latest","prerelease":false,"html_url":"https://example/rolling"},
      {"tag_name":"v2.0.0","prerelease":true,"html_url":"https://example/pre"},
      {"tag_name":"v1.2.0","prerelease":false,"html_url":"https://example/v120"},
      {"tag_name":"v1.0.0","prerelease":false,"html_url":"https://example/v100"}
    ]
    """;
    var newer = UpdateCheck.ParseNewest(releases, new Version(1, 0, 0));
    Check("update check finds newest stable", newer is { } n && n.Version == new Version(1, 2, 0) && n.Url == "https://example/v120",
        newer?.Version.ToString());
    Check("update check ignores rolling and prerelease",
        UpdateCheck.ParseNewest(releases, new Version(1, 2, 0)) is null);
    Check("four-part local version compares as equal",
        UpdateCheck.ParseNewest("""[{"tag_name":"v1.0.0","prerelease":false,"html_url":"x"}]""", new Version(1, 0, 0, 0)) is null);
    // Forgejo's releases API (the project's home since 1.5.1) uses GitHub's field names.
    const string forgejo = """[{"id":96,"tag_name":"v1.5.0","name":"v1.5.0","draft":false,"prerelease":false,"html_url":"https://git.blancodagoat.dev/blancodagoat/DejaVu/releases/tag/v1.5.0"}]""";
    Check("update check skips a draft release",
        UpdateCheck.ParseNewest("""[{"tag_name":"v9.0.0","draft":true,"prerelease":false,"html_url":"x"}]""", new Version(1, 0, 0)) is null);
    Check("update check reads a Forgejo release",
        UpdateCheck.ParseNewest(forgejo, new Version(1, 4, 0)) is { } f && f.Version == new Version(1, 5, 0)
        && f.Url.StartsWith(AppInfo.ProjectUrl + "/releases/"));
}

// Issue reporting: the clipboard report must scrub identity and fit one Discord message
// (2000 characters) no matter how large the log tail is.
{
    var report = IssueReport.BuildReport(
        "Recording failed", "v1.0 · Windows 10.0.26200 · AV1 hw: no",
        @"2026-08-12 saved C:\Users\casey\Videos\DejaVu\clip.mp4", @"C:\Users\casey", "casey");
    Check("report names what happened", report.StartsWith("**What happened:** Recording failed"));
    Check("report carries the environment", report.Contains("AV1 hw: no"));
    Check("report carries the log", report.Contains("clip.mp4"));
    Check("report leaks no username", !report.Contains("casey", StringComparison.OrdinalIgnoreCase));
    Check("support invite is the DejaVu Discord", AppInfo.SupportInvite == "https://discord.gg/NHke2BwRA6");

    Eq("scrub folds profile path first",
        IssueReport.Scrub(@"C:\Users\casey\x and casey again", @"C:\Users\casey", "casey"),
        "~\\x and <user> again");

    var huge = IssueReport.BuildReport("t", "e", new string('x', 100_000), @"C:\Users\casey", "casey");
    Check("huge log tail still fits one Discord message", huge.Length <= IssueReport.MaxReportChars, $"{huge.Length} chars");
    Check("a cut report still closes its code block", huge.EndsWith("```"));
}

// AV1 self-test at the primary display's size, timed. Needs a GPU with hardware AV1;
// prints SKIP without one. Usage: -- av1selftest
if (args.Length > 0 && args[0] == "av1selftest")
{
    if (!CaptureEngine.Av1Available)
    {
        Console.WriteLine("SKIP av1selftest: no hardware AV1 encoder and decoder on this PC.");
        return 0;
    }

    int w, h;
    using (var sizing = new CaptureEngine(Native.PrimaryMonitor(), IntPtr.Zero, 60, 70))
    {
        (w, h) = (sizing.Width, sizing.Height);
    }

    var clock = System.Diagnostics.Stopwatch.StartNew();
    bool ok = CaptureEngine.SelfTestAv1(w, h, 60, 70, TimeSpan.FromSeconds(5));
    Console.WriteLine($"av1selftest: {(ok ? "passed" : "FAILED")} in {clock.ElapsedMilliseconds} ms at {w}x{h}");
    Check("AV1 self-test passes on hardware that encodes AV1", ok);
    Check("a passed self-test leaves AV1 allowed", CaptureEngine.Av1.Allows(CaptureEngine.Av1Available));
    Check("the self-test leaves no temp file behind",
        !File.Exists(Path.Combine(Path.GetTempPath(), $"dejavu_av1test_{Environment.ProcessId}.mp4")));
    Console.WriteLine($"{passed} passed, {failed} failed");
    return failed == 0 ? 0 : 1;
}

// Inspect: decode-probe arbitrary files. Usage: -- inspect <file> [<file>...]
if (args.Length > 1 && args[0] == "inspect")
{
    foreach (var file in args.Skip(1))
    {
        try
        {
            var (frames, luma, duration) = Mf.ProbeVideo(file, maxSamples: 500_000);
            Console.WriteLine($"{Path.GetFileName(file)}: decoded {frames} frames over {duration.TotalSeconds:F1}s, mean luma {luma:F1} ({new FileInfo(file).Length / 1024} KB)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{Path.GetFileName(file)}: UNREADABLE — {ex.Message}");
        }
    }

    return 0;
}

// Mux one vid/aud pair and probe the result. Usage: -- mux <video> <audio> <output>
if (args.Length == 4 && args[0] == "mux")
{
    try
    {
        Mp4Concat.MuxParallel(args[1], args[2], args[3]);
        var (muxFrames, muxLuma, _) = Mf.ProbeVideo(args[3]);
        Console.WriteLine($"mux ok: {new FileInfo(args[3]).Length / 1024} KB, decoded {muxFrames} frames, luma {muxLuma:F1}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"mux FAILED: {ex.Message}");
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

    // The Desktop Duplication source through the same engine (#6). Same assertions:
    // it either delivers frames into a decodable segment or it is not a capture path.
    // A second GPU driving the primary display makes duplication legitimately
    // impossible, so that one failure is reported rather than failed.
    string dup = Path.Combine(Path.GetTempPath(), "dejavu_dup.mp4");
    try
    {
        string? dupError = null;
        int dupRotated = 0;
        using (var engine = new CaptureEngine(monitor, IntPtr.Zero, 60, 70, duplicate: true))
        {
            engine.Error += e => dupError = e;
            Check("duplication is the live source", engine.UsingDuplication);
            engine.Start(dup);
            Thread.Sleep(3000);
            int dupFrames = engine.FramesInSegment;
            // The seam matters more here than on the WGC path: the pump thread blocks on
            // the writer lock the finalize holds, and it must not be holding the GDI DC
            // on the staging texture while it waits.
            using var dupRotate = new ManualResetEventSlim(false);
            engine.Rotate(Path.Combine(Path.GetTempPath(), "dejavu_dup2.mp4"),
                frames => { dupRotated = frames; dupRotate.Set(); });
            Thread.Sleep(1000);
            engine.Stop();
            dupRotate.Wait(5000);
            Check("duplication reported no errors", dupError is null, dupError);
            Check("duplication delivered frames", dupFrames > 30, $"got {dupFrames}");
            Check("duplication survived a rotate", dupRotated > 30, $"got {dupRotated}");
        }

        var probe = Mf.ProbeVideo(dup, maxSamples: 30);
        Check("duplication segment decodes", probe.Frames > 0, $"{probe.Frames} frames");
        // The failure this catches: CopyResource no-ops on a texture mismatch and encodes
        // untouched allocator memory, which decodes as a valid, entirely black clip.
        Check("duplication captured the desktop, not black", probe.Luma > 1.0, $"luma {probe.Luma:F1}");
        Console.WriteLine(
            $"duplication: {new FileInfo(dup).Length / 1024} KB, {probe.Frames} frames, luma {probe.Luma:F1}");
        File.Delete(Path.Combine(Path.GetTempPath(), "dejavu_dup2.mp4"));
    }
    catch (Exception ex)
    {
        Console.WriteLine("SKIP duplication: " + ex.Message);
    }
    finally
    {
        File.Delete(dup);
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

            // Include mode is the inverse: our tree IS the mix, so the tone must survive.
            // A wrong mode flag captures everything but us and lands near silence here.
            double included = CaptureLevel(Environment.ProcessId, include: true);
            Check("include mode hears the captured app's tone", included > tone / 10,
                $"tone {tone:F5}, included {included:F5}");
            Console.WriteLine(
                $"audio levels: ambient {ambient:F4}, +tone {heard:F4}, excluded {excluded:F4}, included {included:F4}");
        }
    }
    finally
    {
        player.Stop();
    }

    static double CaptureLevel(int excludePid, bool include = false)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dejavu_cap_{excludePid}_{include}.mp4");
        using (var capture = AudioLoopback.TryStart(path, excludePid, include))
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
    if (!DesktopDeliversFrames())
    {
        Console.WriteLine("SKIP smoke: the desktop is not delivering frames (screen locked or asleep).");
        Console.WriteLine($"{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    // d3d11.dll and dxgi.dll shims beside the exe (ReShade, dxvk, ENB) must not break
    // capture. The resolver pins our own P/Invokes to System32 (issue #2), and
    // SetDefaultDllDirectories keeps dependency resolution — System32 d3d11 pulling in
    // dxgi — away from the shims too (issue #3). version.dll is a real PE that exports
    // none of either surface, so an unpinned probe dies on it.
    var decoys = new[]
    {
        Path.Combine(AppContext.BaseDirectory, "d3d11.dll"),
        Path.Combine(AppContext.BaseDirectory, "dxgi.dll"),
    };
    try
    {
        foreach (var decoy in decoys)
        {
            File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), decoy, overwrite: true);
        }

        using var shadowProbe = System.Diagnostics.Process.Start(Environment.ProcessPath!, "probe");
        bool exited = shadowProbe!.WaitForExit(60_000);
        Check("capture survives shadowing d3d11.dll and dxgi.dll", exited && shadowProbe.ExitCode == 0,
            exited ? $"probe exit {shadowProbe.ExitCode}" : "probe hung");
    }
    finally
    {
        foreach (var decoy in decoys)
        {
            try { File.Delete(decoy); } catch { }
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
