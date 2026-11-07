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
        foreach (double sourceRate in new[] { 48010.0, 47990.0, 48048.0 })
        {
            var r = Simulate(sourceRate, minutes: 25);
            tenHzFast ??= r;
            Console.WriteLine(
                $"      drift {sourceRate:F0} Hz: offset {r.EarlyLatencyMs:F2} -> {r.LateLatencyMs:F2} ms over 25 min, "
                + $"range {r.MinLatencyMs:F2}..{r.MaxLatencyMs:F2} ms, ratio {r.FinalRatio:F6}, "
                + $"max 2nd diff {r.MaxSecondDiff:F1}, max backlog {r.MaxBacklogFrames} frames");
            string tag = $"{sourceRate:F0} Hz source into a {Rate} Hz mix";
            check($"{tag}: never dropped a frame", r.Dropped == 0, $"dropped {r.Dropped}");
            check($"{tag}: never ran dry after priming", r.Underruns == 0, $"underruns {r.Underruns}");
            check($"{tag}: offset does not grow over 25 min",
                Math.Abs(r.LateLatencyMs - r.EarlyLatencyMs) < 3,
                $"minute 1-2 {r.EarlyLatencyMs:F2} ms, minute 24-25 {r.LateLatencyMs:F2} ms");
            check($"{tag}: offset stays small", r.MaxLatencyMs < 80 && r.MinLatencyMs > 0,
                $"range {r.MinLatencyMs:F2} .. {r.MaxLatencyMs:F2} ms");
            check($"{tag}: no clicks", r.MaxSecondDiff < 40, $"max second difference {r.MaxSecondDiff:F1}");
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
        {
            var r = Simulate(48010, minutes: 3, gapAtSeconds: 60, gapSeconds: 5);
            check("a quiet device does not throw and resumes", r.Completed);
            check("a quiet device's gap plays as silence", r.GapSilent, $"max |sample| in gap {r.GapPeak}");
            check("sync after a quiet gap matches sync before it",
                Math.Abs(r.LateLatencyMs - r.EarlyLatencyMs) < 3,
                $"before {r.EarlyLatencyMs:F2} ms, after {r.LateLatencyMs:F2} ms");
            check("no clicks across a quiet gap's resume", r.MaxSecondDiff < 40, $"max second difference {r.MaxSecondDiff:F1}");
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
            mixer.MixInto(mix);
            check("loud plus loud saturates", mix.All(s => s == short.MaxValue));
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
        int MaxBacklogFrames, bool GapSilent, int GapPeak);

    /// <summary>One-millisecond ticks of simulated time. The source delivers packets at
    /// jittered 7-13 ms intervals, the mix pulls 10 ms at jittered 8-12 ms intervals —
    /// the shape of two WASAPI loopback clients polled by one pump.</summary>
    private static Result Simulate(
        double sourceRate, int minutes, int gapAtSeconds = -1, int gapSeconds = 0)
    {
        var rand = new Random(11);
        var mixer = new DriftMixer(2, Rate);
        long totalMs = minutes * 60_000L;
        long gapStart = gapAtSeconds * 1000L, gapEnd = gapStart + gapSeconds * 1000L;

        double sourceDue = 0;       // fractional source frames owed by elapsed time
        long sourceFrame = 0;       // next source frame index
        int nextPacketMs = 10, nextPullMs = 10;
        long outFrame = 0;          // mix frames written so far
        double mixDue = 0;
        var packet = new short[4096 * 2];
        var mix = new short[4096 * 2];

        long underrunsAtPriming = -1;
        double earlySum = 0, lateSum = 0, minLat = double.MaxValue, maxLat = double.MinValue;
        int earlyN = 0, lateN = 0, maxBacklog = 0, gapPeak = 0;
        double maxSecond = 0;
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
                nextPacketMs = rand.Next(7, 14);
                int frames = (int)sourceDue;
                sourceDue -= frames;
                if (!inGap && frames > 0)
                {
                    for (int i = 0; i < frames; i++, sourceFrame++)
                    {
                        double t = sourceFrame / sourceRate;
                        packet[2 * i] = (short)Math.Round(Math.Sin(2 * Math.PI * SineHz * t) * SineAmp);
                        packet[2 * i + 1] = (short)Math.Round((sourceFrame % RampPeriod) * RampScale - 15000);
                    }

                    mixer.Queue(packet.AsSpan(0, frames * 2));
                }
            }

            mixDue += Rate / 1000.0;
            if (--nextPullMs <= 0)
            {
                nextPullMs = rand.Next(8, 13);
                int frames = (int)mixDue;
                mixDue -= frames;
                maxBacklog = Math.Max(maxBacklog, mixer.BufferedFrames);
                var span = mix.AsSpan(0, frames * 2);
                span.Clear();
                mixer.MixInto(span);
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
            gapPeak == 0, gapPeak);
    }
}
