namespace DejaVu;

/// <summary>
/// Whether this session may still encode AV1. The hardware probe only proves an AV1
/// encoder and decoder exist, not that the fragmented-MP4 sink can write what the
/// encoder produces. On some stacks the sink writer accepts the first few samples and
/// only then fails with MF_E_SINK_HEADERS_NOT_FOUND (#7, #9, #10); judged on "failed
/// before its first sample", that never counted, and every rebuilt engine chose AV1
/// again, failing about once a second while the buffer filled with unreadable files.
/// </summary>
/// <remarks>
/// The rule: until one AV1 segment has been finalized and read back, any failure means
/// this PC's AV1 path cannot write MP4 and the session moves to H.264 for good. After
/// that, a failure is a device loss (TDR, driver update, GPU switch) that H.264 would
/// not have survived either, so AV1 stays, except for the header error, which is the
/// sink rejecting this encoder's output and never a device loss. Rejection is sticky:
/// every later segment then matches, and the save mux never sees mixed codecs.
/// </remarks>
internal sealed class Av1Gate
{
    /// <summary>MF_E_SINK_HEADERS_NOT_FOUND: "Sink could not create valid output file
    /// because required headers were not provided to the sink."</summary>
    public const int SinkHeadersNotFound = unchecked((int)0xC00D4A45);

    private readonly object sync = new();
    private bool rejected;
    private bool verified;
    private bool selfTestClaimed;

    public bool Rejected
    {
        get { lock (sync) { return rejected; } }
    }

    /// <summary>An AV1 segment has been read back this session.</summary>
    public bool Verified
    {
        get { lock (sync) { return verified; } }
    }

    public bool Allows(bool hardwareAvailable)
    {
        lock (sync)
        {
            return hardwareAvailable && !rejected;
        }
    }

    /// <summary>An AV1 segment was finalized and decoded back: later failures are
    /// device losses, not this PC's AV1 path being unable to write MP4.</summary>
    public void OnSegmentVerified()
    {
        lock (sync)
        {
            verified = true;
        }
    }

    /// <summary>An AV1 writer failed with <paramref name="hresult"/>. True only for the
    /// call that rejects AV1, so the caller logs and restarts exactly once.</summary>
    public bool OnFailure(int hresult)
    {
        lock (sync)
        {
            if (rejected || (verified && hresult != SinkHeadersNotFound))
            {
                return false;
            }

            rejected = true;
            return true;
        }
    }

    /// <summary>AV1 is out for the session (a failed self-test, an encoder that refused
    /// the stream). True only for the call that rejected it.</summary>
    public bool Reject()
    {
        lock (sync)
        {
            if (rejected)
            {
                return false;
            }

            rejected = true;
            return true;
        }
    }

    /// <summary>True for the one caller that should run the startup self-test; false
    /// once it has been claimed, or when AV1 is already out and there is nothing to test.</summary>
    public bool TryClaimSelfTest()
    {
        lock (sync)
        {
            if (selfTestClaimed || rejected)
            {
                return false;
            }

            selfTestClaimed = true;
            return true;
        }
    }
}
