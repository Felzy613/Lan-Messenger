import XCTest
@testable import LanMessenger

// Guards the AVCC <-> Annex-B conversion, which is the biggest cross-platform
// risk in the remote-desktop work: VideoToolbox and Media Foundation package
// H.264 differently, neither decoder accepts the other's packaging, and the
// failure is not a clean error — it is a picture that never appears.
//
// The tests that matter here run against `windows_h264_sample.h264`, a stream
// produced by the real Microsoft H264 Encoder MFT on Dave's Dell
// (spikes/windows-mf-probe, 2026-09-14). Testing a converter against its own
// output proves only that it is self-consistent; testing it against a genuine
// artefact of the other platform's encoder is what actually de-risks this.
final class H264BitstreamTests: XCTestCase {

    // MARK: - Synthetic structure

    private func nal(_ type: UInt8, _ fill: UInt8 = 0xAA, count: Int = 8) -> Data {
        Data([type] + [UInt8](repeating: fill, count: count))
    }

    func testScannerConsumesFourByteStartCodesWhole() {
        // The bug this pins: a 4-byte start code contains a 3-byte one at
        // offset+1, so a scanner that advances a single byte finds a phantom NAL
        // inside every real one and reports exactly double. That is not
        // hypothetical — the first analysis of the Windows fixture reported
        // "126 four-byte and 126 three-byte codes", and the suspiciously equal
        // counts were the only clue.
        var stream = Data()
        stream.append(contentsOf: [0, 0, 0, 1]); stream.append(nal(H264NALType.sps))
        stream.append(contentsOf: [0, 0, 0, 1]); stream.append(nal(H264NALType.pps))
        stream.append(contentsOf: [0, 0, 0, 1]); stream.append(nal(H264NALType.idrSlice))

        let units = H264Bitstream.scanAnnexB(stream)
        XCTAssertEqual(units.count, 3, "a 4-byte start code must be consumed whole")
        XCTAssertEqual(units.map { $0.type },
                       [H264NALType.sps, H264NALType.pps, H264NALType.idrSlice])
    }

    func testScannerHandlesThreeByteStartCodes() {
        var stream = Data()
        stream.append(contentsOf: [0, 0, 1]); stream.append(nal(H264NALType.sps))
        stream.append(contentsOf: [0, 0, 1]); stream.append(nal(H264NALType.idrSlice))
        let units = H264Bitstream.scanAnnexB(stream)
        XCTAssertEqual(units.count, 2)
        XCTAssertEqual(units.map { $0.type }, [H264NALType.sps, H264NALType.idrSlice])
    }

    func testScannerHandlesMixedStartCodeSizes() {
        // Legal, and some encoders do it. Our own output is uniformly 4-byte,
        // but the reader must not assume the peer's is.
        var stream = Data()
        stream.append(contentsOf: [0, 0, 0, 1]); stream.append(nal(H264NALType.sps))
        stream.append(contentsOf: [0, 0, 1]);    stream.append(nal(H264NALType.pps))
        stream.append(contentsOf: [0, 0, 0, 1]); stream.append(nal(H264NALType.idrSlice))
        XCTAssertEqual(H264Bitstream.scanAnnexB(stream).map { $0.type },
                       [H264NALType.sps, H264NALType.pps, H264NALType.idrSlice])
    }

    func testEmptyAndStartCodeOnlyBuffers() {
        XCTAssertTrue(H264Bitstream.scanAnnexB(Data()).isEmpty)
        XCTAssertTrue(H264Bitstream.scanAnnexB(Data([0, 0, 0, 1])).isEmpty,
                      "a start code with no payload is not a NAL unit")
        XCTAssertThrowsError(try H264Bitstream.annexBToAVCC(Data([1, 2, 3])))
    }

    // MARK: - Round trips

    func testAnnexBToAVCCAndBack() throws {
        let slice = nal(H264NALType.nonIDRSlice, 0x11, count: 40)
        let stream = H264Bitstream.annexB(from: [slice])

        let avcc = try H264Bitstream.annexBToAVCC(stream)
        XCTAssertEqual(avcc.count, 4 + slice.count, "4-byte length prefix plus payload")
        XCTAssertEqual(Array(avcc.prefix(4)), [0, 0, 0, UInt8(slice.count)])

        let back = try H264Bitstream.avccToAnnexB(avcc, nalLengthSize: 4)
        XCTAssertEqual(back, stream)
    }

