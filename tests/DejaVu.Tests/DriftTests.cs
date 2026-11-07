using DejaVu;

/// <summary>
/// Extra loopback devices (#11) each run on their own clock. These simulate a second
/// device whose clock is off from the main stream's, over a whole 25-minute buffer,
/// and check that the mix stays in sync with no growing offset and no clicks.
/// </summary>
internal static class DriftTests
{
    private const int Rate = 48000;

    // The right channel carries a sawtooth that encodes the producer's own frame
    // index, so the output tells exactly which source instant it is playing: a ramp
    // survives cubic interpolation unchanged. The left channel is a sine, whose second
    // difference stays tiny unless a frame is dropped, repeated or jumps.
    private const int RampPeriod = 24000;
    private const double RampScale = 30000.0 / RampPeriod;
    private const double SineHz = 440;
    private const double SineAmp = 8000;

    public delegate void Checker(string name, bool ok, string? detail = null);

    public static void Run(Checker check)
    {
        Result? tenHzFast = null;
        foreach (var (sourceRate, stamps) in new[] { (48010.0, false), (47990.0, false), (48048.0, false), (48010.0, true), (47990.0, true) })
        {
            var r = Simulate(sourceRate, minutes: 25, stamps: stamps);
            tenHzFast ??= r;
            Console.WriteLine(
                $"      drift {sourceRate:F0} Hz{(stamps ? ", timestamped" : "")}: offset {r.EarlyLatencyMs:F2} -> {r.LateLatencyMs:F2} ms over 25 min, "
                + $"range {r.MinLatencyMs:F2}..{r.MaxLatencyMs:F2} ms, ratio {r.FinalRatio:F6}, "
                + $"max 2nd diff {r.MaxSecondDiff:F1}, max backlog {r.MaxBacklogFrames} frames");
            string tag = $"{sourceRate:F0} Hz source into a {Rate} Hz mix{(stamps ? " (timestamped)" : "")}";
            check($"{tag}: never dropped a frame", r.Dropped == 0, $"dropped {r.Dropped}");
            check($"{tag}: never ran dry after priming", r.Underruns == 0, $"underruns {r.Underruns}");
            check($"{tag}: offset does not grow over 25 min",
                Math.Abs(r.LateLatencyMs - r.EarlyLatencyMs) < 3,
                $"minute 1-2 {r.EarlyLatencyMs:F2} ms, minute 24-25 {r.LateLatencyMs:F2} ms");
            check($"{tag}: offset stays small", r.MaxLatencyMs < 80 && r.MinLatencyMs > 0,
                $"range {r.MinLatencyMs:F2} .. {r.MaxLatencyMs:F2} ms");
            check($"{tag}: no clicks", r.MaxSecondDiff < 40, $"max second difference {r.MaxSecondDiff:F1}");
            // Every segment opens a new mixer: steering hard while it settles bends the
            // pitch for a moment once a minute.
            check($"{tag}: no pitch bend while a new mixer settles",
                r.StartSteer < 0.001, $"steered up to {r.StartSteer * 100:F3} % off the clock gap in the first 3 s");
            check($"{tag}: ratio locks onto the clock gap",
                Math.Abs(r.FinalRatio - sourceRate / Rate) < 5e-5,
                $"ratio {r.FinalRatio:F6}, expected {sourceRate / Rate:F6}");
        }

        // Control: without correction the same 10 Hz gap piles up 12.5 s of backlog over
        // 25 minutes. The mixer must bound it (so the test above is not vacuous).
        {
            var r = tenHzFast!;
            check("drift correction holds backlog far below the uncorrected 12.5 s",
                r.MaxBacklogFrames < Rate / 10, $"max backlog {r.MaxBacklogFrames} frames");
        }

        // Loopback goes quiet when the device renders nothing: no packets at all.
        // A gap must cost silence, never an exception, and sync must come back the
        // same afterwards.
        foreach (bool stamps in new[] { false, true })
        {
            string tag = stamps ? " (timestamped)" : "";
            var r = Simulate(48010, minutes: 3, gapAtSeconds: 60, gapSeconds: 5, stamps: stamps);
            check($"a quiet device does not throw and resumes{tag}", r.Completed);
            check($"a quiet device's gap plays as silence{tag}", r.GapSilent, $"max |sample| in gap {r.GapPeak}");
            check($"sync after a quiet gap matches sync before it{tag}",
                Math.Abs(r.LateLatencyMs - r.EarlyLatencyMs) < (stamps ? 1 : 3),
                $"before {r.EarlyLatencyMs:F2} ms, after {r.LateLatencyMs:F2} ms");
            check($"no clicks across a quiet gap's resume{tag}", r.MaxSecondDiff < 40, $"max second difference {r.MaxSecondDiff:F1}");
        }

        // Every segment opens the devices afresh, and where the extra's 10 ms packets
        // fall against the main stream's pulls is new each time. Counted in frames, the
        // cushion then sits anywhere in a packet's width: the chat device moved 10-20 ms
        // against the game from one minute to the next in real clips. Held against
        // capture timestamps, the delay must be the same whatever the packet phase, and
        // from the first second on.
        {
            var plain = new List<double>();
            var stamped = new List<(double Start, double Late)>();
            for (int phase = 0; phase < 10; phase++)
            {
                plain.Add(Simulate(48010, minutes: 0.2, phaseMs: phase, fixedCadence: true).StartLatencyMs);
                var r = Simulate(48010, minutes: 0.2, phaseMs: phase, fixedCadence: true, stamps: true);
                stamped.Add((r.StartLatencyMs, r.LateLatencyMs));
            }

            Console.WriteLine($"      packet phase 0-9 ms: delay by frame count {string.Join(" ", plain.Select(v => v.ToString("F1")))} ms; "
                + $"by timestamps {string.Join(" ", stamped.Select(v => v.Start.ToString("F1")))} ms");
            check("control: counted in frames, the delay depends on packet phase (so the test can tell)",
                plain.Max() - plain.Min() > 3, $"{plain.Min():F2}..{plain.Max():F2} ms");
            check("timestamped: the delay does not depend on packet phase",
                stamped.Max(v => v.Start) - stamped.Min(v => v.Start) < 1,
                $"{stamped.Min(v => v.Start):F2}..{stamped.Max(v => v.Start):F2} ms");
            check("timestamped: the delay is the cushion from the first second on",
                stamped.All(v => Math.Abs(v.Start - 40) < 2 && Math.Abs(v.Late - v.Start) < 1),
                string.Join(" ", stamped.Select(v => $"{v.Start:F2}/{v.Late:F2}")));
        }

        // A device that becomes one the main stream hears is muted at once, without a click.
        {
            var mixer = new DriftMixer(2, Rate);
            var sine = new short[Rate / 5 * 2];
            for (int i = 0; i < sine.Length / 2; i++)
            {
                sine[2 * i] = sine[2 * i + 1] = (short)Math.Round(SineAmp * Math.Sin(2 * Math.PI * SineHz * i / Rate));
            }

            mixer.Queue(sine);
            var mix = new short[Rate / 50 * 2];
            mixer.MixInto(mix);
            var tail = mix[^2];
            mixer.Mute();
            var after = new short[Rate / 50 * 2];
            mixer.MixInto(after);
            var left = new[] { (double)mix[^4], tail }.Concat(Enumerable.Range(0, after.Length / 2).Select(i => (double)after[2 * i])).ToArray();
            double worst = Enumerable.Range(2, left.Length - 2).Max(i => Math.Abs(left[i] - 2 * left[i - 1] + left[i - 2]));
            check("a muted device fades out, no click", worst < 40, $"max 2nd diff {worst:F1}");
            check("a muted device is silent after 5 ms", after.Skip(Rate / 200 * 2 + 2).All(v => v == 0));
            check("a muted device keeps being drained", mixer.BufferedFrames < sine.Length / 2 - Rate / 50 * 2 + 10, $"{mixer.BufferedFrames} buffered");
        }

        // The main stream stalls (its pump suspended): the backlog is capped, never
        // unbounded memory, and mixing carries on afterwards.
        {
            var mixer = new DriftMixer(2, Rate);
            var packet = new short[480 * 2];
            for (int i = 0; i < 300; i++)
            {
                mixer.Queue(packet);
            }

            check("a stalled consumer caps the backlog", mixer.BufferedFrames <= mixer.CapacityFrames,
                $"{mixer.BufferedFrames} > {mixer.CapacityFrames}");
            check("trimming is counted", mixer.DroppedFrames > 0);

            // Right at the ceiling, the next packet trims back to the cushion.
            var fresh = new DriftMixer(2, Rate);
            while (fresh.BufferedFrames + 480 <= fresh.CapacityFrames)
            {
                fresh.Queue(packet);
            }

            fresh.Queue(packet);
            check("a capped backlog is trimmed back to the cushion", fresh.BufferedFrames == fresh.TargetFrames,
                $"{fresh.BufferedFrames} frames, cushion {fresh.TargetFrames}");

            var after = new short[480 * 2];
            mixer.MixInto(after);
            check("mixing carries on after a trim", mixer.Primed);
        }

        // Silence packets mix as nothing; the mix underneath is untouched.
        {
            var mixer = new DriftMixer(2, Rate);
            mixer.QueueSilence(Rate / 10);
            var mix = new short[480 * 2];
            Array.Fill(mix, (short)1234);
            mixer.MixInto(mix);
            check("queued silence leaves the main mix untouched", mix.All(s => s == 1234));
        }

        // Saturation, not wraparound, when both streams are loud.
        {
            var mixer = new DriftMixer(2, Rate);
            var loud = new short[Rate / 10 * 2];
            Array.Fill(loud, (short)30000);
            mixer.Queue(loud);
            var mix = new short[480 * 2];
            Array.Fill(mix, (short)30000);
            mixer.MixInto(mix);  // past the 5 ms fade-in
            Array.Fill(mix, (short)30000);
            mixer.MixInto(mix);
            check("loud plus loud saturates", mix.All(s => s == short.MaxValue));
        }

        // A segment starts, or a quiet device wakes, mid-waveform: the first sample out
        // may be anywhere on a loud sine. It must fade in, not step in (a click).
        {
            var mixer = new DriftMixer(2, Rate);
            var sine = new short[Rate / 5 * 2];
            for (int i = 0; i < sine.Length / 2; i++)
            {
                // Starts at the crest: the worst onset there is.
                sine[2 * i] = sine[2 * i + 1] = (short)Math.Round(SineAmp * Math.Cos(2 * Math.PI * SineHz * i / Rate));
            }

            mixer.Queue(sine);
            var mix = new short[Rate / 10 * 2];
            mixer.MixInto(mix);
            var left = Enumerable.Range(0, mix.Length / 2).Select(i => (double)mix[2 * i]).ToArray();
            double worst = Enumerable.Range(2, left.Length - 2).Max(i => Math.Abs(left[i] - 2 * left[i - 1] + left[i - 2]));
            check("onset after priming fades in, no click", Math.Abs(left[0]) < 200 && worst < 40,
                $"first sample {left[0]}, max 2nd diff {worst:F1}");
            check("onset reaches full level within 10 ms", left.Skip(Rate / 100).Take(Rate / 50).Max() > SineAmp * 0.95);
        }

        // The source runs dry mid-pull: what is left must fade out, not stop dead.
        {
            var mixer = new DriftMixer(2, Rate);
            var sine = new short[(mixer.TargetFrames + 960) * 2];
            for (int i = 0; i < sine.Length / 2; i++)
            {
                sine[2 * i] = sine[2 * i + 1] = (short)Math.Round(SineAmp * Math.Sin(2 * Math.PI * SineHz * i / Rate));
            }

            mixer.Queue(sine);
            var mix = new short[(mixer.TargetFrames + 2400) * 2];
            mixer.MixInto(mix);
            var left = Enumerable.Range(0, mix.Length / 2).Select(i => (double)mix[2 * i]).ToArray();
            double worst = Enumerable.Range(2, left.Length - 2).Max(i => Math.Abs(left[i] - 2 * left[i - 1] + left[i - 2]));
            check("running dry fades out, no click", worst < 40, $"max 2nd diff {worst:F1}");
            check("running dry is counted", mixer.Underruns == 1 && !mixer.Primed);
        }

        // Nothing queued yet: mixing is a no-op until the cushion has built up.
        {
            var mixer = new DriftMixer(2, Rate);
            mixer.Queue(new short[100 * 2].Select(_ => (short)5000).ToArray());
            var mix = new short[480 * 2];
            mixer.MixInto(mix);
            check("no output before the cushion fills", mix.All(s => s == 0));
        }
    }

