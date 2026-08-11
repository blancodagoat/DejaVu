using System.Runtime.InteropServices;

namespace DejaVu;

/// <summary>
/// Shared Media Foundation interop for the remuxer and the audio encoder. Interfaces are
/// declared flat (no managed inheritance) because the CLR lays out ComImport vtables per
/// declared interface; only the slots actually called carry real signatures, the rest are
/// order-preserving placeholders. Every GUID here was read out of the Windows SDK headers,
/// not memory.
/// </summary>
internal static class Mf
{
    public const int MF_VERSION = 0x00020070;
    public const uint SOURCE_READER_ALL_STREAMS = 0xFFFFFFFE;
    public const uint SOURCE_READER_ANY_STREAM = 0xFFFFFFFE;
    public const uint SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;
    public const uint SOURCE_READER_FIRST_AUDIO_STREAM = 0xFFFFFFFD;
    public const uint READERF_ENDOFSTREAM = 0x00000002;

    public static readonly Guid MediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid MediaType_Audio = new("73647561-0000-0010-8000-00AA00389B71");
    public static readonly Guid AudioFormat_PCM = new("00000001-0000-0010-8000-00AA00389B71");
    public static readonly Guid AudioFormat_AAC = new("00001610-0000-0010-8000-00AA00389B71");

    public static readonly Guid MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    public static readonly Guid MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    public static readonly Guid MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    public static readonly Guid MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    public static readonly Guid MT_AUDIO_BLOCK_ALIGNMENT = new("322de230-9eeb-43bd-ab7a-ff412251541d");

    private static readonly object StartGate = new();
    private static bool started;

    public static void EnsureStarted()
    {
        lock (StartGate)
        {
            if (!started)
            {
                Check(MFStartup(MF_VERSION, 0));
                started = true;
            }
        }
    }

    public static void Check(int hr)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    /// <summary>An audio media type; pass 0 to skip an attribute.</summary>
    public static IMFMediaType CreateAudioType(
        Guid subtype, int sampleRate, int channels, int bits, int avgBytesPerSecond, int blockAlign)
    {
        Check(MFCreateMediaType(out var type));
        var major = MT_MAJOR_TYPE;
        var audio = MediaType_Audio;
        var sub = MT_SUBTYPE;
        Check(type.SetGUID(ref major, ref audio));
        Check(type.SetGUID(ref sub, ref subtype));
        SetU32(type, MT_AUDIO_SAMPLES_PER_SECOND, sampleRate);
        SetU32(type, MT_AUDIO_NUM_CHANNELS, channels);
        SetU32(type, MT_AUDIO_BITS_PER_SAMPLE, bits);
        SetU32(type, MT_AUDIO_AVG_BYTES_PER_SECOND, avgBytesPerSecond);
        SetU32(type, MT_AUDIO_BLOCK_ALIGNMENT, blockAlign);
        return type;
    }

    private static void SetU32(IMFMediaType type, Guid key, int value)
    {
        if (value > 0)
        {
            Check(type.SetUINT32(ref key, (uint)value));
        }
    }

