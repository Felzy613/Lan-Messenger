import Foundation

// Handles sending and receiving text messages, receipts, and typing indicators.
// Operates on the main actor so UI-facing state updates happen safely.
// Persistence (HistoryStore) is updated after each operation; save() is called periodically.

@MainActor
final class MessagingService {

    static let shared = MessagingService()

    weak var coordinator: NetworkCoordinator?

    // Called by AppModel to update UI state. Every callback names the
    // conversation by its id (`PeerID`) — the peer's identity key — never by
    // the address a packet happened to arrive from. DHCP hands the same
    // addresses to different machines, so an address is not a person.
    var onMessageReceived: ((String, MessageEntry) -> Void)?      // peerID, entry
    var onStatusUpdate: ((String, String, String) -> Void)?       // peerID, messageId, status
    var onTypingUpdate: ((String, String, Bool) -> Void)?         // peerID, senderName, active
    var onMessageDeleted: ((String, String) -> Void)?             // peerID, messageId
    var onMessageEdited: ((String, String, String, Double) -> Void)?  // peerID, messageId, newText, editedAt

    /// Whether `ip` is an address the device holding `key` has been seen at.
    ///
    /// The gate for packets that only *claim* a sender — typing, receipts and
    /// `delete_message` are not encrypted, so their `sender_public_key_b64` is
    /// whatever the sender typed. Encrypted packets prove the key by decrypting
    /// and do not need this. Filed by source address, those packets at least
    /// needed a completed TCP connection from that address; filed by a bare
    /// claimed key they would need nothing at all, since public keys are
    /// public. Binding the claim to an address that key is known at keeps them
    /// exactly as hard to forge as before, and adds the identity check on top.
    ///
    /// Unset means "accept", so the service stays usable without an AppModel.
    var isBoundAddress: ((_ key: String, _ ip: String) -> Bool)?

    /// A message decrypted under `key` arrived from `ip` — the device holding
    /// that key is there now. How a peer whose discovery beacons never reach
    /// us still gets its replies: the address is learned from traffic that
    /// proved who sent it, never from a claim. Fresh messages only; a
    /// duplicate may be a replay.
    var onPeerAddressProven: ((_ key: String, _ ip: String, _ sender: String) -> Void)?

    /// The conversation an unencrypted packet belongs to, or nil to drop it.
    private func claimedPeer(_ key: String, fromIP ip: String, _ what: String) -> String? {
        guard PeerID.isKey(key) else {
            NetLogger.info("Recv", "\(what) from \(ip) dropped — no usable sender key")
            return nil
        }
        if let bound = isBoundAddress, !bound(key, ip) {
            NetLogger.info("Recv", "\(what) from \(ip) dropped — not an address \(key.prefix(8)) is known at")
            return nil
        }
        return key
    }
    // Fired once the cloud relay Worker confirms an outgoing message was
    // actually stored — not when the upload is merely attempted. Lets the UI
    // show the "via relay" badge promptly instead of only after the
    // recipient later retrieves the message (which is what a full history
    // reload previously depended on).
    var onDeliveryPathUpdate: ((String) -> Void)?                  // messageId

    private let tcpPort = 54232
    private var typingSentAt: [String: Date] = [:]
    private var lastTypingState: [String: Bool] = [:]

    // Pending-queue delivery bookkeeping. deliverPending is invoked on every
    // discovery heartbeat (~1.5 s), not just the offline→online transition, so
    // a still-in-flight send must not be re-fired every cycle. The in-flight
    // set prevents concurrent duplicate sends; the per-message attempt clock
    // backs off retries against a persistently unreachable peer.
    private var pendingInFlight: Set<String> = []
    private var pendingLastTry: [String: Date] = [:]
    private let pendingRetryInterval: TimeInterval = 10

    // Cloud-relay outbox retry bookkeeping — kept separate from the local TCP
    // retry state above so the two transports retry independently and never
    // block each other.
    private var relayOutboxInFlight: Set<String> = []
    private var relayOutboxLastTry: [String: Date] = [:]

    private init() {}

    // MARK: - Receive

    func handlePacket(_ packet: ValidatedPacket) {
        switch packet {
        case .text(let pkt, let ip):    handleText(pkt, fromIP: ip)
        case .typing(let pkt, let ip):  handleTyping(pkt, fromIP: ip)
        case .receipt(let pkt, let ip): handleReceipt(pkt, fromIP: ip)
        case .delete(let pkt, let ip):  handleDeleteMessage(pkt, fromIP: ip)
        case .edit(let pkt, let ip):    handleEditMessage(pkt, fromIP: ip)
        default: break
        }
    }

    // MARK: - Send text

