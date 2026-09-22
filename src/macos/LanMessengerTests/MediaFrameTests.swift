import XCTest
import CryptoKit
@testable import LanMessenger

// Guards the remote-desktop media framing, mux and demux. Every test here maps
// to a failure that is silent, cross-version, or only reproducible under load:
//
//   * A length computed from the plaintext instead of the sealed payload is off
//     by exactly the 16-byte GCM tag, and because the length sits inside the
//     AAD, EVERY frame then fails the peer's tag check with no other symptom.
//   * Re-encoding a parsed header to build the AAD differs by one byte from any
//     peer that sets a reserved flag bit — a failure that appears only after a
//     future version ships.
//   * An input event stuck behind a 500 KiB keyframe is the exact complaint the
//     protocol calls out, and it is invisible to inspection.
//   * A sequence gate initialised to 0 rejects a legitimate first frame and the
//     session dies at hello looking like a crypto fault.
//
// See PROTOCOL.md → Remote Desktop → Media Framing.
final class MediaFrameTests: XCTestCase {

    private let key  = SymmetricKey(size: .bits256)
    private let salt = Data([0xDE, 0xAD, 0xBE, 0xEF])

    // MARK: - Header layout

    func testHeaderIsTwentyTwoBytesAndMatchesCryptoAAD() {
        XCTAssertEqual(MediaFrameCodec.headerLength, 22)
        XCTAssertEqual(MediaFrameCodec.headerLength, RemoteSessionCrypto.headerLength,
                       "the AAD is the header; if these diverge every frame fails authentication")
        XCTAssertEqual(MediaFrameCodec.postLengthHeader, 18)
    }

    func testEncodedHeaderByteLayout() {
        let h = MediaFrameCodec.encodeHeader(
            channel: .video, flags: [.keyframe, .fragmented],
            sequence: 0x0102_0304_0506_0708, captureUs: 0x1112_1314_1516_1718,
            sealedPayloadCount: 100)
        XCTAssertEqual(h.count, 22)
        // length = 18 + 100 = 118
        XCTAssertEqual(Array(h[0..<4]), [0, 0, 0, 118])
        XCTAssertEqual(h[4], MediaChannel.video.rawValue)
        XCTAssertEqual(h[5], 0b0000_0011)
        XCTAssertEqual(Array(h[6..<14]), [1, 2, 3, 4, 5, 6, 7, 8])
        XCTAssertEqual(Array(h[14..<22]), [0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18])
    }

    func testLengthCountsSealedPayloadNotPlaintext() throws {
        // The off-by-16 that would fail every frame on the peer.
        let plaintext = Data(repeating: 7, count: 64)
        let frame = try MediaFrameCodec.encodeFrame(
            channel: .control, flags: [], sequence: 1, captureUs: 9,
            plaintext: plaintext, key: key, salt: salt)
        let header = try MediaFrameCodec.decodeHeader(frame)
        XCTAssertEqual(header.length, 18 + 64 + RemoteSessionCrypto.tagLength)
        XCTAssertEqual(frame.count, 4 + header.length)
        XCTAssertEqual(header.sealedPayloadLength, 64 + RemoteSessionCrypto.tagLength)
    }

    func testMediaCapIsIndependentOfTheJSONCap() {
        // A future "unify the two codecs" refactor must break here rather than
        // silently restore a 50 MiB allocation on a path that runs at 30 fps.
        XCTAssertNotEqual(MediaFrameCodec.maxFrameLength, FrameCodec.maxFrameSize)
        XCTAssertEqual(MediaFrameCodec.maxFrameLength, 4 * 1024 * 1024)
        XCTAssertLessThan(MediaFrameCodec.maxFrameLength, FrameCodec.maxFrameSize)
    }

    // MARK: - Round trip

    func testFrameRoundTrip() throws {
        let plaintext = Data("a media payload".utf8)
        let frame = try MediaFrameCodec.encodeFrame(
            channel: .input, flags: [], sequence: 42, captureUs: 1234,
            plaintext: plaintext, key: key, salt: salt)

        let header = try MediaFrameCodec.decodeHeader(frame)
        XCTAssertEqual(header.channel, .input)
        XCTAssertEqual(header.sequence, 42)
        XCTAssertEqual(header.captureUs, 1234)

        let body = frame.suffix(from: frame.startIndex + 22)
        let opened = try MediaFrameCodec.decodePayload(
            header: header, headerBytes: frame.prefix(22), sealedBody: body, key: key, salt: salt)
        XCTAssertEqual(opened, plaintext)
    }

