import Foundation
import CryptoKit

// One history entry — mirrors the Python MessageEntry dataclass.
struct MessageEntry: Codable, Identifiable {
    var id: String { messageId ?? UUID().uuidString }
    var sender: String
    var text: String
    var incoming: Bool
    var timestamp: Double
    var messageId: String?
    var status: String
    var readReceiptSent: Bool

    enum CodingKeys: String, CodingKey {
        case sender, text, incoming, timestamp, status
        case messageId = "message_id"
        case readReceiptSent = "read_receipt_sent"
    }
}

// Manages reading and writing the encrypted history file.
// Format and key derivation are identical to the Python app so files are portable.
//
// Inner JSON structure: { "<peer_ip>": [MessageEntry, ...] }
// Keyed by peer IP (not public key) — same as Python.
// Max 200 entries per peer.
final class HistoryStore {

    static let shared = HistoryStore()
    static let maxEntriesPerPeer = 200

    private let fileURL: URL
    private let lock = NSLock()

    // All loaded conversations, keyed by peer IP.
    private(set) var history: [String: [MessageEntry]] = [:]

    private init() {
        fileURL = ConfigStore.shared.historyFileURL
        load()
    }

    // MARK: - Load

    private func load() {
        guard FileManager.default.fileExists(atPath: fileURL.path),
              let fileJSON = try? String(contentsOf: fileURL, encoding: .utf8) else { return }
        do {
            let plaintext = try HistoryCrypto.decryptHistory(
                fileJSON: fileJSON,
                privateKey: KeyManager.shared.privateKey
            )
            guard let raw = try? JSONDecoder().decode([String: [MessageEntry]].self, from: plaintext) else { return }
            history = raw.mapValues { Array($0.suffix(Self.maxEntriesPerPeer)) }
        } catch {
            // Corrupted or wrong key — start fresh
            history = [:]
        }
    }

    // MARK: - Save

    func save() {
        lock.lock()
        defer { lock.unlock() }

        let trimmed = history.mapValues { Array($0.suffix(Self.maxEntriesPerPeer)) }
        guard let plaintext = try? JSONEncoder().encode(trimmed) else { return }

        do {
            let fileJSON = try HistoryCrypto.encryptHistory(
                plaintext: plaintext,
                privateKey: KeyManager.shared.privateKey
            )
            try fileJSON.write(to: fileURL, atomically: true, encoding: .utf8)
        } catch {}
    }

    // MARK: - Mutations

    func append(entry: MessageEntry, forPeerIP ip: String) {
        var entries = history[ip] ?? []
        entries.append(entry)
        history[ip] = Array(entries.suffix(Self.maxEntriesPerPeer))
    }

    func markReadReceiptSent(messageId: String, peerIP: String) {
        guard var entries = history[peerIP] else { return }
        for i in entries.indices where entries[i].messageId == messageId {
            entries[i].readReceiptSent = true
        }
        history[peerIP] = entries
    }

    func updateStatus(_ status: String, forMessageId messageId: String, peerIP: String) {
        guard var entries = history[peerIP] else { return }
        for i in entries.indices where entries[i].messageId == messageId {
            entries[i].status = status
        }
        history[peerIP] = entries
    }

    func entries(forPeerIP ip: String) -> [MessageEntry] {
        history[ip] ?? []
    }
}
