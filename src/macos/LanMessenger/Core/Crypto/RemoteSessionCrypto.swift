import Foundation
import CryptoKit

// Key exchange and frame encryption for the remote-desktop media channel.
//
// This is deliberately NOT SessionCrypto. Messages use a static X25519
// agreement with a random nonce per packet; a screen stream needs two things
// that construction cannot give:
//
//   1. Forward secrecy. A long-term key compromised next year must not decrypt
//      a screen recording captured today. Messages accept that risk because
//      history is stored locally anyway; a live screen is a different thing.
//   2. A counter nonce. At 30 fps across five sub-channels, random 96-bit
//      nonces are both slower and strictly weaker than a deterministic counter.
//
// The handshake is Noise `KK` in shape: mix a static-ephemeral agreement in
// each direction (which authenticates both peers using the identity keys the
// contacts list already pins) with an ephemeral-ephemeral agreement (which
// provides the forward secrecy). There is no signing key anywhere in this
// protocol — the only identity is the long-term X25519 key — so this mixing IS
// the authentication. See PROTOCOL.md → Remote Desktop → Media Session Key
// Derivation, which is authoritative.
//
// Three rules here are load-bearing and each one is a miserable bug if broken:
//
//   * Role is assigned by the protocol, not chosen. The peer that sent
//     `remote_invite` is the initiator. `es` and `se` are defined relative to
//     role, so two peers that both believe they are the initiator derive
//     swapped keys and fail to decrypt each other with no useful error.
//   * The transcript binds the negotiated parameters into the key. Without it
//     any field added to `params` later would be unauthenticated and a MITM
//     could downgrade it without breaking the handshake.
//   * Keys are per-session and never survive a reconnect. Reusing a key with a
//     sequence counter reset to zero is catastrophic AES-GCM nonce reuse.

enum RemoteSessionCryptoError: Error, CustomStringConvertible {
    case invalidPublicKey
    case invalidSessionID
    case invalidSequence
    case sealFailed
    case openFailed
    case transcriptMismatch

    var description: String {
        switch self {
        case .invalidPublicKey:    return "Public key is not 32 raw bytes"
        case .invalidSessionID:    return "Session id is not 32 lowercase hex characters"
        case .invalidSequence:     return "Sequence number is out of range"
        case .sealFailed:          return "Media frame encryption failed"
        case .openFailed:          return "Media frame authentication failed"
        case .transcriptMismatch:  return "Handshake transcript did not match"
        }
    }
}

/// Which half of the handshake this client is performing.
///
/// Assigned by the protocol, never chosen: the peer that sent `remote_invite`
/// is `.initiator`. It is also the peer that will be *viewing*; the responder
/// is the host whose screen is shared.
enum RemoteSessionRole: String, Codable, Equatable {
    case initiator
    case responder

    var opposite: RemoteSessionRole { self == .initiator ? .responder : .initiator }
}

// MARK: - Negotiated parameters

/// One value in the handshake parameter object.
///
/// Restricted to strings and integers on purpose. Canonical serialization of
/// floating point is where every "both sides must produce identical bytes"
/// scheme goes to die — 0.1 has no single canonical spelling. If a parameter
/// ever needs a fraction, send it as a scaled integer.
enum RemoteParamValue: Equatable {
    case string(String)
    case int(Int)
}

/// The parameters both peers agree on, bound into the handshake transcript.
///
/// Canonical form is hand-rolled rather than delegated to JSONEncoder /
/// System.Text.Json: neither platform guarantees key ordering or escaping
/// identical to the other's, and a one-byte difference here surfaces as a key
/// confirmation failure that looks like a crypto bug.
struct RemoteHandshakeParams: Equatable {
    private(set) var values: [String: RemoteParamValue]

    init(_ values: [String: RemoteParamValue] = [:]) {
        self.values = values
    }

    subscript(key: String) -> RemoteParamValue? {
        get { values[key] }
        set { values[key] = newValue }
    }

    /// Deterministic bytes for the transcript: keys sorted by UTF-8 byte order,
    /// no insignificant whitespace, minimal string escaping.
    func canonicalBytes() -> Data {
        var out = Data([UInt8(ascii: "{")])
        let sorted = values.keys.sorted { Array($0.utf8).lexicographicallyPrecedes(Array($1.utf8)) }
        for (index, key) in sorted.enumerated() {
            if index > 0 { out.append(UInt8(ascii: ",")) }
            out.append(Self.canonicalString(key))
            out.append(UInt8(ascii: ":"))
            switch values[key]! {
            case .string(let s): out.append(Self.canonicalString(s))
            case .int(let i):    out.append(contentsOf: Array(String(i).utf8))
            }
        }
        out.append(UInt8(ascii: "}"))
        return out
    }

