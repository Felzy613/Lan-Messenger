import Foundation

// Conversion between the two H.264 bitstream packagings, which is the single
// biggest cross-platform risk in the remote-desktop work.
//
// VideoToolbox speaks AVCC: each NAL unit prefixed by its big-endian length,
// with SPS/PPS held out-of-band in the CMVideoFormatDescription.
// Media Foundation speaks Annex-B: each NAL unit prefixed by a 3- or 4-byte
// start code, with SPS/PPS emitted in-band ahead of every IDR.
//
// Neither decoder accepts the other's packaging, and the failure mode is not a
// clean error — it is a picture that never appears, or green garbage. So both
// directions are implemented here as pure byte manipulation over Data, with no
// CoreMedia types, which is what makes them testable against a real
// Windows-encoded fixture rather than against our own output.
//
// Observed on the real Windows encoder (spikes/windows-mf-probe, 2026-09-14):
// 60 frames produced 126 NAL units, SPS and PPS in-band before every IDR and at
// stream start, and 4-byte start codes throughout. Three-byte codes are still
// handled — that run only exercised the software MFT, and the Quick Sync MFT
// was not sampled.

enum H264NALType {
    static let nonIDRSlice: UInt8 = 1
    static let idrSlice: UInt8    = 5
    static let sei: UInt8         = 6
    static let sps: UInt8         = 7
    static let pps: UInt8         = 8
    static let accessUnitDelimiter: UInt8 = 9
}

/// One NAL unit located inside a buffer. `payload` excludes the start code or
/// length prefix; `type` is the low 5 bits of the first payload byte.
struct H264NALUnit: Equatable {
    let type: UInt8
    let payload: Data

    var isParameterSet: Bool { type == H264NALType.sps || type == H264NALType.pps }
    var isKeyframe: Bool { type == H264NALType.idrSlice }
}

enum H264BitstreamError: Error, Equatable, CustomStringConvertible {
    case noStartCodeFound
    case truncatedLengthPrefix(offset: Int)
    case nalLengthExceedsBuffer(offset: Int, length: Int)
    case unsupportedLengthSize(Int)
    case missingParameterSets

    var description: String {
        switch self {
        case .noStartCodeFound:
            return "no Annex-B start code in the buffer"
        case .truncatedLengthPrefix(let offset):
            return "AVCC length prefix truncated at offset \(offset)"
        case .nalLengthExceedsBuffer(let offset, let length):
            return "AVCC NAL at offset \(offset) claims \(length) bytes, past the end of the buffer"
        case .unsupportedLengthSize(let size):
            return "NAL length prefix size \(size) is not 1, 2 or 4"
        case .missingParameterSets:
            return "stream carries no SPS/PPS"
        }
    }
}

enum H264Bitstream {

    /// NAL types that carry no picture data and are dropped when building
    /// sample data. Parameter sets live in the format description, and an access
    /// unit delimiter is decoration a decoder does not need.
    static let nonPictureTypes: Set<UInt8> = [
        H264NALType.sps, H264NALType.pps, H264NALType.accessUnitDelimiter,
    ]

    // MARK: - Annex-B

    /// Splits an Annex-B buffer into NAL units.
    ///
    /// Scanning for `00 00 01` naively is safe, and deliberately so: H.264
    /// emulation-prevention bytes guarantee that sequence never occurs inside a
    /// NAL payload, so there is nothing to escape and no state to track. A
    /// 4-byte start code is a 3-byte code preceded by an extra zero, so the
    /// scanner must consume the whole code before continuing — advancing by one
    /// byte instead finds a phantom 3-byte code inside every 4-byte one, and
    /// reports exactly twice as many units as exist.
    static func scanAnnexB(_ data: Data) -> [H264NALUnit] {
        let bytes = [UInt8](data)
        let starts = startCodes(in: bytes)

        var units: [H264NALUnit] = []
        units.reserveCapacity(starts.count)
        for (index, start) in starts.enumerated() {
            let payloadStart = start.offset + start.codeLength
            let payloadEnd = index + 1 < starts.count ? starts[index + 1].offset : bytes.count
            guard payloadEnd > payloadStart else { continue }
            let payload = Data(bytes[payloadStart..<payloadEnd])
            units.append(H264NALUnit(type: payload[payload.startIndex] & 0x1F, payload: payload))
        }
        return units
    }

