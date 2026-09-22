using LanMessenger.Core.Crypto;
using System.Buffers.Binary;

namespace LanMessenger.Core.Networking.Media;

// Value types and codec for the remote-desktop media frame.
// Mirror of the macOS MediaFrame.swift + MediaFrameCodec.swift — the two MUST
// agree byte for byte, which is what media_frame_vector.json exists to prove.
//
// Wire layout (PROTOCOL.md -> Remote Desktop -> Media Framing):
//
//   [4B length][1B channel][1B flags][8B sequence][8B capture_us][sealed payload]
//
// all big-endian, header 22 bytes, and `length` counts every byte after itself
// -- so `length == 18 + sealedPayloadCount`, NOT the plaintext count. That
// distinction is the single easiest thing to get wrong here; see
// MediaFrameCodec.EncodeFrame, which is the only sanctioned way to build one.

/// <summary>The five multiplexed sub-channels.</summary>
/// <remarks>
/// Unknown ids (5-255) are deliberately not an error: a newer peer may add a
/// sub-channel, and the reader's contract is to authenticate the frame and then
/// discard it, not to kill the session over forward compatibility.
/// </remarks>
public enum MediaChannel : byte
{
    Control = 0,
    Video   = 1,
    Input   = 2,
    Cursor  = 3,
    Stats   = 4,
}

/// <summary>Header flag bits.</summary>
/// <remarks>
/// Bits 3-7 are reserved. The validation table says receivers must *ignore*
/// them rather than reject, which is why <see cref="MediaFrameHeader"/> keeps
/// the raw byte alongside the masked view.
/// </remarks>
[Flags]
public enum MediaFlags : byte
{
    None          = 0,
    Keyframe      = 1 << 0,
    Fragmented    = 1 << 1,
    FinalFragment = 1 << 2,
}

public static class MediaFlagsExtensions
{
    /// <summary>Bits a current implementation understands.</summary>
    public const byte KnownMask = 0b0000_0111;
    /// <summary>Bits reserved for future use; must round-trip untouched.</summary>
    public const byte ReservedMask = 0b1111_1000;
}

/// <summary>A parsed media frame header.</summary>
/// <remarks>
/// Carries BOTH RawChannel/RawFlags (exactly as they arrived) and the
/// interpreted Channel/Flags. That is not redundancy — it is the fix for a bug
/// that would only appear across versions, after shipping: the AEAD's AAD is the
/// 22 plaintext header bytes *as received*, so a reader that parses a header and
/// then re-encodes it to build the AAD will differ by one byte from any peer
/// that sets a reserved flag bit, and every frame will fail its tag check with
/// no other symptom. Readers must therefore retain the received bytes verbatim;
/// the parsed view exists only for interpretation.
/// </remarks>
public readonly record struct MediaFrameHeader(
    int Length,            // on-wire value: 18 + sealedPayloadCount
    byte RawChannel,
    byte RawFlags,
    ulong Sequence,
    ulong CaptureUs)
{
    /// <summary>null for an unknown (forward-compatible) sub-channel id.</summary>
    public MediaChannel? Channel =>
        Enum.IsDefined(typeof(MediaChannel), RawChannel) ? (MediaChannel)RawChannel : null;

    /// <summary>Known flag bits only; reserved bits are preserved in RawFlags.</summary>
    public MediaFlags Flags => (MediaFlags)(RawFlags & MediaFlagsExtensions.KnownMask);

    public bool IsKeyframe      => Flags.HasFlag(MediaFlags.Keyframe);
    public bool IsFragmented    => Flags.HasFlag(MediaFlags.Fragmented);
    public bool IsFinalFragment => Flags.HasFlag(MediaFlags.FinalFragment);

    /// <summary>Size of the sealed payload (ciphertext ‖ 16-byte tag) that follows.</summary>
    public int SealedPayloadLength => Length - MediaFrameCodec.PostLengthHeader;
}

