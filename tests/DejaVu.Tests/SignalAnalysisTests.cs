/// <summary>
/// The live multi-device checks judge real clips with <see cref="MixJudge"/>; these prove
/// it on synthetic audio first. A perfect two-device mix must pass every check with the
/// exact marker count, also under loud outside sound, and each defect the live checks
/// look for must be caught by its own check and by no other:
///
///   dropped block   the chat stream silent for 20 ms (what a mixer underrun leaves;
///                   the mixer fades its edges, so it is not also a click)
///   click           a single-sample spike
///   slip            one chat sample lost (a trim or a repeat moves the tone's phase)
///   pitch wobble    the chat tone bent ±0.25 % at 0.2 Hz
///   doubled copy    the chat device heard a second time, 12 ms later
///   offset jump     the chat stream 12 ms later from mid-clip on (15 periods of its
///                   tone, so the tone itself stays continuous)
///
/// Headless, so CI runs them.
/// </summary>
internal static class SignalAnalysisTests
{
    private const int Rate = Markers.Rate;
    private const double Seconds = 30;

    public static readonly SignalSpec A = new()
    {
        ToneHz = 440, ToneAmp = 0.0015, MarkerAmp = 0.0015, MarkerUp = true, PeriodSec = 2, PhaseSec = 0.5,
    };

    public static readonly SignalSpec B = new()
    {
        ToneHz = 1250, ToneAmp = 0.0015, MarkerAmp = 0.0015, MarkerUp = false, PeriodSec = 2, PhaseSec = 1.5,
    };

    // Markers wholly inside the clip: A at 0.5 + 2k, B at 1.5 + 2k, each 0.1 s long.
    private const int ExpectedMarkers = 15;

    private sealed record Defects
    {
        public double DropoutAt { get; init; } = -1;
        public double ClickAt { get; init; } = -1;
        public double SlipAt { get; init; } = -1;
        public double Vibrato { get; init; }
        public double DoubleMs { get; init; }
        public double JumpAt { get; init; } = -1;
        public bool Noise { get; init; }
        public double SeamAt { get; init; } = -1;
    }