    func testEmptyPayloadRoundTrips() throws {
        let frame = try MediaFrameCodec.encodeFrame(
            channel: .stats, flags: [], sequence: 0, captureUs: 0,
            plaintext: Data(), key: key, salt: salt)
        XCTAssertEqual(frame.count, 22 + RemoteSessionCrypto.tagLength)
        let header = try MediaFrameCodec.decodeHeader(frame)
        XCTAssertEqual(header.length, MediaFrameCodec.minFrameLength)
    }

    // MARK: - Reserved flag bits

    func testReservedFlagBitsSurviveRoundTripAndDoNotBreakAuthentication() throws {
        // A future peer sets bit 5. We must ignore it for interpretation but
        // authenticate against the byte as received.
        let rawFlags: UInt8 = 0b0010_0001          // keyframe + a reserved bit
        var header = MediaFrameCodec.encodeHeader(
            channel: .video, flags: [.keyframe], sequence: 3, captureUs: 5,
            sealedPayloadCount: MediaFrameCodec.sealedLength(plaintextCount: 8))
        header[5] = rawFlags

        let sealed = try RemoteSessionCrypto.seal(
            payload: Data(repeating: 1, count: 8), header: header, key: key, salt: salt, sequence: 3)

        let parsed = try MediaFrameCodec.decodeHeader(header)
        XCTAssertEqual(parsed.rawFlags, rawFlags, "the raw byte must be retained verbatim")
        XCTAssertEqual(parsed.flags, [.keyframe], "interpretation masks the reserved bit off")
        XCTAssertTrue(parsed.isKeyframe)

        // Authenticating against the received bytes succeeds...
        XCTAssertNoThrow(try MediaFrameCodec.decodePayload(
            header: parsed, headerBytes: header, sealedBody: sealed, key: key, salt: salt))

        // ...and against a normalised re-encode it does not. This is the bug.
        let reEncoded = MediaFrameCodec.encodeHeader(
            channel: .video, flags: parsed.flags, sequence: 3, captureUs: 5,
            sealedPayloadCount: MediaFrameCodec.sealedLength(plaintextCount: 8))
        XCTAssertNotEqual(reEncoded, header)
        XCTAssertThrowsError(try MediaFrameCodec.decodePayload(
            header: parsed, headerBytes: reEncoded, sealedBody: sealed, key: key, salt: salt),
            "re-encoding the header to build the AAD must be shown to break authentication")
    }

    func testUnknownChannelIsParsedNotRejected() throws {
        var header = MediaFrameCodec.encodeHeader(
            channel: .control, flags: [], sequence: 1, captureUs: 0, sealedPayloadCount: 16)
        header[4] = 200
        let parsed = try MediaFrameCodec.decodeHeader(header)
        XCTAssertNil(parsed.channel, "forward compatibility: unknown ids parse, the reader discards")
        XCTAssertEqual(parsed.rawChannel, 200)
    }

    // MARK: - Length bounds

    func testLengthBoundsRejectedBeforeAllocation() {
        func lengthBytes(_ n: UInt32) -> Data {
            var d = Data(); withUnsafeBytes(of: n.bigEndian) { d.append(contentsOf: $0) }
            return d + Data(repeating: 0, count: 18)
        }
        XCTAssertThrowsError(try MediaFrameCodec.decodeLength(lengthBytes(UInt32(MediaFrameCodec.maxFrameLength + 1))))
        XCTAssertThrowsError(try MediaFrameCodec.decodeLength(lengthBytes(0)))
        XCTAssertThrowsError(try MediaFrameCodec.decodeLength(lengthBytes(5)),
                             "a length below the 18+16 minimum cannot be a well-formed frame")
        XCTAssertThrowsError(try MediaFrameCodec.decodeLength(lengthBytes(0xFFFF_FFFF)),
                             "a hostile length must not reach an allocation")
        XCTAssertEqual(try MediaFrameCodec.decodeLength(lengthBytes(UInt32(MediaFrameCodec.minFrameLength))),
                       MediaFrameCodec.minFrameLength)
    }

