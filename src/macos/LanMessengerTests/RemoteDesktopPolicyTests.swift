import XCTest
@testable import LanMessenger

// The consent gate. PROTOCOL.md calls these protocol requirements rather than
// interface preferences — "a client that does not enforce them is not
// compatible" — so they are tested like protocol rules: exhaustively, and from
// the direction of the mistake that would be worst to ship.
//
// That direction is always "more permissive than intended". A prompt that never
// appears is a bug report; a prompt that appears for a stranger, or a session
// that starts without one, is the thing that turns a chat app into a RAT.
final class RemoteDesktopPolicyTests: XCTestCase {

    private let daveKey = "ZGF2ZS1rZXktMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDA="
    private let strangerKey = "c3RyYW5nZXItMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDA="
    private let ownKey = "b3duLWtleS0wMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDA="

    private var contacts: [KnownContact] {
        [KnownContact(publicKeyB64: daveKey, username: "Dave", lastIP: "10.0.0.5")]
    }

    private func context(key: String,
                         ip: String = "10.0.0.5",
                         mode: RemoteDesktopMode = .on,
                         inFlight: Bool = false,
                         contacts: [KnownContact]? = nil) -> RemoteInviteContext {
        RemoteInviteContext(peerPublicKeyB64: key, peerIP: ip, ownPublicKeyB64: ownKey,
                            mode: mode, contacts: contacts ?? self.contacts,
                            hasSessionInFlight: inFlight)
    }

    // MARK: - The mode

    func testTheFeatureIsOffByDefault() {
        XCTAssertEqual(AppConfig().remoteDesktopMode, .off)
        XCTAssertFalse(AppConfig().remoteDesktopMode.isEnabled)
    }

    func testAnUnrecognisedModeFailsClosed() {
        // A config written by a newer build, or a corrupted one, must not leave
        // the screen reachable. This is the reason the setting is a string
        // rather than a bool: a bool has no way to express "I don't know".
        XCTAssertEqual(RemoteDesktopMode.parse("viewOnly"), .off)
        XCTAssertEqual(RemoteDesktopMode.parse(""), .off)
        XCTAssertEqual(RemoteDesktopMode.parse(nil), .off)
        XCTAssertEqual(RemoteDesktopMode.parse("ON"), .on, "case should not decide security")
    }

    func testAnUnknownModeInStoredConfigDoesNotThrowAwayTheRestOfIt() throws {
        // Failing closed must not mean failing loudly: a single unrecognised
        // value cannot be allowed to take the user's contacts with it.
        let json = """
        {"username":"Dave","remote_desktop_mode":"unattended","relay_enabled":true}
        """
        let config = try JSONDecoder().decode(AppConfig.self, from: Data(json.utf8))
        XCTAssertEqual(config.remoteDesktopMode, .off)
        XCTAssertEqual(config.username, "Dave")
        XCTAssertTrue(config.relayEnabled)
    }