    public static void Run(DriftTests.Checker check)
    {
        foreach (var (label, defects) in new[]
        {
            ("perfect mix", new Defects()),
            ("perfect mix under loud outside sound", new Defects { Noise = true }),
        })
        {
            var v = MixJudge.Judge(Synthesize(defects), A, B, seams: []);
            Console.WriteLine($"      analysis {label}: " + string.Join("; ", v.Checks.Select(c => $"{c.Key} {(c.Ok ? "ok" : "FAIL")} ({c.Detail})")));
            check($"analysis: a {label} passes every check", !v.Failed.Any(), string.Join(", ", v.Failed));
            check($"analysis: a {label} has exactly the markers played",
                v.A.Hits.Count == ExpectedMarkers && v.B.Hits.Count == ExpectedMarkers && v.A.Missing == 0 && v.B.Missing == 0,
                $"A {v.A.Hits.Count} ({v.A.Missing} missing), B {v.B.Hits.Count} ({v.B.Missing} missing), {ExpectedMarkers} played each");
            check($"analysis: a {label} reads the true gains",
                v.A.Hits.All(h => Math.Abs(h.Gain - 0.2) < 0.02) && v.B.Hits.All(h => Math.Abs(h.Gain - 1) < 0.05),
                $"A {v.A.Hits.Min(h => h.Gain):F3}..{v.A.Hits.Max(h => h.Gain):F3}, B {v.B.Hits.Min(h => h.Gain):F3}..{v.B.Hits.Max(h => h.Gain):F3}");
            check($"analysis: a {label} reads offset 0",
                v.Offsets.Count >= ExpectedMarkers - 1 && v.Offsets.All(o => Math.Abs(o) < 0.1),
                $"{v.Offsets.Count} pairs, {v.Offsets.DefaultIfEmpty(0).Min():F3}..{v.Offsets.DefaultIfEmpty(0).Max():F3} ms");
        }

        foreach (var (label, defects, key) in new[]
        {
            ("dropped block", new Defects { DropoutAt = 12.2 }, "chat no dropouts"),
            ("click", new Defects { ClickAt = 16.3 }, "no clicks"),
            ("slipped sample", new Defects { SlipAt = 8.25 }, "chat no slips"),
            ("pitch wobble", new Defects { Vibrato = 0.0025 }, "chat pitch steady"),
            ("doubled copy", new Defects { DoubleMs = 12 }, "one peak per marker"),
            ("offset jump", new Defects { JumpAt = 20.2 }, "offset steady"),
        })
        {
            var v = MixJudge.Judge(Synthesize(defects), A, B, seams: []);
            var failed = v.Failed.ToList();
            Console.WriteLine($"      analysis {label}: " + string.Join("; ", v.Checks.Where(c => !c.Ok || c.Key == key).Select(c => $"{c.Key} {(c.Ok ? "ok" : "FAIL")} ({c.Detail})")));
            check($"analysis: a {label} is caught by \"{key}\" and nothing else",
                failed.Count == 1 && failed[0] == key, failed.Count == 0 ? "nothing caught it" : string.Join(", ", failed));
        }

        // A segment seam repeats a stretch of the whole mix (the 1.4 path does too): both
        // devices move together, with a click and a slip there. Judged apart, it is none
        // of those, whether the seam is known or estimated from the markers.
        {
            var seam = new Defects { SeamAt = 20.2 };
            var v = MixJudge.Judge(Synthesize(seam), A, B, seams: [20.2]);
            check("analysis: a known seam's repeat is not judged", !v.Failed.Any(), string.Join(", ", v.Failed));
            var est = MixJudge.Judge(Synthesize(seam), A, B, seams: null);
            check("analysis: a seam is estimated from the marker steps and not judged", !est.Failed.Any() && est.Excluded.Count >= 1,
                $"{string.Join(", ", est.Failed)}; windows {string.Join(" ", est.Excluded.Select(e => $"{e.From:F1}-{e.To:F1}"))}");
            var clean = MixJudge.Judge(Synthesize(new Defects()), A, B, seams: null);
            check("analysis: no seam is estimated in an unbroken mix", clean.Excluded.Count == 0, $"{clean.Excluded.Count} windows");

            // Only the chat device moving at a seam is a real fault (its delay differs per
            // segment): the seam window must not hide it.
            var shifted = MixJudge.Judge(Synthesize(new Defects { JumpAt = 20.2 }), A, B, seams: [20.2]);
            check("analysis: the chat device alone moving at a seam is still caught", !shifted.Ok("offset steady"),
                string.Join(", ", shifted.Failed));
        }

        // Nothing on the chat device: no markers invented from the game's or the tones.
        {
            var v = MixJudge.Judge(Synthesize(new Defects(), withB: false), A, B, seams: [], expectB: false);
            check("analysis: a mix without the chat device shows no chat markers", v.B.Hits.Count == 0, $"{v.B.Hits.Count}");
            check("analysis: the game alone passes its checks", !v.Failed.Any(), string.Join(", ", v.Failed));
        }
    }

    private static float[] Synthesize(Defects d, bool withB = true)
    {
        int n = (int)(Seconds * Rate);
        var a = new double[n];
        var b = new double[n];
        var rng = new Random(7);

        double phaseB = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate;
            a[i] = 0.2 * A.ToneAmp * Math.Sin(2 * Math.PI * A.ToneHz * t);
            double fB = B.ToneHz * (1 + d.Vibrato * Math.Sin(2 * Math.PI * 0.2 * t));
            b[i] = B.ToneAmp * Math.Sin(phaseB);
            phaseB += 2 * Math.PI * fB / Rate;
        }

        void AddMarker(double[] x, SignalSpec s, double at, double gain)
        {
            var m = Markers.Template(s.MarkerUp);
            int start = (int)Math.Round(at * Rate);
            for (int i = 0; i < m.Length && start + i < n; i++)
            {
                x[start + i] += gain * s.MarkerAmp * m[i].Real;
            }
        }