    /// JSON string escaping limited to exactly what RFC 8259 requires, so both
    /// platforms produce the same bytes. Notably we do NOT escape `/` or
    /// non-ASCII — Foundation escapes the former by default and would silently
    /// diverge from .NET.
    private static func canonicalString(_ s: String) -> Data {
        var out = Data([UInt8(ascii: "\"")])
        for scalar in s.unicodeScalars {
            switch scalar {
            case "\"":  out.append(contentsOf: Array(#"\""#.utf8))
            case "\\":  out.append(contentsOf: Array(#"\\"#.utf8))
            case "\n":  out.append(contentsOf: Array(#"\n"#.utf8))
            case "\r":  out.append(contentsOf: Array(#"\r"#.utf8))
            case "\t":  out.append(contentsOf: Array(#"\t"#.utf8))
            case "\u{08}": out.append(contentsOf: Array(#"\b"#.utf8))
            case "\u{0C}": out.append(contentsOf: Array(#"\f"#.utf8))
            default:
                if scalar.value < 0x20 {
                    out.append(contentsOf: Array(String(format: "\\u%04x", scalar.value).utf8))
                } else {
                    out.append(contentsOf: Array(String(scalar).utf8))
                }
            }
        }
        out.append(UInt8(ascii: "\""))
        return out
    }
}

// MARK: - Derived session keys

/// The output of a successful handshake.
///
/// Holds both directional keys because either peer may need to seal in one
/// direction and open in the other; `sealingKey(as:)` and `openingKey(as:)`
/// pick correctly from the role so no call site ever reasons about
/// "mine vs theirs", which is the mistake that produces swapped keys.
struct RemoteSessionKeys: Equatable {
    let initiatorToResponder: SymmetricKey
    let responderToInitiator: SymmetricKey
    let initiatorToResponderSalt: Data   // 4 bytes
    let responderToInitiatorSalt: Data   // 4 bytes
    /// SHA-256 over the handshake transcript. Exchanged as key confirmation.
    let transcript: Data

    func sealingKey(as role: RemoteSessionRole) -> SymmetricKey {
        role == .initiator ? initiatorToResponder : responderToInitiator
    }

    func openingKey(as role: RemoteSessionRole) -> SymmetricKey {
        role == .initiator ? responderToInitiator : initiatorToResponder
    }

    func sealingSalt(as role: RemoteSessionRole) -> Data {
        role == .initiator ? initiatorToResponderSalt : responderToInitiatorSalt
    }

    func openingSalt(as role: RemoteSessionRole) -> Data {
        role == .initiator ? responderToInitiatorSalt : initiatorToResponderSalt
    }

    static func == (lhs: RemoteSessionKeys, rhs: RemoteSessionKeys) -> Bool {
        lhs.transcript == rhs.transcript
            && lhs.initiatorToResponderSalt == rhs.initiatorToResponderSalt
            && lhs.responderToInitiatorSalt == rhs.responderToInitiatorSalt
            && lhs.initiatorToResponder == rhs.initiatorToResponder
            && lhs.responderToInitiator == rhs.responderToInitiator
    }
}

// MARK: - Handshake and frame crypto

enum RemoteSessionCrypto {

    static let protocolLabel = "lan-messenger-remote-v1"
    static let keyLength     = 32
    static let saltLength    = 4
    static let nonceLength   = 12
    static let tagLength     = 16
    /// 4-byte length + 1 channel + 1 flags + 8 sequence + 8 capture_us.
    static let headerLength  = 22

    // MARK: Handshake

    /// Derives the media session keys.
    ///
    /// `myRole` says which half we are performing; every other argument is
    /// labelled by role rather than by ownership so the two peers can pass the
    /// same values in the same slots and arrive at the same keys.
    static func deriveKeys(
        myRole: RemoteSessionRole,
        myEphemeralPrivate: Curve25519.KeyAgreement.PrivateKey,
        myStaticPrivate: Curve25519.KeyAgreement.PrivateKey,
        peerEphemeralPublicKeyB64: String,
        peerStaticPublicKeyB64: String,
        sessionID: String,
        params: RemoteHandshakeParams
    ) throws -> RemoteSessionKeys {

        let sessionIDBytes = try sessionIDBytes(sessionID)
        let peerEphemeral  = try publicKey(peerEphemeralPublicKeyB64)
        let peerStatic     = try publicKey(peerStaticPublicKeyB64)

        // es authenticates the responder, se authenticates the initiator, ee
        // supplies forward secrecy. Which agreement we can actually compute
        // depends on our role — we hold only our own private keys — but the
        // concatenation order is fixed by the protocol, not by role.
        let es: Data
        let se: Data
        switch myRole {
        case .initiator:
            // es = X25519(eph_initiator_priv, static_responder_pub)
            es = try agree(myEphemeralPrivate, peerStatic)
            // se = X25519(static_initiator_priv, eph_responder_pub)
            se = try agree(myStaticPrivate, peerEphemeral)
        case .responder:
            // Same two secrets, reached from the other side of each agreement.
            es = try agree(myStaticPrivate, peerEphemeral)
            se = try agree(myEphemeralPrivate, peerStatic)
        }
        let ee = try agree(myEphemeralPrivate, peerEphemeral)

        let myEphemeralPublic = myEphemeralPrivate.publicKey.rawRepresentation
        let myStaticPublic    = myStaticPrivate.publicKey.rawRepresentation
        let peerEphemeralRaw  = peerEphemeral.rawRepresentation
        let peerStaticRaw     = peerStatic.rawRepresentation

        let transcript = transcriptHash(
            sessionIDBytes: sessionIDBytes,
            staticInitiator:  myRole == .initiator ? myStaticPublic    : peerStaticRaw,
            staticResponder:  myRole == .initiator ? peerStaticRaw     : myStaticPublic,
            ephemeralInitiator: myRole == .initiator ? myEphemeralPublic : peerEphemeralRaw,
            ephemeralResponder: myRole == .initiator ? peerEphemeralRaw  : myEphemeralPublic,
            params: params
        )

        var ikm = Data()
        ikm.append(es)
        ikm.append(se)
        ikm.append(ee)

        let okm = HKDF<SHA256>.deriveKey(
            inputKeyMaterial: SymmetricKey(data: ikm),
            salt: sessionIDBytes,
            info: transcript,
            outputByteCount: keyLength * 2 + saltLength * 2
        ).withUnsafeBytes { Data($0) }

        return RemoteSessionKeys(
            initiatorToResponder: SymmetricKey(data: okm[0..<32]),
            responderToInitiator: SymmetricKey(data: okm[32..<64]),
            initiatorToResponderSalt: Data(okm[64..<68]),
            responderToInitiatorSalt: Data(okm[68..<72]),
            transcript: transcript
        )
    }

    /// SHA-256 over the protocol label, session id, both static publics, both
    /// ephemeral publics, and the canonical parameters — in that fixed order.
    ///
    /// Fed to HKDF as `info`, which is what makes the negotiated parameters
    /// tamper-evident: change any of them and both sides derive different keys,
    /// so the tamper surfaces as a key confirmation failure rather than as a
    /// silent downgrade.
    static func transcriptHash(
        sessionIDBytes: Data,
        staticInitiator: Data,
        staticResponder: Data,
        ephemeralInitiator: Data,
        ephemeralResponder: Data,
        params: RemoteHandshakeParams
    ) -> Data {
        var hasher = SHA256()
        hasher.update(data: Data(protocolLabel.utf8))
        hasher.update(data: sessionIDBytes)
        hasher.update(data: staticInitiator)
        hasher.update(data: staticResponder)
        hasher.update(data: ephemeralInitiator)
        hasher.update(data: ephemeralResponder)
        hasher.update(data: params.canonicalBytes())
        return Data(hasher.finalize())
    }

    /// Constant-time comparison for key confirmation.
    ///
    /// Timing here is not a realistic attack surface, but a confirmation check
    /// is exactly the kind of code that gets copied somewhere it does matter.
    static func transcriptsMatch(_ a: Data, _ b: Data) -> Bool {
        guard a.count == b.count else { return false }
        var difference: UInt8 = 0
        for (x, y) in zip(a, b) { difference |= x ^ y }
        return difference == 0
    }

    // MARK: Frame crypto

    /// `direction_salt(4) || sequence(8)`, big-endian.
    ///
    /// Deterministic rather than random, which is safe only because sequence is
    /// strictly increasing within a session and a session's keys never outlive
    /// its socket. Both halves of that sentence are enforced elsewhere; if
    /// either stops being true this construction becomes a vulnerability.
    static func nonce(salt: Data, sequence: UInt64) -> Data {
        precondition(salt.count == saltLength, "direction salt must be 4 bytes")
        var out = Data(salt)
        withUnsafeBytes(of: sequence.bigEndian) { out.append(contentsOf: $0) }
        return out
    }

    /// Seals one media payload. `header` is the 22 plaintext header bytes,
    /// authenticated as AAD so the framing itself cannot be tampered with —
    /// including the length prefix, so a truncation attack fails the tag check.
    static func seal(
        payload: Data,
        header: Data,
        key: SymmetricKey,
        salt: Data,
        sequence: UInt64
    ) throws -> Data {
        let nonceData = nonce(salt: salt, sequence: sequence)
        do {
            let sealed = try AES.GCM.seal(
                payload,
                using: key,
                nonce: try AES.GCM.Nonce(data: nonceData),
                authenticating: header
            )
            return sealed.ciphertext + sealed.tag
        } catch {
            throw RemoteSessionCryptoError.sealFailed
        }
    }

    /// Opens one media payload. `body` is `ciphertext || 16-byte tag`, matching
    /// the layout the rest of the protocol already uses.
    static func open(
        body: Data,
        header: Data,
        key: SymmetricKey,
        salt: Data,
        sequence: UInt64
    ) throws -> Data {
        guard body.count >= tagLength else { throw RemoteSessionCryptoError.openFailed }
        let nonceData = nonce(salt: salt, sequence: sequence)
        do {
            let box = try AES.GCM.SealedBox(
                nonce: try AES.GCM.Nonce(data: nonceData),
                ciphertext: body.dropLast(tagLength),
                tag: body.suffix(tagLength)
            )
            return try AES.GCM.open(box, using: key, authenticating: header)
        } catch {
            throw RemoteSessionCryptoError.openFailed
        }
    }

    // MARK: Identity fingerprint

    /// Short fingerprint of an identity key, for the consent prompt.
    ///
    /// A display name is trivially spoofable by any peer on the LAN; the pinned
    /// public key is not. Shown as four space-separated groups of four hex
    /// characters — long enough to be meaningful, short enough to be read
    /// aloud, which is the only way it ever gets verified in practice.
    static func fingerprint(publicKeyB64: String) -> String? {
        guard let raw = Data(base64Encoded: publicKeyB64), raw.count == 32 else { return nil }
        let digest = SHA256.hash(data: raw)
        let hex = digest.prefix(8).map { String(format: "%02x", $0) }.joined()
        return stride(from: 0, to: hex.count, by: 4).map {
            let start = hex.index(hex.startIndex, offsetBy: $0)
            let end = hex.index(start, offsetBy: 4)
            return String(hex[start..<end])
        }.joined(separator: " ")
    }

    // MARK: Session ids

    /// A `session_id` shares `message_id`'s shape: 32 lowercase hex characters.
    static func isSessionID(_ value: String) -> Bool {
        value.count == 32 && value.allSatisfy { $0.isHexDigit && !$0.isUppercase }
    }

    static func newSessionID() -> String {
        UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
    }

    // MARK: - Private helpers

    private static func sessionIDBytes(_ sessionID: String) throws -> Data {
        guard isSessionID(sessionID) else { throw RemoteSessionCryptoError.invalidSessionID }
        var out = Data(capacity: 16)
        var index = sessionID.startIndex
        while index < sessionID.endIndex {
            let next = sessionID.index(index, offsetBy: 2)
            guard let byte = UInt8(sessionID[index..<next], radix: 16) else {
                throw RemoteSessionCryptoError.invalidSessionID
            }
            out.append(byte)
            index = next
        }
        return out
    }

    private static func publicKey(_ b64: String) throws -> Curve25519.KeyAgreement.PublicKey {
        guard let raw = Data(base64Encoded: b64), raw.count == 32 else {
            throw RemoteSessionCryptoError.invalidPublicKey
        }
        guard let key = try? Curve25519.KeyAgreement.PublicKey(rawRepresentation: raw) else {
            throw RemoteSessionCryptoError.invalidPublicKey
        }
        return key
    }

    private static func agree(
        _ priv: Curve25519.KeyAgreement.PrivateKey,
        _ pub: Curve25519.KeyAgreement.PublicKey
    ) throws -> Data {
        let secret = try priv.sharedSecretFromKeyAgreement(with: pub)
        return secret.withUnsafeBytes { Data($0) }
    }
}
