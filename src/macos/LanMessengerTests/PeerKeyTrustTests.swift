import XCTest
import CryptoKit
@testable import LanMessenger

// The classification behind the consent prompt's warning.
//
// PROTOCOL.md requires the prompt to distinguish a key matching the saved
// contact from a new or changed one, because a display name is trivially
// spoofable by anyone on the LAN and so is an IP address. Getting this wrong in
// the safe-looking direction — reporting a stranger's key as pinned — is the
// single worst bug this feature could ship, so the tests lean on that direction.
final class PeerKeyTrustTests: XCTestCase {

    private let daveKey = "ZGF2ZS1rZXktMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDA="
    private let strangerKey = "c3RyYW5nZXItMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDA="

    private func dave(at ip: String = "10.0.0.5") -> KnownContact {
        KnownContact(publicKeyB64: daveKey, username: "Dave", lastIP: ip)
    }

    // MARK: - The safe case

    func testAPinnedKeyIsRecognised() {
        let trust = PeerKeyTrustEvaluator.evaluate(
            peerPublicKeyB64: daveKey, peerIP: "10.0.0.5", contacts: [dave()])
        XCTAssertEqual(trust, .pinned(username: "Dave"))
        XCTAssertFalse(trust.requiresWarning)
    }

    func testAPinnedKeyFromANewAddressIsStillPinned() {
        // Peers roam, and the app already migrates conversation history when a
        // saved contact turns up on a new IP. Warning here would train the user
        // to click through the warning that matters.
        let trust = PeerKeyTrustEvaluator.evaluate(
            peerPublicKeyB64: daveKey, peerIP: "192.168.1.77", contacts: [dave()])
        XCTAssertEqual(trust, .pinned(username: "Dave"))
    }

    // MARK: - The case the prompt exists for

    func testAnUnfamiliarKeyAtAFamiliarAddressIsFlagged() {
        // Either Dave reinstalled, or somebody is standing where Dave was. The
        // prompt cannot tell, and neither can this — so it names the previous
        // occupant and lets the human decide.
        let trust = PeerKeyTrustEvaluator.evaluate(
            peerPublicKeyB64: strangerKey, peerIP: "10.0.0.5", contacts: [dave()])
        XCTAssertEqual(trust, .changedAtKnownAddress(username: "Dave",
                                                     previousPublicKeyB64: daveKey))
        XCTAssertTrue(trust.requiresWarning)
        XCTAssertFalse(trust.isPinned, "a changed key must never read as pinned")
    }

    func testAStrangerAtAStrangeAddressIsUnknown() {
        let trust = PeerKeyTrustEvaluator.evaluate(
            peerPublicKeyB64: strangerKey, peerIP: "172.16.9.9", contacts: [dave()])
        XCTAssertEqual(trust, .unknown)
        XCTAssertTrue(trust.requiresWarning)
    }

    func testNoContactsMeansNothingIsTrusted() {
        XCTAssertEqual(
            PeerKeyTrustEvaluator.evaluate(peerPublicKeyB64: daveKey, peerIP: "10.0.0.5",
                                           contacts: []),
            .unknown)
    }

    // MARK: - Degenerate input

    func testAnEmptyKeyIsNeverTrusted() {
        // A packet with no key should not have reached here at all, but "no key
        // supplied" must not be able to match a contact whose key is also blank.
        let blank = KnownContact(publicKeyB64: "", username: "Ghost", lastIP: "10.0.0.5")
        XCTAssertEqual(
            PeerKeyTrustEvaluator.evaluate(peerPublicKeyB64: "", peerIP: "10.0.0.5",
                                           contacts: [blank]),
            .unknown)
    }

    func testAContactWithNoRecordedAddressDoesNotMatchAnEmptyIP() {
        // Contacts imported from the legacy Python config can carry an empty
        // lastIP. Matching "" to "" would make every such contact vouch for
        // every unknown key that arrives without a source address.
        let addressless = KnownContact(publicKeyB64: daveKey, username: "Dave", lastIP: "")
        XCTAssertEqual(
            PeerKeyTrustEvaluator.evaluate(peerPublicKeyB64: strangerKey, peerIP: "",
                                           contacts: [addressless]),
            .unknown)
    }

    func testTheFirstPreviousOccupantIsNamedWhenSeveralQualify() {
        let older = KnownContact(publicKeyB64: "b2xkZXItMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMA==",
                                 username: "Older", lastIP: "10.0.0.5")
        let trust = PeerKeyTrustEvaluator.evaluate(
            peerPublicKeyB64: strangerKey, peerIP: "10.0.0.5", contacts: [older, dave()])
        XCTAssertEqual(trust, .changedAtKnownAddress(username: "Older",
                                                     previousPublicKeyB64: older.publicKeyB64))
    }

    // MARK: - What the prompt shows alongside it

    func testTheFingerprintIsStableAndKeySpecific() throws {
        // The fingerprint is what a user actually compares out of band, so it
        // has to be derived from the key and nothing else.
        let a = Curve25519.KeyAgreement.PrivateKey().publicKey.rawRepresentation.base64EncodedString()
        let b = Curve25519.KeyAgreement.PrivateKey().publicKey.rawRepresentation.base64EncodedString()

        let first = try XCTUnwrap(RemoteSessionCrypto.fingerprint(publicKeyB64: a))
        XCTAssertEqual(first, RemoteSessionCrypto.fingerprint(publicKeyB64: a))
        XCTAssertNotEqual(first, RemoteSessionCrypto.fingerprint(publicKeyB64: b))
        XCTAssertNil(RemoteSessionCrypto.fingerprint(publicKeyB64: "not-base64!"),
                     "unparseable input has no fingerprint to show")
        XCTAssertNil(RemoteSessionCrypto.fingerprint(
            publicKeyB64: Data(repeating: 0, count: 31).base64EncodedString()),
            "a key that is not 32 raw bytes has no fingerprint to show")
    }
}
