import Foundation
import CryptoKit
import Security

enum KeyManagerError: Error {
    case keychainRead(OSStatus)
    case keychainWrite(OSStatus)
    case keychainDelete(OSStatus)
    case invalidKeyData
}

// Manages the X25519 private key in the macOS Keychain.
// The raw 32-byte private key is stored under a fixed service+account pair.
// Only the 32-byte raw representation is stored; the full key object is
// reconstructed on every load.
final class KeyManager {

    static let shared = KeyManager()

    private let service = "com.dave.lanmessenger"
    private let account = "privateKey"

    private(set) var privateKey: Curve25519.KeyAgreement.PrivateKey

    private init() {
        if let loaded = try? KeyManager.load(service: "com.dave.lanmessenger", account: "privateKey") {
            privateKey = loaded
        } else {
            let fresh = Curve25519.KeyAgreement.PrivateKey()
            try? KeyManager.save(fresh, service: "com.dave.lanmessenger", account: "privateKey")
            privateKey = fresh
        }
    }

    var publicKeyB64: String {
        privateKey.publicKey.rawRepresentation.base64EncodedString()
    }

    var publicKeyData: Data {
        privateKey.publicKey.rawRepresentation
    }

    // Replaces the stored key (used during Python config migration).
    func replaceKey(_ newKey: Curve25519.KeyAgreement.PrivateKey) throws {
        try KeyManager.save(newKey, service: service, account: account)
        privateKey = newKey
    }

    // Import from a raw 32-byte base64 string (Python config.json migration).
    func importFromBase64(_ b64: String) throws {
        guard let rawData = Data(base64Encoded: b64),
              rawData.count == 32 else {
            throw KeyManagerError.invalidKeyData
        }
        let key = try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: rawData)
        try replaceKey(key)
    }

    // MARK: - Keychain helpers

    private static func save(_ key: Curve25519.KeyAgreement.PrivateKey, service: String, account: String) throws {
        let rawData = key.rawRepresentation
        let query: [CFString: Any] = [
            kSecClass:       kSecClassGenericPassword,
            kSecAttrService: service,
            kSecAttrAccount: account,
            kSecValueData:   rawData,
        ]
        SecItemDelete(query as CFDictionary)     // remove any existing entry first
        let status = SecItemAdd(query as CFDictionary, nil)
        guard status == errSecSuccess else {
            throw KeyManagerError.keychainWrite(status)
        }
    }

    private static func load(service: String, account: String) throws -> Curve25519.KeyAgreement.PrivateKey {
        let query: [CFString: Any] = [
            kSecClass:            kSecClassGenericPassword,
            kSecAttrService:      service,
            kSecAttrAccount:      account,
            kSecReturnData:       true,
            kSecMatchLimit:       kSecMatchLimitOne,
        ]
        var item: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &item)
        guard status == errSecSuccess, let data = item as? Data else {
            throw KeyManagerError.keychainRead(status)
        }
        return try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: data)
    }
}