    func testOversizePlaintextIsRefused() {
        XCTAssertThrowsError(try MediaFrameCodec.encodeFrame(
            channel: .video, flags: [], sequence: 0, captureUs: 0,
            plaintext: Data(repeating: 0, count: MediaFrameCodec.maxPlaintext + 1),
            key: key, salt: salt))
    }

    // MARK: - Sequence gate

    func testSequenceGateAcceptsZeroFirst() {
        // A reconnect restarts at zero against fresh keys. Initialising the gate
        // to 0 instead of nil would reject this and kill the session at hello.
        var gate = MediaSequenceGate()
        XCTAssertNil(gate.lastAccepted)
        XCTAssertTrue(gate.admit(0))
        XCTAssertEqual(gate.lastAccepted, 0)
    }

    func testSequenceGateRequiresStrictIncrease() {
        var gate = MediaSequenceGate()
        XCTAssertTrue(gate.admit(0))
        XCTAssertTrue(gate.admit(1))
        XCTAssertFalse(gate.admit(1), "a repeat is a replay")
        XCTAssertFalse(gate.admit(0), "a rewind is a replay")
        XCTAssertTrue(gate.admit(99), "gaps are fine; only order matters")
        XCTAssertFalse(gate.admit(98))
    }

    // MARK: - Scheduler: fragmentation

    func testSmallVideoFrameIsNotMarkedFragmented() {
        let s = MediaWriteScheduler()
        s.submit(MediaOutboundFrame(channel: .video, payload: Data(repeating: 1, count: 100), captureUs: 7))
        let seg = s.nextSegment()
        XCTAssertEqual(seg?.flags, [])
        XCTAssertFalse(seg?.flags.contains(.fragmented) ?? true,
                       "a one-fragment frame marked fragmented faults the peer's reassembler")
        XCTAssertNil(s.nextSegment())
    }

    func testLargeVideoFrameFragmentsWithCorrectFlags() {
        let s = MediaWriteScheduler()
        let size = MediaFrameCodec.maxFragmentPayload * 2 + 500
        s.submit(MediaOutboundFrame(channel: .video, payload: Data(repeating: 9, count: size),
                                    captureUs: 11, keyframe: true))
        var segs: [MediaSegment] = []
        while let seg = s.nextSegment() { segs.append(seg) }

        XCTAssertEqual(segs.count, 3)
        XCTAssertEqual(segs.map { $0.payload.count },
                       [MediaFrameCodec.maxFragmentPayload, MediaFrameCodec.maxFragmentPayload, 500])
        for (i, seg) in segs.enumerated() {
            XCTAssertTrue(seg.flags.contains(.fragmented))
            XCTAssertTrue(seg.flags.contains(.keyframe),
                          "keyframe rides every segment so a mid-frame joiner can classify it")
            XCTAssertEqual(seg.flags.contains(.finalFragment), i == segs.count - 1)
            XCTAssertEqual(seg.captureUs, 11)
        }
    }

    // MARK: - Scheduler: interleaving (the headline requirement)

    func testInputNeverWaitsBehindAKeyframe() {
        // The product requirement, stated as an assertion: submit a big
        // keyframe, pull one fragment, submit an input event, and the very next
        // unit off the scheduler must be the input — not the remaining 31
        // video segments.
        let s = MediaWriteScheduler()
        s.submit(MediaOutboundFrame(channel: .video,
                                    payload: Data(repeating: 3, count: 512 * 1024),
                                    captureUs: 1, keyframe: true))
        XCTAssertEqual(s.nextSegment()?.channel, .video)

        s.submit(MediaOutboundFrame(channel: .input, payload: Data([0xAB]), captureUs: 2))
        XCTAssertEqual(s.nextSegment()?.channel, .input,
                       "an input event must overtake the remainder of a keyframe")
        XCTAssertEqual(s.nextSegment()?.channel, .video, "and video then resumes where it left off")
    }

    func testChannelPriorityOrder() {
        let s = MediaWriteScheduler()
        s.submit(MediaOutboundFrame(channel: .stats,   payload: Data([4]), captureUs: 0))
        s.submit(MediaOutboundFrame(channel: .cursor,  payload: Data([3]), captureUs: 0))
        s.submit(MediaOutboundFrame(channel: .input,   payload: Data([2]), captureUs: 0))
        s.submit(MediaOutboundFrame(channel: .control, payload: Data([1]), captureUs: 0))
        s.submit(MediaOutboundFrame(channel: .video,   payload: Data([5]), captureUs: 0))

        XCTAssertEqual([s.nextSegment()?.channel, s.nextSegment()?.channel,
                        s.nextSegment()?.channel, s.nextSegment()?.channel,
                        s.nextSegment()?.channel],
                       [.control, .input, .cursor, .stats, .video])
    }

