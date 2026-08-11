using System.Runtime.InteropServices;

namespace DejaVu;

/// <summary>
/// Pass-through remuxing on top of <see cref="Mf"/>: compressed H.264/AAC samples are
/// copied between containers with retimed timestamps — fast, lossless, no transforms.
/// <see cref="Concat"/> appends files end to end; <see cref="MuxParallel"/> joins a video
/// file and an audio file onto one timeline.
/// </summary>
internal static class Mp4Concat
{
    public static void Concat(IReadOnlyList<string> inputs, string output)
    {
        Mf.EnsureStarted();

        // Every readable input is opened before the sink writer exists, and unreadable
        // ones (still locked by a dying recorder, truncated by a crash) are skipped
        // rather than fatal. A writer is only created once there is something to write,
        // so no failed attempt ever leaves a half-open output file behind.
        var readers = new List<Mf.IMFSourceReader>();
        foreach (var input in inputs)
        {
            if (Mf.MFCreateSourceReaderFromURL(input, IntPtr.Zero, out var candidate) >= 0)
            {
                readers.Add(candidate);
            }
        }

        if (readers.Count == 0)
        {
            throw new InvalidOperationException("None of the buffered segments are readable.");
        }

        try
        {
            WriteAll(readers, output);
        }
        finally
        {
            foreach (var reader in readers)
            {
                Marshal.ReleaseComObject(reader);
            }
        }
    }

