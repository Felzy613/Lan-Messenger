import Foundation
import CryptoKit

// One frame per call, in a fixed order, with every bound checked before any
// allocation. Does no queue work, starts no timers, and performs no main-actor
// hops — routing 30+ frames a second through the @MainActor coordinator delegate
// would put the same shape of load on the UI thread that already had to be
// throttled to 12 Hz on the file-transfer progress path.

enum MediaReadOutcome: Equatable {
    case frame(MediaInboundFrame)
    /// A fragment was consumed; more to come.
    case partial
    /// Authentic but discarded: an unknown sub-channel, or input before the
    /// control grant. The sequence has still been consumed.
    case dropped(channel: UInt8, reason: String)
    case closed
    case fault(MediaProtocolError)
}

final class MediaFrameReader {

    private let link: MediaLink
    private let openingKey: SymmetricKey
    private let openingSalt: Data
    private let maxFrameLength: Int

    private var gate = MediaSequenceGate()
    private let reassembler: MediaReassembler

    /// Set by the session when `control_grant` is seen or sent.
    ///
    /// Never inferred from `remote_accept`: accepting an invite grants viewing
    /// only, and control is a separate escalation the host approves. A reader
    /// that armed input on accept would silently turn a view-only session into a
    /// controllable one.
    var inputArmed = false

    private(set) var framesRead: UInt64 = 0
    private(set) var bytesRead: UInt64 = 0

    init(link: MediaLink,
         openingKey: SymmetricKey,
         openingSalt: Data,
         maxFrameLength: Int = MediaFrameCodec.maxFrameLength,
         reassembler: MediaReassembler = MediaReassembler()) {
        self.link = link
        self.openingKey = openingKey
        self.openingSalt = openingSalt
        self.maxFrameLength = maxFrameLength
        self.reassembler = reassembler
    }

    /// Reads, authenticates and reassembles exactly one frame.
    func readFrame() -> MediaReadOutcome {
        // 1. Length prefix.
        var lengthBytes = [UInt8](repeating: 0, count: 4)
        switch link.readExact(into: &lengthBytes, count: 4) {
        case .closed:          return .closed
        case .failed(let e):   return .fault(.linkFailed(e))
        case .ok:              break
        }

        // 2. Validate BEFORE allocating. The JSON path sizes its buffer from the
        //    length and checks afterwards, which is affordable once per message
        //    and is not affordable thirty times a second.
        let length: Int
        do {
            length = try MediaFrameCodec.decodeLength(Data(lengthBytes))
        } catch let error as MediaProtocolError {
            return .fault(error)
        } catch {
            return .fault(.frameTooSmall(0))
        }
        guard length <= maxFrameLength else { return .fault(.frameTooLarge(length)) }

        // 3. Remaining header bytes. The full 22 are retained VERBATIM and are
        //    what goes to the AEAD as AAD — never a re-encode of the parsed
        //    struct, because reserved flag bits must round-trip untouched.
        var restBytes = [UInt8](repeating: 0, count: MediaFrameCodec.postLengthHeader)
        switch link.readExact(into: &restBytes, count: MediaFrameCodec.postLengthHeader) {
        case .closed:          return .closed
        case .failed(let e):   return .fault(.linkFailed(e))
        case .ok:              break
        }
        let headerBytes = Data(lengthBytes) + Data(restBytes)

        let header: MediaFrameHeader
        do {
            header = try MediaFrameCodec.decodeHeader(headerBytes)
        } catch let error as MediaProtocolError {
            return .fault(error)
        } catch {
            return .fault(.frameTooSmall(length))
        }

        // 4. Sequence gate, before any decryption work. A non-increasing
        //    sequence is what would otherwise let an AEAD nonce repeat.
        let last = gate.lastAccepted
        guard gate.admit(header.sequence) else {
            return .fault(.sequenceNotIncreasing(got: header.sequence, last: last ?? 0))
        }

        // 5. Sealed body, sized from the already-validated length.
        let bodyCount = header.sealedPayloadLength
        var bodyBytes = [UInt8](repeating: 0, count: bodyCount)
        switch link.readExact(into: &bodyBytes, count: bodyCount) {
        case .closed:          return .closed
        case .failed(let e):   return .fault(.linkFailed(e))
        case .ok:              break
        }

        // 6. Authenticate. On a media channel an AEAD failure is a protocol
        //    violation, never a droppable chunk — the file-transfer path's
        //    blanket "drop the bad chunk and carry on" is explicitly not the
        //    model, because here it would mean accepting forged input.
        let plaintext: Data
        do {
            plaintext = try MediaFrameCodec.decodePayload(
                header: header, headerBytes: headerBytes, sealedBody: Data(bodyBytes),
                key: openingKey, salt: openingSalt)
        } catch let error as MediaProtocolError {
            return .fault(error)
        } catch {
            return .fault(.decryptFailed(sequence: header.sequence))
        }

        framesRead += 1
        bytesRead += UInt64(4 + length)

        // 7. Dispatch. Unknown channels are decrypted first — so the frame is
        //    proved authentic and a fault is not misattributed — and only then
        //    discarded. That is forward compatibility, not a close.
        guard let channel = header.channel else {
            return .dropped(channel: header.rawChannel, reason: "unknown sub-channel")
        }
        if channel == .input && !inputArmed {
            return .dropped(channel: header.rawChannel, reason: "input before control_grant")
        }

        // 8. Reassembly, keyed per channel so an interleaved frame on another
        //    sub-channel cannot disturb a partially assembled video frame.
        switch reassembler.accept(header: header, plaintext: plaintext) {
        case .complete(let frame): return .frame(frame)
        case .partial:             return .partial
        case .fault(let error):    return .fault(error)
        }
    }
}

// MARK: - Writer

/// Seals and writes one segment, allocating its sequence inside the call so that
/// allocation order equals wire order.
///
/// Single-caller by contract: only the session's write queue. Allocating a
/// sequence at submit time instead would break under interleaving — submit order
/// and wire order differ, so the peer would see a non-increasing sequence, be
/// obliged to close, and the bug would reproduce only under load, only with mixed
/// sub-channels, and look exactly like a network fault.
final class MediaFrameWriter {

    private let link: MediaLink
    private let sealingKey: SymmetricKey
    private let sealingSalt: Data
    private var sequence: UInt64 = 0

    private(set) var framesWritten: UInt64 = 0
    private(set) var bytesWritten: UInt64 = 0

    init(link: MediaLink, sealingKey: SymmetricKey, sealingSalt: Data) {
        self.link = link
        self.sealingKey = sealingKey
        self.sealingSalt = sealingSalt
    }

    var nextSequence: UInt64 { sequence }

    /// PRECONDITION: called only from the session's write queue.
    @discardableResult
    func writeSegment(_ segment: MediaSegment) -> Bool {
        let seq = sequence
        sequence += 1
        do {
            let frame = try MediaFrameCodec.encodeFrame(
                channel: segment.channel, flags: segment.flags, sequence: seq,
                captureUs: segment.captureUs, plaintext: segment.payload,
                key: sealingKey, salt: sealingSalt)
            // One contiguous write. Splitting header and body would emit two
            // small packets per frame with TCP_NODELAY set and hand back exactly
            // the latency that disabling Nagle bought.
            guard link.writeAll(frame) else { return false }
            framesWritten += 1
            bytesWritten += UInt64(frame.count)
            return true
        } catch {
            return false
        }
    }
}