    /// <summary>Writes one PCM block as a sample. Time and duration in 100 ns units.</summary>
    public static void WritePcm(
        IMFSinkWriter writer, int streamIndex, IntPtr source, int bytes, long time, long duration)
    {
        Check(MFCreateMemoryBuffer((uint)bytes, out var buffer));
        try
        {
            Check(buffer.Lock(out var dest, out _, out _));
            unsafe
            {
                if (source == IntPtr.Zero)
                {
                    new Span<byte>((void*)dest, bytes).Clear();
                }
                else
                {
                    System.Buffer.MemoryCopy((void*)source, (void*)dest, bytes, bytes);
                }
            }

            Check(buffer.Unlock());
            Check(buffer.SetCurrentLength((uint)bytes));

            Check(MFCreateSample(out var sample));
            try
            {
                Check(sample.AddBuffer(buffer));
                Check(sample.SetSampleTime(time));
                Check(sample.SetSampleDuration(duration));
                Check(writer.WriteSample(streamIndex, sample));
            }
            finally
            {
                Marshal.ReleaseComObject(sample);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
        }
    }

    /// <summary>
    /// Mean absolute sample level (0..1) of a file's first audio stream, decoded to
    /// 16-bit PCM. Test instrumentation for the loopback-exclusion checks.
    /// </summary>
    public static double MeasureAudioLevel(string path)
    {
        EnsureStarted();
        Check(MFCreateSourceReaderFromURL(path, IntPtr.Zero, out var reader));
        try
        {
            Check(reader.SetStreamSelection(SOURCE_READER_ALL_STREAMS, false));
            Check(reader.SetStreamSelection(SOURCE_READER_FIRST_AUDIO_STREAM, true));

            var pcm = CreateAudioType(AudioFormat_PCM, 0, 0, 16, 0, 0);
            try
            {
                Check(reader.SetCurrentMediaType(SOURCE_READER_FIRST_AUDIO_STREAM, IntPtr.Zero, pcm));
            }
            finally
            {
                Marshal.ReleaseComObject(pcm);
            }

            long total = 0;
            long count = 0;
            while (true)
            {
                Check(reader.ReadSample(
                    SOURCE_READER_FIRST_AUDIO_STREAM, 0, out _, out uint flags, out _, out IntPtr samplePtr));
                if (samplePtr == IntPtr.Zero)
                {
                    if ((flags & READERF_ENDOFSTREAM) != 0)
                    {
                        break;
                    }

                    continue;
                }

                var sample = (IMFSample)Marshal.GetObjectForIUnknown(samplePtr);
                Marshal.Release(samplePtr);
                try
                {
                    Check(sample.ConvertToContiguousBuffer(out var buffer));
                    try
                    {
                        Check(buffer.Lock(out var data, out _, out uint length));
                        unsafe
                        {
                            var samples = new ReadOnlySpan<short>((void*)data, (int)length / 2);
                            foreach (var s in samples)
                            {
                                total += Math.Abs((int)s);
                            }

                            count += samples.Length;
                        }

                        Check(buffer.Unlock());
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(buffer);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(sample);
                }

                if ((flags & READERF_ENDOFSTREAM) != 0)
                {
                    break;
                }
            }

            return count == 0 ? 0 : total / (count * 32768.0);
        }
        finally
        {
            Marshal.ReleaseComObject(reader);
        }
    }

    [DllImport("mfplat.dll")]
    private static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateMediaType(out IMFMediaType type);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateSample(out IMFSample sample);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateMemoryBuffer(uint maxLength, out IMFMediaBuffer buffer);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    public static extern int MFCreateSourceReaderFromURL(
        string url, IntPtr attributes, out IMFSourceReader reader);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    public static extern int MFCreateSinkWriterFromURL(
        string url, IntPtr byteStream, IntPtr attributes, out IMFSinkWriter writer);

    [ComImport]
    [Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFSourceReader
    {
        [PreserveSig] int GetStreamSelection(uint streamIndex, out bool selected);
        [PreserveSig] int SetStreamSelection(uint streamIndex, [MarshalAs(UnmanagedType.Bool)] bool selected);
        [PreserveSig] int GetNativeMediaType(uint streamIndex, uint typeIndex, out IMFMediaType type);
        [PreserveSig] int GetCurrentMediaType(uint streamIndex, out IMFMediaType type);
        [PreserveSig] int SetCurrentMediaType(uint streamIndex, IntPtr reserved, IMFMediaType type);
        [PreserveSig] int SetCurrentPosition(IntPtr guidTimeFormat, IntPtr position);
        [PreserveSig] int ReadSample(
            uint streamIndex, uint controlFlags, out uint actualStreamIndex, out uint streamFlags,
            out long timestamp, out IntPtr sample);
        [PreserveSig] int Flush(uint streamIndex);
        [PreserveSig] int GetServiceForStream(uint streamIndex, IntPtr service, IntPtr riid, out IntPtr obj);
        [PreserveSig] int GetPresentationAttribute(uint streamIndex, IntPtr attribute, IntPtr value);
    }

    [ComImport]
    [Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFSinkWriter
    {
        [PreserveSig] int AddStream(IMFMediaType type, out int streamIndex);
        [PreserveSig] int SetInputMediaType(int streamIndex, IMFMediaType type, IntPtr parameters);
        [PreserveSig] int BeginWriting();
        [PreserveSig] int WriteSample(int streamIndex, IMFSample sample);
        [PreserveSig] int SendStreamTick(int streamIndex, long timestamp);
        [PreserveSig] int PlaceMarker(int streamIndex, IntPtr context);
        [PreserveSig] int NotifyEndOfSegment(int streamIndex);
        [PreserveSig] int Flush(int streamIndex);
        [PreserveSig] int Finalize_();
        [PreserveSig] int GetServiceForStream(int streamIndex, IntPtr service, IntPtr riid, out IntPtr obj);
        [PreserveSig] int GetStatistics(int streamIndex, IntPtr statistics);
    }

    [ComImport]
    [Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaType
    {
        // IMFAttributes
        [PreserveSig] int _GetItem();
        [PreserveSig] int _GetItemType();
        [PreserveSig] int _CompareItem();
        [PreserveSig] int _Compare();
        [PreserveSig] int _GetUINT32();
        [PreserveSig] int _GetUINT64();
        [PreserveSig] int _GetDouble();
        [PreserveSig] int _GetGUID();
        [PreserveSig] int _GetStringLength();
        [PreserveSig] int _GetString();
        [PreserveSig] int _GetAllocatedString();
        [PreserveSig] int _GetBlobSize();
        [PreserveSig] int _GetBlob();
        [PreserveSig] int _GetAllocatedBlob();
        [PreserveSig] int _GetUnknown();
        [PreserveSig] int _SetItem();
        [PreserveSig] int _DeleteItem();
        [PreserveSig] int _DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int _SetUINT64();
        [PreserveSig] int _SetDouble();
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int _SetString();
        [PreserveSig] int _SetBlob();
        [PreserveSig] int _SetUnknown();
        [PreserveSig] int _LockStore();
        [PreserveSig] int _UnlockStore();
        [PreserveSig] int _GetCount();
        [PreserveSig] int _GetItemByIndex();
        [PreserveSig] int _CopyAllItems();
        // IMFMediaType
        [PreserveSig] int GetMajorType(out Guid majorType);
        [PreserveSig] int _IsCompressedFormat();
        [PreserveSig] int _IsEqual();
        [PreserveSig] int _GetRepresentation();
        [PreserveSig] int _FreeRepresentation();
    }

    [ComImport]
    [Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFSample
    {
        // IMFAttributes
        [PreserveSig] int _GetItem();
        [PreserveSig] int _GetItemType();
        [PreserveSig] int _CompareItem();
        [PreserveSig] int _Compare();
        [PreserveSig] int _GetUINT32();
        [PreserveSig] int _GetUINT64();
        [PreserveSig] int _GetDouble();
        [PreserveSig] int _GetGUID();
        [PreserveSig] int _GetStringLength();
        [PreserveSig] int _GetString();
        [PreserveSig] int _GetAllocatedString();
        [PreserveSig] int _GetBlobSize();
        [PreserveSig] int _GetBlob();
        [PreserveSig] int _GetAllocatedBlob();
        [PreserveSig] int _GetUnknown();
        [PreserveSig] int _SetItem();
        [PreserveSig] int _DeleteItem();
        [PreserveSig] int _DeleteAllItems();
        [PreserveSig] int _SetUINT32();
        [PreserveSig] int _SetUINT64();
        [PreserveSig] int _SetDouble();
        [PreserveSig] int _SetGUID();
        [PreserveSig] int _SetString();
        [PreserveSig] int _SetBlob();
        [PreserveSig] int _SetUnknown();
        [PreserveSig] int _LockStore();
        [PreserveSig] int _UnlockStore();
        [PreserveSig] int _GetCount();
        [PreserveSig] int _GetItemByIndex();
        [PreserveSig] int _CopyAllItems();
        // IMFSample
        [PreserveSig] int _GetSampleFlags();
        [PreserveSig] int _SetSampleFlags();
        [PreserveSig] int GetSampleTime(out long time);
        [PreserveSig] int SetSampleTime(long time);
        [PreserveSig] int GetSampleDuration(out long duration);
        [PreserveSig] int SetSampleDuration(long duration);
        [PreserveSig] int _GetBufferCount();
        [PreserveSig] int _GetBufferByIndex();
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
        [PreserveSig] int _RemoveBufferByIndex();
        [PreserveSig] int _RemoveAllBuffers();
        [PreserveSig] int _GetTotalLength();
        [PreserveSig] int _CopyToBuffer();
    }

    [ComImport]
    [Guid("045FA593-8799-42b8-BC8D-8968C6453507")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr data, out uint maxLength, out uint currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out uint length);
        [PreserveSig] int SetCurrentLength(uint length);
        [PreserveSig] int GetMaxLength(out uint length);
    }
}
