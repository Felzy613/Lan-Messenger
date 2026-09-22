import XCTest
import CryptoKit
import CoreMedia
import CoreVideo
import VideoToolbox
@testable import LanMessenger

// The first time anything travels the whole video path.
//
// Every piece of this was already tested and none of them had ever met: the
// transport was exercised against in-memory doubles carrying synthetic
// payloads, the encoder was driven standalone, the decoder was fed a file. Each
// of those proves a component; none of them proves a pipeline, and integration
// is where the remaining surprises in this feature live.
//
// So this runs the real thing end to end, in one process, with no sockets and no
// display:
//
//   pixel buffers → H264Encoder → AVCC→Annex-B → MediaOutboundFrame
//     → MediaWriteScheduler → framing + AES-GCM seal → PairedMediaLink
//     → unseal + sequence gate + reassembly → MediaInboundFrame
//     → H264Decoder → CMSampleBuffer → VTDecompressionSession
//
// Only capture and presentation are stubbed, because both need a grant and a
// screen. Everything between them is production code, including two real
// `MediaSession`s with independently derived directional keys — so a swapped
// role or a nonce mistake fails here rather than on a live call.
final class VideoPipelineEndToEndTests: XCTestCase {

    private let sessionID = "3a7f1c5e9b2d4068a1c3e5f70921b4d6"

    // MARK: - Harness

    private struct Wired {
        let host: MediaSession
        let viewer: MediaSession
        let send: VideoSendPipeline
        let receive: VideoReceivePipeline
    }

    /// Two sessions facing each other over a paired link, with a send pipeline
    /// on one end and a receive pipeline on the other.
    private func wire(dimensions: H264VideoDimensions,
                      onSample: @escaping (CMSampleBuffer) -> Void) throws -> Wired {
        let hostStatic = Curve25519.KeyAgreement.PrivateKey()
        let viewerStatic = Curve25519.KeyAgreement.PrivateKey()
        let hostEphemeral = Curve25519.KeyAgreement.PrivateKey()
        let viewerEphemeral = Curve25519.KeyAgreement.PrivateKey()
        let params = RemoteHandshakeParams(["protocol": .int(1)])

        // Derived independently on each side, exactly as the handshake does it.
        // Both sides deciding they are the initiator derives swapped keys, and
        // the only symptom is "decryption randomly fails" once video is already
        // flowing — so this is worth doing properly even in a test harness.
        let hostKeys = try RemoteSessionCrypto.deriveKeys(
            myRole: .initiator,
            myEphemeralPrivate: hostEphemeral, myStaticPrivate: hostStatic,
            peerEphemeralPublicKeyB64: viewerEphemeral.publicKey.rawRepresentation.base64EncodedString(),
            peerStaticPublicKeyB64: viewerStatic.publicKey.rawRepresentation.base64EncodedString(),
            sessionID: sessionID, params: params)
        let viewerKeys = try RemoteSessionCrypto.deriveKeys(
            myRole: .responder,
            myEphemeralPrivate: viewerEphemeral, myStaticPrivate: viewerStatic,
            peerEphemeralPublicKeyB64: hostEphemeral.publicKey.rawRepresentation.base64EncodedString(),
            peerStaticPublicKeyB64: hostStatic.publicKey.rawRepresentation.base64EncodedString(),
            sessionID: sessionID, params: params)

        let links = PairedMediaLink.pair()
        let host = MediaSession(
            sessionID: sessionID,
            peerPublicKeyB64: viewerStatic.publicKey.rawRepresentation.base64EncodedString(),
            peerIP: links.a.peerIP, role: .initiator, keys: hostKeys, link: links.a)
        let viewer = MediaSession(
            sessionID: sessionID,
            peerPublicKeyB64: hostStatic.publicKey.rawRepresentation.base64EncodedString(),
            peerIP: links.b.peerIP, role: .responder, keys: viewerKeys, link: links.b)

        let receive = VideoReceivePipeline()
        receive.onSample = onSample
        // A viewer's keyframe request going back to the host: the loop that
        // makes recovery possible, and the only place these two pipelines know
        // about each other.
        let send = try VideoSendPipeline(
            dimensions: dimensions, bitrate: 2_000_000, frameRate: 30,
            submit: { [weak host] frame in host?.submit(frame) ?? .queued })
        receive.onKeyframeNeeded = { [weak send] _ in send?.latchKeyframe() }

        viewer.onFrame = { frame in receive.accept(frame) }

        host.start()
        viewer.start()
        return Wired(host: host, viewer: viewer, send: send, receive: receive)
    }

    private func tearDown(_ wired: Wired) {
        wired.send.stop()
        wired.host.stop()
        wired.viewer.stop()
    }

    // MARK: - The whole path

