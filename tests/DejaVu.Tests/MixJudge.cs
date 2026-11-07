using System.Numerics;

/// <summary>
/// The verdicts over a recorded mix of signal A (the game device) and signal B (the chat
/// device), as one pure function so <see cref="SignalAnalysisTests"/> can prove every
/// check on synthetic audio before the live modes trust it on real clips.
///
/// Markers are found by a matched filter, then kept only when they are sharp (a real
/// chirp compresses to a 0.5 ms spike; outside sound such as music or a game on the
/// default device makes broad bumps) and when they sit in the slot the render schedule
/// predicts. Without both gates, outside sound once counted 500 markers where 149 were
/// played.
///
/// Segment seams (the buffer's one-minute files joined in a save) repeat or skip a few
/// tens of ms on the 1.4 path too, so every check is judged away from them.
/// </summary>
internal static class MixJudge
{
    private const int Rate = Markers.Rate;

    /// <summary>How far the chat device's offset against the game may wander across a
    /// clip. Both renderers place their markers on the same QPC instants, so the offset is
    /// the recorder's alone; a quarter of the 45 ms at which picture and sound are seen
    /// to part (ITU-R BT.1359).</summary>
    public const double OffsetSpreadLimitMs = 10;

    /// <summary>Largest allowed deviation of the 1-second mean tone frequency from its
    /// median: 0.2 %, 3.5 cents, under the ~5 cent smallest pitch change a trained ear
    /// hears on a pure tone. The drift corrector may steer up to 0.5 % but settles near
    /// the real clock gap (tens of ppm).</summary>
    public const double WobbleLimit = 0.002;

    /// <summary>A 100 ms block whose frequency is this far (relative) off its four
    /// neighbours is a phase step: one slipped or repeated sample moves a 1250 Hz tone's
    /// phase by 0.16 rad, 105-210 ppm over one or two blocks. A slow wobble bends a block
    /// against its neighbours by a few tens of ppm at most.</summary>
    public const double SlipLimit = 80e-6;

    /// <summary>High band (above 8 kHz) amplitude, in LSB, that counts as a click. Every
    /// test signal stays under 5.5 kHz. Raised to four times the band's 99.9th percentile
    /// when outside sound fills it.</summary>
    public const double ClickThreshold = 6;

    /// <summary>A marker may sit this far from where the previous one predicts it: seams
    /// move the timeline by up to ~60 ms. After a missed slot (a device gone for a
    /// while, several seams in a row) the next one is looked for a quarter period wide.</summary>
    public const double SlotMs = 100;

    /// <summary>Peak against the mean matched-filter envelope 1-50 ms around it.</summary>
    public const double MinSharpness = 8;

    /// <summary>A second copy of a marker at this fraction of the first is a device
    /// heard twice (or an echo).</summary>
    public const double SecondPeakLimit = 0.25;

    public readonly record struct Candidate(double Position, double Gain, double Sharpness);

    /// <summary>One marker: where its strongest copy sits (samples), its gain against
    /// the rendered amplitude, and the strongest sharp OTHER copy within ±300 ms.</summary>
    public readonly record struct Hit(double Position, double Gain, double SecondRatio, double SecondOffsetMs);

    /// <summary>Markers found in schedule order, and the slots where one was due but
    /// none was found.</summary>
    public sealed record Track(List<Hit> Hits, int Missing);

    public sealed record Check(string Key, string Name, bool Ok, string Detail);

    public sealed record Verdict(
        List<Check> Checks, Track A, Track B, List<double> Offsets, List<int> Clicks, double ClickLimit,
        double WobbleA, double WobbleB, List<int> SlipsB, List<double> DropoutsB, List<(double From, double To)> Excluded,
        double Seconds)
    {
        public bool Ok(string key) => Checks.First(c => c.Key == key).Ok;

        public IEnumerable<string> Failed => Checks.Where(c => !c.Ok).Select(c => c.Key);
    }

    public static readonly string[] Keys =
    [
        "game present", "chat present", "one peak per marker", "no clicks", "chat no dropouts",
        "game pitch steady", "chat pitch steady", "chat no slips", "offset steady",
    ];

