import XCTest
import VideoToolbox
import CoreVideo
@testable import LanMessenger

// Exercises the real VideoToolbox encoder. Not a mock: these tests create a
// hardware-backed compression session, push synthetic NV12 frames through it,
// and assert on the bitstream that comes out.
//
// That matters because almost every way this component can be wrong is invisible
// to a mock — a property silently rejected, parameter sets read from the wrong
// index, a non-contiguous block buffer truncated, a keyframe flag inverted. All
// of those produce plausible-looking objects and an unplayable stream.
final class H264EncoderTests: XCTestCase {

    private let width = 320, height = 240

    /// A synthetic 420v frame. The luma ramp varies per frame because a constant
    /// image encodes to almost nothing and would not prove the encoder ran.
    private func makePixelBuffer(seed: UInt8) throws -> CVPixelBuffer {
        var buffer: CVPixelBuffer?
        let attrs: [CFString: Any] = [
            kCVPixelBufferIOSurfacePropertiesKey: [:] as CFDictionary,
        ]
        let status = CVPixelBufferCreate(
            kCFAllocatorDefault, width, height,
            kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange,
            attrs as CFDictionary, &buffer)
        let pixelBuffer = try XCTUnwrap(buffer, "CVPixelBufferCreate failed (\(status))")

        CVPixelBufferLockBaseAddress(pixelBuffer, [])
        defer { CVPixelBufferUnlockBaseAddress(pixelBuffer, []) }

        if let luma = CVPixelBufferGetBaseAddressOfPlane(pixelBuffer, 0) {
            let rowBytes = CVPixelBufferGetBytesPerRowOfPlane(pixelBuffer, 0)
            for row in 0..<height {
                let line = luma.advanced(by: row * rowBytes).assumingMemoryBound(to: UInt8.self)
                for column in 0..<width {
                    line[column] = UInt8((Int(seed) &+ row &+ column) % 220 + 16)
                }
            }
        }
        if let chroma = CVPixelBufferGetBaseAddressOfPlane(pixelBuffer, 1) {
            let rowBytes = CVPixelBufferGetBytesPerRowOfPlane(pixelBuffer, 1)
            for row in 0..<(height / 2) {
                memset(chroma.advanced(by: row * rowBytes), 128, rowBytes)
            }
        }
        return pixelBuffer
    }

    /// Encodes `count` frames and returns them in submission order.
    private func encodeFrames(count: Int, forceKeyframeAt: Set<Int> = []) throws -> [H264EncodedFrame] {
        let encoder = try H264Encoder(configuration: H264EncoderConfiguration(
            width: width, height: height, bitrate: 2_000_000, expectedFrameRate: 30))

        let lock = NSLock()
        var frames: [H264EncodedFrame] = []
        var failure: H264EncoderError?
        encoder.onEncodedFrame = { frame in
            lock.lock(); frames.append(frame); lock.unlock()
        }
        encoder.onError = { error in
            lock.lock(); failure = failure ?? error; lock.unlock()
        }

        for index in 0..<count {
            let pixelBuffer = try makePixelBuffer(seed: UInt8(index * 7 % 200))
            try encoder.encode(pixelBuffer: pixelBuffer,
                               captureUs: UInt64(index) * 33_333,
                               forceKeyframe: forceKeyframeAt.contains(index))
        }
        encoder.flush()
        encoder.invalidate()

        lock.lock(); defer { lock.unlock() }
        if let failure { throw failure }
        return frames
    }

    // MARK: - Session

    func testSessionCreatesWithLowLatencyConfiguration() throws {
        // Chiefly a guard on the encoder specification: EnableLowLatencyRateControl
        // is a creation-time key, and passing it as a property instead fails
        // silently, so "the session was created at all" is the only signal.
        XCTAssertNoThrow(try H264Encoder(configuration:
            H264EncoderConfiguration(width: width, height: height)))
    }

    func testRejectsAbsurdDimensions() {
        XCTAssertThrowsError(try H264Encoder(configuration:
            H264EncoderConfiguration(width: 0, height: 0)))
    }

    // MARK: - Output shape

    func testEncodesFramesAndFirstIsKeyframe() throws {
        let frames = try encodeFrames(count: 12)
        XCTAssertFalse(frames.isEmpty, "the encoder produced nothing")
        XCTAssertTrue(frames[0].isKeyframe, "a stream must open with a keyframe")
        XCTAssertNotNil(frames[0].parameterSets, "keyframes must carry parameter sets")
        XCTAssertTrue(frames.allSatisfy { !$0.data.isEmpty })
    }

    func testCaptureTimestampSurvivesTheEncoder() throws {
        // The timestamp is what the whole latency budget is measured against, and
        // it travels through VideoToolbox's async callback — easy to lose.
        let frames = try encodeFrames(count: 6)
        let timestamps = frames.map { $0.captureUs }
        XCTAssertEqual(timestamps, timestamps.sorted(), "frames must arrive in capture order")
        XCTAssertTrue(timestamps.allSatisfy { $0 % 33_333 == 0 },
                      "capture timestamps were not preserved verbatim: \(timestamps)")
    }

    func testParameterSetsAreWellFormed() throws {
        let frames = try encodeFrames(count: 4)
        let sets = try XCTUnwrap(frames.first?.parameterSets)
        XCTAssertFalse(sets.sps.isEmpty, "no SPS")
        XCTAssertFalse(sets.pps.isEmpty, "no PPS")
        XCTAssertEqual(sets.sps[0][sets.sps[0].startIndex] & 0x1F, H264NALType.sps)
        XCTAssertEqual(sets.pps[0][sets.pps[0].startIndex] & 0x1F, H264NALType.pps)
    }

