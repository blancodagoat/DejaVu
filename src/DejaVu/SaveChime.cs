using System.Media;

namespace DejaVu;

/// <summary>
/// Audible save feedback. Balloons are suppressed exactly when this app matters (Focus
/// Assist's "when I'm playing a game" default), so the chime is the only confirmation a
/// fullscreen player gets. Synthesized at startup — two ascending notes for a saved
/// clip, one low buzz for a failure — so there is no sound asset to ship or lose.
/// </summary>
internal static class SaveChime
{
    private static readonly SoundPlayer Saved = Build([(660, 90), (990, 140)]);
    private static readonly SoundPlayer Failed = Build([(220, 200)]);

    public static void Success() => Play(Saved);

    public static void Failure() => Play(Failed);

    private static void Play(SoundPlayer player)
    {
        try
        {
            player.Stream!.Position = 0;
            player.Play();
        }
        catch
        {
            // No audio device; the clip itself is unaffected.
        }
    }

    private static SoundPlayer Build((int Hz, int Ms)[] notes)
    {
        const int rate = 44100;
        int total = 0;
        foreach (var (_, ms) in notes)
        {
            total += rate * ms / 1000;
        }

        var pcm = new short[total];
        int offset = 0;
        foreach (var (hz, ms) in notes)
        {
            int count = rate * ms / 1000;
            for (int i = 0; i < count; i++)
            {
                // Linear fade over each note keeps the seams and the tail click-free.
                double fade = 1.0 - (double)i / count;
                pcm[offset + i] = (short)(Math.Sin(2 * Math.PI * hz * i / rate) * 9000 * fade);
            }

            offset += count;
        }

        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            int bytes = pcm.Length * 2;
            writer.Write("RIFF"u8);
            writer.Write(36 + bytes);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);        // PCM
            writer.Write((short)1);        // mono
            writer.Write(rate);
            writer.Write(rate * 2);        // bytes/sec
            writer.Write((short)2);        // block align
            writer.Write((short)16);       // bits
            writer.Write("data"u8);
            writer.Write(bytes);
            foreach (var sample in pcm)
            {
                writer.Write(sample);
            }
        }

        stream.Position = 0;
        return new SoundPlayer(stream);
    }
}