    func sendText(
        _ text: String,
        toPeerIP ip: String,
        peerPublicKeyB64: String,
        peerRelayIdHash: String? = nil,
        replyTo: MessageEntry? = nil
    ) {
        let messageId = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        let timestamp = Date().timeIntervalSince1970
        let aad = Data(messageId.utf8)

        let replyPreview = replyTo.map { Self.replyPreviewText(for: $0) }
        let replySender = replyTo?.sender

        // Record in history immediately (outgoing)
        let entry = MessageEntry(
            sender: ConfigStore.shared.config.username,
            text: text,
            incoming: false,
            timestamp: timestamp,
            messageId: messageId,
            status: "Sending",
            readReceiptSent: false,
            replyToMessageId: replyTo?.messageId,
            replyToPreview: replyPreview,
            replyToSender: replySender
        )
        // Filed under the recipient's identity key — the same key it is about
        // to be encrypted to — never under the address it happens to be sent to.
        let peer = peerPublicKeyB64
        HistoryStore.shared.append(entry: entry, forPeer: peer)
        HistoryStore.shared.save()
        onMessageReceived?(peer, entry)

        guard let (nonceB64, ctB64) = try? SessionCrypto.encryptForPeer(
            myPrivate: KeyManager.shared.privateKey,
            peerPublicKeyB64: peerPublicKeyB64,
            plaintext: Data(text.utf8),
            aad: aad
        ) else {
            updateStatus("Failed", forMessageId: messageId, peer: peer)
            return
        }

        var packet: [String: Any] = [
            "type": "text",
            "message_id": messageId,
            "timestamp": timestamp,
            "sender": ConfigStore.shared.config.username,
            "sender_public_key_b64": KeyManager.shared.publicKeyB64,
            "port": tcpPort,
            "nonce": nonceB64,
            "ciphertext": ctB64,
        ]
        if let replyId = replyTo?.messageId {
            packet["reply_to_message_id"] = replyId
            if let preview = replyPreview { packet["reply_to_preview"] = preview }
            if let s = replySender { packet["reply_to_sender"] = s }
        }

        // No live address means the peer is not on the LAN right now. Queue and
        // relay straight away rather than dialling an address remembered from
        // earlier: that address may belong to somebody else by now.
        guard !ip.isEmpty else {
            NetLogger.info("Send", "peer \(peer.prefix(8)) not on the LAN — queueing msgId=\(messageId) for relay/redelivery")
            queuePendingMessage(messageId: messageId, text: text, peerPublicKeyB64: peerPublicKeyB64,
                                peerRelayIdHash: peerRelayIdHash, timestamp: timestamp)
            updateStatus("Queued", forMessageId: messageId, peer: peer)
            HistoryStore.shared.save()
            return
        }

        sendJSON(packet, toIP: ip, port: tcpPort) { [weak self] success in
            guard let self else { return }
            let status = success ? "Sent" : "Queued"
            if success {
                NetLogger.info("Send", "TCP delivered msgId=\(messageId) peer=\(ip)")
            } else {
                NetLogger.info("Send", "TCP failed msgId=\(messageId) peer=\(ip) — queueing locally and falling back to relay")
                self.queuePendingMessage(
                    messageId: messageId,
                    text: text,
                    peerPublicKeyB64: peerPublicKeyB64,
                    peerRelayIdHash: peerRelayIdHash,
                    timestamp: timestamp
                )
            }
            self.updateStatus(status, forMessageId: messageId, peer: peer)
            HistoryStore.shared.save()
        }
    }

    // Returns a short preview text suitable for showing in a reply chip.
    static func replyPreviewText(for entry: MessageEntry) -> String {
        if entry.text.hasPrefix("__FILE__:") {
            let path = String(entry.text.dropFirst("__FILE__:".count))
            return "📎 \(URL(fileURLWithPath: path).lastPathComponent)"
        }
        return String(entry.text.prefix(80))
    }

    // MARK: - Send typing indicator

    func sendTyping(active: Bool, toPeerIP ip: String, peerPublicKeyB64 peer: String) {
        let now = Date()
        if !active, lastTypingState[peer] == false { return }
        if active {
            if lastTypingState[peer] == true, let sent = typingSentAt[peer], now.timeIntervalSince(sent) < 3 { return }
        }
        lastTypingState[peer] = active
        typingSentAt[peer] = now

        let packet: [String: Any] = [
            "type": "typing",
            "active": active,
            "sender": ConfigStore.shared.config.username,
            "sender_public_key_b64": KeyManager.shared.publicKeyB64,
            "port": tcpPort,
        ]
        sendJSON(packet, toIP: ip, port: tcpPort, completion: nil)
    }

    // MARK: - Send receipt

