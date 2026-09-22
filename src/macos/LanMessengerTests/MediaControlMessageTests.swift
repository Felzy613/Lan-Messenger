import XCTest
@testable import LanMessenger

// The control sub-channel codec, and the shared vector that keeps the two
// platforms saying the same thing.
//
// The vector is the important part. Every other test here could pass on both
// platforms while the two encoders disagreed about key order, number formatting
// or string escaping — and the symptom of that is not a test failure, it is a
// session that works Mac-to-Mac and Windows-to-Windows and fails across.
final class MediaControlMessageTests: XCTestCase {

    private struct Vector: Decodable {
        struct Case: Decodable { let name: String; let json: String }
        let cases: [Case]
    }

    private func vector() throws -> [String: String] {
        let url: URL
        if let bundled = Bundle(for: MediaControlMessageTests.self)
            .url(forResource: "media_control_vector", withExtension: "json") {
            url = bundled
        } else {
            url = URL(fileURLWithPath: #file).deletingLastPathComponent()
                .appendingPathComponent("media_control_vector.json")
        }
        let decoded = try JSONDecoder().decode(Vector.self, from: try Data(contentsOf: url))
        return Dictionary(uniqueKeysWithValues: decoded.cases.map { ($0.name, $0.json) })
    }

    // MARK: - Conformance

    func testEveryCaseEncodesToExactlyTheSharedVector() throws {
        let expected = try vector()
        for (name, message) in MediaControlVectorCases.all {
            let encoded = String(data: try MediaControlCodec.encode(message), encoding: .utf8)
            XCTAssertEqual(encoded, expected[name], "\(name) does not match the shared vector")
        }
    }

    func testEveryCaseDecodesBackFromTheSharedVector() throws {
        let expected = try vector()
        for (name, message) in MediaControlVectorCases.all {
            let json = try XCTUnwrap(expected[name], "\(name) missing from the vector")
            let decoded = try MediaControlCodec.decode(Data(json.utf8))
            XCTAssertEqual(decoded, message, "\(name) did not round-trip")
        }
    }

    func testTheVectorCoversEveryMessageTypeWeCanSend() throws {
        // A type added without a vector case is one whose cross-platform
        // encoding nothing checks.
        let covered = Set(MediaControlVectorCases.all.map { $0.1.type })
        let sendable = ["hello", "hello_ack", "video_config", "keyframe_request",
                        "control_request", "control_grant", "control_revoke",
                        "display_list", "display_select", "host_state",
                        "ping", "pong", "stats"]
        XCTAssertEqual(covered, Set(sendable))
        XCTAssertEqual(try vector().count, sendable.count)
    }

    // MARK: - Forward compatibility

    func testAnUnknownTypeDecodesRatherThanThrows() throws {
        // The whole point of a discriminated channel is that it can grow.
        // Faulting here would turn every future extension into a flag-day
        // upgrade — the lesson PacketValidator already taught once.
        let decoded = try MediaControlCodec.decode(
            Data(#"{"t":"clipboard_v2","payload":"x"}"#.utf8))
        XCTAssertEqual(decoded, .unknown(type: "clipboard_v2"))
        XCTAssertEqual(decoded.type, "clipboard_v2", "the name survives, so a log can say it")
    }

    func testAKnownTypeWithExtraFieldsStillDecodes() throws {
        // A newer peer adding a field must not break an older one.
        let decoded = try MediaControlCodec.decode(
            Data(#"{"t":"keyframe_request","reason":"flush","urgency":"high"}"#.utf8))
        XCTAssertEqual(decoded, .keyframeRequest(reason: "flush"))
    }

    // MARK: - Hostile input

    func testAnOversizedPayloadIsRefusedOnBothSides() {
        // The media frame cap is 4 MiB. Without a second limit here a peer can
        // make a host parse four megabytes of JSON per frame, on the session's
        // read queue, for free.
        let huge = Data(repeating: 0x20, count: MediaControlCodec.maxPayloadBytes + 1)
        XCTAssertThrowsError(try MediaControlCodec.decode(huge))

        let longReason = String(repeating: "x", count: MediaControlCodec.maxPayloadBytes)
        XCTAssertThrowsError(try MediaControlCodec.encode(.keyframeRequest(reason: longReason)))
    }

    func testMalformedPayloadsFaultRatherThanGuess() {
        XCTAssertThrowsError(try MediaControlCodec.decode(Data("not json".utf8)))
        XCTAssertThrowsError(try MediaControlCodec.decode(Data("[1,2,3]".utf8)))
        XCTAssertThrowsError(try MediaControlCodec.decode(Data("{}".utf8)))
        XCTAssertThrowsError(try MediaControlCodec.decode(Data(#"{"t":""}"#.utf8)))
        XCTAssertThrowsError(try MediaControlCodec.decode(Data(#"{"t":"video_config"}"#.utf8)))
    }

    func testAnImplausibleVideoConfigIsRecognisedAsSuch() {
        // Decoding it is fine; acting on it is not. A config claiming two
        // million pixels wide is not a resolution, it is an allocation request.
        XCTAssertTrue(VideoConfig(width: 1920, height: 1080).isPlausible)
        XCTAssertFalse(VideoConfig(width: 0, height: 1080).isPlausible)
        XCTAssertFalse(VideoConfig(width: 2_000_000, height: 1080).isPlausible)
        XCTAssertFalse(VideoConfig(width: 1920, height: -1).isPlausible)
        XCTAssertFalse(VideoConfig(width: 1920, height: 1080, scale: 0).isPlausible)
        XCTAssertFalse(VideoConfig(width: 1920, height: 1080, scale: 99).isPlausible)
    }

    func testOneBadDisplayDoesNotCostThePicker() throws {
        // Losing one monitor from a picker is recoverable; losing the picker is
        // not.
        let json = #"{"t":"display_list","displays":[{"display_id":1,"width":1920,"height":1080,"is_primary":true,"name":"Good"},"nonsense"]}"#
        let decoded = try MediaControlCodec.decode(Data(json.utf8))
        guard case .displayList(let displays) = decoded else {
            return XCTFail("expected a display list, got \(decoded)")
        }
        XCTAssertEqual(displays.count, 1)
        XCTAssertEqual(displays.first?.name, "Good")
    }

    // MARK: - Host state

    func testAnOrdinaryHostStateSaysNothing() {
        // All-false is the normal case and must not produce a banner.
        XCTAssertFalse(RemoteHostState().isNotable)
        XCTAssertTrue(RemoteHostState(secureDesktop: true).isNotable)
        XCTAssertTrue(RemoteHostState(elevatedFocus: true).isNotable)
        XCTAssertTrue(RemoteHostState(locked: true).isNotable)
    }

    // MARK: - Round trip

    func testEverySendableMessageRoundTrips() throws {
        for (name, message) in MediaControlVectorCases.all {
            let again = try MediaControlCodec.decode(try MediaControlCodec.encode(message))
            XCTAssertEqual(again, message, "\(name) did not survive its own codec")
        }
    }
}
