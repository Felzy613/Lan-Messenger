namespace LanMessenger.Core.Networking.Media;

// The demux: per-channel fragment buffers, so an interleaved control or input
// frame arriving between two video segments never disturbs the partially
// assembled video frame. That per-channel keying is the reader-side half of the
// writer's interleaving design — without it, interleaving would corrupt video.
// Mirror of the macOS MediaReassembler.swift.

/// <summary>A fully reassembled inbound frame.</summary>
public readonly record struct MediaInboundFrame(
    MediaChannel Channel,
    MediaFlags Flags,
    /// Sequence of the FINAL fragment.
    ulong Sequence,
    /// Capture timestamp from the FIRST fragment.
    ulong CaptureUs,
    byte[] Payload,
    int FragmentCount);

public enum MediaReassemblyKind { Complete, Partial, Fault }

public readonly record struct MediaReassemblyOutcome(
    MediaReassemblyKind Kind,
    MediaInboundFrame Frame = default,
    MediaFaultKind Fault = default,
    string FaultMessage = "")
{
    public static MediaReassemblyOutcome Complete(MediaInboundFrame f) =>
        new(MediaReassemblyKind.Complete, f);
    public static MediaReassemblyOutcome Partial => new(MediaReassemblyKind.Partial);
    public static MediaReassemblyOutcome FaultWith(MediaFaultKind kind, string message) =>
        new(MediaReassemblyKind.Fault, default, kind, message);
}

public sealed class MediaReassembler
{
    private sealed class Pending
    {
        public readonly List<byte> Payload = [];
        public ulong CaptureUs;
        public MediaFlags Flags;
        public int FragmentCount;
    }

    private readonly Dictionary<byte, Pending> _pending = [];
    private readonly int _maxAssembledBytes;
    private readonly int _maxFragments;

    /// <summary>Two caps, not one, and they close different holes.</summary>
    /// <remarks>
    /// The 4 MiB wire cap bounds a single *frame*; it says nothing about the
    /// reassembled total, so the byte cap is needed to bound a long fragment
    /// chain. But a byte cap alone still lets a stream of zero-length fragments
    /// grow a per-channel buffer forever while every individual frame passes the
    /// wire check — a real allocation vector — so the fragment count is capped
    /// independently.
    /// </remarks>
    public MediaReassembler(
        int maxAssembledBytes = MediaFrameCodec.MaxFrameLength, int maxFragments = 512)
    {
        _maxAssembledBytes = maxAssembledBytes;
        _maxFragments = maxFragments;
    }

    public MediaReassemblyOutcome Accept(MediaFrameHeader header, byte[] plaintext)
    {
        if (header.Channel is not { } channel) return MediaReassemblyOutcome.Partial;
        byte key = header.RawChannel;

        // Not fragmented: a whole frame in one go.
        if (!header.IsFragmented)
        {
            if (_pending.ContainsKey(key))
            {
                // The writer never does this, so it is corruption or an attack.
                _pending.Remove(key);
                return MediaReassemblyOutcome.FaultWith(MediaFaultKind.WholeFrameMidReassembly,
                    $"unfragmented frame on channel {channel} with a partial frame pending");
            }
            return MediaReassemblyOutcome.Complete(new MediaInboundFrame(
                channel, header.Flags, header.Sequence, header.CaptureUs, plaintext, 1));
        }

        if (!_pending.TryGetValue(key, out var entry))
        {
            entry = new Pending { CaptureUs = header.CaptureUs, Flags = header.Flags };
            _pending[key] = entry;
        }

        if (entry.FragmentCount + 1 > _maxFragments)
        {
            _pending.Remove(key);
            return MediaReassemblyOutcome.FaultWith(MediaFaultKind.FragmentCountExceeded,
                $"reassembly on channel {channel} exceeded {entry.FragmentCount + 1} fragments");
        }
        if (entry.Payload.Count + plaintext.Length > _maxAssembledBytes)
        {
            _pending.Remove(key);
            return MediaReassemblyOutcome.FaultWith(MediaFaultKind.ReassemblyOverflow,
                $"reassembly on channel {channel} exceeded {entry.Payload.Count + plaintext.Length} bytes");
        }

        entry.Payload.AddRange(plaintext);
        entry.FragmentCount++;

        if (!header.IsFinalFragment) return MediaReassemblyOutcome.Partial;

        _pending.Remove(key);
        return MediaReassemblyOutcome.Complete(new MediaInboundFrame(
            channel,
            entry.Flags,          // first fragment's flags, incl. keyframe
            header.Sequence,      // final fragment's sequence
            entry.CaptureUs,      // first fragment's timestamp
            [.. entry.Payload],
            entry.FragmentCount));
    }

    /// <summary>Bytes currently held for a channel. Tests and the stats summary only.</summary>
    public int PendingBytes(MediaChannel channel) =>
        _pending.TryGetValue((byte)channel, out var e) ? e.Payload.Count : 0;

    public void Reset() => _pending.Clear();
}

/// <summary>Strictly-increasing sequence enforcement.</summary>
/// <remarks>
/// LastAccepted starts null rather than 0, and that choice is load-bearing. The
/// protocol says a reconnect restarts sequencing from zero against a key that has
/// never been used, so the first frame of a session legitimately carries sequence
/// 0. Initialising to 0 and requiring "> last" would reject it, and the session
/// would die at hello with a sequence violation that looks exactly like a crypto
/// fault — a miserable thing to diagnose across two platforms. The null sentinel
/// accepts the first frame whatever its value and enforces strict increase from
/// then on.
/// </remarks>
public struct MediaSequenceGate
{
    public ulong? LastAccepted { get; private set; }

    /// <summary>Returns false if the frame must be rejected (and the session closed).</summary>
    public bool Admit(ulong sequence)
    {
        if (LastAccepted is { } last && sequence <= last) return false;
        LastAccepted = sequence;
        return true;
    }
}
