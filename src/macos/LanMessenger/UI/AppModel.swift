import Foundation
import SwiftUI
import AppKit

// Represents a discovered or saved peer.
struct PeerInfo: Identifiable {
    var id: String { publicKeyB64 }
    var ip: String
    var username: String
    var port: Int
    var publicKeyB64: String
    var lastSeen: Date
    // Explicit, authoritative presence. Set by the presence evaluator, by
    // heartbeats (discovery/TCP), and by goodbye/network-loss — never inferred
    // from `lastSeen` at read time, so the UI updates reactively and macOS and
    // Windows agree. See PresenceEvaluator.
    var presence: PeerPresence = .online
    // Every IP this peer has advertised (from discovery `ips`), used as probe
    // targets so a multi-homed or roaming peer can still be reconfirmed.
    var knownIPs: [String] = []
    var isOnline: Bool { presence == .online }
}

// ViewModel for one conversation row in the sidebar.
struct ConversationViewModel: Identifiable {
    var id: String { peerIP }
    var peerIP: String
    var peerName: String
    var peerPublicKeyB64: String
    var photoB64: String?
    var lastMessage: String
    var lastTimestamp: Date?
    var unreadCount: Int
    var isTyping: Bool
    var typingSender: String
    var isOnline: Bool
    var isArchived: Bool
}

// Root state object. Wires up all services and is the single source of truth for the UI.
@MainActor
final class AppModel: ObservableObject {

    // MARK: - Published UI state
    @Published var peers: [String: PeerInfo] = [:]                  // keyed by publicKeyB64

    // Cache of ip → publicKeyB64 for every peer we've ever seen a packet from.
    // Persists across peer timeout/reconnect so we can reply to unsaved contacts
    // even if they've gone offline or aren't in the contacts list.
    private var knownPeerKeys: [String: String] = [:]
    @Published var conversations: [ConversationViewModel] = []
    @Published var archivedConversations: [ConversationViewModel] = []
    @Published var selectedPeerIP: String?
    @Published var messages: [String: [MessageEntry]] = [:]          // keyed by peerIP
    // In-memory only — not persisted to ConfigStore/disk. Lets the user switch
    // conversations without losing an in-progress, unsent draft.
    @Published var drafts: [String: String] = [:]
    @Published var typingStates: [String: (sender: String, active: Bool)] = [:]
    @Published var activeTransfers: [String: (label: String, bytes: Int64, total: Int64)] = [:]
    @Published var showMigrationPrompt = false
    @Published var pendingImportKeyData: Data? = nil
    @Published var availableUpdate: UpdateInfo? = nil
    @Published var updateProgress: UpdateProgress = .idle
    @Published var isLocalNetworkAvailable: Bool = true

    // MARK: - Services
    let coordinator = NetworkCoordinator()

    private var peerTimeoutTimer: Timer?
    private var updateCheckTimer: Timer?
    private var relayPollTimer: Timer?
    // Suppresses overlapping relay polls when the previous fetch is still inflight
    // (e.g. on slow networks). Cleared at end of fetchRelayMessages.
    private var relayFetchInFlight: Bool = false
    // Interval between routine relay polls. Short enough to feel snappy for
    // peers that left the LAN momentarily; long enough not to thrash the Worker.
    private let relayPollInterval: TimeInterval = 30

    // SHA256(relay_id) for each peer, populated from discovery packets.
    // Used to upload queued messages to the cloud relay mailbox of offline peers.
    private var peerRelayIdHashes: [String: String] = [:]   // keyed by peerPublicKeyB64

    // Lets the AppDelegate reach the live model from @MainActor lifecycle hooks
    // (e.g. applicationWillTerminate, which must send the goodbye synchronously
    // before the process exits — a Task hop would be dropped).
    static weak var shared: AppModel?

    // MARK: - Init

    init() {
        Self.shared = self
        wireDelegates()
        start()
    }

    // MARK: - Start

    private func start() {
        // First launch: replace the bare "User" default with the system's full
        // name so peers immediately see something meaningful instead of "User".
        if ConfigStore.shared.config.username == "User" {
            let fallback = NSFullUserName().trimmingCharacters(in: .whitespacesAndNewlines)
            if !fallback.isEmpty, fallback != "User" {
                ConfigStore.shared.config.username = fallback
                ConfigStore.shared.save()
            }
        }
        // Unicast beacon hints: last-known IPs of saved contacts. Reaches
        // contacts across subnets or on networks that filter broadcast/
        // multicast — the main reason saved contacts were slow to
        // (re)discover. Runs on the discovery queue; ConfigStore is a plain
        // (non-actor) singleton already read cross-thread elsewhere (e.g.
        // discovery.buildPayload), so no actor-isolation hop is needed here.
        coordinator.unicastHints = {
            var seen = Set<String>()
            var ips: [String] = []
            for contact in ConfigStore.shared.config.contacts {
                let ip = contact.lastIP
                guard !ip.isEmpty, !seen.contains(ip) else { continue }
                seen.insert(ip)
                ips.append(ip)
                if ips.count >= 32 { break }
            }
            return ips
        }

        coordinator.start()
        isLocalNetworkAvailable = coordinator.isLocalNetworkAvailable
        NotificationService.shared.requestAuthorization()
        removeOwnContact()
        loadHistory()
        startPeerTimeoutTimer()
        checkMigration()
        applyDockPolicy()
        reconcileLoginItem()
        scheduleAutoUpdateCheck()
        startRelayPolling()
        registerLifecycleObservers()
    }