    private sealed record Result(
        bool Completed, long Dropped, long Underruns, double EarlyLatencyMs, double LateLatencyMs,
        double MinLatencyMs, double MaxLatencyMs, double MaxSecondDiff, double FinalRatio,
        int MaxBacklogFrames, bool GapSilent, int GapPeak, double StartLatencyMs, double StartSteer);

    /// <summary>One-millisecond ticks of simulated time. The source delivers packets at
    /// jittered 7-13 ms intervals, the mix pulls 10 ms at jittered 8-12 ms intervals —
    /// the shape of two WASAPI loopback clients polled by one pump.</summary>
    private static Result Simulate(
        double sourceRate, double minutes, int gapAtSeconds = -1, int gapSeconds = 0,
        bool stamps = false, int phaseMs = 0, bool fixedCadence = false)
    {
        var rand = new Random(11);
        var mixer = new DriftMixer(2, Rate);
        long totalMs = (long)(minutes * 60_000L);
        long gapStart = gapAtSeconds * 1000L, gapEnd = gapStart + gapSeconds * 1000L;

        double sourceDue = 0;       // fractional source frames owed by elapsed time
        long sourceFrame = 0;       // next source frame index
        int nextPacketMs = 10 + phaseMs, nextPullMs = 10;
        long outFrame = 0;          // mix frames written so far
        double mixDue = 0;
        var packet = new short[4096 * 2];
        var mix = new short[4096 * 2];

        long underrunsAtPriming = -1;
        double earlySum = 0, lateSum = 0, minLat = double.MaxValue, maxLat = double.MinValue, startSum = 0;
        int earlyN = 0, lateN = 0, maxBacklog = 0, gapPeak = 0, startN = 0;
        double maxSecond = 0, startSteer = 0;
        short l1 = 0, l2 = 0;
        int continuous = 0;         // consecutive non-silent output frames, for the click check
        double lastRamp = double.NaN;
        long lastGapUnderruns = 0;

        double earlyFrom = gapAtSeconds >= 0 ? gapAtSeconds - 30 : 60;
        double earlyTo = gapAtSeconds >= 0 ? gapAtSeconds : 120;
        double lateFrom = gapAtSeconds >= 0 ? (gapEnd / 1000.0) + 30 : minutes * 60 - 60;

        for (long ms = 1; ms <= totalMs; ms++)
        {
            bool inGap = ms >= gapStart && ms < gapEnd;
            sourceDue += sourceRate / 1000.0;
            if (inGap)
            {
                // Loopback delivers nothing while the device is idle: the source's
                // timeline moves on, its frames are simply never handed over.
                long skip = (long)sourceDue;
                sourceFrame += skip;
                sourceDue -= skip;
            }

            if (--nextPacketMs <= 0)
            {
                nextPacketMs = fixedCadence ? 10 : rand.Next(7, 14);
                int frames = (int)sourceDue;
                sourceDue -= frames;
                if (!inGap && frames > 0)
                {
                    // The source clock IS real time at its true rate: its frame n was
                    // captured at n / sourceRate seconds.
                    double time = stamps ? sourceFrame / sourceRate : double.NaN;
                    for (int i = 0; i < frames; i++, sourceFrame++)
                    {
                        double t = sourceFrame / sourceRate;
                        packet[2 * i] = (short)Math.Round(Math.Sin(2 * Math.PI * SineHz * t) * SineAmp);
                        packet[2 * i + 1] = (short)Math.Round((sourceFrame % RampPeriod) * RampScale - 15000);
                    }

                    mixer.Queue(packet.AsSpan(0, frames * 2), time);
                }
            }

            mixDue += Rate / 1000.0;
            if (--nextPullMs <= 0)
            {
                nextPullMs = fixedCadence ? 10 : rand.Next(8, 13);
                int frames = (int)mixDue;
                mixDue -= frames;
                maxBacklog = Math.Max(maxBacklog, mixer.BufferedFrames);
                var span = mix.AsSpan(0, frames * 2);
                span.Clear();
                mixer.MixInto(span, stamps ? outFrame / (double)Rate : double.NaN);
                if (ms < 3000 && mixer.Primed)
                {
                    startSteer = Math.Max(startSteer, Math.Abs(mixer.Ratio - sourceRate / Rate));
                }

                if (underrunsAtPriming < 0 && mixer.Primed)
                {
                    underrunsAtPriming = mixer.Underruns;
                }

                for (int i = 0; i < frames; i++, outFrame++)
                {
                    short left = span[2 * i], right = span[2 * i + 1];
                    double now = outFrame / (double)Rate;
                    bool silent = left == 0 && right == 0;

                    if (inGap || (ms >= gapEnd && ms < gapEnd + 100 && gapSeconds > 0))
                    {
                        // During the gap (plus the backlog it drains first) only check
                        // that nothing invented is heard once the backlog is gone.
                        if (ms >= gapStart + 200 && inGap)
                        {
                            gapPeak = Math.Max(gapPeak, Math.Max(Math.Abs((int)left), Math.Abs((int)right)));
                        }

                        continuous = 0;
                        continue;
                    }

                    if (silent)
                    {
                        continuous = 0;
                        continue;
                    }

                    if (++continuous >= 3 && now > 1)
                    {
                        maxSecond = Math.Max(maxSecond, Math.Abs(left - 2 * l1 + l2));
                    }

                    l2 = l1;
                    l1 = left;

                    // Recover the source frame now playing from the ramp and turn it
                    // into a latency: mix time minus the source instant it carries.
                    // Interpolating across the sawtooth's wrap yields any value at all,
                    // so only a sample one source frame on from its neighbour counts.
                    double r = (right + 15000) / RampScale;
                    bool onRamp = Math.Abs(r - lastRamp - 1) < 1.0;
                    lastRamp = r;
                    if (!onRamp)
                    {
                        continue;
                    }

                    double estimate = (now - 0.04) * sourceRate;
                    double k = Math.Round((estimate - r) / RampPeriod);
                    double src = k * RampPeriod + r;
                    double latency = (now - src / sourceRate) * 1000;

                    if (now >= 60)
                    {
                        minLat = Math.Min(minLat, latency);
                        maxLat = Math.Max(maxLat, latency);
                    }

                    if (now >= 0.2 && now < 5)
                    {
                        startSum += latency;
                        startN++;
                    }

                    if (now >= earlyFrom && now < earlyTo)
                    {
                        earlySum += latency;
                        earlyN++;
                    }
                    else if (now >= lateFrom)
                    {
                        lateSum += latency;
                        lateN++;
                    }
                }

                if (inGap)
                {
                    lastGapUnderruns = mixer.Underruns;
                }
            }
        }

        // Underruns that belong to priming (and, in the gap run, to the gap itself) are
        // expected; anything beyond those is a dry spell in steady state.
        long steadyUnderruns = gapSeconds > 0
            ? mixer.Underruns - Math.Max(lastGapUnderruns, 0)
            : mixer.Underruns - Math.Max(underrunsAtPriming, 0);

        return new Result(
            true, mixer.DroppedFrames, steadyUnderruns,
            earlyN == 0 ? double.NaN : earlySum / earlyN,
            lateN == 0 ? double.NaN : lateSum / lateN,
            minLat, maxLat, maxSecond, mixer.Ratio, maxBacklog,
            gapPeak == 0, gapPeak, startN == 0 ? double.NaN : startSum / startN, startSteer);
    }
}
