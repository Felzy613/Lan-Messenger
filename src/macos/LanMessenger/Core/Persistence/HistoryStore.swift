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
    // True when the sender has replaced this message's text after sending it.
    // `timestamp` keeps the ORIGINAL send time so an edit doesn't move the
    // message in the thread; `editedAt` records when the edit happened.
    var edited: Bool
    var editedAt: Double?

    enum CodingKeys: String, CodingKey {
        case sender, text, incoming, timestamp, status
        case messageId = "message_id"
        case readReceiptSent = "read_receipt_sent"
        case replyToMessageId = "reply_to_message_id"
        case replyToPreview = "reply_to_preview"
        case replyToSender = "reply_to_sender"
        case deliveryPath = "delivery_path"
        case deleted
        case edited
        case editedAt = "edited_at"
        // _stableId is intentionally excluded — it is a session-only value, never persisted.
    }

    init(sender: String, text: String, incoming: Bool, timestamp: Double,
         messageId: String?, status: String, readReceiptSent: Bool,
         replyToMessageId: String? = nil, replyToPreview: String? = nil,
         replyToSender: String? = nil, deliveryPath: String? = nil, deleted: Bool = false,
         edited: Bool = false, editedAt: Double? = nil) {
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
        self.edited = edited
        self.editedAt = editedAt
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
        edited = try c.decodeIfPresent(Bool.self, forKey: .edited) ?? false
        editedAt = try c.decodeIfPresent(Double.self, forKey: .editedAt)
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
// Inner JSON structure: { "<conversation id>": [MessageEntry, ...] }
//
// Keyed by conversation id — the peer's identity key (see `PeerID`). It used to
// be the peer's LAN address, which DHCP recycles between machines, so a thread
// could be filed under a name later given to somebody else. The file shape is
// unchanged; only what the names mean is, and history written under addresses
// is re-filed once, at load. Max 200 entries per peer.
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

    // All loaded conversations, keyed by conversation id (`PeerID`).
    private(set) var history: [String: [MessageEntry]] = [:]

    private init() {
        // Explicit rather than inherited from ConfigStore's own guard: the test
        // process can open the real keychain key, so this file is the one that
        // must never be the real one. See TestIsolation.
        fileURL = TestIsolation.isActive
            ? TestIsolation.scratchURL("history.enc")
            : ConfigStore.shared.historyFileURL
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
            let capped = raw.mapValues { Array($0.suffix(Self.maxEntriesPerPeer)) }

            // Re-file anything still named by address. Idempotent: a key is
            // already an id, so a history that has been migrated passes
            // through untouched and nothing is written.
            let contacts = ConfigStore.shared.config.contacts.map {
                PeerID.Contact(publicKeyB64: $0.publicKeyB64, lastIP: $0.lastIP)
            }
            let (rekeyed, moved) = PeerID.rekey(history: capped, contacts: contacts,
                                                cap: Self.maxEntriesPerPeer)
            history = rekeyed
            if !moved.isEmpty {
                let unattributed = moved.values.filter(PeerID.isLegacy).count
                NetLogger.info("History",
                    "re-filed \(moved.count) conversation(s) from address to identity key"
                    + (unattributed > 0 ? " (\(unattributed) kept under their address: no single contact owned it)" : ""))
                save()
            }
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

    func append(entry: MessageEntry, forPeer peer: String) {
        var entries = history[peer] ?? []
        entries.append(entry)
        history[peer] = Array(entries.suffix(Self.maxEntriesPerPeer))
    }

    /// Moves everything filed under `source` into `target`, merged as
    /// `PeerID.merge` does. Returns false when there was nothing to move.
    @discardableResult
    func merge(from source: String, into target: String) -> Bool {
        guard source != target, let moving = history.removeValue(forKey: source) else { return false }
        history[target] = PeerID.merge([history[target] ?? [], moving], cap: Self.maxEntriesPerPeer)
        return true
    }

    func markReadReceiptSent(messageId: String, peer: String) {
        guard var entries = history[peer] else { return }
        for i in entries.indices where entries[i].messageId == messageId {
            entries[i].readReceiptSent = true
        }
        history[peer] = entries
    }

    // Marks every incoming entry for a peer as read, regardless of whether it has
    // a messageId.  File-transfer entries (messageId == nil) are not handled by
    // markReadReceiptSent and would otherwise remain unread after an app restart.
    func markAllIncomingRead(forPeer peer: String) {
        guard var entries = history[peer] else { return }
        var changed = false
        for i in entries.indices where entries[i].incoming && !entries[i].readReceiptSent {
            entries[i].readReceiptSent = true
            changed = true
        }
        if changed { history[peer] = entries }
    }

    // Marks a message entry as having transited the cloud relay. Called once
    // the Worker has *confirmed* an outgoing message was stored (see
    // MessagingService.markRelayStored). Scans every bucket rather than
    // taking a conversation id — an outgoing message's bucket is known at send time,
    // but retries of a failed store (fired from the relay-outbox retry loop,
    // which only knows the messageId) need to find it without re-resolving
    // the conversation from state that may have changed since it was queued.
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
    func updateStatus(_ status: String, forMessageId messageId: String, peer: String) -> Bool {
        guard var entries = history[peer] else { return false }
        var applied = false
        for i in entries.indices where entries[i].messageId == messageId {
            if MessageStatus.shouldApply(status, over: entries[i].status) {
                entries[i].status = status
                applied = true
            }
        }
        if applied { history[peer] = entries }
        return applied
    }

    func entries(forPeer peer: String) -> [MessageEntry] {
        history[peer] ?? []
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
    //
    // `requireIncoming` is the same security gate applyEdit uses, and for the
    // same reason: a peer knows the message_id of everything we sent them, so
    // an inbound delete naming one of OUR outgoing messages must be refused
    // rather than allowed to blank what we said. Inbound notices pass true; our
    // own "delete for everyone" passes false.
    @discardableResult
    func markDeleted(messageId: String, peer: String, requireIncoming: Bool) -> Bool {
        guard var entries = history[peer] else { return false }
        var changed = false
        for i in entries.indices where entries[i].messageId == messageId {
            if entries[i].incoming != requireIncoming { continue }
            entries[i].deleted = true
            entries[i].text = ""
            entries[i].replyToMessageId = nil
            entries[i].replyToPreview = nil
            entries[i].replyToSender = nil
            changed = true
        }
        if changed {
            history[peer] = entries
            save()
        }
        return changed
    }

    /// Replaces the text of the entry identified by `messageId`, marking it
    /// edited. Returns true when an entry was actually changed.
    ///
    /// `requireIncoming` is the security gate, and it is not optional: a peer
    /// knows the `message_id` of every message we ever sent them, so an inbound
    /// `edit_message` naming one of OUR outgoing messages must be refused
    /// rather than allowed to rewrite what we said. Inbound edits pass true;
    /// our own edits of our own messages pass false.
    ///
    /// Attachments and already-deleted messages are never editable —
    /// `__FILE__:` text is a local path, not a body the peer can replace.
    @discardableResult
    func applyEdit(messageId: String,
                   peer: String,
                   newText: String,
                   editedAt: Double,
                   requireIncoming: Bool) -> Bool {
        guard var entries = history[peer] else { return false }
        var changed = false
        for i in entries.indices where entries[i].messageId == messageId {
            let e = entries[i]
            if e.incoming != requireIncoming { continue }
            if e.deleted { continue }
            if e.text.hasPrefix("__FILE__:") { continue }
            entries[i].text = newText
            entries[i].edited = true
            entries[i].editedAt = editedAt
            changed = true
        }
        if changed {
            history[peer] = entries
            save()
        }
        return changed
    }

    // Removes the first entry matching `entry` via sameEntry — used for
    // "delete for me", a local-only operation that never sends a packet.
    func removeEntry(matching entry: MessageEntry, peer: String) {
        guard var entries = history[peer] else { return }
        guard let idx = entries.firstIndex(where: { MessageEntry.sameEntry($0, entry) }) else { return }
        entries.remove(at: idx)
        history[peer] = entries
        save()
    }

    // Drops all messages for a conversation. Caller persists via save().
    func delete(peer: String) {
        history.removeValue(forKey: peer)
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