    func testCapturedFramesArriveAsDecodablePictures() throws {
        let dimensions = H264VideoDimensions(width: 320, height: 240)
        let frameCount = 20

        let lock = NSLock()
        var samples: [CMSampleBuffer] = []
        let arrived = expectation(description: "pictures arrive at the viewer")
        arrived.expectedFulfillmentCount = frameCount
        arrived.assertForOverFulfill = false

        let wired = try wire(dimensions: dimensions) { sample in
            lock.lock(); samples.append(sample); lock.unlock()
            arrived.fulfill()
        }
        defer { tearDown(wired) }

        for index in 0..<frameCount {
            wired.send.encode(
                pixelBuffer: try H264DecoderTests.makePixelBuffer(
                    seed: UInt8(index * 13 % 200),
                    width: dimensions.width, height: dimensions.height),
                captureUs: UInt64(index + 1) * 33_333)
        }
        wait(for: [arrived], timeout: 20)

        lock.lock(); let received = samples; lock.unlock()
        XCTAssertGreaterThanOrEqual(received.count, frameCount)

        // Arriving is not the same as being a picture. Everything above could be
        // moving well-formed rubbish, so the bytes go through a real decoder.
        let pictures = try Self.decodePictures(received)
        XCTAssertEqual(pictures.count, received.count,
                       "\(received.count - pictures.count) frames crossed the wire and failed to decode")
        XCTAssertTrue(pictures.allSatisfy {
            $0.width == dimensions.width && $0.height == dimensions.height })
        XCTAssertEqual(wired.receive.dimensions, dimensions,
                       "the viewer learned the picture size from the stream itself")
    }

    func testTheCaptureClockSurvivesTheEntireJourney() throws {
        // capture_us is carried in the media frame header, reassembled from the
        // *first* fragment, and set as the sample's presentation time. It is the
        // only basis for a glass-to-glass latency figure, and it crosses three
        // components that could each quietly substitute a clock of their own.
        let dimensions = H264VideoDimensions(width: 320, height: 240)
        let frameCount = 10

        let lock = NSLock()
        var timestamps: [UInt64] = []
        let arrived = expectation(description: "timestamps arrive")
        arrived.expectedFulfillmentCount = frameCount
        arrived.assertForOverFulfill = false

        let wired = try wire(dimensions: dimensions) { sample in
            let pts = CMSampleBufferGetPresentationTimeStamp(sample)
            lock.lock(); timestamps.append(UInt64(max(pts.value, 0))); lock.unlock()
            arrived.fulfill()
        }
        defer { tearDown(wired) }

        let submitted = (0..<frameCount).map { UInt64($0 + 1) * 33_333 + 7 }
        for (index, captureUs) in submitted.enumerated() {
            wired.send.encode(
                pixelBuffer: try H264DecoderTests.makePixelBuffer(
                    seed: UInt8(index * 9 % 200),
                    width: dimensions.width, height: dimensions.height),
                captureUs: captureUs)
        }
        wait(for: [arrived], timeout: 20)

        lock.lock(); let received = timestamps; lock.unlock()
        XCTAssertEqual(Array(received.prefix(frameCount)), submitted,
                       "capture timestamps were altered somewhere between encode and present")
    }

    func testAKeyframeRequestFromTheViewerReachesTheEncoder() throws {
        // The recovery loop. A viewer that flushed its decoder, or joined late,
        // asks for an IDR; the host latches it and the next captured frame
        // carries it. Without this a stalled viewer stays black until the
        // encoder's own keyframe interval happens to come round — four seconds
        // of nothing, which users report as a crash.
        let dimensions = H264VideoDimensions(width: 320, height: 240)

        let lock = NSLock()
        var keyframes = 0
        let arrived = expectation(description: "a second keyframe arrives")
        arrived.assertForOverFulfill = false

        let wired = try wire(dimensions: dimensions) { sample in
            guard H264Decoder.isKeyframe(sample) else { return }
            lock.lock(); keyframes += 1; let count = keyframes; lock.unlock()
            if count >= 2 { arrived.fulfill() }
        }
        defer { tearDown(wired) }

        // Frame 0 opens the stream with the encoder's own IDR.
        wired.send.encode(pixelBuffer: try H264DecoderTests.makePixelBuffer(
            seed: 1, width: dimensions.width, height: dimensions.height), captureUs: 33_333)

        // Now the viewer asks, as it would after a presenter flush.
        wired.receive.reset(reason: "presenter_flush")

        for index in 1..<6 {
            wired.send.encode(pixelBuffer: try H264DecoderTests.makePixelBuffer(
                seed: UInt8(index * 17 % 200),
                width: dimensions.width, height: dimensions.height),
                captureUs: UInt64(index + 1) * 33_333)
        }
        wait(for: [arrived], timeout: 20)

        lock.lock(); let total = keyframes; lock.unlock()
        XCTAssertGreaterThanOrEqual(total, 2, "the request never became an IDR")
        XCTAssertGreaterThan(wired.receive.stats.keyframeRequests, 0)
    }

