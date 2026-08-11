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

    // Video encode path — all values read out of the Windows SDK headers.
    public static readonly Guid VideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid VideoFormat_AV1 = new("31305641-0000-0010-8000-00AA00389B71");
    public static readonly Guid VideoFormat_ARGB32 = new("00000015-0000-0010-8000-00AA00389B71");
    public static readonly Guid VideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");
    public static readonly Guid MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");
    public static readonly Guid SINK_WRITER_D3D_MANAGER = new("ec822da2-e1e9-4b29-a0d8-563c719f5269");
    public static readonly Guid SINK_WRITER_DISABLE_THROTTLING = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");
    public static readonly Guid TRANSCODE_CONTAINERTYPE = new("150ff23f-4abc-478b-ac4f-e1916fba1cca");
    public static readonly Guid TranscodeContainerType_FMPEG4 = new("9ba876f1-419f-4b77-a1e0-35959d9d4004");

    /// <summary>True when a hardware encoder MFT for the subtype is registered.</summary>
    public static bool HasHardwareEncoder(Guid subtype)
    {
        var info = new MFT_REGISTER_TYPE_INFO { guidMajorType = MediaType_Video, guidSubtype = subtype };
        var category = new Guid("f79eac7d-e545-4387-bdee-d647d7bde42a"); // MFT_CATEGORY_VIDEO_ENCODER
        // HARDWARE | SYNCMFT | ASYNCMFT | SORTANDFILTER
        if (MFTEnumEx(category, 0x47, IntPtr.Zero, ref info, out var activates, out int count) < 0)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            Marshal.Release(Marshal.ReadIntPtr(activates, i * IntPtr.Size));
        }

        if (activates != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(activates);
        }

        return count > 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_REGISTER_TYPE_INFO
    {
        public Guid guidMajorType;
        public Guid guidSubtype;
    }

    [DllImport("mfplat.dll")]
    private static extern int MFTEnumEx(
        Guid category, uint flags, IntPtr inputType, ref MFT_REGISTER_TYPE_INFO outputType,
        out IntPtr activates, out int count);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateAttributes(out IMFAttributes attributes, uint initialSize);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateDXGIDeviceManager(out uint resetToken, out IMFDXGIDeviceManager manager);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateVideoSampleAllocatorEx(
        ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object allocator);

    [ComImport]
    [Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFAttributes
    {
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
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] int _SetDouble();
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int _SetString();
        [PreserveSig] int _SetBlob();
        [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] int _LockStore();
        [PreserveSig] int _UnlockStore();
        [PreserveSig] int _GetCount();
        [PreserveSig] int _GetItemByIndex();
        [PreserveSig] int _CopyAllItems();
    }

    [ComImport]
    [Guid("eb533d5d-2db6-40f8-97a9-494692014f07")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFDXGIDeviceManager
    {
        [PreserveSig] int CloseDeviceHandle(IntPtr handle);
        [PreserveSig] int GetVideoService(IntPtr handle, ref Guid riid, out IntPtr service);
        [PreserveSig] int LockDevice(IntPtr handle, ref Guid riid, out IntPtr device, bool block);
        [PreserveSig] int OpenDeviceHandle(out IntPtr handle);
        [PreserveSig] int ResetDevice(IntPtr device, uint resetToken);
        [PreserveSig] int TestDevice(IntPtr handle);
        [PreserveSig] int UnlockDevice(IntPtr handle, bool saveState);
    }

    [ComImport]
    [Guid("545b3a48-3283-4f62-866f-a62d8f598f9f")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFVideoSampleAllocatorEx
    {
        // IMFVideoSampleAllocator
        [PreserveSig] int SetDirectXManager([MarshalAs(UnmanagedType.IUnknown)] object manager);
        [PreserveSig] int UninitializeSampleAllocator();
        [PreserveSig] int InitializeSampleAllocator(uint requestedFrames, IMFMediaType type);
        [PreserveSig] int AllocateSample(out IMFSample sample);
        // IMFVideoSampleAllocatorEx
        [PreserveSig] int InitializeSampleAllocatorEx(
            uint initialSamples, uint maximumSamples, IMFAttributes? attributes, IMFMediaType type);
    }

    [ComImport]
    [Guid("e7174cfa-1c9e-48b1-8866-626226bfc258")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFDXGIBuffer
    {
        [PreserveSig] int GetResource(ref Guid riid, out IntPtr resource);
        [PreserveSig] int GetSubresourceIndex(out uint index);
        [PreserveSig] int _GetUnknown();
        [PreserveSig] int _SetUnknown();
    }

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

    public static readonly Guid VideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");

    /// <summary>
    /// Decodes up to <paramref name="maxSamples"/> frames of the first video stream and
    /// returns (decoded frame count, mean luma 0..255). A file that parses but decodes to
    /// nothing or to black fails here — the check the container metadata can't fake.
    /// </summary>
    public static (int Frames, double Luma) ProbeVideo(string path, int maxSamples = 10)
    {
        EnsureStarted();
        Check(MFCreateSourceReaderFromURL(path, IntPtr.Zero, out var reader));
        try
        {
            Check(reader.SetStreamSelection(SOURCE_READER_ALL_STREAMS, false));
            Check(reader.SetStreamSelection(SOURCE_READER_FIRST_VIDEO_STREAM, true));

            Check(MFCreateMediaType(out var nv12));
            try
            {
                var major = MT_MAJOR_TYPE;
                var video = MediaType_Video;
                var sub = MT_SUBTYPE;
                var fmt = VideoFormat_NV12;
                Check(nv12.SetGUID(ref major, ref video));
                Check(nv12.SetGUID(ref sub, ref fmt));
                Check(reader.SetCurrentMediaType(SOURCE_READER_FIRST_VIDEO_STREAM, IntPtr.Zero, nv12));
            }
            finally
            {
                Marshal.ReleaseComObject(nv12);
            }

            int frames = 0;
            long total = 0;
            long count = 0;
            while (frames < maxSamples)
            {
                Check(reader.ReadSample(
                    SOURCE_READER_FIRST_VIDEO_STREAM, 0, out _, out uint flags, out _, out IntPtr samplePtr));
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
                            // NV12: the first two-thirds of the buffer is the luma plane.
                            var luma = new ReadOnlySpan<byte>((void*)data, (int)(length * 2 / 3));
                            // Sample sparsely; exactness is not the point.
                            for (int i = 0; i < luma.Length; i += 251)
                            {
                                total += luma[i];
                                count++;
                            }
                        }

                        Check(buffer.Unlock());
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(buffer);
                    }

                    frames++;
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

            return (frames, count == 0 ? 0 : total / (double)count);
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
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
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
        [PreserveSig] int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
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
