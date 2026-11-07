namespace DejaVu;

/// <summary>
/// Mixes one extra loopback stream into the main one when the two run on different
/// clocks (#11). Every audio device has its own crystal: a 48 kHz device that really
/// runs at 48.010 kHz hands over 10 extra frames a second, 15,000 over a 25-minute
/// buffer. Trimming or padding the backlog clicks; letting it grow drifts the voice off
/// the picture. Instead the stream is resampled on the fly, and the resampling ratio is
/// steered so the backlog holds at a small fixed cushion: no growing offset, no gaps.
///
/// The cushion is held in time, not in frames, when both streams carry capture
/// timestamps (WASAPI's QPC position): the backlog counted in frames depends on where
/// the two devices' 10 ms packets fall against each other, which differs every time a
/// segment opens them, and moved the chat device 10-20 ms against the game from one
/// minute to the next. Measured against the main stream's own timestamps, the extra
/// sits the same 40 ms behind every time, from its first sample.
/// Single-threaded: the audio pump owns it.
/// </summary>
internal sealed class DriftMixer
{
    // Cushion the controller holds, and the hard ceiling past which the backlog is cut
    // back (the main stream stalled; only then does anything get dropped).
    private const double TargetSeconds = 0.04;
    private const double CapacitySeconds = 0.5;

    // Steering: at most ±0.5 % (8.6 cents, inaudible, and ~25× any real clock gap),
    // reached when the smoothed backlog is a whole cushion off target. The backlog is
    // smoothed over about a second so 10 ms packet jitter does not wobble the ratio.
    private const double MaxDeviation = 0.005;
    private const double SmoothingSeconds = 1.0;

    // Starting and running dry happen mid-waveform: a 5 ms raised-cosine fade at each
    // edge turns what would be a step (a click) into an inaudible ramp.
    private const double FadeSeconds = 0.005;

    // Timestamps further apart than this are not one clock domain (or are stale): the
    // backlog is then judged by frame count alone.
    private const double MaxStampGapSeconds = 0.25;

    private readonly int channels;
    private readonly int sampleRate;
    private readonly short[] ring;
    private readonly int capacityFrames;
    private readonly double gain;
    private readonly float[] previous;

    private readonly int fadeFrames;
    private int fadeInDone;  // output frames since priming, for the fade-in
    private int muteDone;    // output frames since Mute, for the fade-out

    // Capture time (seconds) just past the newest queued frame; NaN when unknown.
    private double tailTime = double.NaN;

    private int head;   // frame index of x0, the frame the read position sits on
    private int count;  // frames buffered from head
    private double fraction;
    private double smoothed;

    public DriftMixer(int channels, int sampleRate)
    {
        this.channels = channels;
        this.sampleRate = sampleRate;
        TargetFrames = (int)(sampleRate * TargetSeconds);
        capacityFrames = (int)(sampleRate * CapacitySeconds);
        ring = new short[capacityFrames * channels];
        previous = new float[channels];
        gain = MaxDeviation / TargetFrames;
        smoothed = TargetFrames;
        fadeFrames = Math.Max(1, (int)(sampleRate * FadeSeconds));
    }

    public int TargetFrames { get; }

    public int CapacityFrames => capacityFrames;

    public int BufferedFrames => count;

    /// <summary>Source frames consumed per output frame. Above 1 the source runs fast.</summary>
    public double Ratio { get; private set; } = 1;

    /// <summary>True once the cushion has built up and output is flowing.</summary>
    public bool Primed { get; private set; }

    /// <summary>Times the source ran dry. A loopback device that renders nothing
    /// delivers no packets, so this counts quiet spells as well as real dry-outs.</summary>
    public long Underruns { get; private set; }

    /// <summary>Frames thrown away because the backlog hit its ceiling.</summary>
    public long DroppedFrames { get; private set; }

    /// <summary>Faded out and silent from now on (the source keeps being consumed).</summary>
    public bool Muted { get; private set; }

    /// <summary>Frames skipped when priming, to start exactly one cushion behind.</summary>
    public long AlignedFrames { get; private set; }

    /// <summary>Queues a packet. <paramref name="time"/> is the capture time of its first
    /// frame, in seconds on the same clock as the main stream's (QPC), or NaN.</summary>
    public void Queue(ReadOnlySpan<short> interleaved, double time = double.NaN) =>
        Append(interleaved, interleaved.Length / channels, time);

    public void QueueSilence(int frames, double time = double.NaN) => Append(default, frames, time);

    /// <summary>Fades this source out over 5 ms and keeps it silent: the device became
    /// one the main stream already hears.</summary>
    public void Mute() => Muted = true;