    func testStatisticsAgreeAcrossTheWire() throws {
        let dimensions = H264VideoDimensions(width: 320, height: 240)
        let frameCount = 15

        let arrived = expectation(description: "frames arrive")
        arrived.expectedFulfillmentCount = frameCount
        arrived.assertForOverFulfill = false

        let wired = try wire(dimensions: dimensions) { _ in arrived.fulfill() }
        defer { tearDown(wired) }

        for index in 0..<frameCount {
            wired.send.encode(pixelBuffer: try H264DecoderTests.makePixelBuffer(
                seed: UInt8(index * 5 % 200),
                width: dimensions.width, height: dimensions.height),
                captureUs: UInt64(index + 1) * 33_333)
        }
        wait(for: [arrived], timeout: 20)

        let sent = wired.send.stats
        let got = wired.receive.stats
        XCTAssertGreaterThanOrEqual(sent.framesSubmitted, frameCount)
        XCTAssertEqual(sent.framesDropped, 0, "a 320x240 stream should not overrun the scheduler")
        XCTAssertGreaterThanOrEqual(got.framesAccepted, frameCount)

        // Every byte submitted is a byte received: the header, the seal and the
        // fragmentation are all accounted for and nothing was silently truncated.
        XCTAssertEqual(got.bytesAccepted, sent.bytesSubmitted,
                       "payload bytes changed size in transit")
        XCTAssertEqual(got.framesDropped, 0)
        XCTAssertEqual(sent.keyframesSubmitted, 1, "one IDR opens a short stream")
    }

    func testALargeKeyframeIsFragmentedAndReassembled() throws {
        // A 1080p IDR is far larger than the 16 KiB fragment size, so this is
        // the ordinary case rather than an edge one — and a reassembler that is
        // subtly wrong produces a decode failure only on keyframes, which reads
        // as "it works but recovers badly".
        let dimensions = H264VideoDimensions(width: 1920, height: 1080)

        let lock = NSLock()
        var largest = 0
        var samples: [CMSampleBuffer] = []
        let arrived = expectation(description: "a 1080p picture arrives")
        arrived.assertForOverFulfill = false

        let wired = try wire(dimensions: dimensions) { sample in
            lock.lock()
            samples.append(sample)
            largest = max(largest, CMSampleBufferGetTotalSampleSize(sample))
            let count = samples.count
            lock.unlock()
            if count >= 3 { arrived.fulfill() }
        }
        defer { tearDown(wired) }

        for index in 0..<6 {
            wired.send.encode(pixelBuffer: try H264DecoderTests.makePixelBuffer(
                seed: UInt8(index * 23 % 200),
                width: dimensions.width, height: dimensions.height),
                captureUs: UInt64(index + 1) * 33_333)
        }
        wait(for: [arrived], timeout: 30)

        lock.lock(); let received = samples; let biggest = largest; lock.unlock()
        XCTAssertGreaterThan(biggest, 16_384,
                             "no frame exceeded one fragment; this did not test reassembly")
        let pictures = try Self.decodePictures(received)
        XCTAssertEqual(pictures.count, received.count)
        XCTAssertTrue(pictures.allSatisfy { $0.width == 1920 && $0.height == 1080 })
    }

    // MARK: - Oracle

    /// Shared with `H264DecoderTests`: a real VTDecompressionSession, so
    /// "a picture arrived" means a picture and not a plausible byte sequence.
    static func decodePictures(_ samples: [CMSampleBuffer]) throws -> [(width: Int, height: Int)] {
        var session: VTDecompressionSession?
        defer {
            if let session {
                VTDecompressionSessionWaitForAsynchronousFrames(session)
                VTDecompressionSessionInvalidate(session)
            }
        }

        let lock = NSLock()
        var sizes: [(width: Int, height: Int)] = []
        var failure: OSStatus = noErr

        for sample in samples {
            guard let formatDescription = CMSampleBufferGetFormatDescription(sample) else { continue }
            if let existing = session,
               !VTDecompressionSessionCanAcceptFormatDescription(
                    existing, formatDescription: formatDescription) {
                VTDecompressionSessionWaitForAsynchronousFrames(existing)
                VTDecompressionSessionInvalidate(existing)
                session = nil
            }
            if session == nil {
                var created: VTDecompressionSession?
                let attributes: [CFString: Any] = [
                    kCVPixelBufferPixelFormatTypeKey: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange,
                ]
                guard VTDecompressionSessionCreate(
                    allocator: kCFAllocatorDefault, formatDescription: formatDescription,
                    decoderSpecification: nil, imageBufferAttributes: attributes as CFDictionary,
                    outputCallback: nil, decompressionSessionOut: &created) == noErr,
                    let created else { continue }
                session = created
            }
            guard let active = session else { continue }
            _ = VTDecompressionSessionDecodeFrame(
                active, sampleBuffer: sample, flags: [], infoFlagsOut: nil
            ) { status, _, imageBuffer, _, _ in
                lock.lock(); defer { lock.unlock() }
                guard status == noErr else {
                    if failure == noErr { failure = status }
                    return
                }
                guard let imageBuffer else { return }
                sizes.append((CVPixelBufferGetWidth(imageBuffer),
                              CVPixelBufferGetHeight(imageBuffer)))
            }
        }

        if let session { VTDecompressionSessionWaitForAsynchronousFrames(session) }
        lock.lock(); defer { lock.unlock() }
        XCTAssertEqual(failure, noErr, "the decoder reported \(failure) on at least one frame")
        return sizes
    }
}