    /// Extracts the parameter sets an Annex-B stream carries in-band.
    static func parameterSets(inAnnexB data: Data) -> (sps: [Data], pps: [Data]) {
        var sps: [Data] = [], pps: [Data] = []
        for unit in scanAnnexB(data) {
            if unit.type == H264NALType.sps { sps.append(unit.payload) }
            if unit.type == H264NALType.pps { pps.append(unit.payload) }
        }
        return (sps, pps)
    }

    /// True if the buffer contains an IDR slice.
    static func annexBContainsKeyframe(_ data: Data) -> Bool {
        scanAnnexB(data).contains { $0.isKeyframe }
    }

    /// Splits an Annex-B buffer into access units, one per coded picture.
    ///
    /// **A slice is the boundary** — not an access unit delimiter, and not a
    /// parameter set. VideoToolbox emits no delimiters at all and parameter sets
    /// only at IDRs, so splitting on those collapsed a 60-frame stream into two
    /// units and the far side decoded almost nothing. Every slice (type 1 or 5)
    /// is exactly one picture; any AUD, SPS, PPS or SEI ahead of it belongs to
    /// it, and travels with it.
    ///
    /// NAL units trailing the last slice are dropped. They are the prelude to a
    /// picture this buffer does not contain, and handing a decoder a sample with
    /// no picture in it is how you get a stall rather than an error.
    static func splitAccessUnits(_ data: Data) -> [Data] {
        let bytes = [UInt8](data)
        let starts = startCodes(in: bytes)
        guard !starts.isEmpty else { return [] }

        var units: [Data] = []
        var unitStart = starts[0].offset
        for (index, start) in starts.enumerated() {
            let payloadStart = start.offset + start.codeLength
            guard payloadStart < bytes.count else { continue }
            let type = bytes[payloadStart] & 0x1F
            guard type == H264NALType.nonIDRSlice || type == H264NALType.idrSlice else { continue }
            let end = index + 1 < starts.count ? starts[index + 1].offset : bytes.count
            units.append(Data(bytes[unitStart..<end]))
            unitStart = end
        }
        return units
    }

    // MARK: - Annex-B → AVCC (receiving from Windows)

    /// Rewrites Annex-B sample data as AVCC length-prefixed NAL units.
    ///
    /// Parameter sets and access unit delimiters are dropped by default: SPS and
    /// PPS belong in the CMVideoFormatDescription, and feeding them in the
    /// sample data as well is a decode error on VideoToolbox rather than a
    /// harmless duplicate.
    static func annexBToAVCC(
        _ data: Data,
        nalLengthSize: Int = 4,
        dropping: Set<UInt8> = nonPictureTypes
    ) throws -> Data {
        guard nalLengthSize == 1 || nalLengthSize == 2 || nalLengthSize == 4 else {
            throw H264BitstreamError.unsupportedLengthSize(nalLengthSize)
        }
        let units = scanAnnexB(data)
        guard !units.isEmpty else { throw H264BitstreamError.noStartCodeFound }

        var out = Data()
        for unit in units where !dropping.contains(unit.type) {
            appendLength(unit.payload.count, size: nalLengthSize, to: &out)
            out.append(unit.payload)
        }
        return out
    }

    // MARK: - AVCC → Annex-B (sending to Windows)

