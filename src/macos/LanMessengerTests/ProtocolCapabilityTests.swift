import XCTest
@testable import LanMessenger

// The optional discovery `caps` field.
//
// It exists for one reason: `PacketValidator` drops unknown packet types
// *silently*. A client that sends `remote_invite` to a peer too old to know the
// type gets no reply, no error and no timeout of its own — it simply waits
// forever. Advertising the capability turns that hang into a disabled menu item.
//
// Which makes the compatibility direction the important one. Every client
// shipped so far omits this field, and every one of them must keep working
// unchanged; a newer client will advertise tokens this build has never heard of,
// and those must not upset anything either. The tests below are mostly about
// what happens when the field is absent, malformed or unfamiliar.
final class ProtocolCapabilityTests: XCTestCase {

    private func decode(_ json: String) throws -> DiscoveryPacket {
        try JSONDecoder().decode(DiscoveryPacket.self, from: Data(json.utf8))
    }

    private let legacyBeacon = """
    {"type":"discovery","username":"Dave","port":54232,
     "public_key_b64":"AAAA","ips":["10.0.0.5"]}
    """

    // MARK: - Compatibility

    func testAClientThatPredatesTheFieldStillDecodes() throws {
        let packet = try decode(legacyBeacon)
        XCTAssertEqual(packet.username, "Dave")
        XCTAssertNil(packet.caps)
        XCTAssertFalse(packet.supportsRemoteDesktop,
                       "absence must never be read as 'probably supports it'")
    }

    func testALegacyBeaconStillValidates() throws {
        let packet = PacketValidator.validateDiscovery(
            data: Data(legacyBeacon.utf8), senderIP: "10.0.0.5",
            ownPublicKeyB64: "BBBB", ownIPs: [])
        XCTAssertNotNil(packet, "adding an optional field must not drop older peers")
        XCTAssertFalse(packet?.supportsRemoteDesktop ?? true)
    }

    func testUnknownTokensAreToleratedAndDoNotImplySupport() throws {
        let packet = try decode("""
        {"type":"discovery","username":"Dave","port":54232,"public_key_b64":"AAAA",
         "ips":[],"caps":["audio-v3","clipboard-v9"]}
        """)
        XCTAssertEqual(packet.caps, ["audio-v3", "clipboard-v9"])
        XCTAssertFalse(packet.supportsRemoteDesktop)
    }

    func testAMalformedCapsFieldIsTreatedAsAbsentRatherThanFatal() throws {
        // A peer with a broken capability field is still a peer. Failing the
        // whole packet would make it vanish from the network entirely, over a
        // field that is optional by definition.
        for malformed in ["\"remote-desktop-v1\"", "7", "{\"a\":1}", "null"] {
            let packet = try decode("""
            {"type":"discovery","username":"Dave","port":54232,"public_key_b64":"AAAA",
             "ips":[],"caps":\(malformed)}
            """)
            XCTAssertEqual(packet.username, "Dave", "caps=\(malformed) dropped the packet")
            XCTAssertFalse(packet.supportsRemoteDesktop)
        }
    }

    // MARK: - Bounds

    func testCapsAreBoundedBecauseDiscoveryIsUnauthenticatedUDP() throws {
        // Anyone on the LAN can send this, and the tokens are retained per peer.
        let many = (0..<200).map { "\"token-\($0)\"" }.joined(separator: ",")
        let packet = try decode("""
        {"type":"discovery","username":"Dave","port":54232,"public_key_b64":"AAAA",
         "ips":[],"caps":[\(many)]}
        """)
        XCTAssertEqual(packet.caps?.count, DiscoveryPacket.maxCapTokens)
    }

    func testOverlongAndEmptyTokensAreDropped() throws {
        let long = String(repeating: "x", count: DiscoveryPacket.maxCapTokenLength + 1)
        let packet = try decode("""
        {"type":"discovery","username":"Dave","port":54232,"public_key_b64":"AAAA",
         "ips":[],"caps":["\(long)","","remote-desktop-v1"]}
        """)
        XCTAssertEqual(packet.caps, ["remote-desktop-v1"])
        XCTAssertTrue(packet.supportsRemoteDesktop)
    }

    // MARK: - What we send

    func testOurOwnBeaconAdvertisesRemoteDesktopUnderTheWireKey() throws {
        let beacon = DiscoveryPacket(
            type: "discovery", username: "Dave", port: 54232,
            publicKeyB64: "AAAA", ips: ["10.0.0.5"], relayIdHash: nil)
        let encoded = try JSONEncoder().encode(beacon)
        let json = try XCTUnwrap(
            JSONSerialization.jsonObject(with: encoded) as? [String: Any])

        // The key is asserted literally: the Windows side writes the same string
        // and a rename on one platform is invisible to the other until a user
        // reports that remote desktop is greyed out for half their contacts.
        let caps = try XCTUnwrap(json["caps"] as? [String])
        XCTAssertEqual(caps, ["remote-desktop-v1"])
        XCTAssertEqual(ProtocolCapability.remoteDesktopV1, "remote-desktop-v1")
    }

    func testTheAdvertisementSurvivesARoundTrip() throws {
        let sent = DiscoveryPacket(type: "discovery", username: "Dave", port: 54232,
                                   publicKeyB64: "AAAA", ips: [])
        let received = try JSONDecoder().decode(
            DiscoveryPacket.self, from: try JSONEncoder().encode(sent))
        XCTAssertTrue(received.supportsRemoteDesktop)
    }

    func testAPacketCanBeBuiltWithNoCapabilitiesAtAll() throws {
        // Used by tests and by any path that must look like an older client.
        let bare = DiscoveryPacket(type: "discovery", username: "Dave", port: 54232,
                                   publicKeyB64: "AAAA", ips: [], relayIdHash: nil, caps: nil)
        let json = try XCTUnwrap(JSONSerialization.jsonObject(
            with: try JSONEncoder().encode(bare)) as? [String: Any])
        XCTAssertNil(json["caps"], "a nil caps must be omitted, not sent as null")
    }

    // MARK: - The peer record

    func testPeerInfoDoesNotAssumeCapabilityItWasNotTold() {
        let silent = PeerInfo(ip: "10.0.0.5", username: "Dave", port: 54232,
                              publicKeyB64: "AAAA", lastSeen: Date())
        XCTAssertFalse(silent.supportsRemoteDesktop)

        let capable = PeerInfo(ip: "10.0.0.5", username: "Dave", port: 54232,
                               publicKeyB64: "AAAA", lastSeen: Date(),
                               caps: [ProtocolCapability.remoteDesktopV1])
        XCTAssertTrue(capable.supportsRemoteDesktop)
    }
}
