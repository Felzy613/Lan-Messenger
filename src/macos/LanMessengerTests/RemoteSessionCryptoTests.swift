import XCTest
import CryptoKit
@testable import LanMessenger

// Guards the remote-desktop media handshake. Every test here corresponds to a
// failure mode that is either silent, catastrophic, or both:
//
//   * Swapped roles derive swapped keys, and the only symptom is "decryption
//     randomly fails" once video is already flowing.
//   * An unbound transcript lets a man in the middle downgrade negotiated
//     parameters without breaking the handshake at all.
//   * Canonical parameter bytes that differ by one byte across platforms turn
//     into a key confirmation failure that looks like a crypto bug rather than
//     a serialization bug.
//   * A nonce reused across a reconnect is catastrophic AES-GCM failure, not a
//     degradation.
//
// See PROTOCOL.md → Remote Desktop → Media Session Key Derivation.
final class RemoteSessionCryptoTests: XCTestCase {

    // Fixed keys so failures are reproducible rather than one-in-a-run.
    private let initiatorStatic = Curve25519.KeyAgreement.PrivateKey()
    private let responderStatic = Curve25519.KeyAgreement.PrivateKey()
    private let initiatorEph    = Curve25519.KeyAgreement.PrivateKey()
    private let responderEph    = Curve25519.KeyAgreement.PrivateKey()

    private let sessionID = "9f2c4a6e8b0d1f3a5c7e9b1d3f5a7c9e"

    private func params() -> RemoteHandshakeParams {
        RemoteHandshakeParams([
            "protocol": .int(1),
            "video":    .string("h264"),
            "display":  .int(0),
        ])
    }

    private func derive(
        role: RemoteSessionRole,
        sessionID: String? = nil,
        params: RemoteHandshakeParams? = nil
    ) throws -> RemoteSessionKeys {
        let sid = sessionID ?? self.sessionID
        let p   = params ?? self.params()
        switch role {
        case .initiator:
            return try RemoteSessionCrypto.deriveKeys(
                myRole: .initiator,
                myEphemeralPrivate: initiatorEph,
                myStaticPrivate: initiatorStatic,
                peerEphemeralPublicKeyB64: responderEph.publicKey.rawRepresentation.base64EncodedString(),
                peerStaticPublicKeyB64: responderStatic.publicKey.rawRepresentation.base64EncodedString(),
                sessionID: sid,
                params: p)
        case .responder:
            return try RemoteSessionCrypto.deriveKeys(
                myRole: .responder,
                myEphemeralPrivate: responderEph,
                myStaticPrivate: responderStatic,
                peerEphemeralPublicKeyB64: initiatorEph.publicKey.rawRepresentation.base64EncodedString(),
                peerStaticPublicKeyB64: initiatorStatic.publicKey.rawRepresentation.base64EncodedString(),
                sessionID: sid,
                params: p)
        }
    }

    // MARK: - The core property

    func testBothPeersDeriveIdenticalKeys() throws {
        let a = try derive(role: .initiator)
        let b = try derive(role: .responder)

        XCTAssertEqual(a.transcript, b.transcript, "transcripts must match — this is the key confirmation value")
        XCTAssertEqual(a, b, "both peers must arrive at the same key material")
        XCTAssertEqual(a.initiatorToResponderSalt.count, 4)
        XCTAssertEqual(a.responderToInitiatorSalt.count, 4)
    }

    func testDirectionalKeysDifferFromEachOther() throws {
        let keys = try derive(role: .initiator)
        XCTAssertNotEqual(keys.initiatorToResponder, keys.responderToInitiator,
                          "one key in both directions would let a reflected frame authenticate")
        XCTAssertNotEqual(keys.initiatorToResponderSalt, keys.responderToInitiatorSalt)
    }

    func testSealingKeySelectionIsRoleSymmetric() throws {
        let initiator = try derive(role: .initiator)
        let responder = try derive(role: .responder)

        // What the initiator seals with, the responder must open with.
        XCTAssertEqual(initiator.sealingKey(as: .initiator), responder.openingKey(as: .responder))
        XCTAssertEqual(initiator.openingKey(as: .initiator), responder.sealingKey(as: .responder))
        XCTAssertEqual(initiator.sealingSalt(as: .initiator), responder.openingSalt(as: .responder))
    }

