import Foundation
import VideoToolbox
import CoreMedia
import CoreVideo

// VideoToolbox H.264 encoder for the remote-desktop host path.
//
// Produces AVCC (length-prefixed NAL units) with the parameter sets carried in
// the CMVideoFormatDescription. `H264Bitstream` converts that to the Annex-B a
// Media Foundation decoder needs; this class stays out of that business and
// hands the parameter sets to the caller so the conversion has them.
//
// Settings here are chosen for remote control rather than for video, and two of
// them are counter-intuitive enough to be worth stating:
//
//  * **High profile, not Baseline.** Baseline's only latency-relevant property
//    is that it forbids B-frames, and `AllowFrameReordering = false` already
//    does that at any profile. So Baseline buys nothing on latency while costing
//    10-20% bitrate efficiency, because it forces CAVLC and disables the 8x8
//    transform. On screen content full of text edges that difference is visible.
//  * **Video range, not full range.** An unsignalled full-range stream is how
//    you get washed-out blacks and clipped whites on the far side: Media
//    Foundation assumes studio range unless told otherwise. 420v end to end,
//    with explicit BT.709 tags, avoids the whole class of problem.

struct H264EncoderConfiguration {
    var width: Int
    var height: Int
    /// This is a LAN. 4:2:0 chroma fringes coloured text when starved, so the
    /// budget is generous by video-call standards.
    var bitrate: Int = 10_000_000
    var expectedFrameRate: Int = 30
    /// Seconds between forced keyframes. A viewer that joins or recovers asks
    /// for one out of band rather than waiting for this.
    var keyframeIntervalSeconds: Double = 4
    var maxKeyframeInterval: Int = 120
}

/// One encoded access unit, in AVCC.
struct H264EncodedFrame {
    let data: Data
    let isKeyframe: Bool
    let captureUs: UInt64
    /// Present on keyframes. Re-read every time rather than cached once: the
    /// format description changes on a resolution change, and a cached set then
    /// describes the wrong picture.
    let parameterSets: (sps: [Data], pps: [Data])?
    /// From the format description. Never assume 4 — see H264Bitstream.
    let nalLengthSize: Int
}

enum H264EncoderError: Error, CustomStringConvertible {
    case sessionCreationFailed(OSStatus)
    case encodeFailed(OSStatus)
    case noFormatDescription
    case parameterSetsUnavailable(OSStatus)

    var description: String {
        switch self {
        case .sessionCreationFailed(let s): return "VTCompressionSessionCreate failed (\(s))"
        case .encodeFailed(let s):          return "VTCompressionSessionEncodeFrame failed (\(s))"
        case .noFormatDescription:          return "encoded sample carried no format description"
        case .parameterSetsUnavailable(let s): return "could not read H.264 parameter sets (\(s))"
        }
    }
}

final class H264Encoder {

    /// Delivered from VideoToolbox's own callback thread. Callers that touch the
    /// UI or a session queue must hop themselves.
    var onEncodedFrame: ((H264EncodedFrame) -> Void)?
    var onError: ((H264EncoderError) -> Void)?

    private var session: VTCompressionSession?
    private let configuration: H264EncoderConfiguration
    private let lock = NSLock()

    init(configuration: H264EncoderConfiguration) throws {
        self.configuration = configuration

        // EnableLowLatencyRateControl is an encoder *specification* key, passed
        // at creation. Setting it afterwards with VTSessionSetProperty silently
        // does nothing — it is not a property, and there is no error to notice.
        let spec: [CFString: Any] = [
            kVTVideoEncoderSpecification_EnableLowLatencyRateControl: kCFBooleanTrue!,
        ]

        var created: VTCompressionSession?
        let status = VTCompressionSessionCreate(
            allocator: kCFAllocatorDefault,
            width: Int32(configuration.width),
            height: Int32(configuration.height),
            codecType: kCMVideoCodecType_H264,
            encoderSpecification: spec as CFDictionary,
            imageBufferAttributes: nil,
            compressedDataAllocator: nil,
            // nil callback on purpose: frames come back through
            // VTCompressionSessionEncodeFrameWithOutputHandler below, which
            // takes a Swift closure and so needs no refcon round-trip.
            outputCallback: nil,
            refcon: nil,
            compressionSessionOut: &created)

        guard status == noErr, let session = created else {
            throw H264EncoderError.sessionCreationFailed(status)
        }
        self.session = session
        applyProperties(to: session)
        VTCompressionSessionPrepareToEncodeFrames(session)
    }

    deinit { invalidate() }

    // MARK: - Configuration

