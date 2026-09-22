using LanMessenger.Core.Crypto;

namespace LanMessenger.Core.Networking.Media;

// One frame per call, in a fixed order, with every bound checked before any
// allocation. Mirror of the macOS MediaFrameReader.swift.
//
// Does no queue work, starts no timers, and never marshals to the UI thread —
// routing 30+ frames a second through DispatcherQueue.TryEnqueue would put the
// same shape of load on the UI thread that already had to be throttled to 12 Hz
// on the file-transfer progress path.

public enum MediaReadKind { Frame, Partial, Dropped, Closed, Fault }

public readonly record struct MediaReadOutcome(
    MediaReadKind Kind,
    MediaInboundFrame Frame = default,
    byte DroppedChannel = 0,
    string Reason = "",
    MediaFaultKind Fault = default)
{
    public static MediaReadOutcome FrameRead(MediaInboundFrame f) => new(MediaReadKind.Frame, f);
    public static MediaReadOutcome Partial => new(MediaReadKind.Partial);
    public static MediaReadOutcome Dropped(byte channel, string reason) =>
        new(MediaReadKind.Dropped, default, channel, reason);
    public static MediaReadOutcome Closed => new(MediaReadKind.Closed);
    public static MediaReadOutcome FaultWith(MediaFaultKind kind, string reason) =>
        new(MediaReadKind.Fault, default, 0, reason, kind);
}

public sealed class MediaFrameReader
{
    private readonly IMediaLink _link;
    private readonly byte[] _openingKey;
    private readonly byte[] _openingSalt;
    private readonly int _maxFrameLength;
    private readonly MediaReassembler _reassembler;
    private MediaSequenceGate _gate;

    /// <summary>Set by the session when control_grant is seen or sent.</summary>
    /// <remarks>
    /// Never inferred from remote_accept: accepting an invite grants viewing
    /// only, and control is a separate escalation the host approves. A reader
    /// that armed input on accept would silently turn a view-only session into a
    /// controllable one.
    /// </remarks>
    public bool InputArmed { get; set; }

    public ulong FramesRead { get; private set; }
    public ulong BytesRead { get; private set; }

    public MediaFrameReader(
        IMediaLink link, byte[] openingKey, byte[] openingSalt,
        int maxFrameLength = MediaFrameCodec.MaxFrameLength, MediaReassembler? reassembler = null)
    {
        _link = link;
        _openingKey = openingKey;
        _openingSalt = openingSalt;
        _maxFrameLength = maxFrameLength;
        _reassembler = reassembler ?? new MediaReassembler();
        _gate = new MediaSequenceGate();
    }