    func testInterleavedFrameDoesNotCorruptVideoReassembly() throws {
        // The writer-side interleaving is only safe because the reader keys its
        // buffers per channel. Prove the pair works end to end.
        let s = MediaWriteScheduler()
        let videoPayload = Data((0..<(MediaFrameCodec.maxFragmentPayload * 2)).map { UInt8($0 & 0xFF) })
        s.submit(MediaOutboundFrame(channel: .video, payload: videoPayload, captureUs: 5))
        _ = s.nextSegment()                                     // first video fragment
        s.submit(MediaOutboundFrame(channel: .control, payload: Data("hello".utf8), captureUs: 6))

        let reassembler = MediaReassembler()
        var seq: UInt64 = 0
        var video: MediaInboundFrame?

        // Replay from the start so the first fragment is included.
        let fresh = MediaWriteScheduler()
        fresh.submit(MediaOutboundFrame(channel: .video, payload: videoPayload, captureUs: 5))
        _ = fresh.nextSegment().map { first -> Void in
            let h = MediaFrameCodec.encodeHeader(channel: first.channel, flags: first.flags,
                                                 sequence: seq, captureUs: first.captureUs,
                                                 sealedPayloadCount: first.payload.count + 16)
            _ = reassembler.accept(header: try! MediaFrameCodec.decodeHeader(h), plaintext: first.payload)
            seq += 1
        }
        fresh.submit(MediaOutboundFrame(channel: .control, payload: Data("hello".utf8), captureUs: 6))
        while let seg = fresh.nextSegment() {
            let h = MediaFrameCodec.encodeHeader(channel: seg.channel, flags: seg.flags,
                                                 sequence: seq, captureUs: seg.captureUs,
                                                 sealedPayloadCount: seg.payload.count + 16)
            seq += 1
            if case .complete(let frame) = reassembler.accept(
                header: try MediaFrameCodec.decodeHeader(h), plaintext: seg.payload) {
                if frame.channel == .video { video = frame }
            }
        }

        XCTAssertEqual(video?.payload, videoPayload,
                       "a control frame between two video fragments must not corrupt the video")
        XCTAssertEqual(video?.fragmentCount, 2)
        XCTAssertEqual(video?.captureUs, 5, "timestamp comes from the first fragment")
    }

    // MARK: - Scheduler: drop policy

    func testThirdVideoFrameDropsTheQueuedOneNotTheInProgressOne() {
        let s = MediaWriteScheduler()
        let big = Data(repeating: 1, count: MediaFrameCodec.maxFragmentPayload * 3)
        s.submit(MediaOutboundFrame(channel: .video, payload: big, captureUs: 1))
        _ = s.nextSegment()   // frame A is now mid-fragmentation

        XCTAssertEqual(s.submit(MediaOutboundFrame(channel: .video, payload: Data([2]), captureUs: 2)), .queued)
        let outcome = s.submit(MediaOutboundFrame(channel: .video, payload: Data([3]), captureUs: 3))
        XCTAssertEqual(outcome, .droppedStaleVideo(wasKeyframe: false))
        XCTAssertEqual(s.droppedVideoFrames, 1)

        // A must still finish: abandoning it would leave a dangling
        // fragmented-without-final on the wire and desync the peer for good.
        var videoSegments = 0
        while let seg = s.nextSegment() {
            if seg.channel == .video { videoSegments += 1 }
        }
        XCTAssertGreaterThanOrEqual(videoSegments, 3,
                                    "the in-progress frame must run to completion")
    }

    func testDroppedKeyframeIsLatchedForIDRRecovery() {
        let s = MediaWriteScheduler()
        s.submit(MediaOutboundFrame(channel: .video, payload: Data([1]), captureUs: 1))
        s.submit(MediaOutboundFrame(channel: .video, payload: Data([2]), captureUs: 2, keyframe: true))
        let outcome = s.submit(MediaOutboundFrame(channel: .video, payload: Data([3]), captureUs: 3))

        XCTAssertEqual(outcome, .droppedStaleVideo(wasKeyframe: true))
        XCTAssertEqual(s.droppedKeyframes, 1)
        XCTAssertTrue(s.takeKeyframeDropPending(), "a dropped keyframe must be recoverable via a forced IDR")
        XCTAssertFalse(s.takeKeyframeDropPending(), "the latch is consumed once")
    }