    /// <summary>Judges a mix. <paramref name="seams"/> are seam positions in seconds when
    /// known; null estimates them from the markers (both signals jump together there).
    /// With <paramref name="expectB"/> false only the game checks are made.</summary>
    public static Verdict Judge(float[] mono, SignalSpec a, SignalSpec b, IReadOnlyList<double>? seams, bool expectB = true)
    {
        double seconds = mono.Length / (double)Rate;
        var candA = Candidates(mono, a.MarkerUp, a.MarkerAmp);
        var candB = Candidates(mono, b.MarkerUp, b.MarkerAmp);
        var trackA = Pick(candA, a.PeriodSec, mono.Length);
        var trackB = Pick(candB, b.PeriodSec, mono.Length);

        var excluded = new List<(double From, double To)>();
        if (seams is not null)
        {
            excluded.AddRange(seams.Select(x => (x - 1.0, x + 1.5)));
        }
        else
        {
            excluded.AddRange(EstimateSeams(trackA, trackB, a, b));
        }

        bool Near(double t) => excluded.Any(e => t > e.From && t < e.To);
        bool Overlaps(double from, double to) => excluded.Any(e => e.To > from && e.From < to);

        // Offsets: each A marker against the B marker the schedule puts after it.
        double delta = ((b.PhaseSec - a.PhaseSec) % a.PeriodSec + a.PeriodSec) % a.PeriodSec * Rate;
        int slot = (int)(SlotMs * Rate / 1000);
        var offsets = new List<double>();
        foreach (var ha in trackA.Hits)
        {
            var hb = trackB.Hits.Where(h => Math.Abs(h.Position - ha.Position - delta) <= slot).ToList();
            if (hb.Count == 1 && !Overlaps(ha.Position / Rate, (hb[0].Position + Markers.Length) / Rate))
            {
                offsets.Add((hb[0].Position - ha.Position - delta) * 1000.0 / Rate);
            }
        }

        // Clicks.
        var (allClicks, floor, _) = FindClicks(mono, skipStart: Rate / 2, skipEnd: Rate / 2);
        double clickLimit = Math.Max(ClickThreshold, 4 * floor);
        var clicks = allClicks.Where(c => c.Value > clickLimit && !Near(c.Position / (double)Rate)).Select(c => c.Position).ToList();

        // Tones: blocks near seams left out.
        var fa = TrackTone(mono, a.ToneHz);
        var fb = TrackTone(mono, b.ToneHz);
        for (int i = 0; i < fb.Length; i++)
        {
            if (Overlaps(i * 0.1, (i + 2) * 0.1))
            {
                fa[i] = double.NaN;
                fb[i] = double.NaN;
            }
        }

        double wa = Wobble(fa), wb = Wobble(fb);
        var slipsB = Slips(fb, b.ToneHz).Where(i => (i + 1) * 0.1 > 0.6).ToList();

        // Dropouts in B: windows clear of every marker and seam.
        var markerSpans = trackA.Hits.Concat(trackB.Hits).Select(h => (h.Position / Rate, (h.Position + Markers.Length) / Rate)).ToList();
        var dropouts = Dropouts(mono, b.ToneHz, (from, to) => Overlaps(from, to) || markerSpans.Any(m => m.Item2 > from && m.Item1 < to));

        int expected = (int)Math.Floor((seconds - Markers.Seconds) / a.PeriodSec) - 1;
        var checks = new List<Check>
        {
            new("game present", "game signal present (every scheduled marker, once)",
                trackA.Missing == 0 && trackA.Hits.Count >= expected,
                $"{trackA.Hits.Count} markers, {trackA.Missing} missing, ~{expected + 1} due"),
        };
        if (expectB)
        {
            checks.Add(new("chat present", "chat signal present (every scheduled marker, once)",
                trackB.Missing == 0 && trackB.Hits.Count >= expected,
                $"{trackB.Hits.Count} markers, {trackB.Missing} missing, ~{expected + 1} due"));
        }

        var judged = trackA.Hits.Concat(expectB ? trackB.Hits : []).Where(h => !Near(h.Position / Rate)).ToList();
        var worst = judged.OrderByDescending(h => h.SecondRatio).FirstOrDefault();
        checks.Add(new("one peak per marker", "no echo or doubling (one peak per marker)",
            judged.All(h => h.SecondRatio < SecondPeakLimit),
            $"worst second peak {worst.SecondRatio:F3} at {worst.SecondOffsetMs:F1} ms ({worst.Position / Rate:F1} s)"));
        checks.Add(new("no clicks", "no clicks", clicks.Count == 0,
            $"{clicks.Count} above {clickLimit:F1} LSB" + (clicks.Count == 0 ? "" : " at " + string.Join(", ", clicks.Take(8).Select(c => $"{c / (double)Rate:F2}s")))));
        if (expectB)
        {
            checks.Add(new("chat no dropouts", "chat signal never drops out", dropouts.Count == 0,
                dropouts.Count == 0 ? "none" : $"{dropouts.Count} at " + string.Join(", ", dropouts.Take(8).Select(t => $"{t:F2}s"))));
        }

        checks.Add(new("game pitch steady", "game tone pitch steady", !(wa >= WobbleLimit), $"wobble {wa * 1e6:F0} ppm"));
        if (expectB)
        {
            checks.Add(new("chat pitch steady", "chat tone pitch steady", !(wb >= WobbleLimit), $"wobble {wb * 1e6:F0} ppm"));
            checks.Add(new("chat no slips", "chat tone has no phase slips", slipsB.Count == 0,
                slipsB.Count == 0 ? "none" : $"{slipsB.Count} at " + string.Join(", ", slipsB.Take(8).Select(i => $"{(i + 1) * 0.1:F1}s"))));
            checks.Add(new("offset steady", "game-to-chat offset steady",
                offsets.Count >= 2 && offsets.Max() - offsets.Min() < OffsetSpreadLimitMs,
                offsets.Count < 2 ? $"{offsets.Count} pairs" : $"{offsets.Min():F2}..{offsets.Max():F2} ms over {offsets.Count} pairs"));
        }

        return new Verdict(checks, trackA, trackB, offsets, clicks, clickLimit, wa, wb, slipsB, dropouts, excluded, seconds);
    }