        for (int k = 0; k < ExpectedMarkers; k++)
        {
            AddMarker(a, A, A.PhaseSec + k * A.PeriodSec, 0.2);
            AddMarker(b, B, B.PhaseSec + k * B.PeriodSec, 1);
        }

        double[] Delayed(double[] x, double ms)
        {
            int shift = (int)Math.Round(ms * Rate / 1000);
            var y = new double[n];
            Array.Copy(x, 0, y, shift, n - shift);
            return y;
        }

        if (d.DoubleMs > 0)
        {
            var late = Delayed(b, d.DoubleMs);
            for (int i = 0; i < n; i++)
            {
                b[i] += 0.7 * late[i];
            }
        }

        if (d.JumpAt > 0)
        {
            // 12 ms is 15 periods of 1250 Hz: the tone runs on unbroken, the markers move.
            var late = Delayed(b, 12);
            int from = (int)(d.JumpAt * Rate), fade = Rate / 50;
            for (int i = from; i < n; i++)
            {
                double w = Math.Min(1.0, (i - from) / (double)fade);
                b[i] = (1 - w) * b[i] + w * late[i];
            }
        }

        if (d.DropoutAt > 0)
        {
            int from = (int)(d.DropoutAt * Rate), ramp = Rate / 500, hold = Rate / 50;
            for (int i = 0; i < hold + 2 * ramp; i++)
            {
                double g = i < ramp ? 0.5 + 0.5 * Math.Cos(Math.PI * i / ramp)
                    : i < ramp + hold ? 0
                    : 0.5 - 0.5 * Math.Cos(Math.PI * (i - ramp - hold) / ramp);
                b[from + i] *= g;
            }
        }

        if (d.Noise)
        {
            // Outside sound in the markers' own band: bursts of many random partials.
            var freqs = Enumerable.Range(0, 40).Select(_ => 2200 + rng.NextDouble() * 2600).ToArray();
            var phases = freqs.Select(_ => rng.NextDouble() * 2 * Math.PI).ToArray();
            double amp = 20.0 / short.MaxValue * Math.Sqrt(2.0 / freqs.Length);
            for (double at = 0.9; at + 0.4 < Seconds; at += 3)
            {
                int from = (int)(at * Rate), len = (int)(0.4 * Rate), edge = Rate / 50;
                for (int i = 0; i < len; i++)
                {
                    double env = Math.Min(1.0, Math.Min(i, len - 1 - i) / (double)edge);
                    double t = (double)(from + i) / Rate, v = 0;
                    for (int f = 0; f < freqs.Length; f++)
                    {
                        v += Math.Sin(2 * Math.PI * freqs[f] * t + phases[f]);
                    }

                    a[from + i] += env * amp * v;
                }
            }
        }

        var list = new List<float>(n);
        int slip = d.SlipAt > 0 ? (int)(d.SlipAt * Rate) : -1;
        int click = d.ClickAt > 0 ? (int)(d.ClickAt * Rate) : -1;
        int seamAt = d.SeamAt > 0 ? (int)(d.SeamAt * Rate) : -1;
        for (int i = 0; i < n; i++)
        {
            // The slip loses one chat sample: everything after it on that device is one
            // sample early, the game is untouched.
            double bv = withB ? (slip >= 0 && i >= slip ? (i + 1 < n ? b[i + 1] : 0) : b[i]) : 0;
            double v = (a[i] + bv) * short.MaxValue + (rng.NextDouble() - 0.5) * 2;
            if (i == click)
            {
                v += 400;
            }

            if (i == seamAt)
            {
                // 12 ms of everything played again.
                list.AddRange(list.GetRange(list.Count - Rate * 12 / 1000, Rate * 12 / 1000));
            }

            list.Add((float)Math.Round(v));
        }

        return list.ToArray();
    }
}
