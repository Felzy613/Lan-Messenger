namespace LanMessenger.Core.Networking.Media;

// The mux: fragmentation, interleaving, and the never-buffer-two-video-frames
// drop policy — as a pure state machine with no threads, no clock and no I/O.
// Mirror of the macOS MediaWriteScheduler.swift.
//
// Keeping this pure is the whole reason the head-of-line-blocking fix is
// testable at all: "an input event never waits behind a keyframe" becomes an
// assertion over a sequence of NextSegment() calls rather than a stopwatch.

/// <summary>One logical frame handed to the scheduler by a producer.</summary>
public readonly record struct MediaOutboundFrame(
    MediaChannel Channel, byte[] Payload, ulong CaptureUs, bool Keyframe = false);

/// <summary>One unit the writer seals and writes: at most MaxFragmentPayload bytes.</summary>
public readonly record struct MediaSegment(
    MediaChannel Channel, MediaFlags Flags, ulong CaptureUs, byte[] Payload);

/// <summary>Outcome of a submission.</summary>
/// <remarks>
/// Never discard it — FileTransferService.cs ignoring TryWrite's bool is the
/// precedent not to copy, because a silently dropped record is
/// indistinguishable from a delivered one.
/// </remarks>
public readonly record struct MediaSubmission(
    MediaSubmissionKind Kind, MediaChannel Channel = MediaChannel.Video, bool WasKeyframe = false)
{
    public static MediaSubmission Queued => new(MediaSubmissionKind.Queued);
    public static MediaSubmission DroppedStaleVideo(bool wasKeyframe) =>
        new(MediaSubmissionKind.DroppedStaleVideo, MediaChannel.Video, wasKeyframe);
    public static MediaSubmission Overflow(MediaChannel channel) =>
        new(MediaSubmissionKind.Overflow, channel);
}

public enum MediaSubmissionKind { Queued, DroppedStaleVideo, Overflow }

public sealed class MediaWriteScheduler
{
    private readonly int _fragmentSize;
    private readonly int _channelDepth;
    // Plain object rather than System.Threading.Lock: that type is .NET 9+ and
    // this project targets net8.0-windows.
    private readonly object _gate = new();

    // Video is held in exactly two slots: the one being fragmented and at most
    // one waiting behind it. This is the only place video is ever buffered.
    private MediaOutboundFrame? _inProgress;
    private int _inProgressCursor;
    private MediaOutboundFrame? _queuedVideo;

    // The four non-video channels, each a bounded FIFO.
    private readonly Queue<MediaOutboundFrame> _control = new();
    private readonly Queue<MediaOutboundFrame> _input   = new();
    private readonly Queue<MediaOutboundFrame> _cursor  = new();
    private readonly Queue<MediaOutboundFrame> _stats   = new();

    public int DroppedVideoFrames { get; private set; }
    public int DroppedKeyframes { get; private set; }

    /// <summary>
    /// Latched when a dropped frame was a keyframe, so the encoder driver can
    /// force an IDR on its next submit. Without this a dropped keyframe leaves
    /// the viewer on a degraded image until the next scheduled one.
    /// </summary>
    public bool KeyframeDropPending { get; private set; }

    public MediaWriteScheduler(
        int fragmentSize = MediaFrameCodec.MaxFragmentPayload,
        int videoDepth = 2,
        int channelDepth = 512)
    {
        if (fragmentSize <= 0) throw new ArgumentOutOfRangeException(nameof(fragmentSize));
        // Bounded(2) plus the in-flight frame would be three, which violates the
        // rule. Two total — one in progress, one queued — is the budget, and it
        // is fixed by the protocol rather than tunable.
        if (videoDepth != 2) throw new ArgumentOutOfRangeException(nameof(videoDepth),
            "the protocol fixes the video budget at two frames");
        _fragmentSize = fragmentSize;
        _channelDepth = channelDepth;
    }

    // ---- Submit ------------------------------------------------------------

    /// <summary>Thread-safe.</summary>
    public MediaSubmission Submit(MediaOutboundFrame frame)
    {
        lock (_gate)
        {
            if (frame.Channel == MediaChannel.Video) return SubmitVideoLocked(frame);

            // A vanished input record is a stuck modifier key on the host, and a
            // vanished control record can be the control_grant itself. So these
            // never drop silently: 512 pending records means the socket is
            // wedged and the session is already dead — say so rather than leak.
            switch (frame.Channel)
            {
                case MediaChannel.Control: return Append(_control, frame);
                case MediaChannel.Input:   return Append(_input, frame);
                case MediaChannel.Cursor:
                    // Cursor is the exception: only the newest position has
                    // meaning, so it replaces rather than queues. Faulting a
                    // session because 513 cursor positions piled up is absurd.
                    _cursor.Clear();
                    _cursor.Enqueue(frame);
                    return MediaSubmission.Queued;
                case MediaChannel.Stats:   return Append(_stats, frame);
                default: throw new InvalidOperationException("unreachable");
            }
        }
    }

