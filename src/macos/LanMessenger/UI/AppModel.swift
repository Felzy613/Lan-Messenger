import Foundation
import SwiftUI

// Represents a discovered or saved peer.
struct PeerInfo: Identifiable {
    var id: String { publicKeyB64 }
    var ip: String
    var username: String
    var port: Int
    var publicKeyB64: String
    var lastSeen: Date
    var isOnline: Bool { Date().timeIntervalSince(lastSeen) < 20 }
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
    @Published var conversations: [ConversationViewModel] = []
    @Published var archivedConversations: [ConversationViewModel] = []
    @Published var selectedPeerIP: String?
    @Published var messages: [String: [MessageEntry]] = [:]          // keyed by peerIP
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

    // MARK: - Init

    init() {
        wireDelegates()
        start()
    }

    // MARK: - Start

    private func start() {
        coordinator.start(username: ConfigStore.shared.config.username)
        isLocalNetworkAvailable = coordinator.isLocalNetworkAvailable
        NotificationService.shared.requestAuthorization()
        removeOwnContact()
        loadHistory()
        startPeerTimeoutTimer()
        checkMigration()
        applyDockPolicy()
        scheduleAutoUpdateCheck()
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

    private func upsertPeer(ip: String, username: String, port: Int, publicKeyB64: String) {
        // Last-resort self-suppression — defends against stale `ownIPs` in
        // the discovery service when the machine's network interfaces change.
        if publicKeyB64.isEmpty || publicKeyB64 == KeyManager.shared.publicKeyB64 { return }
        if coordinator.network.localIPs.contains(ip) { return }

        // If we have a saved contact for this device ID whose IP has changed,
        // migrate the conversation history so the user doesn't lose context.
        if let idx = ConfigStore.shared.config.contacts.firstIndex(where: { $0.publicKeyB64 == publicKeyB64 }) {
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

        let info = PeerInfo(ip: ip, username: username, port: port, publicKeyB64: publicKeyB64, lastSeen: Date())
        peers[publicKeyB64] = info
        refreshConversations()
        // Deliver any queued messages and files for this peer.
        MessagingService.shared.deliverPending(toPeerIP: ip, peerPublicKeyB64: publicKeyB64)
        deliverPendingFiles(toPeerIP: ip, peerPublicKeyB64: publicKeyB64)
    }

    private func startPeerTimeoutTimer() {
        // NetworkInterfaceMonitor (owned by the coordinator) keeps ownIPs live
        // across DHCP/Wi-Fi/VPN transitions, so this timer no longer needs to
        // poke it on every tick.
        peerTimeoutTimer = Timer.scheduledTimer(withTimeInterval: 2.0, repeats: true) { [weak self] _ in
            Task { @MainActor [weak self] in
                guard let self else { return }
                let before = self.peers.count
                self.peers = self.peers.filter { $0.value.isOnline }
                if self.peers.count != before { self.refreshConversations() }
            }
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
    }

    private func lastMessagePreview(_ entries: [MessageEntry]) -> String {
        guard let last = entries.last else { return "" }
        if last.text.hasPrefix("__FILE__:") {
            let path = String(last.text.dropFirst("__FILE__:".count))
            return "📎 \(URL(fileURLWithPath: path).lastPathComponent)"
        }
        return last.text
    }

    private func touchPeer(publicKeyB64: String) {
        guard var info = peers[publicKeyB64] else { return }
        info.lastSeen = Date()
        peers[publicKeyB64] = info
    }

    // Trigger a manual UDP discovery broadcast.
    func scan() {
        coordinator.discovery.sendBeacon()
    }

    // MARK: - Messaging

    func sendMessage(_ text: String, toPeerIP ip: String, replyTo: MessageEntry? = nil) {
        // For offline peers, look up the public key from contacts so the message can still be queued.
        let publicKey: String? = peerByIP(ip)?.publicKeyB64
            ?? ConfigStore.shared.config.contacts.first(where: { $0.lastIP == ip })?.publicKeyB64
        guard let key = publicKey else { return }
        MessagingService.shared.sendText(text, toPeerIP: ip, peerPublicKeyB64: key, replyTo: replyTo)
    }

    func sendTyping(_ active: Bool, toPeerIP ip: String) {
        guard let peer = peerByIP(ip) else { return }
        MessagingService.shared.sendTyping(active: active, toPeerIP: ip, peerPublicKeyB64: peer.publicKeyB64)
    }

    func markConversationRead(peerIP: String) {
        guard var entries = messages[peerIP] else { return }
        var changed = false
        for i in entries.indices where entries[i].incoming && !entries[i].readReceiptSent {
            if let id = entries[i].messageId {
                MessagingService.shared.sendReceipt(type: "read_receipt", messageId: id, toPeerIP: peerIP)
                HistoryStore.shared.markReadReceiptSent(messageId: id, peerIP: peerIP)
            }
            entries[i].readReceiptSent = true
            changed = true
        }
        if changed {
            messages[peerIP] = entries
            HistoryStore.shared.save()
            refreshConversations()
        }
    }

    func sendReadReceipt(for entry: MessageEntry, peerIP: String) {
        // Kept for compatibility — delegates to markConversationRead.
        guard entry.incoming, !entry.readReceiptSent else { return }
        markConversationRead(peerIP: peerIP)
    }

    // Queue or send a file. If the peer is offline, the file path is persisted
    // and retried whenever the peer comes back online.
    func sendFile(path: String, toPeerIP ip: String) {
        let publicKey: String? = peerByIP(ip)?.publicKeyB64
            ?? ConfigStore.shared.config.contacts.first(where: { $0.lastIP == ip })?.publicKeyB64
        guard let key = publicKey else { return }

        if peerByIP(ip) != nil {
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
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
        for w in NSApp.windows where w.canBecomeMain && !(w is NSPanel) {
            w.makeKeyAndOrderFront(nil)
            return
        }
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
            if self.selectedPeerIP != ip {
                NotificationService.shared.showMessage(from: entry.sender, text: entry.text)
            }
        }
        MessagingService.shared.onStatusUpdate = { [weak self] ip, msgId, status in
            guard let self else { return }
            if var entries = self.messages[ip] {
                for i in entries.indices where entries[i].messageId == msgId { entries[i].status = status }
                self.messages[ip] = entries
            }
        }
        MessagingService.shared.onTypingUpdate = { [weak self] ip, sender, active in
            guard let self else { return }
            self.typingStates[ip] = (sender, active)
            self.refreshConversations()
        }

        FileTransferService.shared.onProgress = { [weak self] ip, label, bytes, total in
            self?.activeTransfers[ip] = (label, bytes, total)
        }
        FileTransferService.shared.onError = { [weak self] ip, _ in
            // Clear the in-progress banner so the UI doesn't stay stuck at 0%.
            self?.activeTransfers.removeValue(forKey: ip)
        }
        FileTransferService.shared.onComplete = { [weak self] ip, _, localURL in
            guard let self else { return }
            self.activeTransfers.removeValue(forKey: ip)
            guard let url = localURL else { return }   // receiver side — no outgoing bubble needed
            let entry = MessageEntry(
                sender: ConfigStore.shared.config.username,
                text: "__FILE__:\(url.path)",
                incoming: false,
                timestamp: Date().timeIntervalSince1970,
                messageId: nil,
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
        FileTransferService.shared.onIncomingFile = { [weak self] ip, sender, url in
            guard let self else { return }
            NotificationService.shared.showFileReceived(from: sender, filename: url.lastPathComponent)
            // Prefix "__FILE__:" so MessageBubbleView can render a file bubble with an Open button.
            let entry = MessageEntry(
                sender: sender,
                text: "__FILE__:\(url.path)",
                incoming: true,
                timestamp: Date().timeIntervalSince1970,
                messageId: nil,
                status: "",
                readReceiptSent: false
            )
            HistoryStore.shared.append(entry: entry, forPeerIP: ip)
            HistoryStore.shared.save()
            var list = self.messages[ip] ?? []
            list.append(entry)
            self.messages[ip] = list
            self.refreshConversations()
        }
    }

    private func peerByIP(_ ip: String) -> PeerInfo? {
        peers.values.first { $0.ip == ip }
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
        switch packet {
        case .text, .typing, .receipt:
            MessagingService.shared.handlePacket(packet)
        case .fileStart, .fileChunk, .fileEnd:
            FileTransferService.shared.handlePacket(packet)
        case .discovery(let pkt, let ip):
            upsertPeer(ip: ip, username: pkt.username, port: pkt.port, publicKeyB64: pkt.publicKeyB64)
        }
    }

    func coordinator(_ c: NetworkCoordinator, didDiscoverPeer packet: DiscoveryPacket, fromIP ip: String) {
        upsertPeer(ip: ip, username: packet.username, port: packet.port, publicKeyB64: packet.publicKeyB64)
    }

    func coordinatorNetworkAvailabilityChanged(_ c: NetworkCoordinator) {
        let available = c.isLocalNetworkAvailable
        if isLocalNetworkAvailable != available { isLocalNetworkAvailable = available }
    }
}