    func testCursorReplacesRatherThanQueues() {
        // Only the newest cursor position has meaning; faulting a session
        // because 513 of them piled up would be absurd.
        let s = MediaWriteScheduler(channelDepth: 4)
        for i in 0..<50 {
            XCTAssertEqual(s.submit(MediaOutboundFrame(channel: .cursor, payload: Data([UInt8(i)]), captureUs: 0)),
                           .queued)
        }
        XCTAssertEqual(s.nextSegment()?.payload, Data([49]))
        XCTAssertNil(s.nextSegment())
    }

    func testNonVideoOverflowIsReportedNotSilentlyDropped() {
        // A vanished input record is a stuck modifier key on the host.
        let s = MediaWriteScheduler(channelDepth: 3)
        for _ in 0..<3 {
            XCTAssertEqual(s.submit(MediaOutboundFrame(channel: .input, payload: Data([1]), captureUs: 0)), .queued)
        }
        XCTAssertEqual(s.submit(MediaOutboundFrame(channel: .input, payload: Data([1]), captureUs: 0)),
                       .overflow(.input))
    }

    // MARK: - Reassembler faults

    func testWholeFrameMidReassemblyFaults() throws {
        let r = MediaReassembler()
        let partial = MediaFrameCodec.encodeHeader(
            channel: .video, flags: [.fragmented], sequence: 0, captureUs: 0, sealedPayloadCount: 20)
        XCTAssertEqual(r.accept(header: try MediaFrameCodec.decodeHeader(partial), plaintext: Data([1])), .partial)

        let whole = MediaFrameCodec.encodeHeader(
            channel: .video, flags: [], sequence: 1, captureUs: 0, sealedPayloadCount: 20)
        XCTAssertEqual(r.accept(header: try MediaFrameCodec.decodeHeader(whole), plaintext: Data([2])),
                       .fault(.wholeFrameMidReassembly(.video)))
    }

    func testZeroLengthFragmentStormIsBoundedByFragmentCount() throws {
        // A bytes-only cap never fires here: every frame passes the wire check
        // while the buffer grows without limit.
        let r = MediaReassembler(maxFragments: 8)
        let h = try MediaFrameCodec.decodeHeader(MediaFrameCodec.encodeHeader(
            channel: .input, flags: [.fragmented], sequence: 0, captureUs: 0, sealedPayloadCount: 16))
        for _ in 0..<8 { _ = r.accept(header: h, plaintext: Data()) }
        XCTAssertEqual(r.accept(header: h, plaintext: Data()),
                       .fault(.fragmentCountExceeded(.input, count: 9)))
    }

    func testReassemblyByteOverflowFaults() throws {
        let r = MediaReassembler(maxAssembledBytes: 100, maxFragments: 999)
        let h = try MediaFrameCodec.decodeHeader(MediaFrameCodec.encodeHeader(
            channel: .video, flags: [.fragmented], sequence: 0, captureUs: 0, sealedPayloadCount: 80))
        XCTAssertEqual(r.accept(header: h, plaintext: Data(repeating: 0, count: 60)), .partial)
        guard case .fault(.reassemblyOverflow) = r.accept(header: h, plaintext: Data(repeating: 0, count: 60)) else {
            return XCTFail("assembled bytes must be capped independently of the per-frame wire cap")
        }
    }

    func testPerChannelBuffersAreIndependent() throws {
        let r = MediaReassembler()
        func header(_ ch: MediaChannel, _ flags: MediaFlags, _ seq: UInt64) throws -> MediaFrameHeader {
            try MediaFrameCodec.decodeHeader(MediaFrameCodec.encodeHeader(
                channel: ch, flags: flags, sequence: seq, captureUs: 0, sealedPayloadCount: 20))
        }
        XCTAssertEqual(r.accept(header: try header(.video, [.fragmented], 0), plaintext: Data([1])), .partial)
        XCTAssertEqual(r.accept(header: try header(.input, [.fragmented], 1), plaintext: Data([9])), .partial)
        XCTAssertEqual(r.pendingBytes(for: .video), 1)
        XCTAssertEqual(r.pendingBytes(for: .input), 1)

        guard case .complete(let v) = r.accept(
            header: try header(.video, [.fragmented, .finalFragment], 2), plaintext: Data([2])) else {
            return XCTFail("video should complete independently of the pending input frame")
        }
        XCTAssertEqual(v.payload, Data([1, 2]))
        XCTAssertEqual(r.pendingBytes(for: .input), 1, "the input buffer is untouched")
    }