    func testAVCCLengthSizesOtherThanFour() throws {
        let slice = nal(H264NALType.nonIDRSlice, 0x22, count: 20)
        let stream = H264Bitstream.annexB(from: [slice])
        for size in [1, 2, 4] {
            let avcc = try H264Bitstream.annexBToAVCC(stream, nalLengthSize: size)
            XCTAssertEqual(avcc.count, size + slice.count)
            let units = try H264Bitstream.scanAVCC(avcc, nalLengthSize: size)
            XCTAssertEqual(units.count, 1)
            XCTAssertEqual(units[0].payload, slice)
        }
        XCTAssertThrowsError(try H264Bitstream.annexBToAVCC(stream, nalLengthSize: 3),
                             "3 is not a legal NAL length size")
    }

    func testParameterSetsAreDroppedFromSampleData() throws {
        // SPS/PPS belong in the CMVideoFormatDescription. Leaving them in the
        // sample data is a decode error on VideoToolbox, not a harmless
        // duplicate.
        let stream = H264Bitstream.annexB(from: [
            nal(H264NALType.accessUnitDelimiter, 0x01, count: 1),
            nal(H264NALType.sps, 0x67, count: 12),
            nal(H264NALType.pps, 0x68, count: 3),
            nal(H264NALType.idrSlice, 0x65, count: 60),
        ])
        let avcc = try H264Bitstream.annexBToAVCC(stream)
        let units = try H264Bitstream.scanAVCC(avcc, nalLengthSize: 4)
        XCTAssertEqual(units.map { $0.type }, [H264NALType.idrSlice],
                       "only picture data belongs in the sample")
    }

    func testKeyframeGetsParameterSetsPrependedOnTheWayOut() throws {
        // Media Foundation has no out-of-band channel for parameter sets, so a
        // stream whose SPS/PPS live only in our format description decodes to
        // nothing on Windows.
        let sps = nal(H264NALType.sps, 0x67, count: 12)
        let pps = nal(H264NALType.pps, 0x68, count: 3)
        let idr = nal(H264NALType.idrSlice, 0x65, count: 50)
        let avcc = try H264Bitstream.annexBToAVCC(H264Bitstream.annexB(from: [idr]))

        let out = try H264Bitstream.avccToAnnexB(avcc, nalLengthSize: 4,
                                                 parameterSets: (sps: [sps], pps: [pps]))
        XCTAssertEqual(H264Bitstream.scanAnnexB(out).map { $0.type },
                       [H264NALType.sps, H264NALType.pps, H264NALType.idrSlice])
    }

    func testNonKeyframeDoesNotGetParameterSets() throws {
        let sps = nal(H264NALType.sps, 0x67, count: 12)
        let pps = nal(H264NALType.pps, 0x68, count: 3)
        let slice = nal(H264NALType.nonIDRSlice, 0x41, count: 50)
        let avcc = try H264Bitstream.annexBToAVCC(H264Bitstream.annexB(from: [slice]))

        let out = try H264Bitstream.avccToAnnexB(avcc, nalLengthSize: 4,
                                                 parameterSets: (sps: [sps], pps: [pps]))
        XCTAssertEqual(H264Bitstream.scanAnnexB(out).map { $0.type }, [H264NALType.nonIDRSlice],
                       "repeating parameter sets on every frame is pure bitrate waste")
    }

    // MARK: - Malformed AVCC

    func testMalformedAVCCIsRejectedNotMisparsed() {
        // A hostile or corrupt length prefix must fault rather than walk off the
        // end of the buffer.
        var claimsTooMuch = Data([0x00, 0x00, 0xFF, 0xFF])
        claimsTooMuch.append(Data(repeating: 0x41, count: 10))
        XCTAssertThrowsError(try H264Bitstream.scanAVCC(claimsTooMuch, nalLengthSize: 4))

        XCTAssertThrowsError(try H264Bitstream.scanAVCC(Data([0x00, 0x00]), nalLengthSize: 4),
                             "a truncated length prefix must fault")
        XCTAssertThrowsError(try H264Bitstream.scanAVCC(Data([0, 0, 0, 0]), nalLengthSize: 4),
                             "a zero-length NAL would loop forever")
    }

    // MARK: - The real Windows encoder output

