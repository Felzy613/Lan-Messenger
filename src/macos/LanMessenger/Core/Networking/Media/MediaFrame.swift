import Foundation

// Value types for the remote-desktop media frame header.
//
// Wire layout (PROTOCOL.md → Remote Desktop → Media Framing) is:
//
//   [4B length][1B channel][1B flags][8B sequence][8B capture_us][sealed payload]
//
// all big-endian, header 22 bytes, and `length` counts every byte after itself
// — so `length == 18 + sealedPayloadCount`, NOT the plaintext count. That
// distinction is the single easiest thing to get wrong here; see
// MediaFrameCodec.encodeFrame, which is the only sanctioned way to build one.

/// The five multiplexed sub-channels.
///
/// Unknown ids (5-255) are deliberately not an error: a newer peer may add a
/// sub-channel, and the reader's contract is to authenticate the frame and then
/// discard it, not to kill the session over forward compatibility.
enum MediaChannel: UInt8, CaseIterable, Equatable {
    case control = 0
    case video   = 1
    case input   = 2
    case cursor  = 3
    case stats   = 4
}

/// Header flag bits.
///
/// Bits 3-7 are reserved. The validation table says receivers must *ignore*
/// them rather than reject, which is why `MediaFrameHeader` keeps the raw byte
/// alongside this masked view — see the note there.
struct MediaFlags: OptionSet, Equatable {
    let rawValue: UInt8

    static let keyframe      = MediaFlags(rawValue: 1 << 0)
    static let fragmented    = MediaFlags(rawValue: 1 << 1)
    static let finalFragment = MediaFlags(rawValue: 1 << 2)

    /// Bits a current implementation understands.
    static let knownMask: UInt8 = 0b0000_0111
    /// Bits reserved for future use; must round-trip untouched.
    static let reservedMask: UInt8 = 0b1111_1000
}

/// A parsed media frame header.
///
/// Carries BOTH `rawChannel`/`rawFlags` (exactly as they arrived) and the
/// interpreted `channel`/`flags`. That is not redundancy — it is the fix for a
/// bug that would only appear across versions, after shipping: the AEAD's AAD is
/// the 22 plaintext header bytes *as received*, so a reader that parses a header
/// and then re-encodes it to build the AAD will differ by one byte from any peer
/// that sets a reserved flag bit, and every frame will fail its tag check with
/// no other symptom. Readers must therefore retain the received bytes verbatim;
/// the parsed view exists only for interpretation.
struct MediaFrameHeader: Equatable {
    /// On-wire length value: `18 + sealedPayloadCount`.
    let length: Int
    let rawChannel: UInt8
    let rawFlags: UInt8
    let sequence: UInt64
    let captureUs: UInt64

    /// nil for an unknown (forward-compatible) sub-channel id.
    var channel: MediaChannel? { MediaChannel(rawValue: rawChannel) }

    /// Known flag bits only. Reserved bits are intentionally dropped here and
    /// preserved in `rawFlags`.
    var flags: MediaFlags { MediaFlags(rawValue: rawFlags & MediaFlags.knownMask) }

    var isKeyframe: Bool      { flags.contains(.keyframe) }
    var isFragmented: Bool    { flags.contains(.fragmented) }
    var isFinalFragment: Bool { flags.contains(.finalFragment) }

    /// Size of the sealed payload (ciphertext ‖ 16-byte tag) that follows.
    var sealedPayloadLength: Int { length - MediaFrameCodec.postLengthHeader }
}

/// Faults that end a media session.
///
/// Every case here closes the connection. That is deliberate and differs from
/// the file-transfer path, which drops a bad chunk and carries on: on a media
/// channel an authentication failure or a non-increasing sequence is a protocol
/// violation or an attack, not a recoverable blip, and continuing would either
/// reuse an AEAD nonce or accept replayed input.
enum MediaProtocolError: Error, Equatable, CustomStringConvertible {
    case frameTooLarge(Int)
    case frameTooSmall(Int)
    case sequenceNotIncreasing(got: UInt64, last: UInt64)
    case decryptFailed(sequence: UInt64)
    case wholeFrameMidReassembly(MediaChannel)
    case reassemblyOverflow(MediaChannel, bytes: Int)
    case fragmentCountExceeded(MediaChannel, count: Int)
    case linkFailed(Int32)

    var description: String {
        switch self {
        case .frameTooLarge(let n):
            return "media frame length \(n) exceeds \(MediaFrameCodec.maxFrameLength)"
        case .frameTooSmall(let n):
            return "media frame length \(n) is below the \(MediaFrameCodec.minFrameLength)-byte minimum"
        case .sequenceNotIncreasing(let got, let last):
            return "sequence \(got) is not greater than last accepted \(last)"
        case .decryptFailed(let seq):
            return "authentication failed for sequence \(seq)"
        case .wholeFrameMidReassembly(let ch):
            return "unfragmented frame on channel \(ch) with a partial frame pending"
        case .reassemblyOverflow(let ch, let bytes):
            return "reassembly on channel \(ch) exceeded \(bytes) bytes"
        case .fragmentCountExceeded(let ch, let count):
            return "reassembly on channel \(ch) exceeded \(count) fragments"
        case .linkFailed(let code):
            return "media link failed (errno \(code))"
        }
    }
}