    // MARK: - Role assignment

    func testBothPeersClaimingInitiatorDeriveDifferentKeys() throws {
        // The bug this guards: if role is decided locally ("I started it")
        // rather than by the protocol, both peers can believe they are the
        // initiator. They then agree on nothing, and the failure appears as
        // GCM errors during video rather than at handshake time.
        let honest = try derive(role: .initiator)
        let confused = try RemoteSessionCrypto.deriveKeys(
            myRole: .initiator,                       // wrong: responder claiming initiator
            myEphemeralPrivate: responderEph,
            myStaticPrivate: responderStatic,
            peerEphemeralPublicKeyB64: initiatorEph.publicKey.rawRepresentation.base64EncodedString(),
            peerStaticPublicKeyB64: initiatorStatic.publicKey.rawRepresentation.base64EncodedString(),
            sessionID: sessionID,
            params: params())

        XCTAssertNotEqual(honest.transcript, confused.transcript,
                          "a role mismatch must be caught by key confirmation, not discovered mid-stream")
    }

    func testRoleOpposite() {
        XCTAssertEqual(RemoteSessionRole.initiator.opposite, .responder)
        XCTAssertEqual(RemoteSessionRole.responder.opposite, .initiator)
    }

    // MARK: - Transcript binding

    func testChangedParameterChangesTheKeys() throws {
        // This is the whole point of binding the transcript into HKDF's info:
        // a MITM that rewrites a negotiated parameter must break the handshake
        // rather than silently downgrade it.
        let a = try derive(role: .initiator)
        var tampered = params()
        tampered["video"] = .string("h265")
        let b = try derive(role: .initiator, params: tampered)

        XCTAssertNotEqual(a.transcript, b.transcript)
        XCTAssertNotEqual(a.initiatorToResponder, b.initiatorToResponder)
    }

    func testAddedParameterChangesTheKeys() throws {
        let a = try derive(role: .initiator)
        var extra = params()
        extra["input"] = .int(1)   // a future capability field
        let b = try derive(role: .initiator, params: extra)
        XCTAssertNotEqual(a.transcript, b.transcript)
    }

    func testChangedSessionIDChangesTheKeys() throws {
        let a = try derive(role: .initiator)
        let b = try derive(role: .initiator, sessionID: "00112233445566778899aabbccddeeff")
        XCTAssertNotEqual(a.transcript, b.transcript)
        XCTAssertNotEqual(a.initiatorToResponder, b.initiatorToResponder,
                          "a reconnect must never reuse the previous session's keys")
    }

    func testTranscriptsMatchIsConstantTimeAndCorrect() {
        let a = Data([1, 2, 3, 4])
        XCTAssertTrue(RemoteSessionCrypto.transcriptsMatch(a, Data([1, 2, 3, 4])))
        XCTAssertFalse(RemoteSessionCrypto.transcriptsMatch(a, Data([1, 2, 3, 5])))
        XCTAssertFalse(RemoteSessionCrypto.transcriptsMatch(a, Data([1, 2, 3])))
        XCTAssertFalse(RemoteSessionCrypto.transcriptsMatch(a, Data()))
    }

    // MARK: - Canonical parameter bytes

    func testCanonicalBytesAreIndependentOfInsertionOrder() {
        // Dictionary iteration order is not stable, so without explicit sorting
        // two runs on the SAME machine can disagree, never mind two platforms.
        var a = RemoteHandshakeParams()
        a["video"] = .string("h264")
        a["protocol"] = .int(1)
        a["display"] = .int(0)

        var b = RemoteHandshakeParams()
        b["display"] = .int(0)
        b["protocol"] = .int(1)
        b["video"] = .string("h264")

        XCTAssertEqual(a.canonicalBytes(), b.canonicalBytes())
    }