    /// <summary>
    /// A sink writer for file-to-file remuxing. Throttling MUST be disabled: it paces
    /// WriteSample for live pipelines, and on 60-second inputs it blocks the call for
    /// minutes — killed mid-crawl, the output is a full-size file with no index that
    /// nothing can play. Caught live in a memory dump.
    /// </summary>
    private static Mf.IMFSinkWriter CreateFileWriter(string output)
    {
        Mf.Check(Mf.MFCreateAttributes(out var attrs, 1));
        Mf.IMFSinkWriter writer;
        try
        {
            var key = Mf.SINK_WRITER_DISABLE_THROTTLING;
            Mf.Check(attrs.SetUINT32(ref key, 1));
            var attrsPtr = System.Runtime.InteropServices.Marshal.GetIUnknownForObject(attrs);
            try
            {
                Mf.Check(Mf.MFCreateSinkWriterFromURL(output, IntPtr.Zero, attrsPtr, out writer));
            }
            finally
            {
                Marshal.Release(attrsPtr);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(attrs);
        }

        return writer;
    }

    private static void WriteAll(List<Mf.IMFSourceReader> readers, string output)
    {
        var writer = CreateFileWriter(output);
        try
        {
            // Writer streams are declared from the first input's native types; later inputs
            // map onto them by major type (video/audio).
            int videoOut = -1;
            int audioOut = -1;
            bool began = false;
            long offset = 0;

            foreach (var reader in readers)
            {
                Mf.Check(reader.SetStreamSelection(Mf.SOURCE_READER_ALL_STREAMS, true));

                var streamMap = new Dictionary<uint, int>();
                for (uint i = 0; reader.GetNativeMediaType(i, 0, out var type) == 0; i++)
                {
                    Mf.Check(type.GetMajorType(out var major));
                    int outIndex;
                    if (major == Mf.MediaType_Video)
                    {
                        if (videoOut < 0)
                        {
                            Mf.Check(writer.AddStream(type, out videoOut));
                            Mf.Check(writer.SetInputMediaType(videoOut, type, IntPtr.Zero));
                        }

                        outIndex = videoOut;
                    }
                    else if (major == Mf.MediaType_Audio)
                    {
                        if (audioOut < 0)
                        {
                            Mf.Check(writer.AddStream(type, out audioOut));
                            Mf.Check(writer.SetInputMediaType(audioOut, type, IntPtr.Zero));
                        }

                        outIndex = audioOut;
                    }
                    else
                    {
                        Marshal.ReleaseComObject(type);
                        continue;
                    }

                    streamMap[i] = outIndex;
                    Marshal.ReleaseComObject(type);
                }

                if (streamMap.Count == 0)
                {
                    continue;
                }

                if (!began)
                {
                    Mf.Check(writer.BeginWriting());
                    began = true;
                }

                long fileEnd = 0;
                int ended = 0;
                while (ended < streamMap.Count)
                {
                    Mf.Check(reader.ReadSample(
                        Mf.SOURCE_READER_ANY_STREAM, 0, out uint streamIndex, out uint flags,
                        out _, out IntPtr samplePtr));

                    if ((flags & Mf.READERF_ENDOFSTREAM) != 0)
                    {
                        ended++;
                    }

                    if (samplePtr == IntPtr.Zero)
                    {
                        continue;
                    }

                    var sample = (Mf.IMFSample)Marshal.GetObjectForIUnknown(samplePtr);
                    Marshal.Release(samplePtr);

                    if (streamMap.TryGetValue(streamIndex, out int outIndex))
                    {
                        Mf.Check(sample.GetSampleTime(out long time));
                        // Duration is optional on compressed samples; absent counts as zero.
                        long duration = sample.GetSampleDuration(out long d) == 0 ? d : 0;
                        fileEnd = Math.Max(fileEnd, time + duration);

                        Mf.Check(sample.SetSampleTime(time + offset));
                        Mf.Check(writer.WriteSample(outIndex, sample));
                    }

                    Marshal.ReleaseComObject(sample);
                }

                offset += fileEnd;
            }

            Mf.Check(writer.Finalize_());
        }
        finally
        {
            Marshal.ReleaseComObject(writer);
        }
    }

    /// <summary>
    /// Joins a video-only file and an audio-only file onto one timeline, timestamps
    /// untouched. Samples are fed in timestamp order so the sink writer interleaves as it
    /// goes instead of buffering a whole stream.
    /// </summary>
    public static void MuxParallel(string videoPath, string audioPath, string output)
    {
        Mf.EnsureStarted();

        Mf.Check(Mf.MFCreateSourceReaderFromURL(videoPath, IntPtr.Zero, out var video));
        try
        {
            Mf.Check(Mf.MFCreateSourceReaderFromURL(audioPath, IntPtr.Zero, out var audio));
            try
            {
                var writer = CreateFileWriter(output);
                try
                {
                    int videoOut = AddFirstStream(writer, video, Mf.SOURCE_READER_FIRST_VIDEO_STREAM);
                    int audioOut = AddFirstStream(writer, audio, Mf.SOURCE_READER_FIRST_AUDIO_STREAM);
                    Mf.Check(writer.BeginWriting());

                    var nextVideo = Read(video, Mf.SOURCE_READER_FIRST_VIDEO_STREAM);
                    var nextAudio = Read(audio, Mf.SOURCE_READER_FIRST_AUDIO_STREAM);
                    while (nextVideo is not null || nextAudio is not null)
                    {
                        bool takeVideo = nextAudio is null
                            || (nextVideo is not null && nextVideo.Value.Time <= nextAudio.Value.Time);
                        if (takeVideo)
                        {
                            Mf.Check(writer.WriteSample(videoOut, nextVideo!.Value.Sample));
                            Marshal.ReleaseComObject(nextVideo.Value.Sample);
                            nextVideo = Read(video, Mf.SOURCE_READER_FIRST_VIDEO_STREAM);
                        }
                        else
                        {
                            Mf.Check(writer.WriteSample(audioOut, nextAudio!.Value.Sample));
                            Marshal.ReleaseComObject(nextAudio.Value.Sample);
                            nextAudio = Read(audio, Mf.SOURCE_READER_FIRST_AUDIO_STREAM);
                        }
                    }

                    Mf.Check(writer.Finalize_());
                }
                finally
                {
                    Marshal.ReleaseComObject(writer);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(audio);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(video);
        }
    }

    private static int AddFirstStream(Mf.IMFSinkWriter writer, Mf.IMFSourceReader reader, uint stream)
    {
        Mf.Check(reader.GetNativeMediaType(stream, 0, out var type));
        try
        {
            Mf.Check(writer.AddStream(type, out int index));
            Mf.Check(writer.SetInputMediaType(index, type, IntPtr.Zero));
            return index;
        }
        finally
        {
            Marshal.ReleaseComObject(type);
        }
    }

    private static (Mf.IMFSample Sample, long Time)? Read(Mf.IMFSourceReader reader, uint stream)
    {
        while (true)
        {
            Mf.Check(reader.ReadSample(stream, 0, out _, out uint flags, out _, out IntPtr samplePtr));
            if (samplePtr != IntPtr.Zero)
            {
                var sample = (Mf.IMFSample)Marshal.GetObjectForIUnknown(samplePtr);
                Marshal.Release(samplePtr);
                Mf.Check(sample.GetSampleTime(out long time));
                return (sample, time);
            }

            if ((flags & Mf.READERF_ENDOFSTREAM) != 0)
            {
                return null;
            }
        }
    }
}
