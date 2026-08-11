using System.Runtime.InteropServices;

namespace DejaVu;

/// <summary>
/// Stitches MP4 files into one without re-encoding: a Media Foundation source reader pulls
/// the compressed H.264/AAC samples out of each input and a sink writer drops them into the
/// output with retimed timestamps. Pass-through remuxing — fast, lossless, no transforms.
/// Inputs must share encoding parameters, which ours do because one recorder produced them.
/// </summary>
internal static class Mp4Concat
{
    public static void Concat(IReadOnlyList<string> inputs, string output)
    {
        EnsureStarted();

        Check(MFCreateSinkWriterFromURL(output, IntPtr.Zero, IntPtr.Zero, out var writer));
        try
        {
            // Writer streams are declared from the first input's native types; later inputs
            // map onto them by major type (video/audio).
            int videoOut = -1;
            int audioOut = -1;
            bool began = false;
            long offset = 0;

            foreach (var input in inputs)
            {
                Check(MFCreateSourceReaderFromURL(input, IntPtr.Zero, out var reader));
                try
                {
                    Check(reader.SetStreamSelection(MF_SOURCE_READER_ALL_STREAMS, true));

                    var streamMap = new Dictionary<uint, int>();
                    for (uint i = 0; reader.GetNativeMediaType(i, 0, out var type) == 0; i++)
                    {
                        Check(type.GetMajorType(out var major));
                        int outIndex;
                        if (major == MFMediaType_Video)
                        {
                            if (videoOut < 0)
                            {
                                Check(writer.AddStream(type, out videoOut));
                                Check(writer.SetInputMediaType(videoOut, type, IntPtr.Zero));
                            }

                            outIndex = videoOut;
                        }
                        else if (major == MFMediaType_Audio)
                        {
                            if (audioOut < 0)
                            {
                                Check(writer.AddStream(type, out audioOut));
                                Check(writer.SetInputMediaType(audioOut, type, IntPtr.Zero));
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
                        Check(writer.BeginWriting());
                        began = true;
                    }

                    long fileEnd = 0;
                    int ended = 0;
                    while (ended < streamMap.Count)
                    {
                        Check(reader.ReadSample(
                            MF_SOURCE_READER_ANY_STREAM, 0, out uint streamIndex, out uint flags,
                            out _, out IntPtr samplePtr));

                        if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                        {
                            ended++;
                        }

                        if (samplePtr == IntPtr.Zero)
                        {
                            continue;
                        }

                        var sample = (IMFSample)Marshal.GetObjectForIUnknown(samplePtr);
                        Marshal.Release(samplePtr);

                        if (streamMap.TryGetValue(streamIndex, out int outIndex))
                        {
                            Check(sample.GetSampleTime(out long time));
                            // Duration is optional on compressed samples; absent counts as zero.
                            long duration = sample.GetSampleDuration(out long d) == 0 ? d : 0;
                            fileEnd = Math.Max(fileEnd, time + duration);

                            Check(sample.SetSampleTime(time + offset));
                            Check(writer.WriteSample(outIndex, sample));
                        }

                        Marshal.ReleaseComObject(sample);
                    }

                    offset += fileEnd;
                }
                finally
                {
                    Marshal.ReleaseComObject(reader);
                }
            }

            Check(writer.Finalize_());
        }
        finally
        {
            Marshal.ReleaseComObject(writer);
        }
    }

    private static readonly object StartGate = new();
    private static bool started;

    private static void EnsureStarted()
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

    private static void Check(int hr)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    private const int MF_VERSION = 0x00020070;
    private const uint MF_SOURCE_READER_ALL_STREAMS = 0xFFFFFFFE;
    private const uint MF_SOURCE_READER_ANY_STREAM = 0xFFFFFFFE;
    private const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x00000002;

    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00AA00389B71");

    [DllImport("mfplat.dll")]
    private static extern int MFStartup(int version, int flags);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL(
        string url, IntPtr attributes, out IMFSourceReader reader);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    private static extern int MFCreateSinkWriterFromURL(
        string url, IntPtr byteStream, IntPtr attributes, out IMFSinkWriter writer);

    // The interfaces below are declared flat (no managed inheritance) because the CLR lays
    // out ComImport vtables per declared interface; only the slots actually called carry
    // real signatures, the rest are order-preserving placeholders.

    [ComImport]
    [Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSourceReader
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
    private interface IMFSinkWriter
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
    private interface IMFMediaType
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
    private interface IMFSample
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
        [PreserveSig] int _SetSampleDuration();
        [PreserveSig] int _GetBufferCount();
        [PreserveSig] int _GetBufferByIndex();
        [PreserveSig] int _ConvertToContiguousBuffer();
        [PreserveSig] int _AddBuffer();
        [PreserveSig] int _RemoveBufferByIndex();
        [PreserveSig] int _RemoveAllBuffers();
        [PreserveSig] int _GetTotalLength();
        [PreserveSig] int _CopyToBuffer();
    }
}