    func testCanonicalBytesShape() {
        let p = RemoteHandshakeParams(["b": .int(2), "a": .string("x")])
        XCTAssertEqual(String(data: p.canonicalBytes(), encoding: .utf8), #"{"a":"x","b":2}"#)
    }

    func testCanonicalBytesEmptyParams() {
        XCTAssertEqual(String(data: RemoteHandshakeParams().canonicalBytes(), encoding: .utf8), "{}")
    }

    func testCanonicalBytesEscapingMatchesRFC8259Minimum() {
        // Foundation's JSONEncoder escapes "/" by default and .NET does not.
        // Any divergence here is a cross-platform key confirmation failure, so
        // the escaping is hand-rolled and pinned by this test.
        let p = RemoteHandshakeParams(["k": .string("a/b\"c\\d\ne")])
        XCTAssertEqual(String(data: p.canonicalBytes(), encoding: .utf8),
                       #"{"k":"a/b\"c\\d\ne"}"#)
    }

    func testCanonicalBytesPreservesNonASCIIVerbatim() {
        // Escaping non-ASCII as \uXXXX would also be valid JSON, which is
        // exactly why it has to be pinned: "also valid" is not "identical".
        let p = RemoteHandshakeParams(["k": .string("café")])
        XCTAssertEqual(String(data: p.canonicalBytes(), encoding: .utf8), #"{"k":"café"}"#)
    }

    func testCanonicalBytesEscapesControlCharacters() {
        let p = RemoteHandshakeParams(["k": .string("a\u{01}b")])
        // Built rather than written literally: a raw control byte in a source
        // file is both unreadable and rejected by the Swift compiler.
        let expected = "{\"k\":\"a" + "\\u0001" + "b\"}"
        XCTAssertEqual(String(data: p.canonicalBytes(), encoding: .utf8), expected)
    }

    func testCanonicalBytesNegativeInteger() {
        let p = RemoteHandshakeParams(["k": .int(-7)])
        XCTAssertEqual(String(data: p.canonicalBytes(), encoding: .utf8), #"{"k":-7}"#)
    }

    // MARK: - Nonce construction

    func testNonceIsSaltPlusBigEndianSequence() {
        let salt = Data([0xAA, 0xBB, 0xCC, 0xDD])
        let n = RemoteSessionCrypto.nonce(salt: salt, sequence: 1)
        XCTAssertEqual(n.count, 12)
        XCTAssertEqual(Array(n), [0xAA, 0xBB, 0xCC, 0xDD, 0, 0, 0, 0, 0, 0, 0, 1])
    }

    func testNonceIsUniquePerSequence() {
        let salt = Data([1, 2, 3, 4])
        var seen = Set<Data>()
        for seq in UInt64(0)..<1000 {
            XCTAssertTrue(seen.insert(RemoteSessionCrypto.nonce(salt: salt, sequence: seq)).inserted,
                          "counter nonces must never collide within a session")
        }
    }

    // MARK: - Frame seal / open

    private func header(channel: UInt8, flags: UInt8, sequence: UInt64, captureUs: UInt64) -> Data {
        var d = Data()
        withUnsafeBytes(of: UInt32(100).bigEndian) { d.append(contentsOf: $0) }
        d.append(channel)
        d.append(flags)
        withUnsafeBytes(of: sequence.bigEndian) { d.append(contentsOf: $0) }
        withUnsafeBytes(of: captureUs.bigEndian) { d.append(contentsOf: $0) }
        return d
    }

    func testSealOpenRoundTrip() throws {
        let keys = try derive(role: .initiator)
        let h = header(channel: 1, flags: 1, sequence: 42, captureUs: 123_456)
        let payload = Data("a frame of h264".utf8)

        let sealed = try RemoteSessionCrypto.seal(
            payload: payload, header: h,
            key: keys.sealingKey(as: .initiator),
            salt: keys.sealingSalt(as: .initiator), sequence: 42)

        let opened = try RemoteSessionCrypto.open(
            body: sealed, header: h,
            key: keys.openingKey(as: .responder),
            salt: keys.openingSalt(as: .responder), sequence: 42)

        XCTAssertEqual(opened, payload)
        XCTAssertEqual(sealed.count, payload.count + RemoteSessionCrypto.tagLength)
    }

    func testHeaderLengthMatchesProtocol() {
        XCTAssertEqual(header(channel: 0, flags: 0, sequence: 0, captureUs: 0).count,
                       RemoteSessionCrypto.headerLength)
    }

    func testTamperedHeaderFailsAuthentication() throws {
        // The AAD covers the framing, including the length prefix, so a
        // truncation or a channel swap fails the tag check rather than being
        // delivered to the wrong sub-channel.
        let keys = try derive(role: .initiator)
        let h = header(channel: 1, flags: 1, sequence: 7, captureUs: 99)
        let sealed = try RemoteSessionCrypto.seal(
            payload: Data("x".utf8), header: h,
            key: keys.sealingKey(as: .initiator),
            salt: keys.sealingSalt(as: .initiator), sequence: 7)

        for byteIndex in [0, 4, 5, 12, 21] {
            var tampered = h
            tampered[byteIndex] ^= 0x01
            XCTAssertThrowsError(try RemoteSessionCrypto.open(
                body: sealed, header: tampered,
                key: keys.openingKey(as: .responder),
                salt: keys.openingSalt(as: .responder), sequence: 7),
                "tampering with header byte \(byteIndex) must fail authentication")
        }
    }

    func testWrongSequenceFailsAuthentication() throws {
        // A replayed frame carries its original sequence in the header, so
        // opening it under any other counter fails. Combined with the
        // strictly-increasing check in the transport, that closes replay.
        let keys = try derive(role: .initiator)
        let h = header(channel: 1, flags: 0, sequence: 5, captureUs: 1)
        let sealed = try RemoteSessionCrypto.seal(
            payload: Data("x".utf8), header: h,
            key: keys.sealingKey(as: .initiator),
            salt: keys.sealingSalt(as: .initiator), sequence: 5)

        XCTAssertThrowsError(try RemoteSessionCrypto.open(
            body: sealed, header: h,
            key: keys.openingKey(as: .responder),
            salt: keys.openingSalt(as: .responder), sequence: 6))
    }

    func testWrongDirectionKeyFails() throws {
        let keys = try derive(role: .initiator)
        let h = header(channel: 1, flags: 0, sequence: 1, captureUs: 1)
        let sealed = try RemoteSessionCrypto.seal(
            payload: Data("x".utf8), header: h,
            key: keys.sealingKey(as: .initiator),
            salt: keys.sealingSalt(as: .initiator), sequence: 1)

        // Open with the key the initiator uses for INBOUND frames. A frame the
        // initiator sealed must not authenticate under it — otherwise a peer
        // could reflect our own frames back at us and have them accepted.
        XCTAssertThrowsError(try RemoteSessionCrypto.open(
            body: sealed, header: h,
            key: keys.openingKey(as: .initiator),
            salt: keys.openingSalt(as: .initiator), sequence: 1))
    }

    func testTruncatedBodyIsRejected() throws {
        let keys = try derive(role: .initiator)
        let h = header(channel: 1, flags: 0, sequence: 1, captureUs: 1)
        XCTAssertThrowsError(try RemoteSessionCrypto.open(
            body: Data([1, 2, 3]), header: h,
            key: keys.openingKey(as: .responder),
            salt: keys.openingSalt(as: .responder), sequence: 1),
            "a body shorter than the GCM tag must be rejected before any crypto")
    }

    func testEmptyPayloadRoundTrips() throws {
        // Keepalives and acks are legitimately empty.
        let keys = try derive(role: .initiator)
        let h = header(channel: 0, flags: 0, sequence: 1, captureUs: 1)
        let sealed = try RemoteSessionCrypto.seal(
            payload: Data(), header: h,
            key: keys.sealingKey(as: .initiator),
            salt: keys.sealingSalt(as: .initiator), sequence: 1)
        let opened = try RemoteSessionCrypto.open(
            body: sealed, header: h,
            key: keys.openingKey(as: .responder),
            salt: keys.openingSalt(as: .responder), sequence: 1)
        XCTAssertEqual(opened, Data())
    }

    // MARK: - Input validation

    func testRejectsMalformedPublicKey() {
        for bad in ["", "not-base64!!", Data(repeating: 0, count: 31).base64EncodedString()] {
            XCTAssertThrowsError(try RemoteSessionCrypto.deriveKeys(
                myRole: .initiator,
                myEphemeralPrivate: initiatorEph,
                myStaticPrivate: initiatorStatic,
                peerEphemeralPublicKeyB64: bad,
                peerStaticPublicKeyB64: responderStatic.publicKey.rawRepresentation.base64EncodedString(),
                sessionID: sessionID,
                params: params()), "must reject public key: '\(bad)'")
        }
    }

    func testRejectsMalformedSessionID() {
        for bad in ["", "short", "9F2C4A6E8B0D1F3A5C7E9B1D3F5A7C9E", "zz2c4a6e8b0d1f3a5c7e9b1d3f5a7c9e"] {
            XCTAssertThrowsError(try RemoteSessionCrypto.deriveKeys(
                myRole: .initiator,
                myEphemeralPrivate: initiatorEph,
                myStaticPrivate: initiatorStatic,
                peerEphemeralPublicKeyB64: responderEph.publicKey.rawRepresentation.base64EncodedString(),
                peerStaticPublicKeyB64: responderStatic.publicKey.rawRepresentation.base64EncodedString(),
                sessionID: bad,
                params: params()), "must reject session id: '\(bad)'")
        }
    }

    // MARK: - Cross-platform vector

    // The only test here that can catch the two platforms drifting apart.
    // Everything else verifies internal consistency, which both implementations
    // can satisfy independently while still disagreeing on the wire — the
    // canonical parameter bytes and the HKDF input order are exactly the kind
    // of detail two people implement plausibly and differently.
    //
    // Generated by this implementation; `RemoteSessionCryptoTests.cs` asserts
    // the same file. If you change the handshake, regenerate it and update BOTH
    // copies (CLAUDE.md -> check both known_good vectors).
    func testMatchesCrossPlatformVector() throws {
        let v = try loadVector()

        func priv(_ key: String) throws -> Curve25519.KeyAgreement.PrivateKey {
            try Curve25519.KeyAgreement.PrivateKey(
                rawRepresentation: try XCTUnwrap(Data(base64Encoded: try XCTUnwrap(v[key] as? String))))
        }

        let params = RemoteHandshakeParams([
            "protocol": .int(1), "video": .string("h264"), "display": .int(0),
        ])
        XCTAssertEqual(String(data: params.canonicalBytes(), encoding: .utf8),
                       v["params_canonical"] as? String,
                       "canonical parameter bytes drifted — both platforms must produce these exact bytes")

        // Derive from each side independently; both must match the vector.
        for role in [RemoteSessionRole.initiator, .responder] {
            let keys = try RemoteSessionCrypto.deriveKeys(
                myRole: role,
                myEphemeralPrivate: try priv(role == .initiator ? "initiator_eph_priv_b64" : "responder_eph_priv_b64"),
                myStaticPrivate:    try priv(role == .initiator ? "initiator_static_priv_b64" : "responder_static_priv_b64"),
                peerEphemeralPublicKeyB64: try XCTUnwrap(v[role == .initiator ? "responder_eph_pub_b64" : "initiator_eph_pub_b64"] as? String),
                peerStaticPublicKeyB64:    try XCTUnwrap(v[role == .initiator ? "responder_static_pub_b64" : "initiator_static_pub_b64"] as? String),
                sessionID: try XCTUnwrap(v["session_id"] as? String),
                params: params)

            XCTAssertEqual(keys.transcript.base64EncodedString(), v["transcript_b64"] as? String,
                           "transcript mismatch deriving as \(role.rawValue)")
            XCTAssertEqual(keys.initiatorToResponder.withUnsafeBytes { Data($0) }.base64EncodedString(),
                           v["key_i2r_b64"] as? String, "key_i2r mismatch as \(role.rawValue)")
            XCTAssertEqual(keys.responderToInitiator.withUnsafeBytes { Data($0) }.base64EncodedString(),
                           v["key_r2i_b64"] as? String, "key_r2i mismatch as \(role.rawValue)")
            XCTAssertEqual(keys.initiatorToResponderSalt.base64EncodedString(), v["salt_i2r_b64"] as? String)
            XCTAssertEqual(keys.responderToInitiatorSalt.base64EncodedString(), v["salt_r2i_b64"] as? String)
        }

        // And the sealed frame must be byte-identical: AES-GCM with a counter
        // nonce is deterministic, so this pins the nonce layout and the AAD.
        let keys = try RemoteSessionCrypto.deriveKeys(
            myRole: .initiator,
            myEphemeralPrivate: try priv("initiator_eph_priv_b64"),
            myStaticPrivate: try priv("initiator_static_priv_b64"),
            peerEphemeralPublicKeyB64: try XCTUnwrap(v["responder_eph_pub_b64"] as? String),
            peerStaticPublicKeyB64: try XCTUnwrap(v["responder_static_pub_b64"] as? String),
            sessionID: try XCTUnwrap(v["session_id"] as? String),
            params: params)

        let header = try XCTUnwrap(Data(base64Encoded: try XCTUnwrap(v["frame_header_b64"] as? String)))
        let sequence = UInt64(try XCTUnwrap(v["frame_sequence"] as? Int))
        let payload = Data(try XCTUnwrap(v["frame_payload_utf8"] as? String).utf8)

        let sealed = try RemoteSessionCrypto.seal(
            payload: payload, header: header,
            key: keys.sealingKey(as: .initiator),
            salt: keys.sealingSalt(as: .initiator), sequence: sequence)
        XCTAssertEqual(sealed.base64EncodedString(), v["frame_sealed_b64"] as? String,
                       "sealed frame differs — nonce layout or AAD drifted")

        // Round-trip the vector's own ciphertext, not just our reproduction of
        // it, so a decoder-side regression is caught too.
        let opened = try RemoteSessionCrypto.open(
            body: try XCTUnwrap(Data(base64Encoded: try XCTUnwrap(v["frame_sealed_b64"] as? String))),
            header: header,
            key: keys.openingKey(as: .responder),
            salt: keys.openingSalt(as: .responder), sequence: sequence)
        XCTAssertEqual(opened, payload)
    }

    /// Bundle resource under Xcode, `#file`-relative under `swift test` — same
    /// dual-path convention the other vector-loading tests use.
    private func loadVector() throws -> [String: Any] {
        let url: URL
        if let bundled = Bundle(for: RemoteSessionCryptoTests.self)
            .url(forResource: "remote_handshake_vector", withExtension: "json") {
            url = bundled
        } else {
            url = URL(fileURLWithPath: #file).deletingLastPathComponent()
                .appendingPathComponent("remote_handshake_vector.json")
        }
        let data = try Data(contentsOf: url)
        return try XCTUnwrap(try JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    // MARK: - Session ids and fingerprints

    func testNewSessionIDShape() {
        for _ in 0..<50 {
            let sid = RemoteSessionCrypto.newSessionID()
            XCTAssertTrue(RemoteSessionCrypto.isSessionID(sid), "bad session id: \(sid)")
            XCTAssertEqual(sid.count, 32)
        }
    }

    func testIsSessionIDRejectsUppercaseAndWrongLength() {
        XCTAssertFalse(RemoteSessionCrypto.isSessionID("9F2C4A6E8B0D1F3A5C7E9B1D3F5A7C9E"))
        XCTAssertFalse(RemoteSessionCrypto.isSessionID("9f2c4a6e8b0d1f3a5c7e9b1d3f5a7c9"))
        XCTAssertFalse(RemoteSessionCrypto.isSessionID("9f2c4a6e-8b0d-1f3a-5c7e-9b1d3f5a7c9e"))
        XCTAssertTrue(RemoteSessionCrypto.isSessionID("9f2c4a6e8b0d1f3a5c7e9b1d3f5a7c9e"))
    }

    func testFingerprintShapeAndStability() throws {
        let b64 = responderStatic.publicKey.rawRepresentation.base64EncodedString()
        let fp = try XCTUnwrap(RemoteSessionCrypto.fingerprint(publicKeyB64: b64))
        XCTAssertEqual(fp.count, 19, "four groups of four hex plus three spaces")
        XCTAssertEqual(fp.split(separator: " ").count, 4)
        XCTAssertEqual(fp, RemoteSessionCrypto.fingerprint(publicKeyB64: b64))
    }

    func testFingerprintDiffersPerKeyAndRejectsGarbage() throws {
        let a = RemoteSessionCrypto.fingerprint(publicKeyB64: initiatorStatic.publicKey.rawRepresentation.base64EncodedString())
        let b = RemoteSessionCrypto.fingerprint(publicKeyB64: responderStatic.publicKey.rawRepresentation.base64EncodedString())
        XCTAssertNotEqual(a, b)
        XCTAssertNil(RemoteSessionCrypto.fingerprint(publicKeyB64: "nope"))
        XCTAssertNil(RemoteSessionCrypto.fingerprint(publicKeyB64: Data(repeating: 0, count: 31).base64EncodedString()))
    }
}
