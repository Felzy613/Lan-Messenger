import Foundation

// The control sub-channel: channel 0, UTF-8 JSON objects, each with a `t`
// discriminator, encrypted like any other media payload.
//
// PROTOCOL.md specifies the table; this is the codec for it. Everything the two
// sides say to each other that is not a picture or a keystroke goes through
// here — key confirmation, the picture size, keyframe requests, the control
// escalation, display selection, host state, and the keepalive round trip.
//
// Three rules shape the implementation, and all three are about a peer being
// older, newer, or hostile rather than about the happy path:
//
//  * **An unknown `t` decodes rather than throws.** The whole point of a
//    discriminated control channel is that it can grow. A receiver that faults
//    on a message it has not heard of turns every future extension into a
//    flag-day upgrade, and `PacketValidator` already taught this lesson once —
//    silently dropping unknown types is what made the `caps` field necessary.
//  * **Payloads are bounded.** A control frame is a few hundred bytes. The media
//    frame cap is 4 MiB, so without a second limit here a peer can make a host
//    parse four megabytes of JSON per frame, on the session's read queue, for
//    free.
//  * **Numbers are clamped where they are used, not trusted where they arrive.**
//    A `video_config` claiming 2,000,000 pixels wide is not a resolution, it is
//    an allocation request.

/// Picture geometry. Must arrive before the first video frame and again after
/// any resolution or display change — a viewer has no other source for it, and
/// it is what input coordinates are normalized against.
struct VideoConfig: Codable, Equatable {
    var width: Int
    var height: Int
    /// Backing scale of the captured display, for a viewer that wants to present
    /// at native density. Informational: coordinates are normalized regardless.
    var scale: Double
    var displayID: UInt32
    var codec: String

    init(width: Int, height: Int, scale: Double = 1, displayID: UInt32 = 0,
         codec: String = "h264") {
        self.width = width
        self.height = height
        self.scale = scale
        self.displayID = displayID
        self.codec = codec
    }

    enum CodingKeys: String, CodingKey {
        case width, height, scale, codec
        case displayID = "display_id"
    }

    /// The largest picture either platform will agree to describe.
    ///
    /// 8K-ish, well past any real display, and small enough that a peer cannot
    /// use the field as an allocation primitive. A config outside this is not a
    /// display that exists.
    static let maxDimension = 16_384

    var isPlausible: Bool {
        width > 0 && height > 0
            && width <= Self.maxDimension && height <= Self.maxDimension
            && scale > 0 && scale <= 8
    }
}

/// One of the host's displays, for the picker.
struct RemoteDisplayInfo: Codable, Equatable {
    var displayID: UInt32
    var width: Int
    var height: Int
    var isPrimary: Bool
    var name: String

    init(displayID: UInt32, width: Int, height: Int, isPrimary: Bool, name: String) {
        self.displayID = displayID
        self.width = width
        self.height = height
        self.isPrimary = isPrimary
        self.name = name
    }

    enum CodingKeys: String, CodingKey {
        case width, height, name
        case displayID = "display_id"
        case isPrimary = "is_primary"
    }
}

/// What turns an inexplicable frozen image into an explanation.
///
/// A Windows host cannot capture or drive the secure desktop, and cannot inject
/// into a focused elevated window. Neither is a bug and neither is fixable from
/// user mode, so the viewer is told and shows a banner rather than a mystery.
struct RemoteHostState: Codable, Equatable {
    var secureDesktop: Bool
    var elevatedFocus: Bool
    var locked: Bool

    init(secureDesktop: Bool = false, elevatedFocus: Bool = false, locked: Bool = false) {
        self.secureDesktop = secureDesktop
        self.elevatedFocus = elevatedFocus
        self.locked = locked
    }

    enum CodingKeys: String, CodingKey {
        case secureDesktop = "secure_desktop"
        case elevatedFocus = "elevated_focus"
        case locked
    }

    /// Whether anything is worth saying. All-false is the ordinary state and
    /// must not produce a banner.
    var isNotable: Bool { secureDesktop || elevatedFocus || locked }
}