    func testTheModeSurvivesARoundTripUnderTheWireKey() throws {
        var config = AppConfig()
        config.remoteDesktopMode = .on
        let encoded = try JSONEncoder().encode(config)
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: encoded) as? [String: Any])
        XCTAssertEqual(json["remote_desktop_mode"] as? String, "on")

        let decoded = try JSONDecoder().decode(AppConfig.self, from: encoded)
        XCTAssertEqual(decoded.remoteDesktopMode, .on)
    }

    func testAConfigPredatingTheSettingReadsAsOff() throws {
        let legacy = #"{"username":"Dave"}"#
        let config = try JSONDecoder().decode(AppConfig.self, from: Data(legacy.utf8))
        XCTAssertEqual(config.remoteDesktopMode, .off,
                       "an upgrade must never switch the feature on for somebody")
    }

    // MARK: - Inbound: strangers

    func testAStrangerGetsSilenceNotADecline() {
        // A decline confirms that this address runs the app and has the feature.
        // A stranger who can provoke any response at all can use that to probe.
        let decision = RemoteDesktopPolicy.decide(context(key: strangerKey, ip: "172.16.9.9"))
        XCTAssertEqual(decision, .ignore(.notAContact))
    }

    func testTurningTheFeatureOnDoesNotWidenWhoMayReachYou() {
        // Trust is settled before the mode is consulted, so the switch changes
        // what happens for contacts who could already reach you — never who.
        for mode in RemoteDesktopMode.allCases {
            XCTAssertEqual(
                RemoteDesktopPolicy.decide(context(key: strangerKey, ip: "172.16.9.9", mode: mode)),
                .ignore(.notAContact),
                "mode \(mode) let a stranger through")
        }
    }

    func testAChangedKeyAtAKnownAddressIsIgnoredAndNamedDistinctly() {
        // Not a saved contact, so the consent rules say drop. But an unfamiliar
        // key arriving at a familiar address is the exact shape of the attack
        // pinning defends against, and "nothing happened" is a poor account of
        // it in a bug report.
        let decision = RemoteDesktopPolicy.decide(context(key: strangerKey, ip: "10.0.0.5"))
        XCTAssertEqual(decision, .ignore(.keyChangedAtKnownAddress))
        XCTAssertFalse(decision.isPrompt)
    }

    func testAnInviteFromOurselvesIsIgnored() {
        XCTAssertEqual(RemoteDesktopPolicy.decide(context(key: ownKey)), .ignore(.selfInvite))
    }

    func testAnInviteWithNoKeyIsIgnored() {
        XCTAssertEqual(RemoteDesktopPolicy.decide(context(key: "")), .ignore(.malformed))
    }

    func testNoContactsMeansNoPromptEver() {
        XCTAssertEqual(
            RemoteDesktopPolicy.decide(context(key: daveKey, contacts: [])),
            .ignore(.notAContact))
    }

    // MARK: - Inbound: contacts

    func testASavedContactPromptsWhenTheFeatureIsOn() {
        let decision = RemoteDesktopPolicy.decide(context(key: daveKey))
        XCTAssertEqual(decision, .prompt(trust: .pinned(username: "Dave")))
    }

    func testASavedContactIsDeclinedNotIgnoredWhenTheFeatureIsOff() {
        // They already know this address runs the app, so silence tells them
        // nothing they did not know and leaves them hanging — which is exactly
        // the failure `caps` exists to prevent.
        XCTAssertEqual(
            RemoteDesktopPolicy.decide(context(key: daveKey, mode: .off)),
            .decline(.disabled))
    }

    func testASecondInviteWhileOneIsInFlightIsDeclined() {
        XCTAssertEqual(
            RemoteDesktopPolicy.decide(context(key: daveKey, inFlight: true)),
            .decline(.busy))
    }

    func testASavedContactOnANewAddressStillPrompts() {
        // Peers roam. Warning or refusing here would train the user to click
        // through the warning that matters.
        let decision = RemoteDesktopPolicy.decide(context(key: daveKey, ip: "192.168.1.77"))
        XCTAssertEqual(decision, .prompt(trust: .pinned(username: "Dave")))
    }

    func testEveryPromptCarriesAPinnedKey() {
        // The invariant behind the dialog: if a prompt is ever raised for a key
        // that is not pinned, the fingerprint it shows is decoration.
        let cases: [(String, String, RemoteDesktopMode, Bool)] = [
            (daveKey, "10.0.0.5", .on, false),
            (daveKey, "192.168.1.77", .on, false),
            (strangerKey, "10.0.0.5", .on, false),
            (strangerKey, "172.16.9.9", .on, false),
            (daveKey, "10.0.0.5", .off, false),
            (daveKey, "10.0.0.5", .on, true),
            (ownKey, "10.0.0.5", .on, false),
            ("", "10.0.0.5", .on, false),
        ]
        for (key, ip, mode, inFlight) in cases {
            let decision = RemoteDesktopPolicy.decide(
                context(key: key, ip: ip, mode: mode, inFlight: inFlight))
            if case .prompt(let trust) = decision {
                XCTAssertTrue(trust.isPinned,
                              "prompted for an unpinned key: \(key.prefix(8)) at \(ip)")
            }
        }
    }

    // MARK: - Outbound

    private func target(contact: Bool = true, online: Bool = true,
                        capable: Bool = true, inFlight: Bool = false) -> RemoteInviteTarget {
        RemoteInviteTarget(isSavedContact: contact, isOnline: online,
                           advertisesRemoteDesktop: capable, hasSessionInFlight: inFlight)
    }

    func testACapableOnlineContactMayBeInvited() {
        XCTAssertEqual(RemoteDesktopPolicy.availability(mode: .on, target: target()), .available)
    }

    func testTheSwitchGovernsBothDirections() {
        // One setting for the whole feature: a host that will not be viewed does
        // not offer to view. A switch that only half-applies is one users
        // misread.
        XCTAssertEqual(RemoteDesktopPolicy.availability(mode: .off, target: target()),
                       .unavailable(.localFeatureOff))
    }

    func testAPeerThatDoesNotAdvertiseTheCapabilityIsNotOffered() {
        // Sending anyway means an invite silently dropped by PacketValidator and
        // an initiator waiting forever — the whole reason `caps` exists.
        XCTAssertEqual(
            RemoteDesktopPolicy.availability(mode: .on, target: target(capable: false)),
            .unavailable(.peerLacksCapability))
    }

    func testOfflineIsReportedAheadOfAMissingCapability() {
        // Capability is learned from discovery, so a peer we have not heard from
        // may have an empty record rather than a genuinely incapable one.
        // "Offline" is both true and more actionable; "your version does not
        // support it" would be a confident wrong answer.
        XCTAssertEqual(
            RemoteDesktopPolicy.availability(mode: .on,
                                             target: target(online: false, capable: false)),
            .unavailable(.peerOffline))
    }

    func testANonContactIsNeverOffered() {
        XCTAssertEqual(
            RemoteDesktopPolicy.availability(mode: .on, target: target(contact: false)),
            .unavailable(.peerNotAContact))
    }

    func testOneSessionPerPeer() {
        XCTAssertEqual(
            RemoteDesktopPolicy.availability(mode: .on, target: target(inFlight: true)),
            .unavailable(.sessionInFlight))
    }

    func testTheLocalSwitchOutranksEveryPeerProblem() {
        // Whatever else is wrong, "you have this switched off" is the one the
        // user can act on, and the one that must not be hidden behind another.
        XCTAssertEqual(
            RemoteDesktopPolicy.availability(
                mode: .off,
                target: target(contact: false, online: false, capable: false, inFlight: true)),
            .unavailable(.localFeatureOff))
    }
}