    private func windowsSample() throws -> Data {
        let url: URL
        if let bundled = Bundle(for: H264BitstreamTests.self)
            .url(forResource: "windows_h264_sample", withExtension: "h264") {
            url = bundled
        } else {
            url = URL(fileURLWithPath: #file).deletingLastPathComponent()
                .appendingPathComponent("windows_h264_sample.h264")
        }
        return try Data(contentsOf: url)
    }

    func testWindowsSampleStructureMatchesWhatTheEncoderActuallyProduced() throws {
        let data = try windowsSample()
        let units = H264Bitstream.scanAnnexB(data)

        // Pinned against the recorded run: 60 NV12 frames in, 126 NAL units out.
        XCTAssertEqual(units.count, 126, "scanner disagrees with the recorded structure")

        var counts: [UInt8: Int] = [:]
        for unit in units { counts[unit.type, default: 0] += 1 }
        XCTAssertEqual(counts[H264NALType.accessUnitDelimiter], 60, "one AUD per frame")
        XCTAssertEqual(counts[H264NALType.nonIDRSlice], 58)
        XCTAssertEqual(counts[H264NALType.idrSlice], 2)
        XCTAssertEqual(counts[H264NALType.sps], 2)
        XCTAssertEqual(counts[H264NALType.pps], 2)
        XCTAssertEqual((counts[H264NALType.nonIDRSlice] ?? 0) + (counts[H264NALType.idrSlice] ?? 0), 60,
                       "one slice per frame")
    }

    func testWindowsSampleCarriesParameterSetsInBandBeforeEveryIDR() throws {
        // This is the property the macOS receive path depends on. If Media
        // Foundation ever stopped doing it we would have to negotiate parameter
        // sets out of band, so it is worth a test rather than an assumption.
        let units = H264Bitstream.scanAnnexB(try windowsSample())
        let idrIndices = units.indices.filter { units[$0].isKeyframe }
        XCTAssertEqual(idrIndices.count, 2)

        for idr in idrIndices {
            // Look back to the start of the access unit, not a fixed number of
            // NAL units: the encoder puts two SEI messages between the parameter
            // sets and the first IDR, so a narrow window misses them. The access
            // unit delimiter is the real boundary.
            var start = idr
            while start > 0 && units[start - 1].type != H264NALType.accessUnitDelimiter {
                start -= 1
            }
            let accessUnit = units[start..<idr].map { $0.type }
            XCTAssertTrue(accessUnit.contains(H264NALType.sps),
                          "no SPS in the access unit containing the IDR at \(idr), saw \(accessUnit)")
            XCTAssertTrue(accessUnit.contains(H264NALType.pps),
                          "no PPS in the access unit containing the IDR at \(idr), saw \(accessUnit)")
        }
    }

    func testWindowsSampleParameterSetsAreExtractable() throws {
        let (sps, pps) = H264Bitstream.parameterSets(inAnnexB: try windowsSample())
        XCTAssertEqual(sps.count, 2)
        XCTAssertEqual(pps.count, 2)
        // Recorded sizes from the real stream; a converter that silently
        // truncated a parameter set would still "work" without this.
        XCTAssertEqual(sps[0].count, 25)
        XCTAssertEqual(pps[0].count, 4)
        XCTAssertEqual(sps[0][sps[0].startIndex] & 0x1F, H264NALType.sps)
        XCTAssertEqual(pps[0][pps[0].startIndex] & 0x1F, H264NALType.pps)
        XCTAssertEqual(sps[0], sps[1], "both IDRs should carry identical parameter sets")
    }

    func testWindowsSampleConvertsToAVCCAndBackWithoutLoss() throws {
        // The end-to-end property: Windows Annex-B in, AVCC for VideoToolbox,
        // Annex-B back out for the return path, with every picture NAL intact.
        let original = try windowsSample()
        let pictureUnits = H264Bitstream.scanAnnexB(original)
            .filter { !H264Bitstream.nonPictureTypes.contains($0.type) }

        let avcc = try H264Bitstream.annexBToAVCC(original)
        let roundTripped = try H264Bitstream.scanAVCC(avcc, nalLengthSize: 4)

        XCTAssertEqual(roundTripped.count, pictureUnits.count)
        XCTAssertEqual(roundTripped.map { $0.payload }, pictureUnits.map { $0.payload },
                       "payload bytes must survive the conversion untouched")

        let back = try H264Bitstream.avccToAnnexB(avcc, nalLengthSize: 4)
        XCTAssertEqual(H264Bitstream.scanAnnexB(back).map { $0.payload },
                       pictureUnits.map { $0.payload })
    }

    func testWindowsSampleKeyframeDetection() throws {
        let data = try windowsSample()
        XCTAssertTrue(H264Bitstream.annexBContainsKeyframe(data))

        // And a slice-only excerpt must not be mistaken for one.
        let nonIDR = H264Bitstream.scanAnnexB(data).first { $0.type == H264NALType.nonIDRSlice }
        let excerpt = H264Bitstream.annexB(from: [try XCTUnwrap(nonIDR).payload])
        XCTAssertFalse(H264Bitstream.annexBContainsKeyframe(excerpt))
    }
}
