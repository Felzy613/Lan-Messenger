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
    // "relay" when this message transited the cloud relay Worker; nil for direct LAN delivery.
    var deliveryPath: String?
    // True when this entry has been deleted (locally via "delete for me" applied
    // remotely, or "delete for everyone"). When true, `text` and reply preview
    // fields are cleared and the UI renders a "this message was deleted" placeholder.
    var deleted: Bool

    enum CodingKeys: String, CodingKey {
        case sender, text, incoming, timestamp, status
        case messageId = "message_id"
        case readReceiptSent = "read_receipt_sent"
        case replyToMessageId = "reply_to_message_id"
        case replyToPreview = "reply_to_preview"
        case replyToSender = "reply_to_sender"
        case deliveryPath = "delivery_path"
        case deleted
        // _stableId is intentionally excluded — it is a session-only value, never persisted.
    }

    init(sender: String, text: String, incoming: Bool, timestamp: Double,
         messageId: String?, status: String, readReceiptSent: Bool,
         replyToMessageId: String? = nil, replyToPreview: String? = nil,
         replyToSender: String? = nil, deliveryPath: String? = nil, deleted: Bool = false) {
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
        self.deliveryPath = deliveryPath
        self.deleted = deleted
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
        deliveryPath = try c.decodeIfPresent(String.self, forKey: .deliveryPath)
        deleted = try c.decodeIfPresent(Bool.self, forKey: .deleted) ?? false
        _stableId = UUID().uuidString  // generated once at decode time; stable for the session
    }

    // Matches entries without relying on messageId equality alone — useful for
    // "delete for me" on entries that might lack a stable id (e.g. very old
    // file-transfer entries migrated from Python history).
    static func sameEntry(_ a: MessageEntry, _ b: MessageEntry) -> Bool {
        if let aId = a.messageId, let bId = b.messageId {
            return aId == bId
        }
        return a.timestamp == b.timestamp
            && a.sender == b.sender
            && a.text == b.text
            && a.incoming == b.incoming
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

    // Marks every incoming entry for a peer as read, regardless of whether it has
    // a messageId.  File-transfer entries (messageId == nil) are not handled by
    // markReadReceiptSent and would otherwise remain unread after an app restart.
    func markAllIncomingRead(forPeerIP ip: String) {
        guard var entries = history[ip] else { return }
        var changed = false
        for i in entries.indices where entries[i].incoming && !entries[i].readReceiptSent {
            entries[i].readReceiptSent = true
            changed = true
        }
        if changed { history[ip] = entries }
    }

    // Marks a message entry as having transited the cloud relay. Called once
    // the Worker has *confirmed* an outgoing message was stored (see
    // MessagingService.markRelayStored). Scans every bucket rather than
    // taking a peerIP — an outgoing message's bucket is known at send time,
    // but retries of a failed store (fired from the relay-outbox retry loop,
    // which only knows the messageId) need to find it without re-resolving
    // an IP that may have changed since the message was queued.
    func markRelayDelivery(messageId: String) {
        for (ip, entries) in history {
            guard let idx = entries.firstIndex(where: { $0.messageId == messageId }) else { continue }
            if entries[idx].deliveryPath != "relay" {
                var updated = entries
                updated[idx].deliveryPath = "relay"
                history[ip] = updated
            }
            return
        }
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

    // Scans every peer bucket, not just one IP. Relay messages are dispatched
    // through an `ip` that's re-resolved from ephemeral state (live peers,
    // contacts, session cache) on every poll and can legitimately point at a
    // different bucket than where an earlier delivery of the same message_id
    // landed (e.g. macOS purges offline peers from `peers`). A per-IP dedup
    // check misses that case and re-appends the message; this doesn't.
    func containsMessageId(_ messageId: String) -> Bool {
        history.values.contains { entries in
            entries.contains { $0.messageId == messageId }
        }
    }

    // Marks the entry identified by messageId as deleted: clears text and reply
    // preview fields, leaving a "this message was deleted" placeholder. Used for
    // both "delete for everyone" (our own outgoing message) and inbound
    // delete_message notices from a peer.
    func markDeleted(messageId: String, peerIP: String) {
        guard var entries = history[peerIP] else { return }
        var changed = false
        for i in entries.indices where entries[i].messageId == messageId {
            entries[i].deleted = true
            entries[i].text = ""
            entries[i].replyToMessageId = nil
            entries[i].replyToPreview = nil
            entries[i].replyToSender = nil
            changed = true
        }
        if changed {
            history[peerIP] = entries
            save()
        }
    }

    // Removes the first entry matching `entry` via sameEntry — used for
    // "delete for me", a local-only operation that never sends a packet.
    func removeEntry(matching entry: MessageEntry, peerIP: String) {
        guard var entries = history[peerIP] else { return }
        guard let idx = entries.firstIndex(where: { MessageEntry.sameEntry($0, entry) }) else { return }
        entries.remove(at: idx)
        history[peerIP] = entries
        save()
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
        let merged = Self.dedupByMessageId(existing + oldEntries).sorted { $0.timestamp < $1.timestamp }
        history[toIP] = Array(merged.suffix(Self.maxEntriesPerPeer))
    }

    // Keeps the first occurrence of each messageId; entries with no messageId
    // (file transfers, legacy migrated history) are never considered
    // duplicates of each other and are all kept.
    private static func dedupByMessageId(_ entries: [MessageEntry]) -> [MessageEntry] {
        var seen = Set<String>()
        return entries.filter { entry in
            guard let id = entry.messageId else { return true }
            return seen.insert(id).inserted
        }
    }
}