    func sendReceipt(type: String, messageId: String, toPeerIP ip: String) {
        let packet: [String: Any] = [
            "type": type,
            "message_id": messageId,
            "sender": ConfigStore.shared.config.username,
            "sender_public_key_b64": KeyManager.shared.publicKeyB64,
            "port": tcpPort,
        ]
        sendJSON(packet, toIP: ip, port: tcpPort, completion: nil)
    }

    // MARK: - Send delete_message ("delete for everyone" notice)

    // Unencrypted "delete for everyone" notice — same shape as a receipt.
    // Best-effort: sent over a one-shot TCP connection just like sent_receipt/read_receipt.
    func sendDeleteMessage(messageId: String,
                           toPeerIP ip: String,
                           peerPublicKeyB64: String? = nil,
                           peerRelayIdHash: String? = nil) {
        let packet: [String: Any] = [
            "type": "delete_message",
            "message_id": messageId,
            "sender": ConfigStore.shared.config.username,
            "sender_public_key_b64": KeyManager.shared.publicKeyB64,
            "port": tcpPort,
        ]
        // Drop the queued copy too — delivering a message the sender has since
        // deleted would be worse than not delivering it at all.
        dropPendingMessage(messageId: messageId)
        sendJSON(packet, toIP: ip, port: tcpPort) { [weak self] success in
            if success {
                NetLogger.info("Delete", "delivered delete msgId=\(messageId) peer=\(ip)")
                return
            }
            NetLogger.info("Delete", "delete not delivered over LAN msgId=\(messageId) peer=\(ip) — trying relay")
            guard let key = peerPublicKeyB64 else { return }
            self?.sendRelayControl(
                RelayControlEnvelope(op: .delete, target: messageId, text: nil,
                                     at: Date().timeIntervalSince1970),
                peerPublicKeyB64: key,
                peerRelayIdHash: peerRelayIdHash
            )
        }
    }

    /// Removes a still-undelivered message from the local pending queue.
    private func dropPendingMessage(messageId: String) {
        let before = ConfigStore.shared.config.pendingMessages.count
        ConfigStore.shared.config.pendingMessages.removeAll { $0.messageId == messageId }
        guard ConfigStore.shared.config.pendingMessages.count != before else { return }
        ConfigStore.shared.save()
        NetLogger.info("Delete", "dropped queued msgId=\(messageId) before delivery")
    }

    // MARK: - Send edit_message

    /// Sends replacement text for a message we already sent.
    ///
    /// Best-effort in the same sense as `delete_message` — one TCP write, no
    /// queue and no retry — with one important exception: if the original is
    /// still sitting in the pending queue (it never reached the peer), the
    /// queued copy is rewritten so the edit is what eventually gets delivered,
    /// as the message's first and only version.
    ///
    /// Returns nothing; the caller has already applied the edit locally.
    func sendEditMessage(messageId: String,
                         newText: String,
                         toPeerIP ip: String,
                         peerPublicKeyB64: String,
                         peerRelayIdHash: String? = nil,
                         editedAt: Double) {
        rewritePendingMessage(messageId: messageId, newText: newText)

        // AAD is the ORIGINAL message_id, exactly as for the `text` packet that
        // carried the first version.
        let aad = Data(messageId.utf8)
        guard let (nonceB64, ctB64) = try? SessionCrypto.encryptForPeer(
            myPrivate: KeyManager.shared.privateKey,
            peerPublicKeyB64: peerPublicKeyB64,
            plaintext: Data(newText.utf8),
            aad: aad
        ) else {
            NetLogger.warn("Edit", "encrypt failed msgId=\(messageId) peer=\(ip)")
            return
        }

        let packet: [String: Any] = [
            "type": "edit_message",
            "message_id": messageId,
            "timestamp": editedAt,
            "sender": ConfigStore.shared.config.username,
            "sender_public_key_b64": KeyManager.shared.publicKeyB64,
            "port": tcpPort,
            "nonce": nonceB64,
            "ciphertext": ctB64,
        ]
        sendJSON(packet, toIP: ip, port: tcpPort) { [weak self] success in
            if success {
                NetLogger.info("Edit", "delivered edit msgId=\(messageId) peer=\(ip)")
                return
            }
            NetLogger.info("Edit", "edit not delivered over LAN msgId=\(messageId) peer=\(ip) — trying relay")
            self?.sendRelayControl(
                RelayControlEnvelope(op: .edit, target: messageId, text: newText, at: editedAt),
                peerPublicKeyB64: peerPublicKeyB64,
                peerRelayIdHash: peerRelayIdHash
            )
        }
    }

    // MARK: - Relay control records (offline edit / delete)

