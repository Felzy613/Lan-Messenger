import XCTest
import AVFoundation
import CoreMedia
import CoreVideo
import VideoToolbox
@testable import LanMessenger

// The receive half of the video path, tested against a real Windows encoder
// artefact and a real VideoToolbox decoder.
//
// This is the half of cross-platform conformance that was not proven: the
// macOS → Windows direction was closed by the WS0 probe on real hardware, but
// nothing had ever shown a Media Foundation stream decoding here. The fixture
// `windows_h264_sample.h264` is genuine Microsoft H264 Encoder MFT output and
// cannot be regenerated without the Windows machine, which is what makes it
// worth building the test around rather than around our own encoder.
//
// A VTDecompressionSession is the oracle throughout. Almost everything this
// code can get wrong — parameter sets left in the sample data, a format
// description built back to front, access units split on the wrong NAL type —
// produces well-formed objects that simply never turn into a picture, so
// asserting on the objects proves nothing.
final class H264DecoderTests: XCTestCase {

    // MARK: - Fixtures

    private func windowsSample() throws -> Data {
        let url: URL
        if let bundled = Bundle(for: H264DecoderTests.self)
            .url(forResource: "windows_h264_sample", withExtension: "h264") {
            url = bundled
        } else {
            url = URL(fileURLWithPath: #file).deletingLastPathComponent()
                .appendingPathComponent("windows_h264_sample.h264")
        }
        return try Data(contentsOf: url)
    }

    /// An Annex-B buffer from explicit (type, size) NAL units. The payloads are
    /// not decodable and are not meant to be — these exercise the splitter,
    /// which only ever looks at the first byte of each unit.
    private func syntheticAnnexB(_ types: [UInt8]) -> Data {
        H264Bitstream.annexB(from: types.map { type in
            var payload = Data([type & 0x1F])
            payload.append(Data(repeating: 0x42, count: 8))
            return payload
        })
    }

    /// Decodes sample buffers with a real VTDecompressionSession and returns the
    /// size of every picture that came out. Nothing of ours is on the answering
    /// side of this: if the bytes are not H.264 that VideoToolbox accepts, the
    /// list comes back short.
    private func decodePictures(_ samples: [CMSampleBuffer]) throws -> [(width: Int, height: Int)] {
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
            let formatDescription = try XCTUnwrap(CMSampleBufferGetFormatDescription(sample))

            // A resolution change mid-stream invalidates the session. It cannot
            // happen with these fixtures, but silently decoding the new picture
            // against the old description is exactly the bug that would not show
            // up until somebody changed display on a live call.
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
                let status = VTDecompressionSessionCreate(
                    allocator: kCFAllocatorDefault,
                    formatDescription: formatDescription,
                    decoderSpecification: nil,
                    imageBufferAttributes: attributes as CFDictionary,
                    outputCallback: nil,
                    decompressionSessionOut: &created)
                XCTAssertEqual(status, noErr, "VTDecompressionSessionCreate failed (\(status))")
                session = try XCTUnwrap(created)
            }

            let active = try XCTUnwrap(session)
            let status = VTDecompressionSessionDecodeFrame(
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
            XCTAssertEqual(status, noErr, "the decoder rejected a sample outright (\(status))")
        }

        if let session { VTDecompressionSessionWaitForAsynchronousFrames(session) }
        lock.lock(); defer { lock.unlock() }
        XCTAssertEqual(failure, noErr, "the decoder reported \(failure) on at least one frame")
        return sizes
    }

    // MARK: - Access unit splitting

    func testWindowsSampleSplitsIntoOneAccessUnitPerPicture() throws {
        let units = H264Bitstream.splitAccessUnits(try windowsSample())
        XCTAssertEqual(units.count, 60, "60 encoded pictures went in; the splitter disagrees")

        for (index, unit) in units.enumerated() {
            let slices = H264Bitstream.scanAnnexB(unit).filter {
                $0.type == H264NALType.nonIDRSlice || $0.type == H264NALType.idrSlice
            }
            XCTAssertEqual(slices.count, 1, "access unit \(index) carries \(slices.count) slices")
        }
        XCTAssertEqual(units.reduce(0) { $0 + $1.count }, try windowsSample().count,
                       "splitting lost or duplicated bytes")
    }