    // MARK: - Cross-platform golden vector

    // The only test here that can catch the two platforms drifting apart on the
    // wire. Everything else verifies internal consistency, which both
    // implementations can satisfy independently while still disagreeing byte for
    // byte. `MediaFrameTests.cs` asserts this same file; regenerate and update
    // BOTH copies if the framing ever changes.
    func testMatchesCrossPlatformFrameVector() throws {
        let url: URL
        if let bundled = Bundle(for: MediaFrameTests.self)
            .url(forResource: "media_frame_vector", withExtension: "json") {
            url = bundled
        } else {
            url = URL(fileURLWithPath: #file).deletingLastPathComponent()
                .appendingPathComponent("media_frame_vector.json")
        }
        let root = try XCTUnwrap(try JSONSerialization.jsonObject(
            with: try Data(contentsOf: url)) as? [String: Any])
        let vectorKey = SymmetricKey(data: try XCTUnwrap(Data(hex: try XCTUnwrap(root["key_hex"] as? String))))
        let vectorSalt = try XCTUnwrap(Data(hex: try XCTUnwrap(root["salt_hex"] as? String)))

        for entry in try XCTUnwrap(root["frames"] as? [[String: Any]]) {
            let name = try XCTUnwrap(entry["name"] as? String)
            let channel = try XCTUnwrap(MediaChannel(rawValue: UInt8(try XCTUnwrap(entry["channel"] as? Int))))
            let rawFlags = UInt8(try XCTUnwrap(entry["raw_flags"] as? Int))
            let sequence = UInt64(try XCTUnwrap(entry["sequence"] as? UInt64))
            let captureUs = UInt64(try XCTUnwrap(entry["capture_us"] as? UInt64))
            let plaintext = try XCTUnwrap(Data(hex: try XCTUnwrap(entry["plaintext_hex"] as? String)))
            let expected = try XCTUnwrap(entry["frame_hex"] as? String)

            var header = MediaFrameCodec.encodeHeader(
                channel: channel, flags: MediaFlags(rawValue: rawFlags & MediaFlags.knownMask),
                sequence: sequence, captureUs: captureUs,
                sealedPayloadCount: MediaFrameCodec.sealedLength(plaintextCount: plaintext.count))
            header[5] = rawFlags
            let sealed = try RemoteSessionCrypto.seal(
                payload: plaintext, header: header, key: vectorKey, salt: vectorSalt, sequence: sequence)
            XCTAssertEqual((header + sealed).hexString, expected, "frame '\(name)' differs from the vector")

            // Decode the vector's own bytes too, so a reader regression is caught.
            let wire = try XCTUnwrap(Data(hex: expected))
            let parsed = try MediaFrameCodec.decodeHeader(wire)
            XCTAssertEqual(parsed.sequence, sequence, "frame '\(name)' sequence")
            XCTAssertEqual(parsed.captureUs, captureUs, "frame '\(name)' capture_us")
            XCTAssertEqual(parsed.rawFlags, rawFlags, "frame '\(name)' raw flags")
            XCTAssertEqual(try MediaFrameCodec.decodePayload(
                header: parsed, headerBytes: wire.prefix(22),
                sealedBody: wire.suffix(from: wire.startIndex + 22),
                key: vectorKey, salt: vectorSalt), plaintext, "frame '\(name)' payload")
        }
    }
}

private extension Data {
    init?(hex: String) {
        guard hex.count % 2 == 0 else { return nil }
        var out = Data(capacity: hex.count / 2)
        var i = hex.startIndex
        while i < hex.endIndex {
            let j = hex.index(i, offsetBy: 2)
            guard let b = UInt8(hex[i..<j], radix: 16) else { return nil }
            out.append(b); i = j
        }
        self = out
    }
    var hexString: String { map { String(format: "%02x", $0) }.joined() }
}