    /// Carries an edit or delete to a peer who isn't reachable on the LAN, by
    /// dropping an encrypted control record in their relay mailbox. They apply
    /// it on their next poll. No-op when the relay isn't configured for this
    /// peer — the change then stays local, as it did before.
    ///
    /// The record gets its own fresh id rather than the target's: the Worker
    /// dedups `/store` by `message_id` and answers a repeat with
    /// `{ok:true,duplicate:true}`, so re-posting under the original's id would
    /// be dropped while reporting success.
    private func sendRelayControl(_ envelope: RelayControlEnvelope,
                                  peerPublicKeyB64: String,
                                  peerRelayIdHash: String?) {
        guard let hash = peerRelayIdHash, !hash.isEmpty else {
            NetLogger.info("Relay", "skip \(envelope.op.rawValue) control for target=\(envelope.target) — peer has no relay_id_hash")
            return
        }
        let recordId = RelayControlEnvelope.newRecordId()
        let plaintext = envelope.encoded()
        guard let (nonceB64, ctB64) = try? SessionCrypto.encryptForPeer(
            myPrivate: KeyManager.shared.privateKey,
            peerPublicKeyB64: peerPublicKeyB64,
            plaintext: Data(plaintext.utf8),
            aad: Data(recordId.utf8)
        ) else {
            NetLogger.warn("Relay", "encrypt failed for \(envelope.op.rawValue) control target=\(envelope.target)")
            return
        }

        Task {
            let ok = await RelayClient.shared.store(
                peerRelayIdHash: hash,
                messageId: recordId,
                ciphertextB64: ctB64,
                nonceB64: nonceB64,
                timestamp: envelope.at
            )
            NetLogger.info("Relay", ok
                ? "stored \(envelope.op.rawValue) control record=\(recordId) target=\(envelope.target)"
                : "failed to store \(envelope.op.rawValue) control target=\(envelope.target)")
        }
    }

    /// Rewrites a still-undelivered queued message so the peer receives the
    /// edited text rather than the superseded original.
    private func rewritePendingMessage(messageId: String, newText: String) {
        var pending = ConfigStore.shared.config.pendingMessages
        guard let idx = pending.firstIndex(where: { $0.messageId == messageId }) else { return }
        pending[idx].text = newText
        // Deliberately NOT clearing relayStored to force a re-upload: the Worker
        // dedups /store on message_id and answers a repeat with
        // {ok:true,duplicate:true}, so the re-upload would be discarded while
        // reporting success and the peer would still get the original text. A
        // relay copy is superseded by an edit control record instead (see
        // sendRelayControl).
        ConfigStore.shared.config.pendingMessages = pending
        ConfigStore.shared.save()
        NetLogger.info("Edit", "rewrote queued msgId=\(messageId) before delivery")
    }

    // MARK: - Deliver pending messages to a newly-online peer

    func deliverPending(toPeerIP ip: String, peerPublicKeyB64: String) {
        let now = Date()
        let toDeliver = ConfigStore.shared.config.pendingMessages.filter { msg in
            guard msg.peerPublicKeyB64 == peerPublicKeyB64, !pendingInFlight.contains(msg.messageId) else { return false }
            if let last = pendingLastTry[msg.messageId], now.timeIntervalSince(last) < pendingRetryInterval { return false }
            return true
        }
        guard !toDeliver.isEmpty else { return }

        for msg in toDeliver {
            pendingInFlight.insert(msg.messageId)
            pendingLastTry[msg.messageId] = now

            let aad = Data(msg.messageId.utf8)
            guard let (nonceB64, ctB64) = try? SessionCrypto.encryptForPeer(
                myPrivate: KeyManager.shared.privateKey,
                peerPublicKeyB64: peerPublicKeyB64,
                plaintext: Data(msg.text.utf8),
                aad: aad
            ) else {
                pendingInFlight.remove(msg.messageId)
                continue
            }

            let packet: [String: Any] = [
                "type": "text",
                "message_id": msg.messageId,
                "timestamp": msg.timestamp,
                "sender": ConfigStore.shared.config.username,
                "sender_public_key_b64": KeyManager.shared.publicKeyB64,
                "port": tcpPort,
                "nonce": nonceB64,
                "ciphertext": ctB64,
            ]
            let msgId = msg.messageId
            sendJSON(packet, toIP: ip, port: tcpPort) { [weak self] success in
                guard let self else { return }
                // Clearing in-flight on failure lets the next heartbeat retry
                // after the per-message backoff elapses.
                self.pendingInFlight.remove(msgId)
                guard success else { return }
                self.pendingLastTry.removeValue(forKey: msgId)
                self.updateStatus("Sent", forMessageId: msgId, peer: peerPublicKeyB64)
                // Note whether this message had already landed on the relay
                // before removing it from the queue — if so, clean up the
                // Worker copy now that direct LAN delivery beat it there.
                // Best-effort: even if this fails, the global dedup in
                // handleRelayMessage prevents a stale mailbox copy from ever
                // showing up as a duplicate.
                let wasRelayStored = ConfigStore.shared.config.pendingMessages
                    .first(where: { $0.messageId == msgId })?.relayStored ?? false
                // Remove only after confirmed delivery so a TCP failure doesn't
                // silently drop the message from the queue.
                ConfigStore.shared.config.pendingMessages.removeAll { $0.messageId == msgId }
                ConfigStore.shared.save()
                if wasRelayStored {
                    Task { await RelayClient.shared.delete(messageId: msgId) }
                }
            }
        }
    }