    func testAccessUnitBoundaryIsASliceNotADelimiterOrParameterSet() {
        // VideoToolbox-shaped: no access unit delimiters anywhere, parameter sets
        // only at the IDR. Splitting on either of those collapses this to one or
        // two units instead of four — the exact failure that made the Windows
        // decoder emit almost nothing from a 60-frame stream.
        let stream = syntheticAnnexB([
            H264NALType.sps, H264NALType.pps, H264NALType.idrSlice,
            H264NALType.nonIDRSlice,
            H264NALType.nonIDRSlice,
            H264NALType.nonIDRSlice,
        ])
        let units = H264Bitstream.splitAccessUnits(stream)
        XCTAssertEqual(units.count, 4)

        // The parameter sets belong to the picture that follows them, and must
        // travel inside its access unit rather than becoming one of their own.
        let first = H264Bitstream.scanAnnexB(units[0]).map { $0.type }
        XCTAssertEqual(first, [H264NALType.sps, H264NALType.pps, H264NALType.idrSlice])
    }

    func testDelimitersAndSEIStayWithTheirPicture() {
        // Media-Foundation-shaped, which is the other layout on our wire.
        let stream = syntheticAnnexB([
            H264NALType.accessUnitDelimiter, H264NALType.sps, H264NALType.pps,
            H264NALType.sei, H264NALType.idrSlice,
            H264NALType.accessUnitDelimiter, H264NALType.nonIDRSlice,
        ])
        let units = H264Bitstream.splitAccessUnits(stream)
        XCTAssertEqual(units.count, 2)
        XCTAssertEqual(H264Bitstream.scanAnnexB(units[1]).map { $0.type },
                       [H264NALType.accessUnitDelimiter, H264NALType.nonIDRSlice])
    }

    func testTrailingNonPictureUnitsAreDropped() {
        // Parameter sets for a picture that is not in this buffer. Emitting them
        // as an access unit hands the decoder a sample with no picture in it,
        // which stalls rather than errors.
        let stream = syntheticAnnexB([
            H264NALType.idrSlice, H264NALType.sps, H264NALType.pps,
        ])
        XCTAssertEqual(H264Bitstream.splitAccessUnits(stream).count, 1)
        XCTAssertTrue(H264Bitstream.splitAccessUnits(Data([0x41, 0x42])).isEmpty,
                      "a buffer with no start code contains no access units")
    }

    // MARK: - Format description

    func testFormatDescriptionIsBuiltFromInBandParameterSets() throws {
        let decoder = H264Decoder()
        var reported: [H264VideoDimensions] = []
        decoder.onDimensionsChanged = { reported.append($0) }

        let units = H264Bitstream.splitAccessUnits(try windowsSample())
        _ = try decoder.decode(annexB: units[0], captureUs: 0)

        // Pinned against the recorded run: the probe encoded 1280x720 Main.
        // Reading the size out of the SPS is the only way a viewer learns it
        // before `video_config` exists, so it is worth asserting the value and
        // not merely that one was produced.
        let dimensions = try XCTUnwrap(decoder.dimensions)
        XCTAssertEqual(dimensions, H264VideoDimensions(width: 1280, height: 720))
        XCTAssertEqual(reported, [dimensions], "the size must be announced exactly once")

        // Unchanged parameter sets at the second IDR must not churn the
        // description or re-announce a size that did not change.
        for unit in units.dropFirst() { _ = try decoder.decode(annexB: unit, captureUs: 0) }
        XCTAssertEqual(reported, [dimensions],
                       "identical parameter sets rebuilt the format description")
    }

    func testParameterSetOrderIsSPSThenPPS() throws {
        let (sps, pps) = H264Bitstream.parameterSets(inAnnexB: try windowsSample())
        let description = try H264Decoder.makeFormatDescription(sps: [sps[0]], pps: [pps[0]])

        var count = 0
        var nalLengthSize: Int32 = 0
        XCTAssertEqual(CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
            description, parameterSetIndex: 0,
            parameterSetPointerOut: nil, parameterSetSizeOut: nil,
            parameterSetCountOut: &count, nalUnitHeaderLengthOut: &nalLengthSize), noErr)
        XCTAssertEqual(count, 2)
        XCTAssertEqual(nalLengthSize, H264Decoder.nalLengthSize,
                       "the description must describe the AVCC we actually author")

