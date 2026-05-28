import CryptoKit
import Foundation

// MARK: - Data transfer objects

/// Payload sent to POST /store on the cloud relay Worker.
struct RelayStoreRequest: Encodable {
    let relayIdHash: String          // SHA256(relay_id) hex — recipient's mailbox address
    let messageId: String
    let ciphertextB64: String
    let nonceB64: String
    let senderUsername: String
    let senderPublicKeyB64: String
    let timestamp: Double
    let ttlS: Int

    enum CodingKeys: String, CodingKey {
        case relayIdHash       = "relay_id_hash"
        case messageId         = "message_id"
        case ciphertextB64     = "ciphertext_b64"
        case nonceB64          = "nonce_b64"
        case senderUsername    = "sender_username"
        case senderPublicKeyB64 = "sender_public_key_b64"
        case timestamp
        case ttlS              = "ttl_s"
    }
}

/// One pending message returned by GET /pending.
struct RelayPendingMessage: Decodable {
    let messageId: String
    let ciphertextB64: String
    let nonceB64: String
    let senderUsername: String
    let senderPublicKeyB64: String
    let timestamp: Double

    enum CodingKeys: String, CodingKey {
        case messageId          = "message_id"
        case ciphertextB64      = "ciphertext_b64"
        case nonceB64           = "nonce_b64"
        case senderUsername     = "sender_username"
        case senderPublicKeyB64 = "sender_public_key_b64"
        case timestamp
    }
}

// MARK: - RelayClient

/// Thin async HTTP client that speaks to the Cloudflare cloud relay Worker.
///
/// All methods are no-ops when `relayWorkerURL` is empty or the network is
/// unavailable; failures are swallowed so they never affect the critical
/// LAN path.
actor RelayClient {

    static let shared = RelayClient()

    private let session: URLSession = {
        let cfg = URLSessionConfiguration.ephemeral
        cfg.timeoutIntervalForRequest = 6   // fail fast — don't block startup
        cfg.timeoutIntervalForResource = 10
        return URLSession(configuration: cfg)
    }()

    private var workerURL: URL? {
        let raw = ConfigStore.shared.config.relayWorkerURL.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !raw.isEmpty else { return nil }
        return URL(string: raw)
    }

    // MARK: - relay_id derivation

    /// Derives the private relay_id from the device's X25519 private key.
    /// relay_id = SHA256(private_key_bytes || "relay-v1")
    /// This is deterministic and never transmitted — only SHA256(relay_id) is.
    nonisolated func deriveRelayId() -> Data {
        let privateKeyBytes = KeyManager.shared.privateKey.rawRepresentation
        let info = Data("relay-v1".utf8)
        return Data(SHA256.hash(data: privateKeyBytes + info))
    }

    /// Returns the hex string of SHA256(relay_id) — this is what goes in
    /// discovery packets and is used as the mailbox address on the Worker.
    nonisolated func relayIdHash() -> String {
        let relayId = deriveRelayId()
        return Data(SHA256.hash(data: relayId)).map { String(format: "%02x", $0) }.joined()
    }

    // MARK: - Store a message for an offline peer

    /// Posts an encrypted message to the relay Worker mailbox for `peerRelayIdHash`.
    /// Call this fire-and-forget after a message has been placed in the local queue.
    func store(
        peerRelayIdHash: String,
        messageId: String,
        ciphertextB64: String,
        nonceB64: String,
        timestamp: Double
    ) async {
        guard let base = workerURL else { return }
        guard !peerRelayIdHash.isEmpty else { return }

        let body = RelayStoreRequest(
            relayIdHash: peerRelayIdHash,
            messageId: messageId,
            ciphertextB64: ciphertextB64,
            nonceB64: nonceB64,
            senderUsername: ConfigStore.shared.config.username,
            senderPublicKeyB64: KeyManager.shared.publicKeyB64,
            timestamp: timestamp,
            ttlS: 72 * 3600
        )

        guard let data = try? JSONEncoder().encode(body) else { return }
        var req = URLRequest(url: base.appendingPathComponent("store"))
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.httpBody = data

        do {
            let (_, resp) = try await session.data(for: req)
            let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
            if (200..<300).contains(code) {
                NetLogger.info("Relay", "store msgId=\(messageId) → HTTP \(code) (stored on Worker)")
            } else {
                NetLogger.warn("Relay", "store msgId=\(messageId) → HTTP \(code) (Worker rejected upload)")
            }
        } catch {
            NetLogger.warn("Relay", "store msgId=\(messageId) failed: \(error.localizedDescription)")
        }
    }

    // MARK: - Fetch pending messages for this device

    /// Fetches all messages waiting in the cloud relay inbox for this device.
    /// Returns decoded relay messages ready to be dispatched through MessagingService.
    func fetchPending() async -> [RelayPendingMessage] {
        guard let base = workerURL else { return [] }

        let relayId = deriveRelayId()
        let relayIdHex = relayId.map { String(format: "%02x", $0) }.joined()

        var comps = URLComponents(url: base.appendingPathComponent("pending"), resolvingAgainstBaseURL: false)!
        comps.queryItems = [URLQueryItem(name: "relay_id", value: relayIdHex)]
        guard let url = comps.url else { return [] }

        do {
            let (data, resp) = try await session.data(from: url)
            let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
            guard code == 200 else {
                NetLogger.warn("Relay", "fetchPending → HTTP \(code) (Worker error)")
                return []
            }
            let msgs = try JSONDecoder().decode([RelayPendingMessage].self, from: data)
            if msgs.isEmpty {
                NetLogger.verbose("Relay", "fetchPending → inbox empty")
            } else {
                NetLogger.info("Relay", "fetchPending → \(msgs.count) message(s) waiting on relay")
            }
            return msgs
        } catch {
            NetLogger.warn("Relay", "fetchPending failed: \(error.localizedDescription)")
            return []
        }
    }

    // MARK: - Delete a delivered message

    /// Deletes a message from the relay after it has been successfully processed.
    func delete(messageId: String) async {
        guard let base = workerURL else { return }

        let relayId = deriveRelayId()
        let relayIdHex = relayId.map { String(format: "%02x", $0) }.joined()

        var comps = URLComponents(
            url: base.appendingPathComponent("message/\(messageId)"),
            resolvingAgainstBaseURL: false
        )!
        comps.queryItems = [URLQueryItem(name: "relay_id", value: relayIdHex)]
        guard let url = comps.url else { return }

        var req = URLRequest(url: url)
        req.httpMethod = "DELETE"

        do {
            let (_, resp) = try await session.data(for: req)
            let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
            NetLogger.info("Relay", "delete msgId=\(messageId) → HTTP \(code)")
        } catch {
            NetLogger.info("Relay", "delete msgId=\(messageId) failed: \(error.localizedDescription)")
        }
    }
}
