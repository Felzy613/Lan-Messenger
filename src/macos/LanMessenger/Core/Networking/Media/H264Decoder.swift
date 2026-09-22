import Foundation
import CoreMedia

// The receive half of the video path: turns the Annex-B a peer puts on the wire
// into CMSampleBuffers a display layer can decode.
//
// There is deliberately no VTDecompressionSession here. AVSampleBufferDisplayLayer
// decodes what it is handed, on the hardware path, and owns presentation timing
// — a session of our own would be a second decoder producing pixel buffers
// nobody looks at. What this class owns is the part the layer cannot do for
// itself: the CMVideoFormatDescription, which on our wire format arrives in-band
// ahead of every IDR and has to be rebuilt from it.
//
// Three things here are the difference between a picture and a permanent black
// rectangle, and none of them report an error when wrong:
//
//  * **An access unit boundary is a slice.** Splitting on access unit delimiters
//    or parameter sets collapses a VideoToolbox stream into a handful of units,
//    because it emits no delimiters and parameter sets only at IDRs.
//    `H264Bitstream.splitAccessUnits` carries that rule.
//  * **Parameter sets must not also appear in the sample data.** They live in
//    the format description; feeding them in both places is a decode error on
//    VideoToolbox, not a harmless duplicate. `annexBToAVCC` drops them.
//  * **Nothing decodes until an IDR.** A P-frame handed to a freshly built
//    decoder is not a recoverable glitch — it is garbage propagating until the
//    next keyframe. So the decoder drops until it sees one and says so, which is
//    what turns into a `keyframe_request` on the control channel.

/// The picture size a stream is currently carrying.
struct H264VideoDimensions: Equatable {
    let width: Int
    let height: Int
}

enum H264DecoderError: Error, Equatable, CustomStringConvertible {
    case formatDescriptionFailed(OSStatus)
    case blockBufferAllocationFailed(Int)
    case blockBufferFailed(OSStatus)
    case sampleBufferFailed(OSStatus)

    var description: String {
        switch self {
        case .formatDescriptionFailed(let s):
            return "CMVideoFormatDescriptionCreateFromH264ParameterSets failed (\(s))"
        case .blockBufferAllocationFailed(let bytes):
            return "could not allocate \(bytes) bytes for a sample"
        case .blockBufferFailed(let s):
            return "CMBlockBufferCreateWithMemoryBlock failed (\(s))"
        case .sampleBufferFailed(let s):
            return "CMSampleBufferCreateReady failed (\(s))"
        }
    }
}

final class H264Decoder {

    /// Raised when the stream cannot be decoded from where it stands: no
    /// parameter sets yet, or pictures arriving before the first IDR. The
    /// session turns this into a `keyframe_request`. Debounced — it fires once
    /// per stall, not once per dropped frame.
    var onNeedsKeyframe: ((String) -> Void)?

    /// Fires when the picture size changes, including the first time it is
    /// known. The viewer needs this to lay out, and `video_config` is not a
    /// substitute: a host can change resolution mid-session.
    var onDimensionsChanged: ((H264VideoDimensions) -> Void)?

    private(set) var formatDescription: CMVideoFormatDescription?
    private(set) var dimensions: H264VideoDimensions?

    private var sps: [Data] = []
    private var pps: [Data] = []
    private var needsKeyframe = true
    private var keyframeRequestSent = false

    /// The NAL length prefix size of the AVCC *we* author, and the value baked
    /// into the format description we build alongside it. This is the one place
    /// 4 is not an assumption: the rule against hard-coding it applies to a
    /// format description somebody else handed us, where the value has to be
    /// read back. Here both sides of the pair are ours, and they agree by
    /// construction.
    static let nalLengthSize: Int32 = 4

    init() {}

    // MARK: - Decode

    /// Converts one wire payload into sample buffers ready to enqueue.
    ///
    /// Normally returns exactly one: the protocol puts one access unit in one
    /// media frame. It still splits, because a buffer carrying several is legal
    /// Annex-B and a viewer that silently mangles it would be very hard to
    /// diagnose from the far side.
    func decode(annexB: Data, captureUs: UInt64) throws -> [CMSampleBuffer] {
        var samples: [CMSampleBuffer] = []
        for unit in H264Bitstream.splitAccessUnits(annexB) {
            if let sample = try decodeAccessUnit(unit, captureUs: captureUs) {
                samples.append(sample)
            }
        }
        return samples
    }