        var pointer: UnsafePointer<UInt8>?
        var size = 0
        XCTAssertEqual(CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
            description, parameterSetIndex: 0,
            parameterSetPointerOut: &pointer, parameterSetSizeOut: &size,
            parameterSetCountOut: nil, nalUnitHeaderLengthOut: nil), noErr)
        XCTAssertEqual((pointer?.pointee ?? 0) & 0x1F, H264NALType.sps,
                       "index 0 must be the SPS")
    }

    // MARK: - Sample buffers

    func testSampleDataCarriesNoParameterSetsOrDelimiters() throws {
        // Parameter sets live in the format description. Feeding them in the
        // sample data as well is a decode error on VideoToolbox, not a harmless
        // duplicate — and the error surfaces as a picture that never appears.
        let decoder = H264Decoder()
        let samples = try decoder.decode(annexB: try windowsSample(), captureUs: 0)
        XCTAssertFalse(samples.isEmpty)

        for (index, sample) in samples.enumerated() {
            let data = try XCTUnwrap(H264Encoder.copyContiguousData(from: sample))
            let units = try H264Bitstream.scanAVCC(
                data, nalLengthSize: Int(H264Decoder.nalLengthSize))
            XCTAssertFalse(units.isEmpty, "sample \(index) had no NAL units")
            for unit in units {
                XCTAssertFalse(H264Bitstream.nonPictureTypes.contains(unit.type),
                               "sample \(index) still carries NAL type \(unit.type)")
            }
        }
    }

    func testSamplesCarryDisplayImmediatelyAndAnAccurateSyncFlag() throws {
        let decoder = H264Decoder()
        let units = H264Bitstream.splitAccessUnits(try windowsSample())
        var samples: [CMSampleBuffer] = []
        for unit in units { samples += try decoder.decode(annexB: unit, captureUs: 0) }
        XCTAssertEqual(samples.count, units.count)

        for (index, sample) in samples.enumerated() {
            let attachments = try XCTUnwrap(CMSampleBufferGetSampleAttachmentsArray(
                sample, createIfNecessary: false) as? [[CFString: Any]])
            let first = try XCTUnwrap(attachments.first)
            XCTAssertEqual(first[kCMSampleAttachmentKey_DisplayImmediately] as? Bool, true,
                           "sample \(index) would be scheduled against a timebase we never set")
            XCTAssertEqual(H264Decoder.isKeyframe(sample),
                           H264Bitstream.annexBContainsKeyframe(units[index]),
                           "sample \(index) disagrees with its own bitstream about being a keyframe")
        }
        XCTAssertEqual(samples.filter { H264Decoder.isKeyframe($0) }.count, 2,
                       "the fixture carries two IDRs")
    }

    func testCaptureTimestampSurvivesTheDecoder() throws {
        // The capture clock is the whole latency budget. It must arrive at the
        // presentation layer verbatim, not re-derived from a frame counter.
        let decoder = H264Decoder()
        let units = H264Bitstream.splitAccessUnits(try windowsSample())
        for (index, unit) in units.enumerated() {
            let captureUs = UInt64(index) * 33_333 + 1_234
            for sample in try decoder.decode(annexB: unit, captureUs: captureUs) {
                let pts = CMSampleBufferGetPresentationTimeStamp(sample)
                XCTAssertEqual(pts.timescale, 1_000_000)
                XCTAssertEqual(pts.value, CMTimeValue(captureUs))
            }
        }
    }

    // MARK: - Recovery

    func testPicturesBeforeParameterSetsAreDroppedAndAKeyframeAsked() throws {
        let decoder = H264Decoder()
        var reasons: [String] = []
        decoder.onNeedsKeyframe = { reasons.append($0) }

        let orphan = syntheticAnnexB([H264NALType.nonIDRSlice])
        XCTAssertTrue(try decoder.decode(annexB: orphan, captureUs: 0).isEmpty)
        XCTAssertEqual(reasons, ["no_parameter_sets"])

        // Debounced: a stalled stream asks once, not once per frame. At 30 fps
        // the alternative is 30 control messages a second, on the channel the
        // recovery itself has to travel over.
        for _ in 0..<10 { _ = try decoder.decode(annexB: orphan, captureUs: 0) }
        XCTAssertEqual(reasons.count, 1)
    }

    func testStreamJoinedMidGOPWaitsForAnIDR() throws {
        let decoder = H264Decoder()
        var reasons: [String] = []
        decoder.onNeedsKeyframe = { reasons.append($0) }

        let units = H264Bitstream.splitAccessUnits(try windowsSample())
        let firstIDR = try XCTUnwrap(units.firstIndex { H264Bitstream.annexBContainsKeyframe($0) })
        let midGOP = try XCTUnwrap(units.indices.first { $0 > firstIDR
            && !H264Bitstream.annexBContainsKeyframe(units[$0]) })

        // A P-frame handed to a cold decoder is not a recoverable glitch; it is
        // garbage that propagates until the next IDR.
        XCTAssertTrue(try decoder.decode(annexB: units[midGOP], captureUs: 0).isEmpty)
        XCTAssertEqual(reasons, ["no_parameter_sets"])

        let secondIDR = try XCTUnwrap(units.indices.first {
            $0 > midGOP && H264Bitstream.annexBContainsKeyframe(units[$0]) })
        XCTAssertEqual(try decoder.decode(annexB: units[secondIDR], captureUs: 0).count, 1)
        XCTAssertEqual(try decoder.decode(annexB: units[secondIDR + 1], captureUs: 0).count, 1,
                       "the stream must run again once an IDR has landed")
    }

    func testResetAsksForAKeyframeAndStopsDecodingUntilOneArrives() throws {
        let decoder = H264Decoder()
        var reasons: [String] = []
        let units = H264Bitstream.splitAccessUnits(try windowsSample())
        _ = try decoder.decode(annexB: units[0], captureUs: 0)

        decoder.onNeedsKeyframe = { reasons.append($0) }
        decoder.reset(reason: "presenter_flush")
        XCTAssertEqual(reasons, ["presenter_flush"])

        let nextP = try XCTUnwrap(units.indices.first { $0 > 0
            && !H264Bitstream.annexBContainsKeyframe(units[$0]) })
        XCTAssertTrue(try decoder.decode(annexB: units[nextP], captureUs: 0).isEmpty,
                      "everything after a flush is undecodable until an IDR")
    }

    // MARK: - The whole point: a Windows stream decodes here

    func testWindowsEncodedStreamDecodesOnMacOS() throws {
        // The reverse of the direction the WS0 probe proved on real hardware.
        // 60 pictures went into the Microsoft H264 Encoder MFT; 60 must come out
        // of VideoToolbox, at one consistent size.
        let decoder = H264Decoder()
        let samples = try decoder.decode(annexB: try windowsSample(), captureUs: 0)
        XCTAssertEqual(samples.count, 60, "the decoder built the wrong number of samples")

        let pictures = try decodePictures(samples)
        XCTAssertEqual(pictures.count, 60,
                       "VideoToolbox decoded \(pictures.count) of 60 pictures from real " +
                       "Media Foundation output")

        for (index, picture) in pictures.enumerated() {
            XCTAssertEqual(picture.width, 1280, "picture \(index) width")
            XCTAssertEqual(picture.height, 720, "picture \(index) height")
        }
        XCTAssertEqual(decoder.dimensions, H264VideoDimensions(width: 1280, height: 720),
                       "the size read from the SPS must match the pictures that came out")
    }

    func testOurOwnEncoderOutputSurvivesTheRoundTrip() throws {
        // AVCC out of VideoToolbox, to the Annex-B that goes on the wire, back
        // through the receive path and into a decoder. Exercises both halves of
        // the converter against each other, which is the macOS ↔ macOS case and
        // also the cheapest way to catch a regression in either direction.
        let encoder = try H264Encoder(configuration: H264EncoderConfiguration(
            width: 320, height: 240, bitrate: 2_000_000, expectedFrameRate: 30))

        let lock = NSLock()
        var wire = Data()
        encoder.onEncodedFrame = { frame in
            guard let annexB = try? H264Bitstream.avccToAnnexB(
                frame.data, nalLengthSize: frame.nalLengthSize,
                parameterSets: frame.parameterSets) else { return }
            lock.lock(); wire.append(annexB); lock.unlock()
        }

        for index in 0..<12 {
            try encoder.encode(pixelBuffer: try Self.makePixelBuffer(seed: UInt8(index * 11 % 200)),
                               captureUs: UInt64(index) * 33_333)
        }
        encoder.flush()
        encoder.invalidate()

        lock.lock(); let stream = wire; lock.unlock()
        XCTAssertFalse(stream.isEmpty, "the encoder produced nothing")

        let decoder = H264Decoder()
        let samples = try decoder.decode(annexB: stream, captureUs: 7)
        XCTAssertEqual(decoder.dimensions, H264VideoDimensions(width: 320, height: 240))

        let pictures = try decodePictures(samples)
        XCTAssertEqual(pictures.count, samples.count,
                       "\(samples.count - pictures.count) of our own frames failed to decode")
        XCTAssertTrue(pictures.allSatisfy { $0.width == 320 && $0.height == 240 })
    }

    /// A synthetic 420v frame with a per-frame luma ramp — a constant image
    /// encodes to almost nothing and would not prove the encoder ran.
    static func makePixelBuffer(seed: UInt8, width: Int = 320, height: Int = 240) throws -> CVPixelBuffer {
        var buffer: CVPixelBuffer?
        let status = CVPixelBufferCreate(
            kCFAllocatorDefault, width, height,
            kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange,
            [kCVPixelBufferIOSurfacePropertiesKey: [:] as CFDictionary] as CFDictionary,
            &buffer)
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
}