    /// Every property is applied through here because hardware encoder support
    /// varies across Apple Silicon generations and Intel Macs. A property the
    /// encoder does not implement returns `kVTPropertyNotSupportedErr`, which is
    /// information rather than a failure — asserting on it turns a working
    /// session into a crash on somebody else's Mac.
    private func applyProperties(to session: VTCompressionSession) {
        func set(_ key: CFString, _ value: CFTypeRef, _ label: String) {
            let status = VTSessionSetProperty(session, key: key, value: value)
            if status == kVTPropertyNotSupportedErr {
                NetLogger.remote(event: "encoder_property_unsupported", reason: label)
            } else if status != noErr {
                NetLogger.remote(event: "encoder_property_failed", reason: "\(label) (\(status))")
            }
        }

        set(kVTCompressionPropertyKey_RealTime, kCFBooleanTrue!, "RealTime")
        set(kVTCompressionPropertyKey_ProfileLevel,
            kVTProfileLevel_H264_High_AutoLevel, "ProfileLevel=High")
        // No B-frames. This, not the profile, is what removes reorder latency.
        set(kVTCompressionPropertyKey_AllowFrameReordering, kCFBooleanFalse!, "AllowFrameReordering=false")
        set(kVTCompressionPropertyKey_MaxKeyFrameInterval,
            configuration.maxKeyframeInterval as CFNumber, "MaxKeyFrameInterval")
        set(kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration,
            configuration.keyframeIntervalSeconds as CFNumber, "MaxKeyFrameIntervalDuration")
        set(kVTCompressionPropertyKey_AverageBitRate,
            configuration.bitrate as CFNumber, "AverageBitRate")
        set(kVTCompressionPropertyKey_ExpectedFrameRate,
            configuration.expectedFrameRate as CFNumber, "ExpectedFrameRate")
        // Do not hold frames back looking for a better encode. Commonly left at
        // its default, and it is pure added latency for this use.
        // Zero is what we want and some encoders refuse it — this Mac's answers
        // `kVTPropertyNotSupportedErr`. Falling back to one still bounds the
        // encoder to a single held frame, where giving up leaves it unbounded;
        // the log says which of the two took.
        if VTSessionSetProperty(session, key: kVTCompressionPropertyKey_MaxFrameDelayCount,
                                value: 0 as CFNumber) != noErr {
            set(kVTCompressionPropertyKey_MaxFrameDelayCount, 1 as CFNumber,
                "MaxFrameDelayCount=1 (0 refused)")
        }

        // Explicit colour tags so the VUI says what the pixels mean. Without
        // them the far side guesses, and guesses differently from us.
        set(kVTCompressionPropertyKey_ColorPrimaries, kCVImageBufferColorPrimaries_ITU_R_709_2, "ColorPrimaries")
        set(kVTCompressionPropertyKey_TransferFunction, kCVImageBufferTransferFunction_ITU_R_709_2, "TransferFunction")
        set(kVTCompressionPropertyKey_YCbCrMatrix, kCVImageBufferYCbCrMatrix_ITU_R_709_2, "YCbCrMatrix")
    }