    /// Drops decoder state. Called after the presenter flushes and on any
    /// discontinuity — everything after a flush is undecodable until an IDR, so
    /// this also asks for one.
    func reset(reason: String) {
        needsKeyframe = true
        keyframeRequestSent = false
        requestKeyframe(reason)
    }

    private func decodeAccessUnit(_ unit: Data, captureUs: UInt64) throws -> CMSampleBuffer? {
        let sets = H264Bitstream.parameterSets(inAnnexB: unit)
        if !sets.sps.isEmpty, !sets.pps.isEmpty {
            try adoptParameterSets(sps: sets.sps, pps: sets.pps)
        }

        guard let formatDescription else {
            requestKeyframe("no_parameter_sets")
            return nil
        }

        let isKeyframe = H264Bitstream.annexBContainsKeyframe(unit)
        if needsKeyframe {
            guard isKeyframe else {
                requestKeyframe("awaiting_idr")
                return nil
            }
            needsKeyframe = false
            keyframeRequestSent = false
        }

        // Parameter sets and delimiters come out here; SEI stays, which is legal
        // in sample data and occasionally carries something a decoder wants.
        let avcc = try H264Bitstream.annexBToAVCC(unit, nalLengthSize: Int(Self.nalLengthSize))
        guard !avcc.isEmpty else { return nil }

        return try Self.makeSampleBuffer(
            avcc: avcc,
            formatDescription: formatDescription,
            captureUs: captureUs,
            isKeyframe: isKeyframe)
    }

    // MARK: - Format description

    /// Rebuilds the format description when the parameter sets change, and only
    /// then. Rebuilding on every IDR would be correct but throws away the
    /// decoder's warm state several times a minute for no reason; never
    /// rebuilding is worse, because a resolution change then decodes as garbage
    /// against a description of the old picture.
    private func adoptParameterSets(sps newSPS: [Data], pps newPPS: [Data]) throws {
        guard newSPS != sps || newPPS != pps || formatDescription == nil else { return }

        let description = try Self.makeFormatDescription(sps: newSPS, pps: newPPS)
        sps = newSPS
        pps = newPPS
        formatDescription = description

        let size = CMVideoFormatDescriptionGetDimensions(description)
        let updated = H264VideoDimensions(width: Int(size.width), height: Int(size.height))
        if updated != dimensions {
            dimensions = updated
            NetLogger.remote(event: "decoder_format",
                             reason: "\(updated.width)x\(updated.height)")
            onDimensionsChanged?(updated)
        }
        // New parameter sets mean the decoder has to start again from an IDR.
        // The access unit that carried them normally is one, so this usually
        // resolves on the very next line.
        needsKeyframe = true
    }

    static func makeFormatDescription(sps: [Data], pps: [Data]) throws -> CMVideoFormatDescription {
        // SPS first, then PPS: the AVCC decoder configuration is ordered, and a
        // description built the other way round is accepted here and rejected by
        // the decoder later.
        let sets = sps + pps

        var storage: [UnsafeMutablePointer<UInt8>] = []
        defer { storage.forEach { $0.deallocate() } }

        var pointers: [UnsafePointer<UInt8>] = []
        var sizes: [Int] = []
        for set in sets {
            let buffer = UnsafeMutablePointer<UInt8>.allocate(capacity: set.count)
            set.copyBytes(to: buffer, count: set.count)
            storage.append(buffer)
            pointers.append(UnsafePointer(buffer))
            sizes.append(set.count)
        }

        var description: CMFormatDescription?
        let status = CMVideoFormatDescriptionCreateFromH264ParameterSets(
            allocator: kCFAllocatorDefault,
            parameterSetCount: pointers.count,
            parameterSetPointers: &pointers,
            parameterSetSizes: &sizes,
            nalUnitHeaderLength: nalLengthSize,
            formatDescriptionOut: &description)

        guard status == noErr, let description else {
            throw H264DecoderError.formatDescriptionFailed(status)
        }
        return description
    }

    // MARK: - Sample buffers