    // Announce departure on sleep so peers flip us offline instantly instead of
    // waiting out the silence timeout; re-announce on wake. (Quit is handled in
    // the AppDelegate's applicationWillTerminate, which must run synchronously.)
    private func registerLifecycleObservers() {
        let wsnc = NSWorkspace.shared.notificationCenter
        wsnc.addObserver(forName: NSWorkspace.willSleepNotification, object: nil, queue: nil) { [weak self] _ in
            Task { @MainActor in self?.handleWillSleep() }
        }
        wsnc.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: nil) { [weak self] _ in
            Task { @MainActor in self?.scan() }
        }
    }

    private func handleWillSleep() {
        coordinator.sendGoodbye()
        markAllPeersOffline(reason: "sleep")
    }

    // Called from the AppDelegate at quit. Must stay synchronous.
    func sendGoodbyeOnTerminate() {
        coordinator.sendGoodbye()
    }

    // If the user previously asked for launch-at-login but the system's
    // SMAppService record was lost (typical after the app bundle is moved,
    // re-signed, or replaced by the updater), silently re-register so the
    // preference survives across upgrades.
    private func reconcileLoginItem() {
        guard ConfigStore.shared.config.launchAtLogin else { return }
        let status = LoginItemService.currentStatus
        if case .disabled = status {
            _ = LoginItemService.setEnabled(true)
        }
    }

    // Remove any saved contact whose public key matches our own.
    // This cleans up contacts that were accidentally added during testing.
    private func removeOwnContact() {
        let ownKey = KeyManager.shared.publicKeyB64
        let before = ConfigStore.shared.config.contacts.count
        ConfigStore.shared.config.contacts.removeAll { $0.publicKeyB64 == ownKey }
        if ConfigStore.shared.config.contacts.count != before {
            ConfigStore.shared.save()
        }
    }

    func applyDockPolicy() {
        let target: NSApplication.ActivationPolicy = ConfigStore.shared.config.hideFromDock ? .accessory : .regular
        guard NSApp.activationPolicy() != target else { return }
        NSApp.setActivationPolicy(target)
        if target == .regular {
            NSApp.activate(ignoringOtherApps: true)
            for w in NSApp.windows where w.canBecomeMain && !(w is NSPanel) {
                w.makeKeyAndOrderFront(nil)
                break
            }
        }
    }

    // MARK: - Migration

    private func checkMigration() {
        guard ConfigStore.shared.needsMigration else { return }
        let keyBytes = ConfigStore.shared.importPythonConfig()
        pendingImportKeyData = keyBytes
        showMigrationPrompt = true
    }

    func acceptMigrationWithExistingKey() {
        if let keyData = pendingImportKeyData {
            try? KeyManager.shared.importFromBase64(keyData.base64EncodedString())
        }
        showMigrationPrompt = false
        pendingImportKeyData = nil
        loadHistory()
    }

    func acceptMigrationWithFreshKey() {
        showMigrationPrompt = false
        pendingImportKeyData = nil
    }

    // MARK: - Peers

    private func upsertPeer(ip: String, username: String, port: Int, publicKeyB64: String, relayIdHash: String? = nil, advertisedIPs: [String] = []) {
        // Last-resort self-suppression — defends against stale `ownIPs` in
        // the discovery service when the machine's network interfaces change.
        if publicKeyB64.isEmpty || publicKeyB64 == KeyManager.shared.publicKeyB64 { return }
        if coordinator.network.localIPs.contains(ip) { return }

        // If we have a saved contact for this device ID whose IP has changed,
        // migrate the conversation history so the user doesn't lose context.
        if let idx = ConfigStore.shared.config.contacts.firstIndex(where: { $0.publicKeyB64 == publicKeyB64 }) {
            // Refresh the stored display name from the peer's current broadcast
            // when the peer hasn't been manually renamed locally. This makes
            // the sidebar reflect a peer who later set their name in Settings.
            let stored = ConfigStore.shared.config.contacts[idx].username
            let cleaned = username.trimmingCharacters(in: .whitespacesAndNewlines)
            if !cleaned.isEmpty, cleaned != "User", stored != cleaned,
               (stored.isEmpty || stored == "User" || stored == "Unknown") {
                ConfigStore.shared.config.contacts[idx].username = cleaned
                ConfigStore.shared.save()
            }
            let oldIP = ConfigStore.shared.config.contacts[idx].lastIP
            if oldIP != ip {
                if let oldMessages = messages.removeValue(forKey: oldIP) {
                    let merged = (messages[ip] ?? []) + oldMessages
                    messages[ip] = merged.sorted { $0.timestamp < $1.timestamp }
                }
                HistoryStore.shared.migrate(fromIP: oldIP, toIP: ip)
                HistoryStore.shared.save()
                ConfigStore.shared.config.contacts[idx].lastIP = ip
                if let archIdx = ConfigStore.shared.config.archivedConversations.firstIndex(of: oldIP) {
                    ConfigStore.shared.config.archivedConversations[archIdx] = ip
                }
                if let hidIdx = ConfigStore.shared.config.hiddenConversations.firstIndex(of: oldIP) {
                    ConfigStore.shared.config.hiddenConversations[hidIdx] = ip
                }
                ConfigStore.shared.save()
                if selectedPeerIP == oldIP { selectedPeerIP = ip }
            }
        }

        // A heartbeat (beacon or reply) means the peer is reachable now —
        // presence is online and the silence clock resets.
        var knownIPs = advertisedIPs
        if !knownIPs.contains(ip) { knownIPs.insert(ip, at: 0) }
        let info = PeerInfo(ip: ip, username: username, port: port, publicKeyB64: publicKeyB64,
                            lastSeen: Date(), presence: .online, knownIPs: knownIPs)
        peers[publicKeyB64] = info
        knownPeerKeys[ip] = publicKeyB64
        if let hash = relayIdHash, !hash.isEmpty {
            peerRelayIdHashes[publicKeyB64] = hash
            // Persist relay hash into the contact so it survives app restarts.
            // Without this, messages to offline peers skip the relay because
            // peerRelayIdHashes is only populated from live discovery packets.
            if let idx = ConfigStore.shared.config.contacts.firstIndex(where: { $0.publicKeyB64 == publicKeyB64 }),
               ConfigStore.shared.config.contacts[idx].relayIdHash != hash {
                ConfigStore.shared.config.contacts[idx].relayIdHash = hash
                ConfigStore.shared.save()
            }
        }

        migrateSyntheticRelayHistory(publicKeyB64: publicKeyB64, toIP: ip)
        refreshConversations()
        // Deliver any queued messages and files for this peer.
        MessagingService.shared.deliverPending(toPeerIP: ip, peerPublicKeyB64: publicKeyB64)
        deliverPendingFiles(toPeerIP: ip, peerPublicKeyB64: publicKeyB64)
    }

    // How long a non-contact peer may sit offline before it is dropped from the
    // dict. Contacts are kept indefinitely (their key is needed to queue/relay).
    private let nonContactPruneAfter: TimeInterval = 300

    // Drives the LAN presence state machine. Runs every second: re-evaluates
    // every peer from its lastSeen, actively probes the ones that have gone
    // quiet, flips presence on transitions, and prunes long-gone non-contacts.
    // Peers are NOT deleted the instant they go offline — presence is explicit,
    // so an offline contact stays in the dict (matching Windows) and the row
    // simply shows gray.
    private func startPeerTimeoutTimer() {
        peerTimeoutTimer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { [weak self] _ in
            Task { @MainActor [weak self] in self?.evaluatePresence() }
        }
    }

    private func evaluatePresence() {
        let now = Date()
        let contactKeys = Set(ConfigStore.shared.config.contacts.map { $0.publicKeyB64 })
        var changed = false
        var probeTargets: [String] = []
        // Mutate a copy, then assign once — never mutate `peers` while iterating it.
        var updated = peers

        for (key, info) in peers {
            let decision = PresenceEvaluator.decide(lastSeen: info.lastSeen, now: now)

            // Prune non-contact peers that have been gone a long time so the
            // dict can't grow without bound from one-off discovered devices.
            if decision == .offline,
               !contactKeys.contains(key),
               now.timeIntervalSince(info.lastSeen) > nonContactPruneAfter {
                updated.removeValue(forKey: key)
                changed = true
                continue
            }

            if decision.shouldProbe {
                // Reconfirm via unicast before declaring offline. Probe every
                // address the peer has advertised, not just the last one.
                probeTargets.append(contentsOf: info.knownIPs.isEmpty ? [info.ip] : info.knownIPs)
            }

            if info.presence != decision.presence {
                var copy = info
                copy.presence = decision.presence
                updated[key] = copy
                changed = true
            }
        }

        // Send probes outside the loop. Cheap unicast UDP; harmless if the peer
        // has actually left (a datagram to a dead IP is just dropped).
        for ip in Set(probeTargets) { coordinator.probe(ip: ip) }

        if changed {
            peers = updated
            refreshConversations()
        }
    }

    // MARK: - Conversations

    private func refreshConversations() {
        // Threads only exist for saved contacts (or peers we have history with) —
        // random discovered peers must not auto-appear as conversations.
        // `hiddenConversations` covers threads the user deleted; the underlying
        // contact stays saved so they can be re-opened via the "New message" picker.
        let archived = Set(ConfigStore.shared.config.archivedConversations)
        let hidden   = Set(ConfigStore.shared.config.hiddenConversations)
        var active: [ConversationViewModel] = []
        var archivedList: [ConversationViewModel] = []
        var seenIPs = Set<String>()

        // Saved contacts — include whether currently online or offline.
        for contact in ConfigStore.shared.config.contacts {
            let onlinePeer = peers.values.first { $0.publicKeyB64 == contact.publicKeyB64 && $0.isOnline }
            let ip = onlinePeer?.ip ?? contact.lastIP
            if hidden.contains(ip) || hidden.contains(contact.lastIP) { continue }
            seenIPs.insert(ip)
            let entries = messages[ip] ?? []
            let typing = typingStates[ip]
            let vm = ConversationViewModel(
                peerIP: ip,
                peerName: contact.username,
                peerPublicKeyB64: contact.publicKeyB64,
                photoB64: contact.photoB64,
                lastMessage: lastMessagePreview(entries),
                lastTimestamp: entries.last.map { Date(timeIntervalSince1970: $0.timestamp) },
                unreadCount: entries.filter { $0.incoming && !$0.readReceiptSent }.count,
                isTyping: typing?.active ?? false,
                typingSender: typing?.sender ?? "",
                isOnline: onlinePeer != nil,
                isArchived: archived.contains(ip)
            )
            if vm.isArchived { archivedList.append(vm) } else { active.append(vm) }
        }

        // Any IP we have history with but no contact entry —
        // e.g. someone messaged us once and isn't saved. Don't lose those.
        for (ip, entries) in messages {
            guard !seenIPs.contains(ip), !hidden.contains(ip), !entries.isEmpty else { continue }
            let name = entries.last { $0.incoming }?.sender ?? ip
            let onlinePeer = peers.values.first { $0.ip == ip && $0.isOnline }
            let vm = ConversationViewModel(
                peerIP: ip,
                peerName: name,
                peerPublicKeyB64: onlinePeer?.publicKeyB64 ?? "",
                photoB64: nil,
                lastMessage: lastMessagePreview(entries),
                lastTimestamp: entries.last.map { Date(timeIntervalSince1970: $0.timestamp) },
                unreadCount: entries.filter { $0.incoming && !$0.readReceiptSent }.count,
                isTyping: false,
                typingSender: "",
                isOnline: onlinePeer != nil,
                isArchived: archived.contains(ip)
            )
            if vm.isArchived { archivedList.append(vm) } else { active.append(vm) }
        }

        active.sort { ($0.lastTimestamp ?? .distantPast) > ($1.lastTimestamp ?? .distantPast) }
        archivedList.sort { ($0.lastTimestamp ?? .distantPast) > ($1.lastTimestamp ?? .distantPast) }
        conversations = active
        archivedConversations = archivedList

        // Dock badge mirrors unread counts for visible (non-archived) conversations only —
        // archived threads are intentionally out of sight and shouldn't nag the dock icon.
        let totalUnread = active.reduce(0) { $0 + $1.unreadCount }
        NSApp.dockTile.badgeLabel = totalUnread > 0 ? "\(totalUnread)" : nil
    }

    private func lastMessagePreview(_ entries: [MessageEntry]) -> String {
        guard let last = entries.last else { return "" }
        if last.deleted {
            return "This message was deleted"
        }
        if last.text.hasPrefix("__FILE__:") {
            let path = String(last.text.dropFirst("__FILE__:".count))
            return "📎 \(URL(fileURLWithPath: path).lastPathComponent)"
        }
        return last.text
    }

    private func touchPeer(publicKeyB64: String) {
        guard var info = peers[publicKeyB64] else { return }
        let wasOffline = info.presence == .offline
        info.lastSeen = Date()
        info.presence = .online
        peers[publicKeyB64] = info
        // Any inbound TCP traffic proves the peer is back — surface it at once
        // rather than waiting for the next discovery beacon.
        if wasOffline { refreshConversations() }
    }

    // Trigger a manual UDP discovery broadcast.
    func scan() {
        coordinator.discovery.sendBeacon()
    }

    // MARK: - Messaging

    func sendMessage(_ text: String, toPeerIP ip: String, replyTo: MessageEntry? = nil) {
        // For offline peers, look up the public key from contacts or session cache
        // so the message can still be queued. knownPeerKeys covers unsaved contacts.
        let publicKey: String? = peerByIP(ip)?.publicKeyB64
            ?? ConfigStore.shared.config.contacts.first(where: { $0.lastIP == ip })?.publicKeyB64
            ?? knownPeerKeys[ip]
        guard let key = publicKey else { return }
        // Relay is ONLY used when the peer is confirmed offline. If the peer is
        // currently online and TCP fails, that is a transient error — queue locally
        // but do not upload to the cloud relay to avoid spurious relay deliveries.
        let peerIsOnline = peers.values.contains { $0.publicKeyB64 == key && $0.isOnline }
        // Fall back to the contact's persisted relay hash when the peer hasn't
        // been seen live in this session (peerRelayIdHashes is in-memory only).
        let relayHash: String? = peerIsOnline ? nil :
            (peerRelayIdHashes[key] ?? ConfigStore.shared.config.contacts.first(where: { $0.publicKeyB64 == key })?.relayIdHash)
        NetLogger.info("Send", "routing msgId for peer=\(key.prefix(8)) online=\(peerIsOnline) relay=\(relayHash != nil ? "yes" : "no")")
        MessagingService.shared.sendText(
            text,
            toPeerIP: ip,
            peerPublicKeyB64: key,
            peerRelayIdHash: relayHash,
            replyTo: replyTo
        )
    }

    func sendTyping(_ active: Bool, toPeerIP ip: String) {
        guard let peer = peerByIP(ip) else { return }
        MessagingService.shared.sendTyping(active: active, toPeerIP: ip, peerPublicKeyB64: peer.publicKeyB64)
    }

    func markConversationRead(peerIP: String) {
        guard var entries = messages[peerIP] else { return }
        var changed = false
        for i in entries.indices where entries[i].incoming && !entries[i].readReceiptSent {
            // Send read_receipt for any entry that has a stable ID (text messages
            // and file entries that carry a transfer_id as their messageId).
            if let id = entries[i].messageId {
                MessagingService.shared.sendReceipt(type: "read_receipt", messageId: id, toPeerIP: peerIP)
            }
            entries[i].readReceiptSent = true
            changed = true
        }
        if changed {
            messages[peerIP] = entries
            // Persist readReceiptSent for all entry types, including file entries
            // that have no messageId — markReadReceiptSent alone misses those.
            HistoryStore.shared.markAllIncomingRead(forPeerIP: peerIP)
            HistoryStore.shared.save()
            refreshConversations()
        }
    }

    func sendReadReceipt(for entry: MessageEntry, peerIP: String) {
        // Kept for compatibility — delegates to markConversationRead.
        guard entry.incoming, !entry.readReceiptSent else { return }
        markConversationRead(peerIP: peerIP)
    }

    // MARK: - Message deletion

    // "Delete for everyone" only applies to our own outgoing messages that have
    // a stable messageId. "Delete for me" removes the entry locally only and
    // never sends a packet.
    func deleteMessage(_ entry: MessageEntry, peerIP: String, forEveryone: Bool) {
        if forEveryone {
            guard !entry.incoming, let messageId = entry.messageId else { return }
            HistoryStore.shared.markDeleted(messageId: messageId, peerIP: peerIP)
            if var entries = messages[peerIP] {
                for i in entries.indices where entries[i].messageId == messageId {
                    entries[i].deleted = true
                    entries[i].text = ""
                    entries[i].replyToMessageId = nil
                    entries[i].replyToPreview = nil
                    entries[i].replyToSender = nil
                }
                messages[peerIP] = entries
            }
            MessagingService.shared.sendDeleteMessage(messageId: messageId, toPeerIP: peerIP)
            refreshConversations()
        } else {
            HistoryStore.shared.removeEntry(matching: entry, peerIP: peerIP)
            if var entries = messages[peerIP] {
                if let idx = entries.firstIndex(where: { MessageEntry.sameEntry($0, entry) }) {
                    entries.remove(at: idx)
                    messages[peerIP] = entries
                }
            }
            refreshConversations()
        }
    }

    // MARK: - Cloud relay

    /// Starts both an immediate relay fetch and a recurring poll. The poll keeps
    /// the inbox drained while the app is foregrounded so that peers who came
    /// back online (and uploaded queued messages to the Worker after we missed
    /// the LAN window) deliver promptly without requiring a restart.
    private func startRelayPolling() {
        fetchRelayMessages(reason: "startup")
        relayPollTimer?.invalidate()
        relayPollTimer = Timer.scheduledTimer(withTimeInterval: relayPollInterval, repeats: true) { [weak self] _ in
            Task { @MainActor [weak self] in
                self?.fetchRelayMessages(reason: "poll")
            }
        }
    }

    /// Fetches messages waiting in the cloud relay Worker mailbox and dispatches
    /// them through MessagingService. Silent no-op when the relay URL is empty.
    /// Logs the reason so the relay flow is auditable from client.log.
    private func fetchRelayMessages(reason: String) {
        guard !relayFetchInFlight else {
            NetLogger.info("Relay", "fetch skipped (\(reason)) — previous request still in flight")
            return
        }
        relayFetchInFlight = true
        NetLogger.info("Relay", "fetch start reason=\(reason)")
        Task { [weak self] in
            guard let self else { return }
            let msgs = await RelayClient.shared.fetchPending()
            await MainActor.run { [weak self] in
                guard let self else { return }
                self.relayFetchInFlight = false
                guard !msgs.isEmpty else {
                    NetLogger.info("Relay", "fetch done reason=\(reason) — no pending messages")
                    return
                }
                NetLogger.info("Relay", "fetch done reason=\(reason) — delivering \(msgs.count) message(s)")
                for msg in msgs {
                    // Map sender public key → best known peer IP
                    let ip: String = self.peers.values
                        .first(where: { $0.publicKeyB64 == msg.senderPublicKeyB64 })?.ip
                        ?? ConfigStore.shared.config.contacts
                            .first(where: { $0.publicKeyB64 == msg.senderPublicKeyB64 })?.lastIP
                        ?? self.knownPeerKeys.first(where: { $0.value == msg.senderPublicKeyB64 })?.key
                        ?? "relay-\(msg.senderPublicKeyB64.prefix(8))"
                    MessagingService.shared.handleRelayMessage(msg, fromStoredIP: ip)
                }
                self.refreshConversations()
            }
        }
    }

    // Queue or send a file. If the peer is offline, the file path is persisted
    // and retried whenever the peer comes back online.
    func sendFile(path: String, toPeerIP ip: String) {
        let publicKey: String? = peerByIP(ip)?.publicKeyB64
            ?? ConfigStore.shared.config.contacts.first(where: { $0.lastIP == ip })?.publicKeyB64
            ?? knownPeerKeys[ip]
        guard let key = publicKey else { return }

        // Stream immediately only when the peer is actually online. Offline peers
        // remain in the dict now (presence is explicit), so test isOnline rather
        // than mere existence — otherwise the file would skip the persisted queue.
        if peerByIP(ip)?.isOnline == true {
            FileTransferService.shared.enqueue(filePath: path, toPeerIP: ip, peerPublicKeyB64: key)
        } else {
            // Persist the pending file so it survives an app restart while the peer is offline.
            let username = ConfigStore.shared.config.contacts.first { $0.publicKeyB64 == key }?.username ?? "Unknown"
            let pending = PendingFileConfig(
                filePath: path,
                peerPublicKeyB64: key,
                peerUsername: username,
                timestamp: Date().timeIntervalSince1970
            )
            ConfigStore.shared.config.pendingFiles.append(pending)
            ConfigStore.shared.save()

            // Add an outgoing bubble so the user sees the queued file in the chat
            let entry = MessageEntry(
                sender: ConfigStore.shared.config.username,
                text: "__FILE__:\(path)",
                incoming: false,
                timestamp: Date().timeIntervalSince1970,
                messageId: nil,
                status: "Queued",
                readReceiptSent: false
            )
            HistoryStore.shared.append(entry: entry, forPeerIP: ip)
            HistoryStore.shared.save()
            var list = messages[ip] ?? []
            list.append(entry)
            messages[ip] = list
            refreshConversations()
        }
    }

    private func deliverPendingFiles(toPeerIP ip: String, peerPublicKeyB64: String) {
        // 1) Re-trigger any in-memory queue that stalled on an earlier failed attempt.
        FileTransferService.shared.retryQueue(toPeerIP: ip, peerPublicKeyB64: peerPublicKeyB64)

        // 2) Drain the persistent pending-file queue for this peer.
        var pending = ConfigStore.shared.config.pendingFiles
        let toDeliver = pending.filter { $0.peerPublicKeyB64 == peerPublicKeyB64 }
        guard !toDeliver.isEmpty else { return }

        for item in toDeliver {
            guard FileManager.default.fileExists(atPath: item.filePath) else { continue }
            FileTransferService.shared.enqueue(filePath: item.filePath, toPeerIP: ip, peerPublicKeyB64: peerPublicKeyB64)
        }

        pending.removeAll { $0.peerPublicKeyB64 == peerPublicKeyB64 }
        ConfigStore.shared.config.pendingFiles = pending
        ConfigStore.shared.save()
    }

    // MARK: - Conversation actions

    func archiveConversation(peerIP: String) {
        if !ConfigStore.shared.config.archivedConversations.contains(peerIP) {
            ConfigStore.shared.config.archivedConversations.append(peerIP)
            ConfigStore.shared.save()
        }
        if selectedPeerIP == peerIP { selectedPeerIP = nil }
        refreshConversations()
    }

    func unarchiveConversation(peerIP: String) {
        ConfigStore.shared.config.archivedConversations.removeAll { $0 == peerIP }
        ConfigStore.shared.save()
        refreshConversations()
    }

    // Deletes a conversation: removes message history and hides the thread from the
    // sidebar. The contact stays in the saved contacts list — re-open the thread
    // through the "New message" picker.
    func deleteConversation(peerIP: String) {
        messages.removeValue(forKey: peerIP)
        HistoryStore.shared.delete(peerIP: peerIP)
        HistoryStore.shared.save()
        if !ConfigStore.shared.config.hiddenConversations.contains(peerIP) {
            ConfigStore.shared.config.hiddenConversations.append(peerIP)
        }
        ConfigStore.shared.config.archivedConversations.removeAll { $0 == peerIP }
        ConfigStore.shared.save()
        if selectedPeerIP == peerIP { selectedPeerIP = nil }
        refreshConversations()
    }

    func deleteContact(publicKeyB64: String) {
        let removed = ConfigStore.shared.config.contacts.filter { $0.publicKeyB64 == publicKeyB64 }
        ConfigStore.shared.config.contacts.removeAll { $0.publicKeyB64 == publicKeyB64 }
        ConfigStore.shared.save()
        for c in removed { deleteConversation(peerIP: c.lastIP) }
    }

    // Used by the "New message" picker: unhides the contact's thread and selects it
    // so the user can start chatting.
    func startConversation(withContact publicKeyB64: String) {
        guard let contact = ConfigStore.shared.config.contacts.first(where: { $0.publicKeyB64 == publicKeyB64 }) else { return }
        let onlinePeer = peers.values.first { $0.publicKeyB64 == publicKeyB64 }
        let ip = onlinePeer?.ip ?? contact.lastIP
        ConfigStore.shared.config.hiddenConversations.removeAll { $0 == ip || $0 == contact.lastIP }
        ConfigStore.shared.save()
        refreshConversations()
        selectedPeerIP = ip
    }

    // Adds a discovered peer to the saved contacts list. Pass an optional custom name
    // (the WhatsApp-style "name your contact" prompt) — falls back to the peer's
    // self-advertised username if nil/empty.
    @discardableResult
    func addContact(_ peer: PeerInfo, customName: String? = nil) -> Bool {
        if ConfigStore.shared.config.contacts.contains(where: { $0.publicKeyB64 == peer.publicKeyB64 }) {
            return false
        }
        let name = customName?.trimmingCharacters(in: .whitespacesAndNewlines)
        let displayName = (name?.isEmpty == false ? name! : peer.username)
        ConfigStore.shared.config.contacts.append(ContactConfig(
            publicKeyB64: peer.publicKeyB64,
            username: displayName,
            lastIP: peer.ip
        ))
        ConfigStore.shared.save()
        refreshConversations()
        return true
    }

    func updateContact(publicKeyB64: String, username: String, photoB64: String?) {
        guard let idx = ConfigStore.shared.config.contacts.firstIndex(where: { $0.publicKeyB64 == publicKeyB64 }) else { return }
        ConfigStore.shared.config.contacts[idx].username = username
        ConfigStore.shared.config.contacts[idx].photoB64 = photoB64
        ConfigStore.shared.save()
        refreshConversations()
    }

    // Show the main window (used by the menu-bar tray).
    func showMainWindow() {
        WindowController.showMainWindow()
    }

    // MARK: - Updates

    private func scheduleAutoUpdateCheck() {
        // Check on launch (with a short delay so the UI is up first) and every 6 hours.
        DispatchQueue.main.asyncAfter(deadline: .now() + 5) { [weak self] in
            self?.checkForUpdates(silent: true)
        }
        updateCheckTimer = Timer.scheduledTimer(withTimeInterval: 6 * 60 * 60, repeats: true) { [weak self] _ in
            Task { @MainActor [weak self] in self?.checkForUpdates(silent: true) }
        }
    }

    func checkForUpdates(silent: Bool) {
        Task { @MainActor in
            let result = await UpdateService.shared.check(repo: ConfigStore.shared.config.updateRepo)
            ConfigStore.shared.config.lastUpdateCheck = Date().timeIntervalSince1970
            ConfigStore.shared.save()
            switch result {
            case .available(let info): availableUpdate = info
            case .upToDate:            availableUpdate = nil
            case .error:               break  // keep last-known state
            }
        }
    }

    func installUpdate() {
        guard let info = availableUpdate else { return }
        Task { @MainActor in
            updateProgress = .downloading(0)
            do {
                try await UpdateService.shared.downloadAndInstall(info: info) { progress in
                    Task { @MainActor in
                        // Service uses 0…0.9 for download, 0.9…1.0 for SHA256 verify.
                        self.updateProgress = progress < 0.9
                            ? .downloading(progress)
                            : .verifying
                    }
                }
                updateProgress = .installing
            } catch {
                updateProgress = .failed(error.localizedDescription)
            }
        }
    }

    // MARK: - Private helpers

    private func loadHistory() {
        messages = HistoryStore.shared.history
        // Critical: refresh conversation list after loading history so threads are
        // visible immediately, not only after the first discovery beacon arrives.
        refreshConversations()
    }

    private func wireDelegates() {
        coordinator.delegate = self

        MessagingService.shared.coordinator = coordinator
        MessagingService.shared.onMessageReceived = { [weak self] ip, entry in
            guard let self else { return }
            var list = self.messages[ip] ?? []
            list.append(entry)
            self.messages[ip] = list
            // Incoming message from a previously-deleted thread should resurface it.
            if ConfigStore.shared.config.hiddenConversations.contains(ip) {
                ConfigStore.shared.config.hiddenConversations.removeAll { $0 == ip }
                ConfigStore.shared.save()
            }
            self.refreshConversations()
            // Only suppress the notification if the window is actually visible
            // AND the user is already looking at this conversation. When the window
            // is closed/minimized, selectedPeerIP stays set to the last peer, so we
            // must not let it block notifications for that peer.
            let windowVisible = NSApp.windows.contains {
                $0.isVisible && $0.canBecomeMain && !($0 is NSPanel)
            }
            let isViewingConversation = windowVisible && self.selectedPeerIP == ip
            if entry.incoming && !isViewingConversation {
                NotificationService.shared.showMessage(from: entry.sender, text: entry.text)
            }
        }
        MessagingService.shared.onStatusUpdate = { [weak self] ip, msgId, status in
            guard let self else { return }
            if var entries = self.messages[ip] {
                // Update the specific message by its ID.
                for i in entries.indices where entries[i].messageId == msgId {
                    entries[i].status = status
                }
                // Heuristic: promote all outgoing file entries when any message in
                // this conversation gets a higher-ranked acknowledgement. This covers
                // both legacy entries (messageId == nil) and new entries where the
                // receiver hasn't yet sent an individual file receipt (e.g., still
                // running an older version of the app). The rank check ensures we
                // never downgrade a status that was already set by a direct receipt.
                let newRank = Self.statusRank(status)
                for i in entries.indices
                    where entries[i].text.hasPrefix("__FILE__:")
                    && !entries[i].incoming
                    && entries[i].messageId != msgId {
                    if newRank > Self.statusRank(entries[i].status) {
                        entries[i].status = status
                    }
                }
                self.messages[ip] = entries
            }
        }
        MessagingService.shared.onTypingUpdate = { [weak self] ip, sender, active in
            guard let self else { return }
            self.typingStates[ip] = (sender, active)
            self.refreshConversations()
        }
        MessagingService.shared.onMessageDeleted = { [weak self] ip, messageId in
            guard let self else { return }
            if var entries = self.messages[ip] {
                for i in entries.indices where entries[i].messageId == messageId {
                    entries[i].deleted = true
                    entries[i].text = ""
                    entries[i].replyToMessageId = nil
                    entries[i].replyToPreview = nil
                    entries[i].replyToSender = nil
                }
                self.messages[ip] = entries
            }
            self.refreshConversations()
        }

        FileTransferService.shared.onProgress = { [weak self] ip, label, bytes, total in
            self?.activeTransfers[ip] = (label, bytes, total)
        }
        FileTransferService.shared.onError = { [weak self] ip, _ in
            // Clear the in-progress banner so the UI doesn't stay stuck at 0%.
            self?.activeTransfers.removeValue(forKey: ip)
        }
        FileTransferService.shared.onComplete = { [weak self] ip, _, transferId, localURL in
            guard let self else { return }
            self.activeTransfers.removeValue(forKey: ip)
            guard let url = localURL else { return }   // receiver side — no outgoing bubble needed
            let entry = MessageEntry(
                sender: ConfigStore.shared.config.username,
                text: "__FILE__:\(url.path)",
                incoming: false,
                timestamp: Date().timeIntervalSince1970,
                messageId: transferId,   // stable ID enables receipt matching
                status: "Sent",
                readReceiptSent: false
            )
            HistoryStore.shared.append(entry: entry, forPeerIP: ip)
            HistoryStore.shared.save()
            var list = self.messages[ip] ?? []
            list.append(entry)
            self.messages[ip] = list
            self.refreshConversations()
        }
        FileTransferService.shared.onIncomingFile = { [weak self] ip, sender, transferId, url in
            guard let self else { return }
            NotificationService.shared.showFileReceived(from: sender, filename: url.lastPathComponent)
            // Prefix "__FILE__:" so MessageBubbleView can render a file bubble with an Open button.
            let entry = MessageEntry(
                sender: sender,
                text: "__FILE__:\(url.path)",
                incoming: true,
                timestamp: Date().timeIntervalSince1970,
                messageId: transferId,   // stable ID enables read-receipt matching
                status: "",
                readReceiptSent: false
            )
            HistoryStore.shared.append(entry: entry, forPeerIP: ip)
            HistoryStore.shared.save()
            var list = self.messages[ip] ?? []
            list.append(entry)
            self.messages[ip] = list
            self.refreshConversations()
            // Notify the sender that the file was delivered (→ two grey checks).
            MessagingService.shared.sendReceipt(type: "sent_receipt", messageId: transferId, toPeerIP: ip)
        }
    }

    private func peerByIP(_ ip: String) -> PeerInfo? {
        peers.values.first { $0.ip == ip }
    }

    // Migrates history stored under a synthetic "relay-{keyPrefix}" IP — created when
    // a relay message arrived from a peer we had never met on the LAN — to their real IP.
    private func migrateSyntheticRelayHistory(publicKeyB64: String, toIP: String) {
        let syntheticIP = "relay-\(publicKeyB64.prefix(8))"
        guard HistoryStore.shared.history[syntheticIP] != nil else { return }
        HistoryStore.shared.migrate(fromIP: syntheticIP, toIP: toIP)
        HistoryStore.shared.save()
        if let moved = messages.removeValue(forKey: syntheticIP) {
            let merged = (messages[toIP] ?? []) + moved
            messages[toIP] = merged.sorted { $0.timestamp < $1.timestamp }
        }
        NetLogger.info("Relay", "migrated synthetic-IP history from \(syntheticIP) → \(toIP)")
    }

    // Rank used to ensure status only moves forward (Queued → Sending → Sent → Delivered → Read).
    private static func statusRank(_ status: String) -> Int {
        switch status {
        case "Queued":    return 0
        case "Sending":   return 1
        case "Sent":      return 2
        case "Delivered": return 3
        case "Read":      return 4
        default:          return -1
        }
    }
}