    /// Splits an AVCC buffer into NAL units.
    ///
    /// `nalLengthSize` must come from the format description
    /// (`CMVideoFormatDescriptionGetH264ParameterSetAtIndex` reports it), never
    /// be assumed to be 4. VideoToolbox emits 4 in practice, which is exactly
    /// why hard-coding it survives testing and fails later.
    static func scanAVCC(_ data: Data, nalLengthSize: Int) throws -> [H264NALUnit] {
        guard nalLengthSize == 1 || nalLengthSize == 2 || nalLengthSize == 4 else {
            throw H264BitstreamError.unsupportedLengthSize(nalLengthSize)
        }
        let bytes = [UInt8](data)
        var units: [H264NALUnit] = []
        var i = 0
        while i < bytes.count {
            guard i + nalLengthSize <= bytes.count else {
                throw H264BitstreamError.truncatedLengthPrefix(offset: i)
            }
            var length = 0
            for k in 0..<nalLengthSize { length = (length << 8) | Int(bytes[i + k]) }
            let payloadStart = i + nalLengthSize
            guard length > 0, payloadStart + length <= bytes.count else {
                throw H264BitstreamError.nalLengthExceedsBuffer(offset: i, length: length)
            }
            let payload = Data(bytes[payloadStart..<(payloadStart + length)])
            units.append(H264NALUnit(type: payload[payload.startIndex] & 0x1F, payload: payload))
            i = payloadStart + length
        }
        return units
    }

    /// Rewrites AVCC sample data as an Annex-B byte stream.
    ///
    /// When `parameterSets` is supplied and the buffer contains an IDR, the SPS
    /// and PPS are emitted ahead of it. That is not optional politeness: Media
    /// Foundation's decoder has no out-of-band channel for them, so a stream
    /// whose parameter sets are only in our CMVideoFormatDescription decodes to
    /// nothing on the far side.
    static func avccToAnnexB(
        _ data: Data,
        nalLengthSize: Int,
        parameterSets: (sps: [Data], pps: [Data])? = nil
    ) throws -> Data {
        let units = try scanAVCC(data, nalLengthSize: nalLengthSize)
        var out = Data()
        let needsParameterSets = parameterSets != nil && units.contains { $0.isKeyframe }

        if needsParameterSets, let sets = parameterSets {
            for sps in sets.sps { appendStartCode(&out); out.append(sps) }
            for pps in sets.pps { appendStartCode(&out); out.append(pps) }
        }
        for unit in units {
            appendStartCode(&out)
            out.append(unit.payload)
        }
        return out
    }

    /// Builds an Annex-B buffer from explicit NAL payloads. Used by tests and by
    /// the encoder path when parameter sets have to be emitted on their own.
    static func annexB(from payloads: [Data]) -> Data {
        var out = Data()
        for payload in payloads {
            appendStartCode(&out)
            out.append(payload)
        }
        return out
    }

    // MARK: - Private

    /// Locates every Annex-B start code, with the length of the code found.
    ///
    /// The single place the "consume the whole start code" rule lives. A 4-byte
    /// code *contains* a 3-byte one at offset+1, so a scanner that advances one
    /// byte after a match finds a phantom unit inside every 4-byte code and
    /// reports exactly twice as many NAL units as exist. The tell is suspiciously
    /// equal 3-byte and 4-byte counts in one stream.
    private static func startCodes(in bytes: [UInt8]) -> [(offset: Int, codeLength: Int)] {
        var starts: [(offset: Int, codeLength: Int)] = []
        var i = 0
        while i + 3 <= bytes.count {
            if bytes[i] == 0, bytes[i + 1] == 0 {
                if i + 4 <= bytes.count, bytes[i + 2] == 0, bytes[i + 3] == 1 {
                    starts.append((i, 4)); i += 4; continue
                }
                if bytes[i + 2] == 1 {
                    starts.append((i, 3)); i += 3; continue
                }
            }
            i += 1
        }
        return starts
    }

    /// Four-byte start codes everywhere. Three-byte codes are legal and are
    /// parsed, but emitting one size uniformly keeps our output trivially
    /// predictable, and the extra byte per NAL is noise next to a video frame.
    private static func appendStartCode(_ out: inout Data) {
        out.append(contentsOf: [0x00, 0x00, 0x00, 0x01])
    }

    private static func appendLength(_ length: Int, size: Int, to out: inout Data) {
        for shift in stride(from: (size - 1) * 8, through: 0, by: -8) {
            out.append(UInt8((length >> shift) & 0xFF))
        }
    }
}