/// <summary>Faults that end a media session.</summary>
/// <remarks>
/// Every kind here closes the connection. That is deliberate and differs from
/// the file-transfer path, which drops a bad chunk and carries on: on a media
/// channel an authentication failure or a non-increasing sequence is a protocol
/// violation or an attack, not a recoverable blip, and continuing would either
/// reuse an AEAD nonce or accept replayed input.
/// </remarks>
public enum MediaFaultKind
{
    FrameTooLarge,
    FrameTooSmall,
    SequenceNotIncreasing,
    DecryptFailed,
    WholeFrameMidReassembly,
    ReassemblyOverflow,
    FragmentCountExceeded,
    LinkFailed,
}

public sealed class MediaProtocolException : Exception
{
    public MediaFaultKind Kind { get; }
    public MediaProtocolException(MediaFaultKind kind, string message) : base(message) => Kind = kind;
}

/// <summary>
/// Pure encode/decode for media frames. No sockets, no state, no clock — this is
/// the unit the shared golden vector pins byte-for-byte across both platforms.
/// </summary>
public static class MediaFrameCodec
{
    /// <summary>Full header size, length prefix included. Matches the AAD length.</summary>
    public const int HeaderLength = RemoteSessionCrypto.HeaderSize;   // 22

    /// <summary>Header bytes the length prefix counts: channel + flags + sequence + capture_us.</summary>
    public const int PostLengthHeader = 18;

    /// <summary>Media frames are capped far below the 50 MiB JSON frame cap.</summary>
    /// <remarks>
    /// This constant is deliberately independent of FrameCodec.MaxFrameSize and
    /// must stay that way: the reader allocates a body buffer straight from an
    /// attacker-supplied length prefix, thirty times a second. Reusing the JSON
    /// cap here would turn one forged header into a 50 MiB allocation.
    /// MediaCapIsIndependentOfTheJsonCap fails loudly if a future "unify the two
    /// codecs" refactor collapses them.
    /// </remarks>
    public const int MaxFrameLength = 4 * 1024 * 1024;

    /// <summary>
    /// The smallest well-formed frame: the post-length header plus a bare GCM tag
    /// with empty ciphertext. Anything shorter cannot be a frame at all.
    /// </summary>
    public const int MinFrameLength = PostLengthHeader + RemoteSessionCrypto.TagSize;   // 34

    /// <summary>Video fragment size, so a large keyframe cannot park an input event.</summary>
    public const int MaxFragmentPayload = 16 * 1024;

    /// <summary>Largest plaintext that still fits the wire cap once sealed.</summary>
    public static int MaxPlaintext => MaxFrameLength - PostLengthHeader - RemoteSessionCrypto.TagSize;

    /// <summary>Sealed size for a given plaintext: AES-GCM appends a 16-byte tag.</summary>
    public static int SealedLength(int plaintextCount) => plaintextCount + RemoteSessionCrypto.TagSize;

    // ---- Encode ------------------------------------------------------------

