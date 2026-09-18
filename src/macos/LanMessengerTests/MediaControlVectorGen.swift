import XCTest
@testable import LanMessenger

/// Emits the shared control-channel conformance vector. Skipped unless
/// `LANMSG_EMIT_CONTROL_VECTOR=<path>` is set — a generator, not an assertion.
final class MediaControlVectorGen: XCTestCase {
    func testEmitControlVector() throws {
        guard let path = ProcessInfo.processInfo.environment["LANMSG_EMIT_CONTROL_VECTOR"] else {
            throw XCTSkip("set LANMSG_EMIT_CONTROL_VECTOR=<path> to regenerate")
        }
        var lines: [String] = []
        for (name, message) in MediaControlVectorCases.all {
            let json = String(data: try MediaControlCodec.encode(message), encoding: .utf8)!
            lines.append("  {\"name\": \"\(name)\", \"json\": \(jsonString(json))}")
        }
        let doc = "{\n  \"_comment\": \"Shared control-channel vector. Both platforms must encode each case to exactly this JSON, and decode it back to the same message.\",\n  \"cases\": [\n" + lines.joined(separator: ",\n") + "\n  ]\n}\n"
        try doc.write(toFile: path, atomically: true, encoding: .utf8)
        print("wrote \(path)")
    }

    private func jsonString(_ raw: String) -> String {
        let data = try! JSONSerialization.data(withJSONObject: [raw], options: [])
        var s = String(data: data, encoding: .utf8)!
        s.removeFirst(); s.removeLast()
        return s
    }
}

/// The cases, shared by the generator and the assertions.
enum MediaControlVectorCases {
    static let all: [(String, MediaControlMessage)] = [
        ("hello", .hello(transcriptB64: "dHJhbnNjcmlwdA==")),
        ("hello_ack", .helloAck),
        ("video_config", .videoConfig(VideoConfig(width: 1920, height: 1080, scale: 2,
                                                  displayID: 69732928, codec: "h264"))),
        ("keyframe_request", .keyframeRequest(reason: "presenter_flush")),
        ("control_request", .controlRequest),
        ("control_grant", .controlGrant),
        ("control_revoke", .controlRevoke),
        ("display_list", .displayList([
            RemoteDisplayInfo(displayID: 1, width: 1920, height: 1080,
                              isPrimary: true, name: "Built-in Display"),
            RemoteDisplayInfo(displayID: 2, width: 2560, height: 1440,
                              isPrimary: false, name: "Dell U2718Q"),
        ])),
        ("display_select", .displaySelect(displayID: 2)),
        ("host_state", .hostState(RemoteHostState(secureDesktop: true, elevatedFocus: false,
                                                  locked: false))),
        ("ping", .ping(id: 42, sentUs: 1_700_000_000_123_456)),
        ("pong", .pong(id: 42, sentUs: 1_700_000_000_123_456)),
        ("stats", .stats(RemoteSessionStats(roundTripMs: 12, decodedFps: 30, droppedFrames: 3,
                                            decodeQueueDepth: 1, endToEndLatencyMs: 48))),
    ]
}