    private MediaSubmission Append(Queue<MediaOutboundFrame> queue, MediaOutboundFrame frame)
    {
        if (queue.Count >= _channelDepth) return MediaSubmission.Overflow(frame.Channel);
        queue.Enqueue(frame);
        return MediaSubmission.Queued;
    }

    private MediaSubmission SubmitVideoLocked(MediaOutboundFrame frame)
    {
        if (_inProgress is null)
        {
            _inProgress = frame;
            _inProgressCursor = 0;
            return MediaSubmission.Queued;
        }
        if (_queuedVideo is not { } stale)
        {
            _queuedVideo = frame;
            return MediaSubmission.Queued;
        }
        // Both slots full. Drop the QUEUED frame, never the in-progress one:
        // once fragment 0 of a frame is on the wire the peer is reassembling it,
        // and abandoning it mid-fragmentation leaves a dangling
        // fragmented-without-final that desyncs their reassembler for good. The
        // queued frame is also the staler of the two, so dropping it is the
        // right freshness choice as well as the only safe one.
        _queuedVideo = frame;
        DroppedVideoFrames++;
        if (stale.Keyframe)
        {
            DroppedKeyframes++;
            KeyframeDropPending = true;
        }
        return MediaSubmission.DroppedStaleVideo(stale.Keyframe);
    }

    /// <summary>Consumes the latched keyframe-drop flag.</summary>
    public bool TakeKeyframeDropPending()
    {
        lock (_gate)
        {
            bool pending = KeyframeDropPending;
            KeyframeDropPending = false;
            return pending;
        }
    }

    // ---- Drain -------------------------------------------------------------

    /// <summary>Returns the next unit to seal and write, or null when idle.</summary>
    /// <remarks>
    /// Priority is control -> input -> cursor -> stats -> video, and exactly ONE
    /// video segment is emitted per call. Because the writer loop calls this
    /// afresh after every write, anything submitted while a 500 KiB keyframe is
    /// being fragmented goes out *between* its segments rather than after them.
    ///
    /// The order is deliberate: control carries hello and control_grant, and
    /// arming input behind thirty segments of video is how a grant appears to
    /// hang; input is next because a mouse move stuck behind video is the exact
    /// complaint the protocol calls out.
    /// </remarks>
    public MediaSegment? NextSegment()
    {
        lock (_gate)
        {
            foreach (var queue in new[] { _control, _input, _cursor, _stats })
            {
                if (queue.Count > 0)
                {
                    var frame = queue.Dequeue();
                    return new MediaSegment(frame.Channel, MediaFlags.None, frame.CaptureUs, frame.Payload);
                }
            }
            return NextVideoSegmentLocked();
        }
    }

    private MediaSegment? NextVideoSegmentLocked()
    {
        if (_inProgress is null && _queuedVideo is { } next)
        {
            _inProgress = next;
            _queuedVideo = null;
            _inProgressCursor = 0;
        }
        if (_inProgress is not { } frame) return null;

        int remaining = frame.Payload.Length - _inProgressCursor;
        int take = Math.Min(_fragmentSize, remaining);
        byte[] slice = new byte[take];
        Array.Copy(frame.Payload, _inProgressCursor, slice, 0, take);

        bool isFirst = _inProgressCursor == 0;
        _inProgressCursor += take;
        bool isLast = _inProgressCursor >= frame.Payload.Length;

        // A frame that fits in one fragment must NOT be marked fragmented: a
        // reassembler that sees fragmented-without-final and then a whole frame
        // on the same channel faults, which is correct behaviour against a
        // writer that mislabels.
        var flags = MediaFlags.None;
        if (frame.Keyframe) flags |= MediaFlags.Keyframe;
        if (!(isFirst && isLast))
        {
            flags |= MediaFlags.Fragmented;
            if (isLast) flags |= MediaFlags.FinalFragment;
        }

        if (isLast)
        {
            _inProgress = null;
            _inProgressCursor = 0;
        }
        return new MediaSegment(MediaChannel.Video, flags, frame.CaptureUs, slice);
    }

    /// <summary>True when nothing is waiting.</summary>
    public bool IsIdle
    {
        get
        {
            lock (_gate)
            {
                return _inProgress is null && _queuedVideo is null
                    && _control.Count == 0 && _input.Count == 0
                    && _cursor.Count == 0 && _stats.Count == 0;
            }
        }
    }

    /// <summary>
    /// Drops everything. Called on session teardown so a wedged peer's backlog is
    /// not held alive by the session object.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _inProgress = null; _queuedVideo = null; _inProgressCursor = 0;
            _control.Clear(); _input.Clear(); _cursor.Clear(); _stats.Clear();
        }
    }
}
