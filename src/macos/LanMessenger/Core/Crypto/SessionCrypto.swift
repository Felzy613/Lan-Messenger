import Foundation
import CryptoKit

enum SessionCryptoError: Error {
    case invalidPublicKey
    case invalidNonce       // not 12 bytes
    case decryptionFailed
    case encodingFailed
}

// Handles per-peer message encryption/decryption.
//
// Protocol:
//   shared_secret = X25519(my_private, peer_public)
//   symmetric_key = HKDF-SHA256(ikm: shared_secret, salt: [], info: "lan-messenger", len: 32)
//   nonce         = random(12)
//   ciphertext    = AES-256-GCM.seal(plaintext, key, nonce, aad)
//   transmitted   = base64(nonce) + base64(ciphertext ‖ tag)
//
// The 16-byte AES-GCM tag is appended to the ciphertext bytes before base64-encoding,
// matching the Python cryptography library's output format.
enum SessionCrypto {

    // Derive the shared symmetric key from our private key and the peer's public key.
    static func symmetricKey(
        myPrivate: Curve25519.KeyAgreement.PrivateKey,
        theirPublicKeyB64: String
    ) throws -> SymmetricKey {
        guard let rawData = Data(base64Encoded: theirPublicKeyB64),
              rawData.count == 32 else {
            throw SessionCryptoError.invalidPublicKey
        }
        let theirPublic = try Curve25519.KeyAgreement.PublicKey(rawRepresentation: rawData)
        let sharedSecret = try myPrivate.sharedSecretFromKeyAgreement(with: theirPublic)
        return sharedSecret.hkdfDerivedSymmetricKey(
            using: SHA256.self,
            salt: Data(),                          // empty salt — matches Python `salt=None`
            sharedInfo: Data("lan-messenger".utf8),
            outputByteCount: 32
        )
    }

    // Encrypt plaintext. Returns (nonce_b64, ciphertext_b64).
    // ciphertext_b64 encodes `ciphertext ‖ tag` (tag appended, 16 bytes).
    static func encrypt(
        key: SymmetricKey,
        plaintext: Data,
        aad: Data
    ) throws -> (nonceB64: String, ciphertextB64: String) {
        let nonce = AES.GCM.Nonce()
        let sealed = try AES.GCM.seal(plaintext, using: key, nonce: nonce, authenticating: aad)
        let combined = sealed.ciphertext + sealed.tag      // ciphertext ‖ 16-byte tag
        return (
            Data(nonce).base64EncodedString(),
            combined.base64EncodedString()
        )
    }

    // Decrypt. nonce must be exactly 12 bytes. ciphertext includes the 16-byte tag appended.
    static func decrypt(
        key: SymmetricKey,
        nonceB64: String,
        ciphertextB64: String,
        aad: Data
    ) throws -> Data {
        guard let nonceData = Data(base64Encoded: nonceB64), nonceData.count == 12 else {
            throw SessionCryptoError.invalidNonce
        }
        guard let combined = Data(base64Encoded: ciphertextB64), combined.count >= 16 else {
            throw SessionCryptoError.decryptionFailed
        }
        let nonce = try AES.GCM.Nonce(data: nonceData)
        let ciphertext = combined.dropLast(16)
        let tag = combined.suffix(16)
        let box = try AES.GCM.SealedBox(nonce: nonce, ciphertext: ciphertext, tag: tag)
        do {
            return try AES.GCM.open(box, using: key, authenticating: aad)
        } catch {
            throw SessionCryptoError.decryptionFailed
        }
    }

    // Convenience: encrypt for a specific peer given their public key b64.
    static func encryptForPeer(
        myPrivate: Curve25519.KeyAgreement.PrivateKey,
        peerPublicKeyB64: String,
        plaintext: Data,
        aad: Data
    ) throws -> (nonceB64: String, ciphertextB64: String) {
        let key = try symmetricKey(myPrivate: myPrivate, theirPublicKeyB64: peerPublicKeyB64)
        return try encrypt(key: key, plaintext: plaintext, aad: aad)
    }

    // Convenience: decrypt from a specific peer.
    static func decryptFromPeer(
        myPrivate: Curve25519.KeyAgreement.PrivateKey,
        peerPublicKeyB64: String,
        nonceB64: String,
        ciphertextB64: String,
        aad: Data
    ) throws -> Data {
        let key = try symmetricKey(myPrivate: myPrivate, theirPublicKeyB64: peerPublicKeyB64)
        return try decrypt(key: key, nonceB64: nonceB64, ciphertextB64: ciphertextB64, aad: aad)
    }
}