    // MARK: - Private receive handlers

    private func handleText(_ pkt: TextPacket, fromIP ip: String) {
        // Decrypt FIRST. Opening under the claimed key is what proves who sent
        // it — after this, `pkt.senderPublicKeyB64` is an identity rather than
        // a claim, and it is the conversation the message belongs to. Checking
        // for a duplicate before this answered a forged packet naming a real
        // message id with a receipt, confirming to a stranger that we had it.
        let aad = Data(pkt.messageId.utf8)
        guard let plaintext = try? SessionCrypto.decryptFromPeer(
            myPrivate: KeyManager.shared.privateKey,
            peerPublicKeyB64: pkt.senderPublicKeyB64,
            nonceB64: pkt.nonce,
            ciphertextB64: pkt.ciphertext,
            aad: aad
        ) else { return }
        let peer = pkt.senderPublicKeyB64

        // Duplicate suppression: heartbeat-driven queue retries (and a sender
        // whose sent_receipt got lost) can legitimately re-send a message we
        // already have. Don't append it twice — but do re-acknowledge, because
        // a re-send means the sender never saw our first receipt.
        if HistoryStore.shared.entries(forPeer: peer).contains(where: { $0.messageId == pkt.messageId }) {
            NetLogger.info("Recv", "duplicate text msgId=\(pkt.messageId) peer=\(ip) — re-sending receipt only")
            sendReceipt(type: "sent_receipt", messageId: pkt.messageId, toPeerIP: ip)
            return
        }

        onPeerAddressProven?(peer, ip, pkt.sender)
        let text = String(data: plaintext, encoding: .utf8) ?? ""

        // If the packet didn't include a preview but we have the original in history, fill it in.
        var preview = pkt.replyToPreview
        var replyToSender = pkt.replyToSender
        if let replyId = pkt.replyToMessageId, preview == nil {
            if let orig = HistoryStore.shared.entries(forPeer: peer).first(where: { $0.messageId == replyId }) {
                preview = Self.replyPreviewText(for: orig)
                replyToSender = orig.sender
            }
        }

        let entry = MessageEntry(
            sender: pkt.sender,
            text: text,
            incoming: true,
            timestamp: pkt.timestamp,
            messageId: pkt.messageId,
            status: "",
            readReceiptSent: false,
            replyToMessageId: pkt.replyToMessageId,
            replyToPreview: preview,
            replyToSender: replyToSender
        )
        HistoryStore.shared.append(entry: entry, forPeer: peer)
        HistoryStore.shared.save()
        onMessageReceived?(peer, entry)

        // Emit typing=false and sent_receipt (delivered)
        onTypingUpdate?(peer, pkt.sender, false)
        sendReceipt(type: "sent_receipt", messageId: pkt.messageId, toPeerIP: ip)
    }

    private func handleTyping(_ pkt: TypingPacket, fromIP ip: String) {
        guard let peer = claimedPeer(pkt.senderPublicKeyB64, fromIP: ip, "typing") else { return }
        onTypingUpdate?(peer, pkt.sender, pkt.active)
    }

    // Applies an inbound "delete for everyone" notice: marks the matching
    // history entry as deleted (clearing text and reply preview fields) and
    // notifies the UI so the in-memory copy is updated to match.
    private func handleDeleteMessage(_ pkt: ReceiptPacket, fromIP ip: String) {
        guard let peer = claimedPeer(pkt.senderPublicKeyB64, fromIP: ip, "delete_message") else { return }
        // Scoped to the sender's own conversation AND to its incoming messages:
        // a device can only withdraw what that same device said.
        guard HistoryStore.shared.markDeleted(messageId: pkt.messageId, peer: peer, requireIncoming: true) else {
            NetLogger.info("Delete", "ignored inbound delete msgId=\(pkt.messageId) peer=\(ip) — no deletable incoming message")
            return
        }
        onMessageDeleted?(peer, pkt.messageId)
    }

