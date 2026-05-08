import Foundation
import CryptoKit

// Handles encryption/decryption of the local history file.
//
// Key derivation (does NOT use peer exchange):
//   history_key = HKDF-SHA256(
//       ikm  = raw_private_key_bytes (32 bytes),
//       salt = [] (empty),
//       info = "lan-messenger-history",
//       len  = 32
//   )
//
// AAD for all history operations: b"history-v1" (the literal 10 UTF-8 bytes)
//
// File format (JSON):
//   { "nonce": "<base64 12-byte nonce>", "ciphertext": "<base64 ct+tag>" }
//
// Inner plaintext is compact JSON (no extra whitespace) of the history dict.
enum HistoryCrypto {

    static let aad = Data("history-v1".utf8)

    static func historyKey(privateKey: Curve25519.KeyAgreement.PrivateKey) -> SymmetricKey {
        let rawPrivate = privateKey.rawRepresentation
        // HKDF with empty salt and info = "lan-messenger-history"
        return HKDF<SHA256>.deriveKey(
            inputKeyMaterial: SymmetricKey(data: rawPrivate),
            salt: Data(),
            info: Data("lan-messenger-history".utf8),
            outputByteCount: 32
        )
    }

    // Encrypt plaintext JSON bytes. Returns the outer file JSON string.
    static func encryptHistory(
        plaintext: Data,
        privateKey: Curve25519.KeyAgreement.PrivateKey
    ) throws -> String {
        let key = historyKey(privateKey: privateKey)
        let nonce = AES.GCM.Nonce()
        let sealed = try AES.GCM.seal(plaintext, using: key, nonce: nonce, authenticating: aad)
        let combined = sealed.ciphertext + sealed.tag
        let outer: [String: String] = [
            "nonce":      Data(nonce).base64EncodedString(),
            "ciphertext": combined.base64EncodedString(),
        ]
        let outData = try JSONSerialization.data(withJSONObject: outer)
        return String(data: outData, encoding: .utf8) ?? ""
    }

    // Decrypt from the outer file JSON string. Returns inner plaintext bytes.
    static func decryptHistory(
        fileJSON: String,
        privateKey: Curve25519.KeyAgreement.PrivateKey
    ) throws -> Data {
        guard let fileData = fileJSON.data(using: .utf8),
              let outer = try? JSONSerialization.jsonObject(with: fileData) as? [String: String],
              let nonceB64 = outer["nonce"],
              let ciphertextB64 = outer["ciphertext"],
              let nonceData = Data(base64Encoded: nonceB64), nonceData.count == 12,
              let combined = Data(base64Encoded: ciphertextB64), combined.count >= 16 else {
            throw SessionCryptoError.decryptionFailed
        }
        let key = historyKey(privateKey: privateKey)
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
}