    /// <summary>Builds the 22 header bytes.</summary>
    /// <remarks>
    /// <paramref name="sealedPayloadCount"/> is the SEALED size — ciphertext plus
    /// the 16-byte tag — not the plaintext size. Passing the plaintext count
    /// produces a length that is 16 short, which the peer's AEAD then rejects on
    /// every single frame with no other symptom, because the length prefix is
    /// inside the AAD. This is why EncodeFrame is the only sanctioned constructor.
    /// </remarks>
    public static byte[] EncodeHeader(
        MediaChannel channel, MediaFlags flags, ulong sequence, ulong captureUs, int sealedPayloadCount)
    {
        byte[] header = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0), (uint)(PostLengthHeader + sealedPayloadCount));
        header[4] = (byte)channel;
        header[5] = (byte)flags;
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(6), sequence);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(14), captureUs);
        return header;
    }

    /// <summary>Seals one payload and returns the complete on-wire frame.</summary>
    /// <remarks>
    /// The header is built first — with the sealed length already in it — and then
    /// used verbatim as the AEAD's AAD. There is deliberately no path that seals
    /// first and prefixes a length afterwards: the length is part of what is
    /// authenticated, so it has to exist before the seal.
    ///
    /// Returns one contiguous buffer on purpose. The caller must hand it to the
    /// link in a single write; splitting header and body into two writes with
    /// TCP_NODELAY set emits two small packets per frame and gives back exactly
    /// the latency that disabling Nagle bought.
    /// </remarks>
    public static byte[] EncodeFrame(
        MediaChannel channel, MediaFlags flags, ulong sequence, ulong captureUs,
        ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key, ReadOnlySpan<byte> salt)
    {
        if (plaintext.Length > MaxPlaintext)
            throw new MediaProtocolException(MediaFaultKind.FrameTooLarge,
                $"plaintext {plaintext.Length} exceeds {MaxPlaintext}");

        byte[] header = EncodeHeader(channel, flags, sequence, captureUs, SealedLength(plaintext.Length));
        byte[] sealedBody = RemoteSessionCrypto.Seal(plaintext, header, key, salt, sequence);

        byte[] frame = new byte[header.Length + sealedBody.Length];
        header.CopyTo(frame, 0);
        sealedBody.CopyTo(frame, header.Length);
        return frame;
    }

    // ---- Decode ------------------------------------------------------------

    /// <summary>Parses the 4-byte length prefix and validates it BEFORE any allocation.</summary>
    /// <remarks>
    /// Kept separate from DecodeHeader precisely so the reader can bound-check
    /// before sizing a buffer — the JSON path's ReadFrameAsync allocates from the
    /// length first and checks after, which is affordable once per message and is
    /// not affordable thirty times a second.
    /// </remarks>
    public static int DecodeLength(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4) throw new ArgumentException("length prefix needs 4 bytes", nameof(bytes));
        uint raw = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if (raw > MaxFrameLength)
            throw new MediaProtocolException(MediaFaultKind.FrameTooLarge,
                $"media frame length {raw} exceeds {MaxFrameLength}");
        int length = (int)raw;
        if (length < MinFrameLength)
            throw new MediaProtocolException(MediaFaultKind.FrameTooSmall,
                $"media frame length {length} is below the {MinFrameLength}-byte minimum");
        return length;
    }

    /// <summary>Parses a complete 22-byte header.</summary>
    public static MediaFrameHeader DecodeHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderLength)
            throw new MediaProtocolException(MediaFaultKind.FrameTooSmall,
                $"header needs {HeaderLength} bytes, got {bytes.Length}");
        int length = DecodeLength(bytes);
        return new MediaFrameHeader(
            Length: length,
            RawChannel: bytes[4],
            RawFlags: bytes[5],
            Sequence: BinaryPrimitives.ReadUInt64BigEndian(bytes[6..14]),
            CaptureUs: BinaryPrimitives.ReadUInt64BigEndian(bytes[14..22]));
    }

    /// <summary>Opens a sealed body against the header bytes exactly as received.</summary>
    /// <remarks>
    /// <paramref name="headerBytes"/> must be the received 22 bytes, never a
    /// re-encode of the parsed header — see the note on MediaFrameHeader.
    /// </remarks>
    public static byte[] DecodePayload(
        MediaFrameHeader header, ReadOnlySpan<byte> headerBytes, ReadOnlySpan<byte> sealedBody,
        ReadOnlySpan<byte> key, ReadOnlySpan<byte> salt)
    {
        try
        {
            return RemoteSessionCrypto.Open(sealedBody, headerBytes, key, salt, header.Sequence);
        }
        catch (Exception ex) when (ex is not MediaProtocolException)
        {
            throw new MediaProtocolException(MediaFaultKind.DecryptFailed,
                $"authentication failed for sequence {header.Sequence}");
        }
    }
}