    /// Runtime bitrate changes are supported and cheap; the QoS loop uses this
    /// rather than rebuilding the session.
    func setBitrate(_ bitrate: Int) {
        lock.lock(); defer { lock.unlock() }
        guard let session else { return }
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AverageBitRate,
                             value: bitrate as CFNumber)
    }

    /// A runaway guard on the encoder, and the only place the codec's own depth
    /// is visible. Deliberately NOT the protocol's two — see `VideoFrameBudget`:
    /// a codec pipeline's depth is fixed latency rather than growth, and a cap
    /// sitting on it refuses on any jitter. VideoToolbox in `RealTime` mode is
    /// shallow, so this should almost never refuse; the peak it reports is what
    /// says whether that is true.
    let budget = VideoFrameBudget(capacity: VideoFrameBudget.pipelineCapacity)

    // MARK: - Encode

    /// Submits one frame. `forceKeyframe` is how a viewer's keyframe request is
    /// honoured — it is a per-frame option, not a session property.
    func encode(pixelBuffer: CVPixelBuffer, captureUs: UInt64, forceKeyframe: Bool = false) throws {
        lock.lock()
        guard let session else { lock.unlock(); return }
        lock.unlock()

        // A bound, enforced here rather than trusted from the codec. `RealTime`
        // and `AllowFrameReordering=false` make VideoToolbox emit promptly, but
        // neither is a limit: a session that falls behind accumulates
        // submissions, and every one of them is latency the viewer can never pay
        // back. Dropping is the right answer for live screen content — there is
        // no value in a late frame, only in the next one.
        guard budget.tryAcquire() else { return }

        let presentation = CMTime(value: CMTimeValue(captureUs), timescale: 1_000_000)
        var frameProperties: CFDictionary?
        if forceKeyframe {
            frameProperties = [kVTEncodeFrameOptionKey_ForceKeyFrame: kCFBooleanTrue!] as CFDictionary
        }

        // Swift renames VTCompressionSessionEncodeFrameWithOutputHandler onto the
        // same base name with a trailing outputHandler: see VideoToolbox.apinotes.
        let status = VTCompressionSessionEncodeFrame(
            session,
            imageBuffer: pixelBuffer,
            presentationTimeStamp: presentation,
            duration: .invalid,
            frameProperties: frameProperties,
            infoFlagsOut: nil
        ) { [weak self] status, infoFlags, sampleBuffer in
            guard let self else { return }
            // Released on EVERY path out of the callback, including the ones
            // that produce nothing. A slot leaked on an error path shrinks the
            // budget permanently, and after two of them the encoder stops
            // accepting frames with no error anywhere to explain it.
            self.budget.release()
            guard status == noErr else { self.onError?(.encodeFailed(status)); return }
            guard let sampleBuffer,
                  CMSampleBufferDataIsReady(sampleBuffer),
                  !infoFlags.contains(.frameDropped) else { return }
            self.handle(sampleBuffer: sampleBuffer, captureUs: captureUs)
        }

        if status != noErr {
            // The callback never runs for a submit that failed synchronously.
            budget.release()
            throw H264EncoderError.encodeFailed(status)
        }
    }

    /// Drains frames the encoder is still holding. Called on teardown and on a
    /// resolution change, before the session is rebuilt.
    func flush() {
        lock.lock(); let session = self.session; lock.unlock()
        guard let session else { return }
        VTCompressionSessionCompleteFrames(session, untilPresentationTimeStamp: .invalid)
        budget.reset()
    }

    func invalidate() {
        lock.lock()
        guard let session else { lock.unlock(); return }
        self.session = nil
        lock.unlock()
        VTCompressionSessionCompleteFrames(session, untilPresentationTimeStamp: .invalid)
        VTCompressionSessionInvalidate(session)
        budget.reset()
    }

    // MARK: - Output

    private func handle(sampleBuffer: CMSampleBuffer, captureUs: UInt64) {
        guard let formatDescription = CMSampleBufferGetFormatDescription(sampleBuffer) else {
            onError?(.noFormatDescription)
            return
        }

        // A sync frame is one WITHOUT the NotSync attachment. Reading
        // DependsOnOthers instead gets this subtly wrong for some encoders.
        var isKeyframe = true
        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false)
            as? [[CFString: Any]], let first = attachments.first {
            isKeyframe = !((first[kCMSampleAttachmentKey_NotSync] as? Bool) ?? false)
        }

        var nalLengthSize: Int32 = 4
        var parameterSets: (sps: [Data], pps: [Data])?
        if isKeyframe {
            do { parameterSets = try Self.readParameterSets(formatDescription, nalLengthSize: &nalLengthSize) }
            catch let error as H264EncoderError { onError?(error) }
            catch { }
        } else {
            _ = try? Self.readParameterSets(formatDescription, nalLengthSize: &nalLengthSize)
        }

        guard let data = Self.copyContiguousData(from: sampleBuffer) else { return }

        onEncodedFrame?(H264EncodedFrame(
            data: data,
            isKeyframe: isKeyframe,
            captureUs: captureUs,
            parameterSets: parameterSets,
            nalLengthSize: Int(nalLengthSize)))
    }

    /// Reads every parameter set, and the NAL length size along with them.
    ///
    /// Iterating the reported count rather than assuming index 0 is SPS and 1 is
    /// PPS, and reading `nalUnitHeaderLength` rather than assuming 4, are both
    /// cheap here and latent bugs otherwise: VideoToolbox happens to emit two
    /// sets and a 4-byte length today, which is exactly why hard-coding survives
    /// testing.
    static func readParameterSets(
        _ formatDescription: CMFormatDescription,
        nalLengthSize: inout Int32
    ) throws -> (sps: [Data], pps: [Data]) {
        var count = 0
        let probe = CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
            formatDescription, parameterSetIndex: 0,
            parameterSetPointerOut: nil, parameterSetSizeOut: nil,
            parameterSetCountOut: &count, nalUnitHeaderLengthOut: &nalLengthSize)
        guard probe == noErr else { throw H264EncoderError.parameterSetsUnavailable(probe) }

        var sps: [Data] = [], pps: [Data] = []
        for index in 0..<count {
            var pointer: UnsafePointer<UInt8>?
            var size = 0
            let status = CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
                formatDescription, parameterSetIndex: index,
                parameterSetPointerOut: &pointer, parameterSetSizeOut: &size,
                parameterSetCountOut: nil, nalUnitHeaderLengthOut: nil)
            guard status == noErr, let pointer else { continue }
            let payload = Data(bytes: pointer, count: size)
            switch payload[payload.startIndex] & 0x1F {
            case H264NALType.sps: sps.append(payload)
            case H264NALType.pps: pps.append(payload)
            default: break
            }
        }
        return (sps, pps)
    }

    /// `CMBlockBufferGetDataPointer` hands back the first contiguous range, not
    /// necessarily the whole buffer, so a large frame can be silently truncated
    /// by code that trusts it. Copying the reported total length is the only
    /// thing that is always correct.
    static func copyContiguousData(from sampleBuffer: CMSampleBuffer) -> Data? {
        guard let blockBuffer = CMSampleBufferGetDataBuffer(sampleBuffer) else { return nil }
        let total = CMBlockBufferGetDataLength(blockBuffer)
        guard total > 0 else { return nil }
        var bytes = [UInt8](repeating: 0, count: total)
        let status = bytes.withUnsafeMutableBytes { raw in
            CMBlockBufferCopyDataBytes(blockBuffer, atOffset: 0, dataLength: total,
                                       destination: raw.baseAddress!)
        }
        guard status == kCMBlockBufferNoErr else { return nil }
        return Data(bytes)
    }
}
