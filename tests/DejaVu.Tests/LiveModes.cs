using System.Collections.Concurrent;
using DejaVu;

/// <summary>
/// Live multi-device checks (#11) on real endpoints, the ones the multi-audio change left
/// for a human. Each plays quiet test signals (-50 dBFS) on two output devices, records
/// through the app's own code, and judges the clip with <see cref="SignalAnalysis"/>:
///
///   mixlive        both devices in a saved clip: present, no clicks, no pitch wobble,
///                  no echo, a steady offset between them
///   devicelost     an extra device fails mid-segment and comes back (Debug hooks), and
///                  an endpoint ID that does not exist is skipped once
///   longmix [min]  a full-length recording (25 min): the offset between the devices
///                  must not drift more than 10 ms from start to end
///   defaultswitch  the real default device switches to a ticked extra mid-recording:
///                  it must not be mixed twice from that moment
///   restoredefault put back the default device a killed run left switched
///
/// Real-world case first: SteelSeries Sonar's Gaming (game, the default) and Chat
/// endpoints, else any two other active outputs. The default playback device is only
/// ever changed by defaultswitch, and always put back.
/// </summary>
internal static class LiveModes
{
    private const int Rate = Markers.Rate;

    private static int passed, failed;

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            passed++;
            Console.WriteLine($"ok    {name}{(detail is null ? "" : "  (" + detail + ")")}");
            return;
        }

        failed++;
        Console.WriteLine($"FAIL  {name}{(detail is null ? "" : "  -> " + detail)}");
    }

    private static int Done()
    {
        Console.WriteLine($"{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    public static readonly string[] Modes =
        ["mixlive", "devicelost", "longmix", "defaultswitch", "restoredefault", "routeprobe", "rendertone", "includeprobe", "startprobe", "analyzeclip", "markerstats"];

    public static int Run(string[] args)
    {
        if (args[0] == "restoredefault")
        {
            bool had = File.Exists(DefaultDevice.RestoreFile);
            bool ok = DefaultDevice.RestoreFromFile();
            Console.WriteLine(had ? $"default device restored: {ok}" : "nothing to restore");
            return !had || ok ? 0 : 1;
        }

        // A run killed mid-switch left the default changed: undo that before anything.
        if (File.Exists(DefaultDevice.RestoreFile))
        {
            Console.WriteLine($"      a previous run left the default switched; restored: {DefaultDevice.RestoreFromFile()}");
        }

        // Our own segment ring, apart from the live app's and from other test runs.
        var bufferDir = Path.Combine(Path.GetTempPath(), $"DejaVu.AudioCheck.{Environment.ProcessId}");
        Environment.SetEnvironmentVariable("DEJAVU_BUFFER_DIR", bufferDir);
        try
        {
            return args[0] switch
            {
                "mixlive" => MixLive(),
                "devicelost" => DeviceLost(),
                "longmix" => LongMix(args.Length > 1 && int.TryParse(args[1], out int m) ? m : 25),
                "defaultswitch" => DefaultSwitch(),
                "routeprobe" => RouteProbe(),
                "rendertone" => RenderTone(args[1], double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture)),
                "includeprobe" => IncludeProbe(),
                "startprobe" => StartProbe(),
                "analyzeclip" => AnalyzeClip(args),
                "markerstats" => MarkerStats(args[1], double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture)),
                _ => 2,
            };
        }
        finally
        {
            try
            {
                Directory.Delete(bufferDir, recursive: true);
            }
            catch
            {
                // Best effort; it is under %TEMP%.
            }
        }
    }

    private sealed record Pick(
        AudioLoopback.RenderDevice Game, AudioLoopback.RenderDevice Chat, string[] Extras, bool GameIsDefault);

    private static Pick? PickDevices()
    {
        var devices = AudioLoopback.ListRenderDevices();
        Console.WriteLine("      active outputs:");
        foreach (var d in devices)
        {
            Console.WriteLine($"        {(d.IsDefault ? "DEFAULT " : "        ")}{d.Name}  {d.Id}");
        }

        var game = devices.FirstOrDefault(d => d.Name.Contains("Sonar - Gaming", StringComparison.OrdinalIgnoreCase));
        var chat = devices.FirstOrDefault(d => d.Name.Contains("Sonar - Chat", StringComparison.OrdinalIgnoreCase));
        // DEJAVU_TEST_NO_SONAR=1 picks two plain outputs, to tell Sonar's own processing
        // (its ducking and voice effects) apart from the mixer.
        if (Environment.GetEnvironmentVariable("DEJAVU_TEST_NO_SONAR") == "1")
        {
            devices = devices.Where(d => !d.Name.Contains("Sonar", StringComparison.OrdinalIgnoreCase)).ToList();
            game = default;
            chat = default;
        }

        if (game.Id is null || chat.Id is null)
        {
            // No Sonar: any two outputs other than the default, both ticked.
            var others = devices.Where(d => !d.IsDefault).Take(2).ToList();
            if (others.Count < 2)
            {
                return null;
            }

            return new Pick(others[0], others[1], [others[0].Id, others[1].Id], false);
        }

        if (game.IsDefault)
        {
            return new Pick(game, chat, [chat.Id], true);
        }

        // Sonar present but Gaming is not the default: tick whichever of the two is not.
        return new Pick(game, chat, new[] { game, chat }.Where(d => !d.IsDefault).Select(d => d.Id).ToArray(), false);
    }

    private static SignalSpec SpecA(double period) => new()
    {
        ToneHz = 440, ToneAmp = 0.0015, MarkerAmp = 0.0015, MarkerUp = true, PeriodSec = period, PhaseSec = 0,
    };

    private static SignalSpec SpecB(double period) => new()
    {
        ToneHz = 1250, ToneAmp = 0.0015, MarkerAmp = 0.0015, MarkerUp = false, PeriodSec = period, PhaseSec = period / 2,
    };

    private static AppConfig TestConfig(string[] extras, int bufferMinutes = 5) => new()
    {
        SaveRoot = Path.Combine(Path.GetTempPath(), "DejaVu.AudioCheck.Clips"),
        SystemAudio = true,
        Microphone = false,
        SaveSound = false,
        BufferMinutes = bufferMinutes,
        CaptureTarget = Native.ListDisplays().First().DeviceName,
        ExtraAudioDevices = extras,
    };

    /// <summary>Everything the app logs while a test runs (Debug hook), with failures.</summary>
    private sealed class LogTap : IDisposable
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public LogTap() => AppLog.Written += OnLine;

        private void OnLine(string line)
        {
            Lines.Enqueue(line);
            Console.WriteLine("      log: " + line);
        }

        public int Count(string contains) => Lines.Count(l => l.Contains(contains, StringComparison.Ordinal));

        public void Dispose() => AppLog.Written -= OnLine;
    }

    /// <summary>Judges a recorded mix with <see cref="MixJudge"/> and prints what it
    /// measured. <paramref name="seams"/> in seconds when the save logged them; null
    /// estimates them from the markers.</summary>
    private static MixJudge.Verdict Analyze(string label, float[] mono, SignalSpec a, SignalSpec b,
        IReadOnlyList<double>? seams, bool expectB = true)
    {
        var v = MixJudge.Judge(mono, a, b, seams, expectB);
        string Gains(List<MixJudge.Hit> h) => h.Count == 0 ? "-" : $"{h.Min(x => x.Gain):F3}..{h.Max(x => x.Gain):F3}";
        string Spacing(List<MixJudge.Hit> h, double period) => h.Count < 3 ? "-" : string.Join(" ",
            h.Zip(h.Skip(1), (x, y) => ((y.Position - x.Position) / Rate - period * Math.Round((y.Position - x.Position) / Rate / period)) * 1000)
                .Select(d => d.ToString("F1")));
        Console.WriteLine($"      [{label}] {v.Seconds:F1} s of audio; seams {(seams is null ? "estimated" : "logged")}: "
            + string.Join(" ", v.Excluded.Select(e => $"{e.From:F1}-{e.To:F1}s")));
        Console.WriteLine($"      [{label}] A markers {v.A.Hits.Count} ({v.A.Missing} missing), gain {Gains(v.A.Hits)}; "
            + $"B markers {v.B.Hits.Count} ({v.B.Missing} missing), gain {Gains(v.B.Hits)}");
        if (v.Offsets.Count > 0)
        {
            Console.WriteLine($"      [{label}] B-A offset {v.Offsets.Min():F2}..{v.Offsets.Max():F2} ms: "
                + string.Join(" ", v.Offsets.Select(o => o.ToString("F1"))));
        }

        var odd = v.A.Hits.Select(h => ("A", h)).Concat(v.B.Hits.Select(h => ("B", h)))
            .Where(x => x.h.SecondRatio >= MixJudge.SecondPeakLimit).OrderBy(x => x.h.Position).ToList();
        if (odd.Count > 0)
        {
            Console.WriteLine($"      [{label}] markers with a second peak: "
                + string.Join(" ", odd.Take(20).Select(x => $"{x.Item1}@{x.h.Position / Rate:F1}s 2nd{x.h.SecondRatio:F2}@{x.h.SecondOffsetMs:F0}ms")));
        }

        Console.WriteLine($"      [{label}] A spacing off the period (ms): {Spacing(v.A.Hits, a.PeriodSec)}");
        Console.WriteLine($"      [{label}] B spacing off the period (ms): {Spacing(v.B.Hits, b.PeriodSec)}");
        Console.WriteLine($"      [{label}] tone levels A {SignalAnalysis.ToneLevel(mono, a.ToneHz):F1} LSB, "
            + $"B {SignalAnalysis.ToneLevel(mono, b.ToneHz):F1} LSB (rendered {a.ToneAmp * short.MaxValue:F1}); click limit {v.ClickLimit:F1} LSB");
        return v;
    }

    /// <summary>Every verdict of <paramref name="v"/> as a pass/fail line.</summary>
    private static void CheckAll(string label, MixJudge.Verdict v, params string[] skip)
    {
        foreach (var c in v.Checks.Where(c => !skip.Contains(c.Key)))
        {
            Check($"{label}: {c.Name}", c.Ok, c.Detail);
        }
    }

    /// <summary>Times an extra stream ran dry or was trimmed, from the Debug build's
    /// per-segment lines: the only moments its mixer can step its output.</summary>
    private static int ExtraEvents(LogTap tap)
    {
        int n = tap.Count("ms trimmed this segment");
        foreach (var line in tap.Lines.Where(l => l.StartsWith("debug: extra audio device ", StringComparison.Ordinal)))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @": (\d+) underruns");
            if (m.Success)
            {
                n += int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return n;
    }

    /// <summary>Seam positions (s) in a saved clip, from the Debug build's save log:
    /// each segment's audio length, summed.</summary>
    private static List<double>? SeamsFromLog(LogTap tap)
    {
        var line = tap.Lines.LastOrDefault(l => l.StartsWith("debug: concat audio ms: ", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        var ms = line["debug: concat audio ms: ".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var seams = new List<double>();
        double at = 0;
        for (int i = 0; i + 1 < ms.Count; i++)
        {
            at += ms[i] / 1000;
            seams.Add(at);
        }

        return seams;
    }

    private static (SignalRender A, SignalRender B) StartSignals(Pick pick, double period)
    {
        double t0 = SignalRender.Now + 1.0;
        var a = new SignalRender(pick.Game.Id, "game", SpecA(period), t0);
        var b = new SignalRender(pick.Chat.Id, "chat", SpecB(period), t0);
        return (a, b);
    }

    /// <summary>A small window in the recorded display's corner that repaints ten times
    /// a second while a buffer records. The desktop capture delivers frames only when
    /// something changes, and a segment with none is dropped with its audio; on an idle
    /// desktop that took whole segments out of the clips under test.</summary>
    private sealed class FrameTicker : IDisposable
    {
        private readonly Thread thread;
        private System.Windows.Forms.Form? form;

        public FrameTicker()
        {
            using var ready = new ManualResetEventSlim();
            thread = new Thread(() =>
            {
                // On the display the test buffers record (the first one listed).
                var target = Native.ListDisplays().First().DeviceName;
                var area = (System.Windows.Forms.Screen.AllScreens.FirstOrDefault(sc => sc.DeviceName == target)
                    ?? System.Windows.Forms.Screen.PrimaryScreen!).Bounds;
                form = new System.Windows.Forms.Form
                {
                    FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                    ShowInTaskbar = false,
                    TopMost = true,
                    StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                    Bounds = new System.Drawing.Rectangle(area.Right - 24, area.Bottom - 24, 24, 24),
                };
                var timer = new System.Windows.Forms.Timer { Interval = 100 };
                int tick = 0;
                timer.Tick += (_, _) => form.BackColor = (tick++ % 2 == 0) ? System.Drawing.Color.DimGray : System.Drawing.Color.Gray;
                form.Shown += (_, _) => { timer.Start(); ready.Set(); };
                System.Windows.Forms.Application.Run(form);
                timer.Dispose();
            }) { IsBackground = true, Name = "frame ticker" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait(5000);
        }

        public void Dispose()
        {
            form?.BeginInvoke(() => form.Close());
            thread.Join(5000);
        }
    }

    private static float[] DecodeMono(string path)
    {
        var (samples, _, _) = ClipAudio.Decode(path);
        return ClipAudio.Mono(samples);
    }

    // ---- which output devices each main-stream route hears ----

    /// <summary>Plays a marker on each active output in turn and reports, per route the
    /// main stream can take, whether that route hears it. An extra device the main
    /// stream already hears must not be mixed in on top.</summary>
    private static int RouteProbe()
    {
        var devices = AudioLoopback.ListRenderDevices();
        int excludePid = OldestPid(TestConfig([]).AudioExclude);
        var spec = SpecA(1);
        foreach (var d in devices)
        {
            using var render = new SignalRender(d.Id, d.Name, spec, SignalRender.Now + 0.5);
            Thread.Sleep(600);
            var line = $"      {(d.IsDefault ? "DEFAULT " : "        ")}{d.Name}:";
            foreach (var (label, pid, include) in new[]
            {
                ("endpoint", 0, false), ("exclude Discord", excludePid, false), ("include self", Environment.ProcessId, true),
            })
            {
                if (pid == 0 && label != "endpoint")
                {
                    line += $" {label} n/a;";
                    continue;
                }

                var path = Path.Combine(Path.GetTempPath(), $"dejavu_probe_{Environment.ProcessId}.mp4");
                string route;
                using (var cap = AudioLoopback.TryStart(path, pid, include))
                {
                    route = cap?.Route ?? "none";
                    Thread.Sleep(3500);
                }

                var hits = SignalAnalysis.FindMarkers(DecodeMono(path), spec.MarkerUp, spec.MarkerAmp, minGain: 0.02);
                File.Delete(path);
                line += $" {label} {(hits.Count == 0 ? "-" : hits.Average(h => h.Gain).ToString("F2"))} [{route}];";
            }

            Console.WriteLine(line);
        }

        return Done();
    }

    /// <summary>Child process for <see cref="IncludeProbe"/>: markers on one device.</summary>
    private static int RenderTone(string deviceId, double seconds)
    {
        using var render = new SignalRender(deviceId, "child", SpecA(deviceId == SignalRender.FollowDefault ? 2 : 1), SignalRender.Now + 0.3);
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        return 0;
    }

    /// <summary>What include-mode process loopback hears of an app rendering to each
    /// device: a child process plays markers on one output, captured by pid.</summary>
    private static int IncludeProbe()
    {
#if DEBUG
        AudioLoopback.DebugForceProcessLoopback = true;
        foreach (var d in AudioLoopback.ListRenderDevices())
        {
            using var child = System.Diagnostics.Process.Start(Environment.ProcessPath!, $"rendertone \"{d.Id}\" 6")!;
            Thread.Sleep(1500);
            var path = Path.Combine(Path.GetTempPath(), $"dejavu_incprobe_{Environment.ProcessId}.mp4");
            string route;
            using (var cap = AudioLoopback.TryStart(path, child.Id, includeOnly: true))
            {
                route = cap?.Route ?? "none";
                Thread.Sleep(3500);
            }

            child.WaitForExit(10_000);
            var hits = SignalAnalysis.FindMarkers(DecodeMono(path), true, SpecA(1).MarkerAmp, minGain: 0.02);
            File.Delete(path);
            Console.WriteLine($"      {(d.IsDefault ? "DEFAULT " : "        ")}{d.Name}: include child {(hits.Count == 0 ? "-" : hits.Average(h => h.Gain).ToString("F2"))} [{route}]");
        }

        AudioLoopback.DebugForceProcessLoopback = false;
#endif
        return Done();
    }

    /// <summary>"analyzeclip clip.mp4 [period s] [segment s]": the mix verdicts over a
    /// clip recorded earlier (the long run keeps its clips for this).</summary>
    /// <summary>"markerstats clip.mp4 period": every marker candidate above the gain
    /// floor with its sharpness, to tune the gates against a real clip.</summary>
    private static int MarkerStats(string path, double period)
    {
        var mono = DecodeMono(path);
        foreach (var spec in new[] { SpecA(period), SpecB(period) })
        {
            var c = MixJudge.Candidates(mono, spec.MarkerUp, spec.MarkerAmp).Where(x => x.Gain >= 0.05).ToList();
            Console.WriteLine($"      {(spec.MarkerUp ? "A" : "B")}: " + string.Join(" ", c.Select(x => $"{x.Position / Rate:F2}s:g{x.Gain:F2}/s{x.Sharpness:F0}")));
        }

        return 0;
    }

    private static int AnalyzeClip(string[] args)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double period = args.Length > 2 ? double.Parse(args[2], inv) : 10;
        int seg = args.Length > 3 ? int.Parse(args[3], inv) : 0;
        var monoClip = DecodeMono(args[1]);
        if (args.Length > 5 && double.TryParse(args[5], inv, out double untilSeconds))
        {
            // "analyzeclip clip period seg - <seconds>": only the clip's first part.
            monoClip = monoClip[..Math.Min(monoClip.Length, (int)(untilSeconds * Rate))];
        }

        if (args.Length > 4 && args[4] == "levels")
        {
            // Per 10 s: overall RMS and each tone's level, to spot outside sound.
            for (int at = 0; at + Rate * 10 <= monoClip.Length; at += Rate * 10)
            {
                var part = monoClip.AsSpan(at, Rate * 10).ToArray();
                double rms = Math.Sqrt(part.Average(v => (double)v * v));
                Console.WriteLine($"      {at / Rate,5} s: rms {rms,6:F1}, A {SignalAnalysis.ToneLevel(part, 440),5:F1}, B {SignalAnalysis.ToneLevel(part, 1250),5:F1}");
            }
        }

        // Kept clips carry no seam log: seams are estimated from the markers.
        var r = Analyze(Path.GetFileName(args[1]), monoClip, SpecA(period), SpecB(period), seams: null);
        CheckAll(Path.GetFileName(args[1]), r);
        if (r.Offsets.Count >= 10)
        {
            double first = r.Offsets.Take(3).Average(), last = r.Offsets.TakeLast(3).Average();
            Console.WriteLine($"      offset first {first:F2} ms, last {last:F2} ms, drift {last - first:F2} ms over {r.Seconds / 60:F1} min");
        }

        return Done();
    }

    /// <summary>The first second of an extra device's drift corrector, sampled every
    /// 20 ms: priming, underruns and the backlog right after a segment starts.</summary>
    private static int StartProbe()
    {
        if (PickDevices() is not { } pick)
        {
            return Done();
        }

        var (ra, rb) = StartSignals(pick, period: 2);
        using (ra)
        using (rb)
        {
            Thread.Sleep(1500);
            for (int run = 0; run < 3; run++)
            {
                var path = Path.Combine(Path.GetTempPath(), $"dejavu_startprobe_{Environment.ProcessId}.mp4");
                using (var cap = AudioLoopback.TryStart(path, 0, extraDevices: pick.Extras))
                {
                    var line = "";
                    for (int i = 0; i < 100; i++)
                    {
                        var st = cap!.ExtraStats[0];
                        line += $"{st.Backlog}/{(st.Ratio - 1) * 1e6:F0} ";
                        Thread.Sleep(20);
                    }

                    Console.WriteLine($"      run {run}: backlog/ratio ppm every 20 ms: {line}");
                }

                File.Delete(path);
            }
        }

        return Done();
    }

    // ---- item 2: real mixing through the save path ----

    private static int MixLive()
    {
        if (PickDevices() is not { } pick)
        {
            Console.WriteLine("SKIP mixlive: needs two outputs besides the default.");
            return Done();
        }

        Console.WriteLine($"      game: {pick.Game.Name}{(pick.GameIsDefault ? " (default)" : "")}; chat: {pick.Chat.Name}; ticked: {pick.Extras.Length}");

        // Both main-stream routes a monitor capture takes: the default endpoint (no
        // excluded app running) and process loopback minus Discord (Discord running).
        MixLiveRoute(pick, "endpoint", exclude: []);

        // Control: the 1.4 path, nothing ticked, so effects of the main stream itself
        // can be told apart from the mixing (the game signal alone is judged here).
        MixLiveRoute(pick with { Extras = [] }, "control (nothing ticked)", exclude: []);
        var config = TestConfig([]);
        if (OldestPid(config.AudioExclude) > 0)
        {
            // Process loopback minus Discord hears the chat device by itself: ticking it
            // must not add a second copy. What that route does to the chat device's sound
            // on its own (Windows mixes every device into it, each on its own clock) is
            // the 1.4 path: the control without anything ticked shows the same.
            MixLiveRoute(pick, "exclude", exclude: config.AudioExclude);
            MixLiveRoute(pick with { Extras = [] }, "exclude control (nothing ticked)", exclude: config.AudioExclude);
        }
        else
        {
            Console.WriteLine("SKIP mixlive exclude: no excluded app (Discord) running.");
        }

        return Done();
    }

    private static void MixLiveRoute(Pick pick, string route, string[] exclude)
    {
        string label = $"mixlive {route}";
        var config = TestConfig(pick.Extras);
        config.AudioExclude = exclude;
        Directory.CreateDirectory(config.SaveRoot);
        string? failure = null;
        string clip;
        List<double>? seams;
        int extraEvents;
        using (var tap = new LogTap())
        {
            var (ra, rb) = StartSignals(pick, period: 2);
            using (ra)
            using (rb)
            {
                Thread.Sleep(1500);
                using var ticker = new FrameTicker();
                using var buffer = new ReplayBuffer(config, segmentSeconds: 20);
                buffer.Failed += e => failure = e;
                buffer.Start();
                Thread.Sleep(50_000);
                clip = buffer.Save("audiocheck");
                Console.WriteLine($"      render clocks vs QPC: game {ra.ClockPpm:F1} ppm, chat {rb.ClockPpm:F1} ppm; "
                    + $"clock reading scatter game {ra.ClockJitterFrames:F1}, chat {rb.ClockJitterFrames:F1} frames");
            }

            Check($"{label}: recorder reported no failure", failure is null, failure);
            seams = SeamsFromLog(tap);
            extraEvents = ExtraEvents(tap);
            var routes = tap.Lines.Where(l => l.StartsWith("audio: ")).ToList();
            if (pick.Extras.Length > 0)
            {
                Check($"{label}: every segment had both devices",
                    routes.Count >= 2 && routes.All(l => l.Contains(pick.Chat.Name)), string.Join(" | ", routes.Distinct()));
            }

            if (exclude.Length > 0 && pick.Extras.Length > 0)
            {
                // Discord has no audio session on the chat device here, so nothing is added.
                Check($"{label}: the ticked device is not captured a second time",
                    routes.All(l => l.Contains("already hears") && !l.Contains(" + " + pick.Chat.Name)), string.Join(" | ", routes.Distinct()));
            }
        }

        var mono = DecodeMono(clip);
        Console.WriteLine($"      clip: {clip}");
        bool ticked = pick.Extras.Length > 0;
        bool exclusion = exclude.Length > 0;
        // On the exclude route the chat device is in the main stream whether ticked or not.
        var r = Analyze(label, mono, SpecA(2), SpecB(2), seams, expectB: ticked || exclusion);
        if (!ticked && !exclusion)
        {
            Check($"{label}: chat stays out when not ticked", r.B.Hits.Count == 0, $"{r.B.Hits.Count} chat markers");
        }

        // What the main stream does by itself is 1.4's, reported for comparison, not judged.
        // On the exclude route nothing is mixed in any more: the chat device's sound is the
        // process-loopback main stream's, ticked or not.
        string[] mainStreamOnly = !ticked ? MixJudge.Keys.Where(k => k is not ("game present" or "one peak per marker")).ToArray()
            : exclusion ? ["chat present", "no clicks", "chat no dropouts", "chat pitch steady", "chat no slips", "offset steady"] : [];
        foreach (var c in r.Checks.Where(c => mainStreamOnly.Contains(c.Key)))
        {
            Console.WriteLine($"      [{label}] 1.4 main stream, not judged: {c.Name}: {(c.Ok ? "ok" : "no")} ({c.Detail})");
        }

        if (ticked && !exclusion)
        {
            // The mixer only ever steps its output when the extra runs dry, is trimmed, or
            // starts: running continuously it resamples a continuous stream. A click while
            // it ran continuously, with the chat tone unbroken, is the main stream's own
            // (the control shows those too, and outside sound on the default device).
            mainStreamOnly = [.. mainStreamOnly, "no clicks"];
            var click = r.Checks.First(c => c.Key == "no clicks");
            Console.WriteLine($"      [{label}] clicks in the whole mix: {click.Detail}; the extra ran dry or was trimmed {extraEvents} times");
            Check($"{label}: no clicks from the mixing", click.Ok || (extraEvents == 0 && r.Ok("chat no slips") && r.Ok("chat no dropouts")),
                $"{click.Detail}; extra events {extraEvents}");
        }

        CheckAll(label, r, mainStreamOnly);
        if (ticked && exclusion)
        {
            Check($"{label}: the chat device is heard, through the system mix", r.B.Hits.Count >= (int)(r.Seconds / 2) / 2,
                $"{r.B.Hits.Count} chat markers");
        }
    }

    // ---- item 3: device lost and back, and an ID that does not exist ----

    private static int DeviceLost()
    {
#if DEBUG
        if (PickDevices() is not { } pick)
        {
            Console.WriteLine("SKIP devicelost: needs two outputs besides the default.");
            return Done();
        }

        const string Bogus = "{0.0.0.00000000}.{deadbeef-0000-4000-8000-000000000011}";
        var victim = pick.Chat;
        var config = TestConfig([.. pick.Extras, Bogus]);
        // The endpoint route: with Discord excluded, process loopback hears the chat
        // device by itself, so its loss would not show in the clip.
        config.AudioExclude = [];
        Directory.CreateDirectory(config.SaveRoot);
        string? failure = null;
        string clip;
        double lostAt, backAt, start;
        List<double>? seams;
        using (var tap = new LogTap())
        {
            var (ra, rb) = StartSignals(pick, period: 2);
            using (ra)
            using (rb)
            {
                Thread.Sleep(1500);
                using var ticker = new FrameTicker();
                using var buffer = new ReplayBuffer(config, segmentSeconds: 4);
                buffer.Failed += e => failure = e;
                buffer.Start();
                start = SignalRender.Now;
                Thread.Sleep(6_000);

                // Mid-segment: the stream fails, and the device stays unplugged for the
                // next two segment starts.
                AudioLoopback.DebugUnpluggedExtra = victim.Id;
                AudioLoopback.DebugInvalidateExtra = victim.Id;
                lostAt = SignalRender.Now - start;
                Thread.Sleep(9_000);
                AudioLoopback.DebugUnpluggedExtra = null;
                backAt = SignalRender.Now - start;
                Thread.Sleep(11_000);
                clip = buffer.Save("audiocheck");
            }

            seams = SeamsFromLog(tap);
            Check("devicelost: the invalidation fired", AudioLoopback.DebugInvalidateExtra is null);
            Check("devicelost: recorder reported no failure", failure is null, failure);
            Check("devicelost: \"extra audio device lost\" logged once",
                tap.Count($"extra audio device lost: {victim.Name} (0x88890004)") == 1, $"{tap.Count("extra audio device lost")} times");
            Check("devicelost: \"skipped (not connected)\" logged once",
                tap.Count($"extra audio device skipped (not connected): {victim.Id}") == 1,
                $"{tap.Count("skipped (not connected)")} times");
            Check("devicelost: \"back\" logged once", tap.Count($"extra audio device back: {victim.Name}") == 1,
                $"{tap.Count("extra audio device back")} times");
            Check("devicelost: the unknown ID is skipped once",
                tap.Count($"extra audio device skipped (unknown device): {Bogus}") == 1,
                $"{tap.Count(Bogus)} lines name it");
            Check("devicelost: nothing logged as an error",
                !tap.Lines.Any(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("audio pump stopped", StringComparison.Ordinal)),
                string.Join(" | ", tap.Lines.Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))));
            var routes = tap.Lines.Where(l => l.StartsWith("audio: ")).ToList();
            Check("devicelost: segments while unplugged record without it",
                routes.Any(l => !l.Contains(victim.Name)), string.Join(" | ", routes));
            Check("devicelost: the segment after it is back records it again",
                routes.Count > 0 && routes[^1].Contains(victim.Name), routes.LastOrDefault());
        }

        var mono = DecodeMono(clip);
        Console.WriteLine($"      clip: {clip}; lost at {lostAt:F1} s, replugged at {backAt:F1} s of recording");
        var r = Analyze("devicelost", mono, SpecA(2), SpecB(2), seams);
        double clipSeconds = r.Seconds;

        // The game device never stopped: its markers run through the whole clip, gap or not.
        var gapsA = r.A.Hits.Zip(r.A.Hits.Skip(1), (x, y) => (y.Position - x.Position) / Rate).DefaultIfEmpty(0).Max();
        Check("devicelost: the buffer kept recording the game device throughout",
            r.Ok("game present") && gapsA < 2.5, $"{r.A.Hits.Count} markers, {r.A.Missing} missing, widest gap {gapsA:F1} s");

        // The chat device: present, then a hole while it is gone, then present again.
        var bTimes = r.B.Hits.Select(h => h.Position / Rate).ToList();
        var holes = bTimes.Zip(bTimes.Skip(1), (x, y) => (From: x, To: y)).Where(p => p.To - p.From > 3).ToList();
        Check("devicelost: chat drops out once and comes back",
            holes.Count == 1 && bTimes.Count(t => t < holes[0].From + 0.1) >= 1 && bTimes.Count(t => t > holes[0].To - 0.1) >= 3,
            $"chat markers at {string.Join(" ", bTimes.Select(t => t.ToString("F1")))}");
        // Away from seams: each seam repeats a few tens of ms (the 1.4 path does too).
        Check("devicelost: no echo or doubling away from seams", r.Ok("one peak per marker"),
            r.Checks.First(c => c.Key == "one peak per marker").Detail);
        Check("devicelost: no clicks away from seams", r.Ok("no clicks"), r.Checks.First(c => c.Key == "no clicks").Detail);
        File.Delete(clip);
        return Done();
#else
        Console.WriteLine("SKIP devicelost: needs the Debug build's hooks.");
        return Done();
#endif
    }

    // ---- item 4: long run, offset drift ----

    private static int LongMix(int minutes)
    {
        if (PickDevices() is not { } pick)
        {
            Console.WriteLine("SKIP longmix: needs two outputs besides the default.");
            return Done();
        }

        const double Period = 10;
        var config = TestConfig(pick.Extras, bufferMinutes: Math.Clamp(minutes, 5, 25));
        // The plain endpoint route: with Discord excluded, process loopback already
        // hears the chat device and the extra doubles it (see mixlive exclude), which
        // would make a drift measurement meaningless.
        config.AudioExclude = [];
        Directory.CreateDirectory(config.SaveRoot);
        string? failure = null;
        string clip;
        var rawPath = Path.Combine(config.SaveRoot, $"longmix_raw_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
        int excludePid = 0;
        string rawRoute;
        List<double>? seams;
        using (var tap = new LogTap())
        {
            var (ra, rb) = StartSignals(pick, Period);
            using (ra)
            using (rb)
            {
                Thread.Sleep(1500);
                // Two recordings of the same signals at once: the app's buffer (one-minute
                // segments, saved as one clip) and one unbroken capture, the hardest case
                // for the drift corrector since it never restarts.
                using var raw = AudioLoopback.TryStart(rawPath, excludePid, extraDevices: pick.Extras);
                rawRoute = raw?.Route ?? "none";
                using var ticker = new FrameTicker();
                using var buffer = new ReplayBuffer(config, segmentSeconds: 60);
                buffer.Failed += e => failure = e;
                buffer.Start();
                var until = DateTime.UtcNow.AddMinutes(minutes).AddSeconds(30);
                while (DateTime.UtcNow < until)
                {
                    Thread.Sleep(60_000);
                    if (raw?.ExtraStats is { Count: > 0 } st)
                    {
                        var s = st[0];
                        Console.WriteLine($"      {DateTime.Now:HH:mm:ss} raw: ratio {s.Ratio:F6}, backlog {s.Backlog}, dropped {s.Dropped}, underruns {s.Underruns}, dead {s.Dead}");
                    }
                }

                if (raw?.ExtraStats is { Count: > 0 } end)
                {
                    Check("longmix: unbroken capture never trimmed", end[0].Dropped == 0, $"dropped {end[0].Dropped}");
                    Check("longmix: unbroken capture's extra stayed alive", !end[0].Dead);
                }

                clip = buffer.Save("audiocheck");
                Console.WriteLine($"      render clocks vs QPC: game {ra.ClockPpm:F1} ppm, chat {rb.ClockPpm:F1} ppm");
            }

            Check("longmix: recorder reported no failure", failure is null, failure);
            seams = SeamsFromLog(tap);
            Console.WriteLine("      raw route: " + rawRoute);
        }

        int expected = (int)(minutes * 60 / Period) - 1;
        Console.WriteLine($"      clips kept for re-analysis: {clip} {rawPath}");
        foreach (var (label, path, seg) in new (string, string, List<double>?)[] { ("buffer clip", clip, seams), ("unbroken capture", rawPath, []) })
        {
            var mono = DecodeMono(path);
            var r = Analyze(label, mono, SpecA(Period), SpecB(Period), seg);
            CheckAll($"longmix {label}", r);
            if (r.Offsets.Count >= 10)
            {
                double first = r.Offsets.Take(3).Average(), last = r.Offsets.TakeLast(3).Average();
                Console.WriteLine($"      [{label}] offset first 30 s {first:F2} ms, last 30 s {last:F2} ms, over {r.Seconds / 60:F1} min");
                Check($"longmix {label}: drift under 10 ms start to end", Math.Abs(last - first) < 10, $"{last - first:F2} ms");
                Check($"longmix {label}: covers the run", r.Seconds > minutes * 60 - 90, $"{r.Seconds / 60:F1} min");
            }
            else
            {
                Check($"longmix {label}: enough marker pairs", false, $"{r.Offsets.Count}");
            }
        }

        return Done();
    }

    private static int OldestPid(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var procs = System.Diagnostics.Process.GetProcessesByName(name);
            try
            {
                var oldest = procs.Select(p =>
                {
                    try
                    {
                        return (p.Id, Start: p.StartTime);
                    }
                    catch
                    {
                        return (p.Id, Start: DateTime.MaxValue);
                    }
                }).OrderBy(p => p.Start).FirstOrDefault();
                if (oldest.Id != 0)
                {
                    return oldest.Id;
                }
            }
            finally
            {
                foreach (var p in procs)
                {
                    p.Dispose();
                }
            }
        }

        return 0;
    }

    // ---- item 5: the default switches to a ticked device mid-recording ----

    private static int DefaultSwitch()
    {
        if (PickDevices() is not { } pick)
        {
            Console.WriteLine("SKIP defaultswitch: needs two outputs besides the default.");
            return Done();
        }

        var original = DefaultDevice.Get();
        var x = pick.Chat;  // ticked; becomes the default mid-recording
        Console.WriteLine($"      original default: {original}; switching to the ticked {x.Name}");
        int excludePid = OldestPid(TestConfig([]).AudioExclude);
        using (var guard = DefaultDevice.Remember())
        {
            try
            {
                // Both main-stream routes. The plain endpoint is opened on one device
                // and stays on it when the default moves; process loopback (the route
                // whenever Discord is excluded) hears every output device whatever the
                // default is, so a default change is not what decides doubling there.
                var routes = new List<(string Label, int Pid, bool Include)> { ("endpoint", 0, false) };
                if (excludePid > 0)
                {
                    routes.Insert(0, ("exclude", excludePid, false));
                }

                foreach (var (label, pid, include) in routes)
                {
                    SwitchOnce(pick, x, label, pid, include, original!);
                }

                // The route where a default change DOES double a ticked device: app-only
                // capture by process loopback, of a game whose stream follows the default.
                // When the default moves to the ticked device, the game moves with it.
                FollowSwitch(pick, x, original!, simulate: false);
#if DEBUG
                // The same through the Debug hook, no real switch: the extra goes silent.
                FollowSwitch(pick, x, original!, simulate: true);
#endif

                // And the app's own path end to end: the buffer, a save.
                SwitchThroughBuffer(pick, x, original!);
            }
            finally
            {
                guard.Dispose();
                Check("defaultswitch: original default device restored", guard.Restored && DefaultDevice.Get() == original,
                    $"now {DefaultDevice.Get()}");
            }
        }

        return Done();
    }

    private static void SwitchOnce(Pick pick, AudioLoopback.RenderDevice x, string label, int pid, bool include, string original)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dejavu_switch_{label}_{Environment.ProcessId}.mp4");
        double switchAt, backAt;
        string route;
        using (var tap = new LogTap())
        {
            var (ra, rb) = StartSignals(pick, period: 2);
            using (ra)
            using (rb)
            {
                Thread.Sleep(1500);
                using (var cap = AudioLoopback.TryStart(path, pid, include, extraDevices: [x.Id]))
                {
                    if (cap is null)
                    {
                        Check($"defaultswitch {label}: capture started", false);
                        return;
                    }

                    route = cap.Route;
                    var start = SignalRender.Now;
                    Thread.Sleep(9_000);
                    DefaultDevice.SetPlayback(x.Id);
                    switchAt = SignalRender.Now - start;
                    Thread.Sleep(10_000);
                    DefaultDevice.SetPlayback(original);
                    backAt = SignalRender.Now - start;
                    Thread.Sleep(7_000);
                }
            }

            Console.WriteLine($"      [{label}] route {route}; switched at {switchAt:F1} s, back at {backAt:F1} s");
        }

        var mono = DecodeMono(path);
        File.Delete(path);
        JudgeSwitch($"defaultswitch {label}", mono, switchAt, backAt, seams: []);
    }

    /// <summary>A child process plays the game signal on the default device, following
    /// it (automatic stream routing); the capture is app-only on that child, with the
    /// chat device ticked. Switching the default to the chat device moves the game there
    /// mid-segment: the game must still be heard once, and the extra must drop out.</summary>
    private static void FollowSwitch(Pick pick, AudioLoopback.RenderDevice x, string original, bool simulate)
    {
        string label = simulate ? "defaultswitch follow (Debug hook)" : "defaultswitch follow";
        var path = Path.Combine(Path.GetTempPath(), $"dejavu_follow_{Environment.ProcessId}.mp4");
        double switchAt, backAt;
        string route;
        using (var tap = new LogTap())
        {
            using var child = System.Diagnostics.Process.Start(Environment.ProcessPath!, $"rendertone {SignalRender.FollowDefault} 32")!;
            using var chat = new SignalRender(x.Id, "chat", SpecB(2), SignalRender.Now + 0.5);
            try
            {
                Thread.Sleep(2000);
                using (var cap = AudioLoopback.TryStart(path, child.Id, includeOnly: true, extraDevices: [x.Id]))
                {
                    if (cap is null)
                    {
                        Check($"{label}: capture started", false);
                        return;
                    }

                    route = cap.Route;
                    var start = SignalRender.Now;
                    Thread.Sleep(9_000);
                    if (simulate)
                    {
                        AudioLoopback.DebugDefaultChanged = x.Id;
                    }
                    else
                    {
                        DefaultDevice.SetPlayback(x.Id);
                    }

                    switchAt = SignalRender.Now - start;
                    Thread.Sleep(10_000);
                    if (!simulate)
                    {
                        DefaultDevice.SetPlayback(original);
                    }

                    backAt = SignalRender.Now - start;
                    Thread.Sleep(7_000);
                }
            }
            finally
            {
                child.WaitForExit(15_000);
            }

            Console.WriteLine($"      [{label}] route {route}; switched at {switchAt:F1} s, back at {backAt:F1} s");
            Check($"{label}: the route hears every device (process loopback)", route.Contains("process loopback"), route);
            Check($"{label}: the extra was muted once, on the switch",
                tap.Count("now the default device, already in the system mix") == 1,
                $"{tap.Count("now the default device")} lines");
        }

        var mono = DecodeMono(path);
        File.Delete(path);
        var v = Analyze(label, mono, SpecA(2), SpecB(2), seams: []);
        const double lag = 0.3;
        // A stream that moves device jumps in time: follow the schedule per stretch.
        var candA = MixJudge.Candidates(mono, true, SpecA(2).MarkerAmp);
        var candB = MixJudge.Candidates(mono, false, SpecB(2).MarkerAmp);
        List<MixJudge.Hit> Within(List<MixJudge.Candidate> c, double from, double to) =>
            MixJudge.Pick(c.Where(x => x.Position / Rate > from && x.Position / Rate < to).ToList(), 2, mono.Length).Hits;
        var gameBefore = Within(candA, 0, switchAt - 0.5);
        var gameDuring = Within(candA, switchAt + 0.5 - lag, backAt - lag);
        var chatAfter = Within(candB, switchAt + 0.5, double.MaxValue);
        var clicks = v.Clicks.Select(c => c / (double)Rate)
            .Where(t => Math.Abs(t - switchAt) > 0.5 && Math.Abs(t - backAt) > 0.5).ToList();
        string Describe(List<MixJudge.Hit> hits) =>
            string.Join(" ", hits.Select(h => $"{h.Position / Rate:F1}s:{h.Gain:F2}/{h.SecondRatio:F2}@{h.SecondOffsetMs:F0}ms"));
        Console.WriteLine($"      [{label}] game before: {Describe(gameBefore)}");
        Console.WriteLine($"      [{label}] game while the chat device is the default: {Describe(gameDuring)}");
        if (!simulate)
        {
            // Sonar's Gaming device is turned down, its Chat device is not: the game's
            // level tells which device its stream is on.
            double before = gameBefore.Count == 0 ? 0 : gameBefore.Average(h => h.Gain);
            double during = gameDuring.Count == 0 ? 0 : gameDuring.Average(h => h.Gain);
            Console.WriteLine($"      [{label}] game level before {before:F2}, while switched {during:F2}");
            Check($"{label}: the game's stream really moved with the default", gameDuring.Count > 0 && Math.Abs(during - before) > 0.2 * Math.Max(before, during),
                $"level {before:F2} -> {during:F2}");
        }

        Check($"{label}: game heard once before the switch", gameBefore.Count >= 3 && gameBefore.All(h => h.SecondRatio < MixJudge.SecondPeakLimit),
            Describe(gameBefore));
        Check($"{label}: game heard once, not twice, once it moved to the ticked device",
            gameDuring.Count >= 3 && gameDuring.All(h => h.SecondRatio < MixJudge.SecondPeakLimit), Describe(gameDuring));
        Check($"{label}: the ticked device's own stream is left out from the switch", chatAfter.Count == 0, Describe(chatAfter));
        // Windows re-routing the game's own stream glitches it at the switch itself.
        Check($"{label}: no clicks away from the switches (±0.5 s)", clicks.Count == 0,
            $"{clicks.Count}{(clicks.Count == 0 ? "" : " at " + string.Join(", ", clicks.Take(8).Select(t => $"{t:F2}s")))}; "
            + $"all: {v.Checks.First(c => c.Key == "no clicks").Detail}");
    }

    private static void SwitchThroughBuffer(Pick pick, AudioLoopback.RenderDevice x, string original)
    {
        var config = TestConfig([x.Id]);
        // The endpoint route: the one a default change could matter to (see SwitchOnce).
        config.AudioExclude = [];
        Directory.CreateDirectory(config.SaveRoot);
        string? failure = null;
        string clip;
        double switchAt, backAt;
        List<double>? seams;
        using (var tap = new LogTap())
        {
            var (ra, rb) = StartSignals(pick, period: 2);
            using (ra)
            using (rb)
            {
                Thread.Sleep(1500);
                using var ticker = new FrameTicker();
                using var buffer = new ReplayBuffer(config, segmentSeconds: 60);
                buffer.Failed += e => failure = e;
                buffer.Start();
                var start = SignalRender.Now;
                Thread.Sleep(10_000);
                DefaultDevice.SetPlayback(x.Id);
                switchAt = SignalRender.Now - start;
                Thread.Sleep(10_000);
                DefaultDevice.SetPlayback(original);
                backAt = SignalRender.Now - start;
                Thread.Sleep(7_000);
                clip = buffer.Save("audiocheck");
            }

            Check("defaultswitch buffer: recorder reported no failure", failure is null, failure);
            seams = SeamsFromLog(tap);
            var routes = tap.Lines.Where(l => l.StartsWith("audio: ")).ToList();
            Console.WriteLine("      [defaultswitch buffer] routes: " + string.Join(" | ", routes));
        }

        var mono = DecodeMono(clip);
        File.Delete(clip);
        // The saved clip starts a moment after Start (activation); markers carry their
        // own times, so judge by the clip's own clock with a second of slack each side.
        JudgeSwitch("defaultswitch buffer", mono, switchAt, backAt, seams);
    }

    /// <summary>Before the switch, during it, after switching back: the chat device's
    /// markers must show one peak each throughout.</summary>
    private static void JudgeSwitch(string label, float[] mono, double switchAt, double backAt, IReadOnlyList<double>? seams)
    {
        var r = Analyze(label, mono, SpecA(2), SpecB(2), seams);
        // Clip time runs slightly behind wall time since the capture starts after us.
        double clipLag = 0.3;
        var during = r.B.Hits.Where(h => h.Position / Rate > switchAt + 1 - clipLag && h.Position / Rate < backAt - clipLag).ToList();
        var before = r.B.Hits.Where(h => h.Position / Rate < switchAt - 1).ToList();
        var after = r.B.Hits.Where(h => h.Position / Rate > backAt + 1).ToList();
        string Describe(List<MixJudge.Hit> hits) =>
            string.Join(" ", hits.Select(h => $"{h.Position / Rate:F1}s:{h.Gain:F2}/{h.SecondRatio:F2}@{h.SecondOffsetMs:F0}ms"));
        Console.WriteLine($"      [{label}] chat markers before: {Describe(before)}");
        Console.WriteLine($"      [{label}] chat markers while it is the default: {Describe(during)}");
        Console.WriteLine($"      [{label}] chat markers after switching back: {Describe(after)}");
        Check($"{label}: chat heard once before the switch", before.Count >= 3 && before.All(h => h.SecondRatio < 0.25));
        Check($"{label}: chat heard, once, while it is the default",
            during.Count >= 3 && during.All(h => h.SecondRatio < 0.25), Describe(during));
        Check($"{label}: chat heard once after switching back", after.Count >= 2 && after.All(h => h.SecondRatio < 0.25), Describe(after));
        double gainBefore = before.Count == 0 ? 0 : before.Average(h => h.Gain);
        Check($"{label}: chat not louder while it is the default (not summed twice in step)",
            during.All(h => h.Gain < gainBefore * 1.3), $"before {gainBefore:F2}, during {Describe(during)}");
    }
}