/// What the stats sub-channel carries. Sent by both sides; each reports what it
/// can see, and neither infers the other's numbers.
struct RemoteSessionStats: Codable, Equatable {
    var roundTripMs: Int
    var decodedFps: Int
    var droppedFrames: Int
    var decodeQueueDepth: Int
    /// Glass-to-glass, derived from `capture_us`. Negative is possible when the
    /// two machines' clocks disagree, and is reported rather than hidden — a
    /// nonsense latency figure is a clock problem worth seeing.
    var endToEndLatencyMs: Int

    init(roundTripMs: Int = 0, decodedFps: Int = 0, droppedFrames: Int = 0,
         decodeQueueDepth: Int = 0, endToEndLatencyMs: Int = 0) {
        self.roundTripMs = roundTripMs
        self.decodedFps = decodedFps
        self.droppedFrames = droppedFrames
        self.decodeQueueDepth = decodeQueueDepth
        self.endToEndLatencyMs = endToEndLatencyMs
    }

    enum CodingKeys: String, CodingKey {
        case roundTripMs = "rtt_ms"
        case decodedFps = "decoded_fps"
        case droppedFrames = "dropped_frames"
        case decodeQueueDepth = "decode_queue"
        case endToEndLatencyMs = "latency_ms"
    }
}

enum MediaControlMessage: Equatable {
    case hello(transcriptB64: String)
    case helloAck
    case videoConfig(VideoConfig)
    case keyframeRequest(reason: String)
    case controlRequest
    case controlGrant
    case controlRevoke
    case displayList([RemoteDisplayInfo])
    case displaySelect(displayID: UInt32)
    case hostState(RemoteHostState)
    case ping(id: UInt64, sentUs: UInt64)
    case pong(id: UInt64, sentUs: UInt64)
    case stats(RemoteSessionStats)
    /// A message this build does not know. Carried rather than dropped so a log
    /// can say what arrived, and so the read loop keeps going.
    case unknown(type: String)

    /// The wire value of `t`.
    var type: String {
        switch self {
        case .hello:           return "hello"
        case .helloAck:        return "hello_ack"
        case .videoConfig:     return "video_config"
        case .keyframeRequest: return "keyframe_request"
        case .controlRequest:  return "control_request"
        case .controlGrant:    return "control_grant"
        case .controlRevoke:   return "control_revoke"
        case .displayList:     return "display_list"
        case .displaySelect:   return "display_select"
        case .hostState:       return "host_state"
        case .ping:            return "ping"
        case .pong:            return "pong"
        case .stats:           return "stats"
        case .unknown(let t):  return t
        }
    }
}

enum MediaControlCodecError: Error, Equatable, CustomStringConvertible {
    case tooLarge(Int)
    case notJSON
    case missingDiscriminator
    case malformed(String)

    var description: String {
        switch self {
        case .tooLarge(let bytes): return "control payload of \(bytes) bytes exceeds the cap"
        case .notJSON:             return "control payload is not a JSON object"
        case .missingDiscriminator: return "control payload has no `t`"
        case .malformed(let type): return "control payload for `\(type)` is malformed"
        }
    }
}

enum MediaControlCodec {

    /// A control frame is a few hundred bytes. The media frame cap is 4 MiB, so
    /// without a second limit a peer can make a host parse four megabytes of
    /// JSON per frame on the session's read queue, for free.
    ///
    /// 64 KiB leaves ample room for a `display_list` on a machine with an
    /// improbable number of monitors, and nothing else here is near it.
    static let maxPayloadBytes = 64 * 1024

    // MARK: - Encode

    static func encode(_ message: MediaControlMessage) throws -> Data {
        var object: [String: Any] = ["t": message.type]

        switch message {
        case .hello(let transcript):
            object["transcript_b64"] = transcript
        case .videoConfig(let config):
            object["config"] = try dictionary(from: config)
        case .keyframeRequest(let reason):
            object["reason"] = reason
        case .displayList(let displays):
            object["displays"] = try displays.map { try dictionary(from: $0) }
        case .displaySelect(let displayID):
            object["display_id"] = displayID
        case .hostState(let state):
            object["state"] = try dictionary(from: state)
        case .ping(let id, let sentUs), .pong(let id, let sentUs):
            object["id"] = id
            object["sent_us"] = sentUs
        case .stats(let stats):
            object["stats"] = try dictionary(from: stats)
        case .helloAck, .controlRequest, .controlGrant, .controlRevoke, .unknown:
            break
        }

        // Sorted keys so the two platforms produce byte-identical output for the
        // same message, which is what makes a shared conformance vector possible.
        let data = try JSONSerialization.data(withJSONObject: object,
                                              options: [.sortedKeys])
        guard data.count <= maxPayloadBytes else {
            throw MediaControlCodecError.tooLarge(data.count)
        }
        return data
    }