    // ---- markers ----

    /// <summary>Every local peak of the matched filter's envelope at or above
    /// <paramref name="floorGain"/>, with its sharpness.</summary>
    public static List<Candidate> Candidates(float[] mono, bool up, double markerAmp, double floorGain = 0.02)
    {
        var template = Markers.Template(up);
        int m = template.Length;
        double norm = template.Sum(c => c.Magnitude * c.Magnitude) / 2 * markerAmp * short.MaxValue;

        const int Fft = 1 << 16;
        int ctx = Rate / 20, near = Rate / 1000;
        int valid = Fft - m;
        int hop = valid - 2 * ctx;
        var tSpec = new Complex[Fft];
        for (int i = 0; i < m; i++)
        {
            tSpec[i] = template[i];
        }

        Dsp.Fft(tSpec, false);
        var list = new List<Candidate>();
        var buf = new Complex[Fft];
        var env = new double[valid];
        var prefix = new double[valid + 1];
        for (int start = -ctx; start + ctx < mono.Length; start += hop)
        {
            for (int i = 0; i < Fft; i++)
            {
                int n = start + i;
                buf[i] = n >= 0 && n < mono.Length ? mono[n] : 0;
            }

            Dsp.Fft(buf, false);
            for (int i = 0; i < Fft; i++)
            {
                buf[i] *= Complex.Conjugate(tSpec[i]);
            }

            Dsp.Fft(buf, true);
            for (int i = 0; i < valid; i++)
            {
                env[i] = buf[i].Magnitude / norm;
                prefix[i + 1] = prefix[i] + env[i];
            }

            for (int i = ctx; i < ctx + hop; i++)
            {
                int n = start + i;
                if (n < 0 || n + m > mono.Length || env[i] < floorGain || env[i] < env[i - 1] || env[i] <= env[i + 1])
                {
                    continue;
                }

                double around = (prefix[i - near + 1] - prefix[i - ctx] + prefix[i + ctx + 1] - prefix[i + near]) / (2 * (ctx - near + 1));
                double a = env[i - 1], bb = env[i], c = env[i + 1];
                double den = a - 2 * bb + c;
                double d = den == 0 ? 0 : 0.5 * (a - c) / den;
                list.Add(new Candidate(n + d, bb, around <= 0 ? double.PositiveInfinity : bb / around));
            }
        }

        return list;
    }