    static func makeSampleBuffer(
        avcc: Data,
        formatDescription: CMVideoFormatDescription,
        captureUs: UInt64,
        isKeyframe: Bool
    ) throws -> CMSampleBuffer {
        let count = avcc.count

        // CMBlockBuffer does not copy. The block has to outlive this scope and
        // be freed exactly once, so it is malloc'd and handed to kCFAllocatorMalloc
        // — CoreMedia frees it when the last reference to the buffer goes.
        guard let block = malloc(count) else {
            throw H264DecoderError.blockBufferAllocationFailed(count)
        }
        avcc.copyBytes(to: UnsafeMutableRawBufferPointer(start: block, count: count))

        var blockBuffer: CMBlockBuffer?
        let blockStatus = CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault,
            memoryBlock: block,
            blockLength: count,
            blockAllocator: kCFAllocatorMalloc,
            customBlockSource: nil,
            offsetToData: 0,
            dataLength: count,
            flags: 0,
            blockBufferOut: &blockBuffer)

        guard blockStatus == kCMBlockBufferNoErr, let blockBuffer else {
            free(block)
            throw H264DecoderError.blockBufferFailed(blockStatus)
        }

        // The capture clock is microseconds, and it is what the whole latency
        // budget is measured against — carry it verbatim rather than
        // re-deriving a presentation time from a frame counter.
        var timing = CMSampleTimingInfo(
            duration: .invalid,
            presentationTimeStamp: CMTime(value: CMTimeValue(captureUs), timescale: 1_000_000),
            decodeTimeStamp: .invalid)
        var sampleSize = count

        var sampleBuffer: CMSampleBuffer?
        let sampleStatus = CMSampleBufferCreateReady(
            allocator: kCFAllocatorDefault,
            dataBuffer: blockBuffer,
            formatDescription: formatDescription,
            sampleCount: 1,
            sampleTimingEntryCount: 1,
            sampleTimingArray: &timing,
            sampleSizeEntryCount: 1,
            sampleSizeArray: &sampleSize,
            sampleBufferOut: &sampleBuffer)

        guard sampleStatus == noErr, let sampleBuffer else {
            throw H264DecoderError.sampleBufferFailed(sampleStatus)
        }

        setAttachments(on: sampleBuffer, isKeyframe: isKeyframe)
        return sampleBuffer
    }

    /// `DisplayImmediately` is what makes the layer show the frame on arrival
    /// instead of scheduling it against a timebase we never set — without it a
    /// remote-desktop stream with no control timebase simply never appears.
    ///
    /// `NotSync` is the inverse flag it looks like: present and true means *not*
    /// a keyframe. Marking it correctly is what lets the layer, and our own
    /// presenter, tell whether a sample can start a fresh decode after a flush.
    private static func setAttachments(on sampleBuffer: CMSampleBuffer, isKeyframe: Bool) {
        guard let attachments = CMSampleBufferGetSampleAttachmentsArray(
            sampleBuffer, createIfNecessary: true), CFArrayGetCount(attachments) > 0 else { return }

        let dictionary = unsafeBitCast(CFArrayGetValueAtIndex(attachments, 0),
                                       to: CFMutableDictionary.self)
        CFDictionarySetValue(
            dictionary,
            Unmanaged.passUnretained(kCMSampleAttachmentKey_DisplayImmediately).toOpaque(),
            Unmanaged.passUnretained(kCFBooleanTrue).toOpaque())
        CFDictionarySetValue(
            dictionary,
            Unmanaged.passUnretained(kCMSampleAttachmentKey_NotSync).toOpaque(),
            Unmanaged.passUnretained(isKeyframe ? kCFBooleanFalse : kCFBooleanTrue).toOpaque())
    }

    /// True if the sample is a keyframe, read back from its own attachments.
    /// The presenter uses this to know when a stall has actually cleared.
    static func isKeyframe(_ sampleBuffer: CMSampleBuffer) -> Bool {
        guard let attachments = CMSampleBufferGetSampleAttachmentsArray(
            sampleBuffer, createIfNecessary: false) as? [[CFString: Any]],
              let first = attachments.first else { return true }
        return !((first[kCMSampleAttachmentKey_NotSync] as? Bool) ?? false)
    }

    // MARK: - Keyframe requests

    private func requestKeyframe(_ reason: String) {
        guard !keyframeRequestSent else { return }
        keyframeRequestSent = true
        NetLogger.remote(event: "decoder_needs_keyframe", reason: reason)
        onNeedsKeyframe?(reason)
    }
}