    // MARK: - Decode

    static func decode(_ data: Data) throws -> MediaControlMessage {
        guard data.count <= maxPayloadBytes else {
            throw MediaControlCodecError.tooLarge(data.count)
        }
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw MediaControlCodecError.notJSON
        }
        guard let type = object["t"] as? String, !type.isEmpty else {
            throw MediaControlCodecError.missingDiscriminator
        }

        switch type {
        case "hello":
            return .hello(transcriptB64: object["transcript_b64"] as? String ?? "")
        case "hello_ack":
            return .helloAck
        case "video_config":
            guard let raw = object["config"],
                  let config: VideoConfig = try? value(from: raw) else {
                throw MediaControlCodecError.malformed(type)
            }
            return .videoConfig(config)
        case "keyframe_request":
            return .keyframeRequest(reason: object["reason"] as? String ?? "")
        case "control_request":
            return .controlRequest
        case "control_grant":
            return .controlGrant
        case "control_revoke":
            return .controlRevoke
        case "display_list":
            guard let raw = object["displays"] as? [Any] else {
                throw MediaControlCodecError.malformed(type)
            }
            // A single unparseable entry drops that display rather than the
            // whole list: losing one monitor from a picker is recoverable,
            // losing the picker is not.
            let displays = raw.compactMap { try? value(from: $0) as RemoteDisplayInfo }
            return .displayList(displays)
        case "display_select":
            guard let id = object["display_id"] as? NSNumber else {
                throw MediaControlCodecError.malformed(type)
            }
            return .displaySelect(displayID: id.uint32Value)
        case "host_state":
            guard let raw = object["state"],
                  let state: RemoteHostState = try? value(from: raw) else {
                throw MediaControlCodecError.malformed(type)
            }
            return .hostState(state)
        case "ping", "pong":
            let id = (object["id"] as? NSNumber)?.uint64Value ?? 0
            let sent = (object["sent_us"] as? NSNumber)?.uint64Value ?? 0
            return type == "ping" ? .ping(id: id, sentUs: sent) : .pong(id: id, sentUs: sent)
        case "stats":
            guard let raw = object["stats"],
                  let stats: RemoteSessionStats = try? value(from: raw) else {
                throw MediaControlCodecError.malformed(type)
            }
            return .stats(stats)
        default:
            // The whole point of a discriminated channel is that it can grow.
            // Faulting here would turn every future extension into a flag-day
            // upgrade — the lesson `PacketValidator` already taught once.
            return .unknown(type: type)
        }
    }

    // MARK: - Private

    private static func dictionary<T: Encodable>(from value: T) throws -> [String: Any] {
        let data = try JSONEncoder().encode(value)
        guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw MediaControlCodecError.malformed("\(T.self)")
        }
        return object
    }

    /// The type check is not a formality.
    ///
    /// `JSONSerialization.data(withJSONObject:)` raises an **ObjC exception**
    /// rather than throwing a Swift error when handed anything that is not a
    /// top-level container — a bare string, a number, null. `try?` does not
    /// catch that, so a peer sending `"displays": ["nonsense"]` would take the
    /// session's read loop down with it. Tolerant-looking code that calls this
    /// without checking first is a remote crash, not a lenient parser.
    private static func value<T: Decodable>(from raw: Any) throws -> T {
        guard JSONSerialization.isValidJSONObject([raw]) , raw is [String: Any] else {
            throw MediaControlCodecError.malformed("\(T.self)")
        }
        let data = try JSONSerialization.data(withJSONObject: raw)
        return try JSONDecoder().decode(T.self, from: data)
    }
}