    // Applies an inbound edit: replaces the stored text of the peer's own
    // earlier message. HistoryStore.applyEdit enforces that only an *incoming*
    // entry can be rewritten — the peer knows the message_id of everything we
    // sent them, so an edit naming one of our outgoing messages is refused.
    private func handleEditMessage(_ pkt: TextPacket, fromIP ip: String) {
        let aad = Data(pkt.messageId.utf8)
        guard let plaintext = try? SessionCrypto.decryptFromPeer(
            myPrivate: KeyManager.shared.privateKey,
            peerPublicKeyB64: pkt.senderPublicKeyB64,
            nonceB64: pkt.nonce,
            ciphertextB64: pkt.ciphertext,
            aad: aad
        ) else {
            NetLogger.warn("Edit", "decrypt failed for inbound edit msgId=\(pkt.messageId) peer=\(ip)")
            return
        }
        let newText = String(data: plaintext, encoding: .utf8) ?? ""
        // Decrypted, so the key is proven: only this device's own conversation
        // is searched, and within it only its incoming messages.
        let peer = pkt.senderPublicKeyB64

        guard HistoryStore.shared.applyEdit(
            messageId: pkt.messageId,
            peer: peer,
            newText: newText,
            editedAt: pkt.timestamp,
            requireIncoming: true
        ) else {
            NetLogger.info("Edit", "ignored inbound edit msgId=\(pkt.messageId) peer=\(ip) — no editable incoming message")
            return
        }
        NetLogger.info("Recv", "edit_message applied msgId=\(pkt.messageId) peer=\(ip)")
        onMessageEdited?(peer, pkt.messageId, newText, pkt.timestamp)
    }

    /// Applies a relay control record. Routed through the same HistoryStore
    /// entry points as the LAN `edit_message` / `delete_message` packets, so the
    /// `requireIncoming` gate applies identically: a peer can only edit or
    /// delete their own messages, never ours.
    private func applyRelayControl(_ envelope: RelayControlEnvelope, peer: String) {
        switch envelope.op {
        case .edit:
            guard let newText = envelope.text,
                  HistoryStore.shared.applyEdit(
                    messageId: envelope.target, peer: peer, newText: newText,
                    editedAt: envelope.at, requireIncoming: true) else {
                NetLogger.info("Relay", "ignored relayed edit target=\(envelope.target) peer=\(peer.prefix(8)) — no editable incoming message")
                return
            }
            NetLogger.info("Relay", "applied relayed edit target=\(envelope.target) peer=\(peer.prefix(8))")
            onMessageEdited?(peer, envelope.target, newText, envelope.at)

        case .delete:
            guard HistoryStore.shared.markDeleted(
                    messageId: envelope.target, peer: peer, requireIncoming: true) else {
                NetLogger.info("Relay", "ignored relayed delete target=\(envelope.target) peer=\(peer.prefix(8)) — no deletable incoming message")
                return
            }
            NetLogger.info("Relay", "applied relayed delete target=\(envelope.target) peer=\(peer.prefix(8))")
            onMessageDeleted?(peer, envelope.target)
        }
    }

    private func handleReceipt(_ pkt: ReceiptPacket, fromIP ip: String) {
        guard let peer = claimedPeer(pkt.senderPublicKeyB64, fromIP: ip, pkt.type) else { return }
        // sent_receipt = the peer has received the message (two grey ticks)
        // read_receipt = the peer has read it (two blue ticks)
        let status = pkt.type == "read_receipt" ? MessageStatus.read : MessageStatus.delivered
        // updateStatus is now rank-aware (see HistoryStore + MessageStatus): a
        // late "Sent" dispatch from the sender's own TCP-write completion
        // cannot regress this, and a "Delivered" cannot regress a prior "Read".
        updateStatus(status, forMessageId: pkt.messageId, peer: peer)
    }

    // MARK: - Helpers

    private func updateStatus(_ status: String, forMessageId id: String, peer: String) {
        // Only notify the UI when the rank-aware HistoryStore actually applied
        // the change — otherwise the OnStatusUpdate listener would re-set the
        // status on its in-memory copy and the message would regress.
        guard HistoryStore.shared.updateStatus(status, forMessageId: id, peer: peer) else { return }
        HistoryStore.shared.save()
        onStatusUpdate?(peer, id, status)
    }