// MARK: - Presenter

@MainActor
final class SampleBufferVideoPresenterTests: XCTestCase {

    private func makeSamples() throws -> [CMSampleBuffer] {
        let url: URL
        if let bundled = Bundle(for: SampleBufferVideoPresenterTests.self)
            .url(forResource: "windows_h264_sample", withExtension: "h264") {
            url = bundled
        } else {
            url = URL(fileURLWithPath: #file).deletingLastPathComponent()
                .appendingPathComponent("windows_h264_sample.h264")
        }
        return try H264Decoder().decode(annexB: try Data(contentsOf: url), captureUs: 0)
    }

    func testRealSamplesEnqueueWithoutFailingTheLayer() throws {
        let presenter = SampleBufferVideoPresenter()
        let samples = try makeSamples()
        for sample in samples { presenter.present(sample) }

        XCTAssertEqual(presenter.presentedFrames, samples.count)
        XCTAssertNotEqual(presenter.layer.status, .failed,
                          "the layer rejected samples a VTDecompressionSession accepts: " +
                          "\(String(describing: presenter.layer.error))")
    }

    func testFlushAsksForOneKeyframeUntilOneArrives() throws {
        let presenter = SampleBufferVideoPresenter()
        var reasons: [String] = []
        presenter.onNeedsKeyframe = { reasons.append($0) }

        presenter.flush()
        presenter.flush()
        XCTAssertEqual(reasons, ["flush"], "a stalled presenter must ask once, not once per call")

        // The stall is over when a keyframe has actually gone in — not when any
        // frame has. Clearing on any enqueue turns a persistent failure into one
        // control message per frame, on the channel the recovery travels over.
        let samples = try makeSamples()
        let firstP = try XCTUnwrap(samples.first { !H264Decoder.isKeyframe($0) })
        presenter.present(firstP)
        presenter.flush()
        XCTAssertEqual(reasons, ["flush"])

        let keyframe = try XCTUnwrap(samples.first { H264Decoder.isKeyframe($0) })
        presenter.present(keyframe)
        presenter.flush()
        XCTAssertEqual(reasons, ["flush", "flush"])
    }

    func testClearRemovesTheImageAndArmsTheNextRequest() throws {
        let presenter = SampleBufferVideoPresenter()
        var reasons: [String] = []
        presenter.onNeedsKeyframe = { reasons.append($0) }

        presenter.flush()
        presenter.clear()
        presenter.flush()
        XCTAssertEqual(reasons, ["flush", "flush"],
                       "a cleared surface is a fresh stall, and must be able to ask again")
    }
}
