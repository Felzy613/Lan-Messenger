import XCTest
import CryptoKit
@testable import LanMessenger

final class CryptoTests: XCTestCase {

    // MARK: - Key agreement symmetry

    func testSharedKeyIsSymmetric() throws {
        let alice = Curve25519.KeyAgreement.PrivateKey()
        let bob   = Curve25519.KeyAgreement.PrivateKey()

        let alicePubB64 = alice.publicKey.rawRepresentation.base64EncodedString()
        let bobPubB64   = bob.publicKey.rawRepresentation.base64EncodedString()

        let keyFromAlice = try SessionCrypto.symmetricKey(myPrivate: alice, theirPublicKeyB64: bobPubB64)
        let keyFromBob   = try SessionCrypto.symmetricKey(myPrivate: bob,   theirPublicKeyB64: alicePubB64)

        XCTAssertEqual(keyFromAlice.withUnsafeBytes { Data($0) },
                       keyFromBob.withUnsafeBytes   { Data($0) },
                       "Both sides must derive the same symmetric key")
    }

    // MARK: - Encrypt / decrypt round-trip

    func testTextEncryptDecryptRoundTrip() throws {
        let alice = Curve25519.KeyAgreement.PrivateKey()
        let bob   = Curve25519.KeyAgreement.PrivateKey()
        let bobPubB64   = bob.publicKey.rawRepresentation.base64EncodedString()
        let alicePubB64 = alice.publicKey.rawRepresentation.base64EncodedString()

        let plaintext = Data("Hello, Bob!".utf8)
        let aad       = Data("test-message-id".utf8)

        let (nonceB64, ctB64) = try SessionCrypto.encryptForPeer(
            myPrivate: alice, peerPublicKeyB64: bobPubB64, plaintext: plaintext, aad: aad)

        let recovered = try SessionCrypto.decryptFromPeer(
            myPrivate: bob, peerPublicKeyB64: alicePubB64,
            nonceB64: nonceB64, ciphertextB64: ctB64, aad: aad)

        XCTAssertEqual(recovered, plaintext)
    }

    func testWrongAADFails() throws {
        let alice = Curve25519.KeyAgreement.PrivateKey()
        let bob   = Curve25519.KeyAgreement.PrivateKey()
        let bobPubB64   = bob.publicKey.rawRepresentation.base64EncodedString()
        let alicePubB64 = alice.publicKey.rawRepresentation.base64EncodedString()

        let (nonceB64, ctB64) = try SessionCrypto.encryptForPeer(
            myPrivate: alice, peerPublicKeyB64: bobPubB64,
            plaintext: Data("secret".utf8), aad: Data("correct-aad".utf8))

        XCTAssertThrowsError(try SessionCrypto.decryptFromPeer(
            myPrivate: bob, peerPublicKeyB64: alicePubB64,
            nonceB64: nonceB64, ciphertextB64: ctB64, aad: Data("wrong-aad".utf8)))
    }

    func testTamperedCiphertextFails() throws {
        let alice = Curve25519.KeyAgreement.PrivateKey()
        let bob   = Curve25519.KeyAgreement.PrivateKey()
        let bobPubB64   = bob.publicKey.rawRepresentation.base64EncodedString()
        let alicePubB64 = alice.publicKey.rawRepresentation.base64EncodedString()
        let aad = Data("aad".utf8)

        let (nonceB64, ctB64) = try SessionCrypto.encryptForPeer(
            myPrivate: alice, peerPublicKeyB64: bobPubB64, plaintext: Data("secret".utf8), aad: aad)

        var ctData = Data(base64Encoded: ctB64)!
        ctData[0] ^= 0xFF   // flip a byte
        let tampered = ctData.base64EncodedString()

        XCTAssertThrowsError(try SessionCrypto.decryptFromPeer(
            myPrivate: bob, peerPublicKeyB64: alicePubB64,
            nonceB64: nonceB64, ciphertextB64: tampered, aad: aad))
    }

    // MARK: - Decrypt known Python-generated vector

    func testDecryptKnownVector() throws {
        let vectors = try loadVectors()
        guard let keys = vectors["keys"] as? [String: String],
              let textVec = vectors["text_message"] as? [String: Any] else {
            XCTFail("Malformed test vectors"); return
        }

        let alicePrivB64 = keys["alice_private_b64"]!
        let bobPrivB64   = keys["bob_private_b64"]!
        let alicePubB64  = keys["alice_public_b64"]!

        let bobPrivRaw  = Data(base64Encoded: bobPrivB64)!
        let bobPrivKey  = try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: bobPrivRaw)

        let nonceB64  = textVec["nonce_b64"] as! String
        let ctB64     = textVec["ciphertext_b64"] as! String
        let msgId     = textVec["message_id"] as! String
        let expected  = textVec["plaintext_utf8"] as! String

        let recovered = try SessionCrypto.decryptFromPeer(
            myPrivate: bobPrivKey,
            peerPublicKeyB64: alicePubB64,
            nonceB64: nonceB64,
            ciphertextB64: ctB64,
            aad: Data(msgId.utf8)
        )
        XCTAssertEqual(String(data: recovered, encoding: .utf8), expected)
    }

    // MARK: - History crypto

    func testHistoryEncryptDecryptRoundTrip() throws {
        let key = Curve25519.KeyAgreement.PrivateKey()
        let plaintext = Data(#"{"192.168.1.1":[]}"#.utf8)
        let fileJSON = try HistoryCrypto.encryptHistory(plaintext: plaintext, privateKey: key)
        let recovered = try HistoryCrypto.decryptHistory(fileJSON: fileJSON, privateKey: key)
        XCTAssertEqual(recovered, plaintext)
    }

    func testHistoryKeyDeterministic() throws {
        let key = Curve25519.KeyAgreement.PrivateKey()
        let k1 = HistoryCrypto.historyKey(privateKey: key)
        let k2 = HistoryCrypto.historyKey(privateKey: key)
        XCTAssertEqual(k1.withUnsafeBytes { Data($0) }, k2.withUnsafeBytes { Data($0) })
    }

    func testDecryptKnownHistoryVector() throws {
        let vectors = try loadVectors()
        guard let keys = vectors["keys"] as? [String: String],
              let histVec = vectors["history"] as? [String: Any] else {
            XCTFail("Malformed test vectors"); return
        }

        let alicePrivRaw = Data(base64Encoded: keys["alice_private_b64"]!)!
        let alicePrivKey = try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: alicePrivRaw)

        let fileJSON = histVec["file_json"] as! String
        let expected = histVec["plaintext_utf8"] as! String

        let recovered = try HistoryCrypto.decryptHistory(fileJSON: fileJSON, privateKey: alicePrivKey)
        XCTAssertEqual(String(data: recovered, encoding: .utf8), expected)
    }
}

// MARK: - Shared helper

private func loadVectors() throws -> [String: Any] {
    if let url = Bundle(for: CryptoTests.self).url(forResource: "known_good_exchange", withExtension: "json") {
        let data = try Data(contentsOf: url)
        return try JSONSerialization.jsonObject(with: data) as! [String: Any]
    }
    let url = URL(fileURLWithPath: #file)
        .deletingLastPathComponent()
        .appendingPathComponent("known_good_exchange.json")
    let data = try Data(contentsOf: url)
    return try JSONSerialization.jsonObject(with: data) as! [String: Any]
}