    private func queuePendingMessage(
        messageId: String,
        text: String,
        peerPublicKeyB64: String,
        peerRelayIdHash: String?,
        timestamp: Double
    ) {
        let username = ConfigStore.shared.config.contacts.first { $0.publicKeyB64 == peerPublicKeyB64 }?.username ?? "Unknown"
        let pending = PendingMessageConfig(
            messageId: messageId,
            peerPublicKeyB64: peerPublicKeyB64,
            peerUsername: username,
            text: text,
            timestamp: timestamp
        )
        ConfigStore.shared.config.pendingMessages.append(pending)
        ConfigStore.shared.save()

        // Upload to cloud relay (only if peer was confirmed offline before sending —
        // the relay hash is nil when the peer was online, preventing spurious relay use).
        guard let hash = peerRelayIdHash, !hash.isEmpty else {
            NetLogger.info("Relay", "skip store msgId=\(messageId) — peer online or has no relay_id_hash; message queued locally only")
            return
        }
        retryRelayStore(messageId: messageId, peerRelayIdHash: hash)
    }

    // MARK: - Cloud relay outbox retry

    /// Attempts (or retries) uploading a queued message to the cloud relay
    /// Worker. Re-encrypts fresh on every call — a new nonce per attempt,
    /// same pattern as deliverPending's direct-TCP retries — since the
    /// pending queue only persists plaintext. Backed off per-message so a
    /// persistently unreachable Worker doesn't get hammered; the in-flight
    /// guard prevents a concurrent duplicate attempt for the same message.
    /// The relay poll timer calls this for every not-yet-confirmed pending
    /// message (see AppModel.retryRelayOutbox) so a store that failed on
    /// the first attempt — transient network blip, Worker cold start, a
    /// full inbox — eventually gets through instead of being lost forever.
    func retryRelayStore(messageId: String, peerRelayIdHash: String) {
        guard !relayOutboxInFlight.contains(messageId) else { return }
        if let last = relayOutboxLastTry[messageId], Date().timeIntervalSince(last) < pendingRetryInterval { return }
        guard let msg = ConfigStore.shared.config.pendingMessages.first(where: { $0.messageId == messageId }),
              !msg.relayStored else { return }

        relayOutboxInFlight.insert(messageId)
        relayOutboxLastTry[messageId] = Date()

        let aad = Data(messageId.utf8)
        guard let (nonceB64, ctB64) = try? SessionCrypto.encryptForPeer(
            myPrivate: KeyManager.shared.privateKey,
            peerPublicKeyB64: msg.peerPublicKeyB64,
            plaintext: Data(msg.text.utf8),
            aad: aad
        ) else {
            relayOutboxInFlight.remove(messageId)
            return
        }

        NetLogger.info("Relay", "store msgId=\(messageId) peer=\(msg.peerPublicKeyB64.prefix(8)) — uploading to cloud relay mailbox")
        Task { [weak self] in
            guard let self else { return }
            let confirmed = await RelayClient.shared.store(
                peerRelayIdHash: peerRelayIdHash,
                messageId: messageId,
                ciphertextB64: ctB64,
                nonceB64: nonceB64,
                timestamp: msg.timestamp
            )
            self.relayOutboxInFlight.remove(messageId)
            guard confirmed else {
                NetLogger.warn("Relay", "store msgId=\(messageId) not confirmed by Worker — will retry")
                return
            }
            self.relayOutboxLastTry.removeValue(forKey: messageId)
            self.markRelayStored(messageId: messageId)
        }
    }

    /// Called once the Worker has confirmed a store. Flips the persisted
    /// `relayStored` flag (so the outbox retry loop stops retrying it),
    /// marks the history entry, and notifies the UI immediately.
    private func markRelayStored(messageId: String) {
        if let idx = ConfigStore.shared.config.pendingMessages.firstIndex(where: { $0.messageId == messageId }) {
            ConfigStore.shared.config.pendingMessages[idx].relayStored = true
            ConfigStore.shared.save()
        }
        HistoryStore.shared.markRelayDelivery(messageId: messageId)
        HistoryStore.shared.save()
        onDeliveryPathUpdate?(messageId)
    }

    // MARK: - Handle relay-delivered messages (from cloud Worker)