    /// <summary>Reads, authenticates and reassembles exactly one frame.</summary>
    public MediaReadOutcome ReadFrame()
    {
        // 1. Length prefix.
        byte[] header = new byte[MediaFrameCodec.HeaderLength];
        switch (_link.ReadExact(header, 4))
        {
            case MediaLinkRead.Closed: return MediaReadOutcome.Closed;
            case MediaLinkRead.Failed: return MediaReadOutcome.FaultWith(MediaFaultKind.LinkFailed, "link read failed");
        }

        // 2. Validate BEFORE allocating. The JSON path sizes its buffer from the
        //    length and checks afterwards, which is affordable once per message
        //    and is not affordable thirty times a second.
        int length;
        try { length = MediaFrameCodec.DecodeLength(header); }
        catch (MediaProtocolException ex) { return MediaReadOutcome.FaultWith(ex.Kind, ex.Message); }
        if (length > _maxFrameLength)
            return MediaReadOutcome.FaultWith(MediaFaultKind.FrameTooLarge, $"length {length}");

        // 3. Remaining header bytes, read straight into the same buffer so the
        //    22 bytes stay contiguous and VERBATIM — they are the AEAD's AAD and
        //    must never be rebuilt from the parsed struct, because reserved flag
        //    bits have to round-trip untouched.
        byte[] rest = new byte[MediaFrameCodec.PostLengthHeader];
        switch (_link.ReadExact(rest, MediaFrameCodec.PostLengthHeader))
        {
            case MediaLinkRead.Closed: return MediaReadOutcome.Closed;
            case MediaLinkRead.Failed: return MediaReadOutcome.FaultWith(MediaFaultKind.LinkFailed, "link read failed");
        }
        rest.CopyTo(header, 4);

        MediaFrameHeader parsed;
        try { parsed = MediaFrameCodec.DecodeHeader(header); }
        catch (MediaProtocolException ex) { return MediaReadOutcome.FaultWith(ex.Kind, ex.Message); }

        // 4. Sequence gate, before any decryption work. A non-increasing sequence
        //    is what would otherwise let an AEAD nonce repeat.
        ulong last = _gate.LastAccepted ?? 0;
        if (!_gate.Admit(parsed.Sequence))
            return MediaReadOutcome.FaultWith(MediaFaultKind.SequenceNotIncreasing,
                $"sequence {parsed.Sequence} is not greater than last accepted {last}");

        // 5. Sealed body, sized from the already-validated length.
        int bodyCount = parsed.SealedPayloadLength;
        byte[] body = new byte[bodyCount];
        switch (_link.ReadExact(body, bodyCount))
        {
            case MediaLinkRead.Closed: return MediaReadOutcome.Closed;
            case MediaLinkRead.Failed: return MediaReadOutcome.FaultWith(MediaFaultKind.LinkFailed, "link read failed");
        }

        // 6. Authenticate. On a media channel an AEAD failure is a protocol
        //    violation, never a droppable chunk — the file-transfer path's
        //    blanket "drop the bad chunk and carry on" is explicitly not the
        //    model, because here it would mean accepting forged input.
        byte[] plaintext;
        try
        {
            plaintext = MediaFrameCodec.DecodePayload(parsed, header, body, _openingKey, _openingSalt);
        }
        catch (MediaProtocolException ex) { return MediaReadOutcome.FaultWith(ex.Kind, ex.Message); }

        FramesRead++;
        BytesRead += (ulong)(4 + length);

        // 7. Dispatch. Unknown channels are decrypted first — so the frame is
        //    proved authentic and a fault is not misattributed — and only then
        //    discarded. That is forward compatibility, not a close.
        if (parsed.Channel is not { } channel)
            return MediaReadOutcome.Dropped(parsed.RawChannel, "unknown sub-channel");
        if (channel == MediaChannel.Input && !InputArmed)
            return MediaReadOutcome.Dropped(parsed.RawChannel, "input before control_grant");

        // 8. Reassembly, keyed per channel so an interleaved frame on another
        //    sub-channel cannot disturb a partially assembled video frame.
        var outcome = _reassembler.Accept(parsed, plaintext);
        return outcome.Kind switch
        {
            MediaReassemblyKind.Complete => MediaReadOutcome.FrameRead(outcome.Frame),
            MediaReassemblyKind.Partial  => MediaReadOutcome.Partial,
            _                            => MediaReadOutcome.FaultWith(outcome.Fault, outcome.FaultMessage),
        };
    }
}

/// <summary>
/// Seals and writes one segment, allocating its sequence inside the call so that
/// allocation order equals wire order.
/// </summary>
/// <remarks>
/// Single-caller by contract: only the session's writer task. Allocating a
/// sequence at submit time instead would break under interleaving — submit order
/// and wire order differ, so the peer would see a non-increasing sequence, be
/// obliged to close, and the bug would reproduce only under load, only with mixed
/// sub-channels, and look exactly like a network fault.
/// </remarks>
public sealed class MediaFrameWriter(IMediaLink link, byte[] sealingKey, byte[] sealingSalt)
{
    private ulong _sequence;

    public ulong NextSequence => _sequence;
    public ulong FramesWritten { get; private set; }
    public ulong BytesWritten { get; private set; }

    /// <summary>PRECONDITION: called only from the session's writer task.</summary>
    public bool WriteSegment(MediaSegment segment)
    {
        ulong seq = _sequence++;
        try
        {
            byte[] frame = MediaFrameCodec.EncodeFrame(
                segment.Channel, segment.Flags, seq, segment.CaptureUs,
                segment.Payload, sealingKey, sealingSalt);
            // One contiguous write. Splitting header and body would emit two
            // small packets per frame with NoDelay set and hand back exactly the
            // latency that disabling Nagle bought.
            if (!link.WriteAll(frame)) return false;
            FramesWritten++;
            BytesWritten += (ulong)frame.Length;
            return true;
        }
        catch (MediaProtocolException) { return false; }
    }
}
