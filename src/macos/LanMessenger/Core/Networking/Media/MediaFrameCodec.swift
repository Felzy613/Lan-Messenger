import Foundation
import CryptoKit

// Pure encode/decode for media frames. No sockets, no state, no clock — this is
// the unit the shared golden vector pins byte-for-byte across both platforms.
//
// See PROTOCOL.md → Remote Desktop → Media Framing, which is authoritative.
enum MediaFrameCodec {

    /// Full header size, length prefix included. Matches the AAD length the
    /// crypto layer expects.
    static let headerLength = RemoteSessionCrypto.headerLength      // 22

    /// Header bytes that the length prefix counts: channel + flags + sequence +
    /// capture_us.
    static let postLengthHeader = 18

    /// Media frames are capped far below the 50 MiB JSON frame cap.
    ///
    /// This constant is deliberately independent of `FrameCodec.maxFrameSize`
    /// and must stay that way: the reader allocates a body buffer straight from
    /// an attacker-supplied length prefix, thirty times a second. Reusing the
    /// JSON cap here would turn one forged header into a 50 MiB allocation.
    /// `testMediaCapIsIndependentOfTheJSONCap` fails loudly if a future
    /// "unify the two codecs" refactor collapses them.
    static let maxFrameLength = 4 * 1024 * 1024

    /// The smallest well-formed frame: the post-length header plus a bare GCM
    /// tag with empty ciphertext. Anything shorter cannot be a frame at all, so
    /// rejecting it is an implication of the spec's bounds rather than a new rule.
    static let minFrameLength = postLengthHeader + RemoteSessionCrypto.tagLength   // 34

    /// Video is fragmented at this size so a large keyframe cannot park a
    /// 20-byte input event behind it.
    static let maxFragmentPayload = 16 * 1024

    /// Largest plaintext that still fits the wire cap once sealed.
    static var maxPlaintext: Int {
        maxFrameLength - postLengthHeader - RemoteSessionCrypto.tagLength
    }

    /// Sealed size for a given plaintext: AES-GCM appends a 16-byte tag.
    static func sealedLength(plaintextCount: Int) -> Int {
        plaintextCount + RemoteSessionCrypto.tagLength
    }

    // MARK: - Encode

    /// Builds the 22 header bytes.
    ///
    /// `sealedPayloadCount` is the SEALED size — ciphertext plus the 16-byte tag
    /// — not the plaintext size. Passing the plaintext count produces a length
    /// that is 16 short, which the peer's AEAD then rejects on every single
    /// frame with no other symptom, because the length prefix is inside the AAD.
    /// This is why `encodeFrame` below is the only sanctioned constructor.
    static func encodeHeader(
        channel: MediaChannel,
        flags: MediaFlags,
        sequence: UInt64,
        captureUs: UInt64,
        sealedPayloadCount: Int
    ) -> Data {
        var header = Data(capacity: headerLength)
        let length = UInt32(postLengthHeader + sealedPayloadCount)
        withUnsafeBytes(of: length.bigEndian)    { header.append(contentsOf: $0) }
        header.append(channel.rawValue)
        header.append(flags.rawValue)
        withUnsafeBytes(of: sequence.bigEndian)  { header.append(contentsOf: $0) }
        withUnsafeBytes(of: captureUs.bigEndian) { header.append(contentsOf: $0) }
        return header
    }

    /// Seals one payload and returns the complete on-wire frame.
    ///
    /// The header is built first — with the sealed length already in it — and
    /// then used verbatim as the AEAD's AAD. There is deliberately no path that
    /// seals first and prefixes a length afterwards: the length is part of what
    /// is authenticated, so it has to exist before the seal.
    ///
    /// Returns one contiguous buffer on purpose. The caller must hand it to the
    /// link in a single write; splitting the header and body into two writes
    /// with TCP_NODELAY set emits two small packets per frame and gives back
    /// exactly the latency that disabling Nagle bought.
    static func encodeFrame(
        channel: MediaChannel,
        flags: MediaFlags,
        sequence: UInt64,
        captureUs: UInt64,
        plaintext: Data,
        key: SymmetricKey,
        salt: Data
    ) throws -> Data {
        guard plaintext.count <= maxPlaintext else {
            throw MediaProtocolError.frameTooLarge(postLengthHeader + sealedLength(plaintextCount: plaintext.count))
        }
        let header = encodeHeader(
            channel: channel,
            flags: flags,
            sequence: sequence,
            captureUs: captureUs,
            sealedPayloadCount: sealedLength(plaintextCount: plaintext.count))

        let sealed = try RemoteSessionCrypto.seal(
            payload: plaintext, header: header, key: key, salt: salt, sequence: sequence)

        var frame = Data(capacity: header.count + sealed.count)
        frame.append(header)
        frame.append(sealed)
        return frame
    }

    // MARK: - Decode

    /// Parses the 4-byte length prefix and validates it BEFORE any allocation.
    ///
    /// Kept separate from `decodeHeader` precisely so the reader can bound-check
    /// before sizing a buffer — the JSON path's `recvFrame` allocates from the
    /// length first and checks after, which is affordable once per message and
    /// is not affordable thirty times a second.
    static func decodeLength(_ bytes: Data) throws -> Int {
        precondition(bytes.count >= 4, "length prefix needs 4 bytes")
        let raw = bytes.prefix(4).withUnsafeBytes { UInt32(bigEndian: $0.loadUnaligned(as: UInt32.self)) }
        let length = Int(raw)
        guard length <= maxFrameLength else { throw MediaProtocolError.frameTooLarge(length) }
        guard length >= minFrameLength else { throw MediaProtocolError.frameTooSmall(length) }
        return length
    }

    /// Parses a complete 22-byte header. `bytes` must be the length prefix
    /// followed by the 18 remaining header bytes.
    static func decodeHeader(_ bytes: Data) throws -> MediaFrameHeader {
        guard bytes.count >= headerLength else {
            throw MediaProtocolError.frameTooSmall(bytes.count)
        }
        let length = try decodeLength(bytes)
        let base = bytes.startIndex
        return MediaFrameHeader(
            length: length,
            rawChannel: bytes[base + 4],
            rawFlags: bytes[base + 5],
            sequence: bytes[(base + 6)..<(base + 14)].withUnsafeBytes {
                UInt64(bigEndian: $0.loadUnaligned(as: UInt64.self))
            },
            captureUs: bytes[(base + 14)..<(base + 22)].withUnsafeBytes {
                UInt64(bigEndian: $0.loadUnaligned(as: UInt64.self))
            })
    }

    /// Opens a sealed body against the header bytes exactly as received.
    ///
    /// `headerBytes` must be the received 22 bytes, never a re-encode of the
    /// parsed header — see the note on `MediaFrameHeader`.
    static func decodePayload(
        header: MediaFrameHeader,
        headerBytes: Data,
        sealedBody: Data,
        key: SymmetricKey,
        salt: Data
    ) throws -> Data {
        do {
            return try RemoteSessionCrypto.open(
                body: sealedBody, header: headerBytes, key: key, salt: salt, sequence: header.sequence)
        } catch {
            throw MediaProtocolError.decryptFailed(sequence: header.sequence)
        }
    }
}