    func testNALLengthSizeIsReportedNotAssumed() throws {
        let frames = try encodeFrames(count: 2)
        let size = try XCTUnwrap(frames.first?.nalLengthSize)
        // VideoToolbox emits 4 in practice. The point is that the value is read
        // from the format description rather than hard-coded, so this asserts it
        // is one of the legal sizes rather than that it is 4.
        XCTAssertTrue([1, 2, 4].contains(size), "illegal NAL length size \(size)")
    }

    // MARK: - The output is genuinely parseable H.264

    func testEncodedOutputParsesAsAVCC() throws {
        let frames = try encodeFrames(count: 8)
        for (index, frame) in frames.enumerated() {
            let units = try H264Bitstream.scanAVCC(frame.data, nalLengthSize: frame.nalLengthSize)
            XCTAssertFalse(units.isEmpty, "frame \(index) contained no NAL units")
            // Every NAL must account for itself exactly: scanAVCC throws if a
            // length prefix overruns, so reaching here means the whole buffer was
            // consumed cleanly and nothing was truncated.
            XCTAssertTrue(units.allSatisfy { !$0.payload.isEmpty })
        }
    }

    func testKeyframeFlagMatchesTheBitstream() throws {
        // The flag comes from the NotSync attachment; the bitstream is the ground
        // truth. If these disagree, the viewer requests IDRs it already has, or
        // worse, never gets one it needs.
        let frames = try encodeFrames(count: 10, forceKeyframeAt: [5])
        for (index, frame) in frames.enumerated() {
            let units = try H264Bitstream.scanAVCC(frame.data, nalLengthSize: frame.nalLengthSize)
            let containsIDR = units.contains { $0.type == H264NALType.idrSlice }
            XCTAssertEqual(frame.isKeyframe, containsIDR,
                           "frame \(index): isKeyframe=\(frame.isKeyframe) but IDR in bitstream=\(containsIDR)")
        }
    }

    func testForcedKeyframeProducesAnIDRMidStream() throws {
        let frames = try encodeFrames(count: 10, forceKeyframeAt: [5])
        let keyframeIndices = frames.indices.filter { frames[$0].isKeyframe }
        XCTAssertTrue(keyframeIndices.contains(0), "the first frame is always a keyframe")
        XCTAssertGreaterThan(keyframeIndices.count, 1,
                             "kVTEncodeFrameOptionKey_ForceKeyFrame produced no extra IDR — " +
                             "this is how a viewer's keyframe request is honoured")
    }

    func testParameterSetsAppearOnKeyframesOnly() throws {
        let frames = try encodeFrames(count: 10, forceKeyframeAt: [5])
        for frame in frames {
            XCTAssertEqual(frame.parameterSets != nil, frame.isKeyframe,
                           "parameter sets should ride keyframes and nothing else")
        }
    }

    // MARK: - The whole point: it must decode on Windows

    func testEncodedStreamConvertsToAnnexBForMediaFoundation() throws {
        // VideoToolbox's AVCC is not decodable by Media Foundation. This is the
        // conversion the Windows viewer depends on, applied to real encoder
        // output rather than to a synthetic buffer.
        let frames = try encodeFrames(count: 8, forceKeyframeAt: [4])
        var stream = Data()
        for frame in frames {
            stream.append(try H264Bitstream.avccToAnnexB(
                frame.data,
                nalLengthSize: frame.nalLengthSize,
                parameterSets: frame.parameterSets))
        }

        let units = H264Bitstream.scanAnnexB(stream)
        XCTAssertFalse(units.isEmpty)
        XCTAssertTrue(H264Bitstream.annexBContainsKeyframe(stream))

        // Every IDR must be preceded by its parameter sets in the same access
        // unit — Media Foundation has no out-of-band channel for them.
        let idrIndices = units.indices.filter { units[$0].isKeyframe }
        XCTAssertFalse(idrIndices.isEmpty)
        for idr in idrIndices {
            var start = idr
            while start > 0 && !units[start - 1].isParameterSet && start > idr - 4 { start -= 1 }
            let preceding = units[max(0, idr - 4)..<idr].map { $0.type }
            XCTAssertTrue(preceding.contains(H264NALType.sps), "IDR at \(idr) has no SPS before it")
            XCTAssertTrue(preceding.contains(H264NALType.pps), "IDR at \(idr) has no PPS before it")
        }
    }

    /// Writes a real macOS-encoded Annex-B stream to the scratch directory so it
    /// can be handed to the Windows decoder. Skipped unless
    /// `LANMSG_EMIT_H264_FIXTURE` is set, because it is a generator rather than
    /// an assertion.
    func testEmitMacOSFixtureForCrossPlatformDecode() throws {
        guard let path = ProcessInfo.processInfo.environment["LANMSG_EMIT_H264_FIXTURE"] else {
            throw XCTSkip("set LANMSG_EMIT_H264_FIXTURE=<path> to regenerate the fixture")
        }
        let frames = try encodeFrames(count: 60, forceKeyframeAt: [30])
        var stream = Data()
        for frame in frames {
            stream.append(try H264Bitstream.avccToAnnexB(
                frame.data, nalLengthSize: frame.nalLengthSize, parameterSets: frame.parameterSets))
        }
        try stream.write(to: URL(fileURLWithPath: path))
        print("wrote \(stream.count) bytes, \(frames.count) frames to \(path)")
    }
}
