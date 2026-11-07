namespace DejaVu;

/// <summary>
/// Mixes one extra loopback stream into the main one when the two run on different
/// clocks (#11). Every audio device has its own crystal: a 48 kHz device that really
/// runs at 48.010 kHz hands over 10 extra frames a second, 15,000 over a 25-minute
/// buffer. Trimming or padding the backlog clicks; letting it grow drifts the voice off
/// the picture. Instead the stream is resampled on the fly, and the resampling ratio is
/// steered so the backlog holds at a small fixed cushion: no growing offset, no gaps.
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

    private readonly int channels;
    private readonly int sampleRate;
    private readonly short[] ring;
    private readonly int capacityFrames;
    private readonly double gain;
    private readonly float[] previous;

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

    public void Queue(ReadOnlySpan<short> interleaved) => Append(interleaved, interleaved.Length / channels);

    public void QueueSilence(int frames) => Append(default, frames);

    private void Append(ReadOnlySpan<short> source, int frames)
    {
        if (frames <= 0)
        {
            return;
        }

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

    /// <summary>Adds resampled source audio into <paramref name="mix"/> (interleaved,
    /// saturating). Frames the source cannot cover are left as they are.</summary>
    public void MixInto(Span<short> mix)
    {
        int frames = mix.Length / channels;
        if (frames == 0)
        {
            return;
        }

        if (!Primed)
        {
            if (count < TargetFrames)
            {
                return;
            }

            Primed = true;
            fraction = 0;
            for (int c = 0; c < channels; c++)
            {
                previous[c] = ring[head * channels + c];
            }
        }

        // Steer from the backlog as it stands before this pull. Frozen while unprimed,
        // so a quiet spell does not leave the ratio slammed to one end on resume.
        double alpha = Math.Min(1.0, frames / (SmoothingSeconds * sampleRate));
        smoothed += alpha * (count - smoothed);
        Ratio = 1 + Math.Clamp(gain * (smoothed - TargetFrames), -MaxDeviation, MaxDeviation);

        for (int f = 0; f < frames; f++)
        {
            // Cubic interpolation needs x0..x2 ahead of the read position.
            if (count < 3)
            {
                Primed = false;
                Underruns++;
                return;
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

                int sum = mix[f * channels + c] + (int)MathF.Round(y);
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