    /// <summary>Follows the render schedule through the candidates: from the best
    /// supported seed, each next marker is the strongest sharp candidate within
    /// <see cref="SlotMs"/> of one period after the last.</summary>
    public static Track Pick(List<Candidate> candidates, double periodSec, int length, double minGain = 0.08)
    {
        var sharp = candidates.Where(c => c.Sharpness >= MinSharpness).OrderBy(c => c.Position).ToList();
        var good = sharp.Where(c => c.Gain >= minGain).ToList();
        if (good.Count == 0)
        {
            return new Track([], 0);
        }

        double period = periodSec * Rate, slot = SlotMs * Rate / 1000;
        Candidate? Best(double at, double width = 0) => good.Where(c => Math.Abs(c.Position - at) <= (width > 0 ? width : slot))
            .Select(c => (Candidate?)c).OrderByDescending(c => c!.Value.Gain).FirstOrDefault();

        // Seed: the candidate with the most neighbours on its grid, then the strongest.
        var seed = good.OrderByDescending(c => c.Gain).Take(200)
            .OrderByDescending(c => Enumerable.Range(-6, 13).Count(k => k != 0 && Best(c.Position + k * period) is not null))
            .ThenByDescending(c => c.Gain).First();

        var picked = new List<Candidate> { seed };
        int missing = 0;
        foreach (int dir in new[] { 1, -1 })
        {
            double last = seed.Position, expected = last + dir * period;
            bool lost = false;
            while (expected >= 0 && expected + Markers.Length <= length)
            {
                if ((lost ? Best(expected, Math.Max(slot, period / 4)) : Best(expected)) is { } found)
                {
                    picked.Add(found);
                    last = found.Position;
                    expected = last + dir * period;
                    lost = false;
                }
                else
                {
                    missing++;
                    expected += dir * period;
                    lost = true;
                }
            }
        }

        var hits = new List<Hit>();
        foreach (var p in picked.OrderBy(p => p.Position))
        {
            double second = 0, secondAt = 0;
            foreach (var c in sharp)
            {
                double dt = c.Position - p.Position;
                if (Math.Abs(dt) > 0.0015 * Rate && Math.Abs(dt) <= 0.3 * Rate && c.Gain > second)
                {
                    second = c.Gain;
                    secondAt = dt * 1000.0 / Rate;
                }
            }

            hits.Add(new Hit(p.Position, p.Gain, second / p.Gain, secondAt));
        }

        return new Track(hits, missing);
    }

    /// <summary>Seams of a clip recorded without a seam list: where A's and B's marker
    /// spacing both step off the period by the same amount, a seam lies between.</summary>
    public static List<(double From, double To)> EstimateSeams(Track a, Track b, SignalSpec sa, SignalSpec sb)
    {
        var spans = new List<(double, double)>();
        foreach (var (track, spec) in new[] { (a, sa), (b, sb) })
        {
            var h = track.Hits;
            for (int i = 0; i + 1 < h.Count; i++)
            {
                double step = (h[i + 1].Position - h[i].Position) / Rate;
                double k = Math.Round(step / spec.PeriodSec);
                if (Math.Abs(step - k * spec.PeriodSec) > 0.004)
                {
                    spans.Add((h[i].Position / Rate - 0.5, h[i + 1].Position / Rate + 1.5));
                }
            }
        }

        return spans;
    }

    // ---- clicks ----

    /// <summary>High-band (above 8 kHz) peaks, merged within 10 ms, and the band's
    /// 99.9th percentile (the floor a threshold is judged against).</summary>
    public static (List<(int Position, double Value)> Peaks, double Floor, double Max) FindClicks(
        float[] mono, int skipStart = 0, int skipEnd = 0)
    {
        var hp = Dsp.HighPass(8000, 127).Select(v => (float)v).ToArray();
        int half = hp.Length / 2;
        var peaks = new List<(int, double)>();
        var hist = new int[4096];
        double max = 0;
        long counted = 0;
        for (int n = half + skipStart; n + half + skipEnd < mono.Length; n++)
        {
            float acc = 0;
            var x = mono.AsSpan(n - half, hp.Length);
            for (int k = 0; k < hp.Length; k++)
            {
                acc += hp[k] * x[k];
            }

            double v = Math.Abs(acc);
            max = Math.Max(max, v);
            hist[Math.Min(hist.Length - 1, (int)(v * 16))]++;
            counted++;
            if (v > ClickThreshold)
            {
                if (peaks.Count > 0 && n - peaks[^1].Item1 <= Rate / 100)
                {
                    if (v > peaks[^1].Item2)
                    {
                        peaks[^1] = (peaks[^1].Item1, v);
                    }
                }
                else
                {
                    peaks.Add((n, v));
                }
            }
        }

        long target = (long)(counted * 0.999), seen = 0;
        double floor = 0;
        for (int i = 0; i < hist.Length; i++)
        {
            seen += hist[i];
            if (seen >= target)
            {
                floor = i / 16.0;
                break;
            }
        }

        return (peaks, floor, max);
    }

    // ---- tones ----

