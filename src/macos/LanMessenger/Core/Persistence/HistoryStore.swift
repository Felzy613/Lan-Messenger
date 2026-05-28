import Foundation
import CryptoKit

// One history entry — mirrors the Python MessageEntry dataclass.
// Reply metadata is local-only and decoded with defaults so older files load fine.
struct MessageEntry: Codable, Identifiable {
    // Stable per-session identity for entries that have no messageId (file
    // transfers, migrated Python history). Generated once at init/decode time;
    // intentionally NOT persisted — on reload a fresh stable UUID is assigned
    // so SwiftUI ForEach identity is consistent within a session.
    //
    // The previous implementation used `var id: String { messageId ?? UUID().uuidString }`,
    // which called UUID() on every property access. Because ForEach reads `.id` on
    // every layout pass, file entries (messageId == nil) got a new identity each
    // frame, causing MediaBubbleView to be destroyed and recreated at ~20 Hz:
    //   new UUID → ForEach recreates view → .task fires → @State update → re-render → repeat.
    private var _stableId: String
    var id: String { messageId ?? _stableId }

    var sender: String
    var text: String
    var incoming: Bool
    var timestamp: Double
    var messageId: String?
    var status: String
    var readReceiptSent: Bool
    var replyToMessageId: String?
    var replyToPreview: String?
    var replyToSender: String?

    enum CodingKeys: String, CodingKey {
        case sender, text, incoming, timestamp, status
        case messageId = "message_id"
        case readReceiptSent = "read_receipt_sent"
        case replyToMessageId = "reply_to_message_id"
        case replyToPreview = "reply_to_preview"
        case replyToSender = "reply_to_sender"
        // _stableId is intentionally excluded — it is a session-only value, never persisted.
    }

    init(sender: String, text: String, incoming: Bool, timestamp: Double,
         messageId: String?, status: String, readReceiptSent: Bool,
         replyToMessageId: String? = nil, replyToPreview: String? = nil,
         replyToSender: String? = nil) {
        self.sender = sender
        self.text = text
        self.incoming = incoming
        self.timestamp = timestamp
        self.messageId = messageId
        self.status = status
        self.readReceiptSent = readReceiptSent
        self.replyToMessageId = replyToMessageId
        self.replyToPreview = replyToPreview
        self.replyToSender = replyToSender
        self._stableId = UUID().uuidString  // generated once; stable for lifetime of this instance
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        sender = try c.decode(String.self, forKey: .sender)
        text = try c.decode(String.self, forKey: .text)
        incoming = try c.decode(Bool.self, forKey: .incoming)
        timestamp = try c.decode(Double.self, forKey: .timestamp)
        messageId = try c.decodeIfPresent(String.self, forKey: .messageId)
        status = try c.decode(String.self, forKey: .status)
        readReceiptSent = try c.decode(Bool.self, forKey: .readReceiptSent)
        replyToMessageId = try c.decodeIfPresent(String.self, forKey: .replyToMessageId)
        replyToPreview = try c.decodeIfPresent(String.self, forKey: .replyToPreview)
        replyToSender = try c.decodeIfPresent(String.self, forKey: .replyToSender)
        _stableId = UUID().uuidString  // generated once at decode time; stable for the session
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

    // Serial queue that owns all encrypt-and-write work. Keeping saves serial
    // means rapid back-to-back calls (receive file + mark read, etc.) never
    // interleave on disk — the last-dispatched snapshot always wins. Background
    // QoS so the OS can defer the write during heavy UI activity.
    private let saveQueue = DispatchQueue(
        label: "com.dave.lanmessenger.history-save",
        qos: .background
    )

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

    // Non-blocking save: JSON-encode on the calling thread (always main, fast —
    // typically < 5 ms for the full 200-message-per-peer cap), then hand the
    // opaque Data blob to a serial background queue for AES-GCM encryption and
    // the atomic file write.  Both of those operations can take 50–300 ms on a
    // loaded system; keeping them off the main thread prevents the spinning
    // beachball that previously appeared whenever a file transfer completed or
    // a message was received.
    //
    // The encode step stays on the calling thread so `history` (a value type
    // dict) is never accessed from multiple threads.  The resulting Data object
    // is an independent heap allocation safe to pass across the thread boundary.
    func save() {
        let trimmed = history.mapValues { Array($0.suffix(Self.maxEntriesPerPeer)) }
        guard let plaintext = try? JSONEncoder().encode(trimmed) else { return }

        // Capture values that must be read on the main actor before we leave it.
        // KeyManager.shared.privateKey is a CryptoKit value type — safe to copy.
        let url = fileURL
        let key = KeyManager.shared.privateKey

        saveQueue.async {
            do {
                let fileJSON = try HistoryCrypto.encryptHistory(
                    plaintext: plaintext,
                    privateKey: key
                )
                try fileJSON.write(to: url, atomically: true, encoding: .utf8)
            } catch {}
        }
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

    // Rank-aware status update — never downgrades a delivered/read message back
    // to "Sent". Without this guard, the late "Sent" dispatch from the sender's
    // TCP-write completion would frequently overwrite the "Delivered" status
    // set by the receiver's sent_receipt, leaving the user with a single
    // check mark forever on cross-platform exchanges. Returns true iff the
    // status was actually applied.
    @discardableResult
    func updateStatus(_ status: String, forMessageId messageId: String, peerIP: String) -> Bool {
        guard var entries = history[peerIP] else { return false }
        var applied = false
        for i in entries.indices where entries[i].messageId == messageId {
            if MessageStatus.shouldApply(status, over: entries[i].status) {
                entries[i].status = status
                applied = true
            }
        }
        if applied { history[peerIP] = entries }
        return applied
    }

    func entries(forPeerIP ip: String) -> [MessageEntry] {
        history[ip] ?? []
    }

    // Drops all messages for a peer IP. Caller is responsible for persisting via save().
    func delete(peerIP: String) {
        history.removeValue(forKey: peerIP)
    }

    // Moves all history entries from one peer IP to another. Used when a saved
    // contact reappears on a different LAN IP — we keep their thread intact.
    // If both keys hold entries, they are merged in timestamp order.
    func migrate(fromIP: String, toIP: String) {
        guard fromIP != toIP, let oldEntries = history.removeValue(forKey: fromIP) else { return }
        let existing = history[toIP] ?? []
        let merged = (existing + oldEntries).sorted { $0.timestamp < $1.timestamp }
        history[toIP] = Array(merged.suffix(Self.maxEntriesPerPeer))
    }
}