    private void Append(ReadOnlySpan<short> source, int frames, double time)
    {
        if (frames <= 0)
        {
            return;
        }

        tailTime = !double.IsNaN(time) ? time + frames / (double)sampleRate
            : !double.IsNaN(tailTime) ? tailTime + frames / (double)sampleRate
            : double.NaN;

        if (count + frames > capacityFrames)
        {
            // Only reachable when the consumer stopped pulling: keep the newest audio,
            // back at the cushion, so it lines up with the main stream when it resumes.
            int drop = count + frames - TargetFrames;
            int dropOld = Math.Min(count, drop);
            int dropNew = drop - dropOld;
            DroppedFrames += drop;
            head = (head + dropOld) % capacityFrames;
            count -= dropOld;
            if (!source.IsEmpty)
            {
                source = source[(dropNew * channels)..];
            }

            frames -= dropNew;
        }

        int tail = (head + count) % capacityFrames;
        for (int f = 0; f < frames; f++)
        {
            int at = ((tail + f) % capacityFrames) * channels;
            for (int c = 0; c < channels; c++)
            {
                ring[at + c] = source.IsEmpty ? (short)0 : source[f * channels + c];
            }
        }

        count += frames;
    }

    /// <summary>The backlog as a delay, in frames: how long before the main stream's
    /// <paramref name="time"/> the next source frame was captured. Without usable
    /// timestamps, the frames queued.</summary>
    private double Backlog(double time)
    {
        if (double.IsNaN(time) || double.IsNaN(tailTime) || Math.Abs(time - tailTime) > MaxStampGapSeconds)
        {
            return count;
        }

        return count + (time - tailTime) * sampleRate;
    }

    /// <summary>Adds resampled source audio into <paramref name="mix"/> (interleaved,
    /// saturating). Frames the source cannot cover are left as they are.
    /// <paramref name="time"/> is the capture time of the mix's first frame on the
    /// clock <see cref="Queue"/> uses, or NaN.</summary>
    public void MixInto(Span<short> mix, double time = double.NaN)
    {
        int frames = mix.Length / channels;
        if (frames == 0)
        {
            return;
        }

        double backlog = Backlog(time);
        if (!Primed)
        {
            if (count < TargetFrames || backlog < TargetFrames)
            {
                return;
            }

            // Start exactly one cushion behind the main stream: skip what is older.
            int skip = (int)Math.Min(backlog - TargetFrames, count - TargetFrames);
            if (skip > 0)
            {
                head = (head + skip) % capacityFrames;
                count -= skip;
                backlog -= skip;
                AlignedFrames += skip;
            }

            Primed = true;
            fraction = 0;
            fadeInDone = 0;
            smoothed = backlog;
            for (int c = 0; c < channels; c++)
            {
                previous[c] = ring[head * channels + c];
            }
        }

        // Steer from the backlog as it stands before this pull. Frozen while unprimed,
        // so a quiet spell does not leave the ratio slammed to one end on resume.
        double alpha = Math.Min(1.0, frames / (SmoothingSeconds * sampleRate));
        smoothed += alpha * (backlog - smoothed);
        Ratio = 1 + Math.Clamp(gain * (smoothed - TargetFrames), -MaxDeviation, MaxDeviation);

        // Output frames the backlog can cover in this pull: cubic interpolation needs
        // x0..x2 ahead of the read position, so frame j needs
        // count - floor(fraction + j * Ratio) >= 3. Short of the whole pull, the last of
        // them fade out instead of stopping dead.
        int covered = (int)Math.Max(0, Math.Ceiling((count - 2 - fraction) / Ratio));
        int fadeOutFrom = covered < frames ? Math.Max(0, covered - fadeFrames) : int.MaxValue;
        int fadeOutLength = covered - fadeOutFrom;

        for (int f = 0; f < frames; f++)
        {
            if (count < 3)
            {
                Primed = false;
                Underruns++;
                return;
            }

            float edge = 1f;
            if (fadeInDone < fadeFrames)
            {
                edge = 0.5f - 0.5f * MathF.Cos(MathF.PI * (fadeInDone + 1) / (fadeFrames + 1));
                fadeInDone++;
            }

            if (f >= fadeOutFrom)
            {
                edge *= 0.5f + 0.5f * MathF.Cos(MathF.PI * (f - fadeOutFrom + 1) / (fadeOutLength + 1));
            }

            if (Muted)
            {
                edge *= muteDone >= fadeFrames ? 0f : 0.5f + 0.5f * MathF.Cos(MathF.PI * (muteDone + 1) / (fadeFrames + 1));
                muteDone++;
            }

            int i0 = head * channels;
            int i1 = ((head + 1) % capacityFrames) * channels;
            int i2 = ((head + 2) % capacityFrames) * channels;
            float t = (float)fraction;
            for (int c = 0; c < channels; c++)
            {
                float xm1 = previous[c], x0 = ring[i0 + c], x1 = ring[i1 + c], x2 = ring[i2 + c];

                // Catmull-Rom: exact on ramps, smooth on everything else, four taps.
                float a = 0.5f * (x2 - xm1) + 1.5f * (x0 - x1);
                float b = xm1 - 2.5f * x0 + 2f * x1 - 0.5f * x2;
                float d = 0.5f * (x1 - xm1);
                float y = ((a * t + b) * t + d) * t + x0;

                int sum = mix[f * channels + c] + (int)MathF.Round(y * edge);
                mix[f * channels + c] = (short)Math.Clamp(sum, short.MinValue, short.MaxValue);
            }

            fraction += Ratio;
            while (fraction >= 1)
            {
                fraction -= 1;
                for (int c = 0; c < channels; c++)
                {
                    previous[c] = ring[head * channels + c];
                }

                head = (head + 1) % capacityFrames;
                count--;
            }
        }
    }
}