// Update-related view-model state.
enum UpdateProgress: Equatable {
    case idle
    case downloading(Double)
    case verifying
    case installing
    case failed(String)
}

// MARK: - NetworkCoordinatorDelegate

extension AppModel: NetworkCoordinatorDelegate {
    func coordinator(_ c: NetworkCoordinator, didReceivePacket packet: ValidatedPacket) {
        // Refresh lastSeen for the sender so TCP activity keeps them online.
        if let key = packet.senderPublicKeyB64 { touchPeer(publicKeyB64: key) }
        // Cache ip → publicKeyB64 so replies work even for unsaved / offline contacts.
        if let key = packet.senderPublicKeyB64, !key.isEmpty {
            knownPeerKeys[packet.senderIP] = key
        }
        switch packet {
        case .text, .typing, .receipt, .delete:
            MessagingService.shared.handlePacket(packet)
        case .fileStart, .fileChunk, .fileEnd:
            FileTransferService.shared.handlePacket(packet)
        case .discovery(let pkt, let ip):
            upsertPeer(ip: ip, username: pkt.username, port: pkt.port,
                       publicKeyB64: pkt.publicKeyB64, advertisedIPs: pkt.ips)
        }
    }

    func coordinator(_ c: NetworkCoordinator, didDiscoverPeer packet: DiscoveryPacket, fromIP ip: String) {
        upsertPeer(
            ip: ip,
            username: packet.username,
            port: packet.port,
            publicKeyB64: packet.publicKeyB64,
            relayIdHash: packet.relayIdHash,
            advertisedIPs: packet.ips
        )
    }