    /// <summary>The tone's frequency in consecutive 100 ms blocks, from the phase advance
    /// of the demodulated tone. Blocks whose tone level is more than 30 % off its median
    /// carry outside sound (or nothing) and are left out (NaN).</summary>
    public static double[] TrackTone(float[] mono, double hz)
    {
        int block = Rate / 10;
        int blocks = mono.Length / block;
        var phases = new double[blocks];
        var magnitude = new double[blocks];
        double w = -2 * Math.PI * hz / Rate;
        for (int bi = 0; bi < blocks; bi++)
        {
            Complex acc = 0;
            for (int i = 0; i < block; i++)
            {
                int n = bi * block + i;
                acc += mono[n] * Complex.FromPolarCoordinates(1, w * n);
            }

            phases[bi] = acc.Phase;
            magnitude[bi] = acc.Magnitude / block * 2;
        }

        var sorted = magnitude.OrderBy(v => v).ToArray();
        double median = sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
        bool Usable(int i) => median >= 1 && Math.Abs(magnitude[i] - median) < 0.3 * median;
        var freq = new double[Math.Max(0, blocks - 1)];
        for (int bi = 0; bi + 1 < blocks; bi++)
        {
            if (!Usable(bi) || !Usable(bi + 1))
            {
                freq[bi] = double.NaN;
                continue;
            }

            double d = Math.IEEERemainder(phases[bi + 1] - phases[bi], 2 * Math.PI);
            freq[bi] = hz + d / (2 * Math.PI * 0.1);
        }

        return freq;
    }

    /// <summary>Largest relative deviation of a 1 s mean frequency from the median
    /// (NaN when too little of the tone is usable).</summary>
    public static double Wobble(double[] freq)
    {
        var valid = freq.Where(f => !double.IsNaN(f)).OrderBy(f => f).ToArray();
        if (valid.Length < 20)
        {
            return double.NaN;
        }

        double median = valid[valid.Length / 2];
        double worst = 0;
        for (int i = 0; i + 10 <= freq.Length; i += 5)
        {
            var window = freq.Skip(i).Take(10).Where(f => !double.IsNaN(f)).ToArray();
            if (window.Length >= 6)
            {
                worst = Math.Max(worst, Math.Abs(window.Average() - median) / median);
            }
        }

        return worst;
    }

    /// <summary>Blocks whose frequency stands off the median of the two blocks either
    /// side by more than <see cref="SlipLimit"/>: a phase step, not a slow bend.</summary>
    public static List<int> Slips(double[] freq, double hz)
    {
        var slips = new List<int>();
        for (int i = 2; i + 2 < freq.Length; i++)
        {
            if (double.IsNaN(freq[i]))
            {
                continue;
            }

            var around = new[] { freq[i - 2], freq[i - 1], freq[i + 1], freq[i + 2] }.Where(f => !double.IsNaN(f)).OrderBy(f => f).ToArray();
            if (around.Length < 3)
            {
                continue;
            }

            double median = around.Length % 2 == 1 ? around[around.Length / 2] : (around[around.Length / 2 - 1] + around[around.Length / 2]) / 2;
            if (Math.Abs(freq[i] - median) > SlipLimit * hz && (slips.Count == 0 || i - slips[^1] > 2))
            {
                slips.Add(i);
            }
        }

        return slips;
    }

    /// <summary>Times (s) where the tone falls below half its median level, in 20 ms
    /// Hann windows every 10 ms, skipping windows <paramref name="skip"/> rejects.</summary>
    public static List<double> Dropouts(float[] mono, double hz, Func<double, double, bool> skip)
    {
        int win = Rate / 50, hop = Rate / 100;
        var window = Enumerable.Range(0, win).Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (win - 1))).ToArray();
        double wsum = window.Sum();
        double w = -2 * Math.PI * hz / Rate;
        var levels = new List<(double T, double Level)>();
        for (int start = Rate / 2; start + win + Rate / 2 <= mono.Length; start += hop)
        {
            double from = start / (double)Rate, to = (start + win) / (double)Rate;
            if (skip(from - 0.01, to + 0.01))
            {
                continue;
            }

            Complex acc = 0;
            for (int i = 0; i < win; i++)
            {
                int n = start + i;
                acc += mono[n] * window[i] * Complex.FromPolarCoordinates(1, w * n);
            }

            levels.Add(((from + to) / 2, acc.Magnitude / wsum * 2));
        }

        if (levels.Count == 0)
        {
            return [];
        }

        double median = levels.Select(l => l.Level).OrderBy(v => v).ElementAt(levels.Count / 2);
        var drops = new List<double>();
        foreach (var (t, level) in levels)
        {
            if (level < 0.5 * median && (drops.Count == 0 || t - drops[^1] > 0.05))
            {
                drops.Add(t);
            }
        }

        return drops;
    }
}