    /// Decrypts and processes a message that arrived via the cloud relay.
    /// The ciphertext was produced by the sender and is decoded here exactly
    /// like a normal LAN text packet. Call from AppModel after fetchPending().
    /// `ip` is only where to send the delivery receipt — the live address of
    /// the sender if they are on the LAN, empty if not. The message is filed
    /// under the sender's key, which decrypting it proves.
    func handleRelayMessage(_ msg: RelayPendingMessage, replyAddress ip: String) {
        // Global dedup: the same message may already have arrived over the LAN,
        // and history written before conversations were filed by key can hold
        // it under an address-named bucket this key's thread never reads. If we
        // already have it, just clean up the mailbox so it doesn't linger for
        // the full TTL.
        if HistoryStore.shared.containsMessageId(msg.messageId) {
            NetLogger.info("Relay", "duplicate relay msg \(msg.messageId) — already in history, deleting from mailbox only")
            Task { await RelayClient.shared.delete(messageId: msg.messageId) }
            return
        }

        let aad = Data(msg.messageId.utf8)
        guard let plaintext = try? SessionCrypto.decryptFromPeer(
            myPrivate: KeyManager.shared.privateKey,
            peerPublicKeyB64: msg.senderPublicKeyB64,
            nonceB64: msg.nonceB64,
            ciphertextB64: msg.ciphertextB64,
            aad: aad
        ) else {
            NetLogger.warn("Relay", "failed to decrypt relay message \(msg.messageId)")
            return
        }
        let text = String(data: plaintext, encoding: .utf8) ?? ""

        // A control record carries an edit or delete the sender made while we
        // were offline. It is never a chat message and must not become a bubble.
        let peer = msg.senderPublicKeyB64
        if let envelope = RelayControlEnvelope.decode(text) {
            applyRelayControl(envelope, peer: peer)
            // Applied or refused, the record is spent: leaving it would replay
            // on every poll until the Worker's 72-hour TTL expires it.
            Task { await RelayClient.shared.delete(messageId: msg.messageId) }
            return
        }

        let entry = MessageEntry(
            sender: msg.senderUsername,
            text: text,
            incoming: true,
            timestamp: msg.timestamp,
            messageId: msg.messageId,
            status: "",
            readReceiptSent: false,
            deliveryPath: "relay"
        )
        HistoryStore.shared.append(entry: entry, forPeer: peer)
        HistoryStore.shared.save()
        onMessageReceived?(peer, entry)
        NetLogger.info("Relay", "delivered relay msg \(msg.messageId) from \(msg.senderUsername) peer=\(peer.prefix(8))")

        // Send sent_receipt so the sender sees "Delivered" for their relayed
        // message — only when they are on the LAN right now. There is no
        // longer a placeholder address to guard against: an empty one means
        // "not here".
        if !ip.isEmpty {
            sendReceipt(type: "sent_receipt", messageId: msg.messageId, toPeerIP: ip)
        }

        // Delete from relay now that we've processed it (best-effort)
        Task {
            await RelayClient.shared.delete(messageId: msg.messageId)
        }
    }

    private func sendJSON(_ dict: [String: Any], toIP ip: String, port: Int, completion: ((Bool) -> Void)?) {
        guard let frame = try? FrameCodec.encodeDict(dict) else { completion?(false); return }
        // An empty address is a peer that is not on the LAN, and must fail
        // rather than reach `inet_addr("")` — which is 0.0.0.0, and connecting
        // to that reaches this machine's own listener.
        guard !ip.isEmpty else {
            DispatchQueue.main.async { completion?(false) }
            return
        }
        DispatchQueue.global(qos: .utility).async {
            let success = self.fireTCP(frame: frame, toIP: ip, port: port)
            DispatchQueue.main.async { completion?(success) }
        }
    }

    nonisolated private func fireTCP(frame: Data, toIP: String, port: Int) -> Bool {
        let fd = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP)
        guard fd >= 0 else { return false }
        defer { Darwin.close(fd) }

        // 5-second send timeout — if a send stalls the background thread
        // returns promptly so the message can be queued for later delivery.
        var tv = timeval(tv_sec: 5, tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))

        var addr = sockaddr_in()
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = UInt16(port).bigEndian
        addr.sin_addr.s_addr = inet_addr(toIP)

        // Non-blocking connect with a 5-second poll timeout.
        // Darwin.connect() without a timeout can block for up to ~75 s when
        // the peer is offline — that's long enough for a user to close the
        // app before the message gets queued, losing it permanently.
        let origFlags = fcntl(fd, F_GETFL, 0)
        _ = fcntl(fd, F_SETFL, origFlags | O_NONBLOCK)
        let connectResult = withUnsafePointer(to: &addr) { ptr in
            ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        if connectResult != 0 {
            guard errno == EINPROGRESS else { return false }
            var pfd = pollfd()
            pfd.fd     = fd
            pfd.events = Int16(POLLOUT)
            guard Darwin.poll(&pfd, 1, 5_000) > 0 else { return false }
            var sockErr: Int32 = 0
            var errLen = socklen_t(MemoryLayout<Int32>.size)
            getsockopt(fd, SOL_SOCKET, SO_ERROR, &sockErr, &errLen)
            guard sockErr == 0 else { return false }
        }
        _ = fcntl(fd, F_SETFL, origFlags)  // restore blocking mode for send

        var sent = 0
        while sent < frame.count {
            let n = frame.withUnsafeBytes { ptr in
                Darwin.send(fd, ptr.baseAddress!.advanced(by: sent), frame.count - sent, 0)
            }
            if n <= 0 { return false }
            sent += n
        }
        return true
    }
}