    // A peer announced its departure (clean quit / sleep / network loss). Flip
    // it offline immediately and push lastSeen into the past so the next
    // presence tick agrees and won't bounce it back online.
    func coordinator(_ c: NetworkCoordinator, didReceiveGoodbyeFrom publicKeyB64: String, fromIP ip: String) {
        guard var info = peers[publicKeyB64] else { return }
        NetLogger.info("Net", "peer \(publicKeyB64.prefix(8)) said goodbye — marking offline")
        info.presence = .offline
        info.lastSeen = .distantPast
        peers[publicKeyB64] = info
        refreshConversations()
    }

    func coordinatorNetworkAvailabilityChanged(_ c: NetworkCoordinator) {
        let available = c.isLocalNetworkAvailable
        let wasAvailable = isLocalNetworkAvailable
        if isLocalNetworkAvailable != available { isLocalNetworkAvailable = available }
        if !available {
            // Our own LAN dropped — we can no longer see anyone, so don't keep
            // showing stale green dots. Beacons will revive real peers on return.
            markAllPeersOffline(reason: "network-down")
        }
        // When the LAN comes back after being offline, re-announce ourselves and
        // drain the relay mailbox immediately — the recipient may have been
        // unreachable while messages piled up on the Worker.
        if available, !wasAvailable {
            NetLogger.info("Net", "network became available — rescanning and fetching relay")
            scan()
            fetchRelayMessages(reason: "network-up")
        }
    }

    // Flip every known peer offline locally (we've lost the ability to observe
    // them). lastSeen is aged out so the presence tick stays in agreement.
    private func markAllPeersOffline(reason: String) {
        guard !peers.isEmpty else { return }
        NetLogger.info("Net", "marking all peers offline (\(reason))")
        for (key, var info) in peers where info.presence != .offline {
            info.presence = .offline
            info.lastSeen = .distantPast
            peers[key] = info
        }
        refreshConversations()
    }
}
