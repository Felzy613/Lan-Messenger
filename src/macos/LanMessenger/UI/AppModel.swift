import Foundation
import SwiftUI
import Network

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
    var lastMessage: String
    var lastTimestamp: Date?
    var unreadCount: Int
    var isTyping: Bool
    var typingSender: String
    var isOnline: Bool
}

// Root state object. Wires up all services and is the single source of truth for the UI.
@MainActor
final class AppModel: ObservableObject {

    // MARK: - Published UI state
    @Published var peers: [String: PeerInfo] = [:]                  // keyed by publicKeyB64
    @Published var conversations: [ConversationViewModel] = []
    @Published var selectedPeerIP: String?
    @Published var messages: [String: [MessageEntry]] = [:]          // keyed by peerIP
    @Published var typingStates: [String: (sender: String, active: Bool)] = [:]
    @Published var activeTransfers: [String: (label: String, bytes: Int64, total: Int64)] = [:]
    @Published var showMigrationPrompt = false
    @Published var pendingImportKeyData: Data? = nil

    // MARK: - Services
    let coordinator = NetworkCoordinator()

    private var peerTimeoutTimer: Timer?

    // MARK: - Init

    init() {
        wireDelegates()
        start()
    }

    // MARK: - Start

    private func start() {
        let localIPs = localIPAddresses()
        coordinator.start(username: ConfigStore.shared.config.username, localIPs: Set(localIPs))
        NotificationService.shared.requestAuthorization()
        loadHistory()
        startPeerTimeoutTimer()
        checkMigration()
        applyDockPolicy()
    }

    func applyDockPolicy() {
        NSApp.setActivationPolicy(ConfigStore.shared.config.hideFromDock ? .accessory : .regular)
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
        let info = PeerInfo(ip: ip, username: username, port: port, publicKeyB64: publicKeyB64, lastSeen: Date())
        peers[publicKeyB64] = info
        refreshConversations()
        // Deliver any queued messages for this peer
        MessagingService.shared.deliverPending(toPeerIP: ip, peerPublicKeyB64: publicKeyB64)
    }

    private func startPeerTimeoutTimer() {
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
        var result: [ConversationViewModel] = []

        // Online peers first
        var seenIPs = Set<String>()
        for (keyB64, peer) in peers {
            seenIPs.insert(peer.ip)
            let entries = messages[peer.ip] ?? []
            let last = entries.last
            let typing = typingStates[peer.ip]
            result.append(ConversationViewModel(
                peerIP: peer.ip,
                peerName: peer.username,
                peerPublicKeyB64: keyB64,
                lastMessage: last?.text ?? "",
                lastTimestamp: last.map { Date(timeIntervalSince1970: $0.timestamp) },
                unreadCount: entries.filter { $0.incoming && $0.status.isEmpty }.count,
                isTyping: typing?.active ?? false,
                typingSender: typing?.sender ?? "",
                isOnline: true
            ))
        }

        // Saved contacts that are currently offline
        for contact in ConfigStore.shared.config.contacts {
            guard !seenIPs.contains(contact.lastIP) else { continue }
            let entries = messages[contact.lastIP] ?? []
            result.append(ConversationViewModel(
                peerIP: contact.lastIP,
                peerName: contact.username,
                peerPublicKeyB64: contact.publicKeyB64,
                lastMessage: entries.last?.text ?? "",
                lastTimestamp: entries.last.map { Date(timeIntervalSince1970: $0.timestamp) },
                unreadCount: 0,
                isTyping: false,
                typingSender: "",
                isOnline: false
            ))
        }

        result.sort { ($0.lastTimestamp ?? .distantPast) > ($1.lastTimestamp ?? .distantPast) }
        conversations = result
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

    func sendMessage(_ text: String, toPeerIP ip: String) {
        guard let peer = peerByIP(ip) else { return }
        MessagingService.shared.sendText(text, toPeerIP: ip, peerPublicKeyB64: peer.publicKeyB64)
    }

    func sendTyping(_ active: Bool, toPeerIP ip: String) {
        guard let peer = peerByIP(ip) else { return }
        MessagingService.shared.sendTyping(active: active, toPeerIP: ip, peerPublicKeyB64: peer.publicKeyB64)
    }

    func sendReadReceipt(for entry: MessageEntry, peerIP: String) {
        guard entry.incoming, let messageId = entry.messageId, !entry.readReceiptSent else { return }
        HistoryStore.shared.markReadReceiptSent(messageId: messageId, peerIP: peerIP)
        MessagingService.shared.sendReceipt(type: "read_receipt", messageId: messageId, toPeerIP: peerIP)
    }

    func sendFile(path: String, toPeerIP ip: String) {
        guard let peer = peerByIP(ip) else { return }
        FileTransferService.shared.enqueue(filePath: path, toPeerIP: ip, peerPublicKeyB64: peer.publicKeyB64)
    }

    // MARK: - Private helpers

    private func loadHistory() {
        messages = HistoryStore.shared.history
    }

    private func wireDelegates() {
        coordinator.delegate = self

        MessagingService.shared.coordinator = coordinator
        MessagingService.shared.onMessageReceived = { [weak self] ip, entry in
            guard let self else { return }
            var list = self.messages[ip] ?? []
            list.append(entry)
            self.messages[ip] = list
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
                sender: "System",
                text: "__FILE__:\(url.path)",
                incoming: true,
                timestamp: Date().timeIntervalSince1970,
                messageId: nil,
                status: "",
                readReceiptSent: false
            )
            var list = self.messages[ip] ?? []
            list.append(entry)
            self.messages[ip] = list
            self.refreshConversations()
        }
    }

    private func peerByIP(_ ip: String) -> PeerInfo? {
        peers.values.first { $0.ip == ip }
    }

    private func localIPAddresses() -> [String] {
        var addresses: [String] = []
        var ifaddr: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&ifaddr) == 0 else { return [] }
        defer { freeifaddrs(ifaddr) }
        var ptr = ifaddr
        while let current = ptr {
            let flags = Int32(current.pointee.ifa_flags)
            guard (flags & IFF_UP) != 0, (flags & IFF_LOOPBACK) == 0,
                  current.pointee.ifa_addr.pointee.sa_family == UInt8(AF_INET) else {
                ptr = current.pointee.ifa_next; continue
            }
            var hostname = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            if getnameinfo(current.pointee.ifa_addr, socklen_t(current.pointee.ifa_addr.pointee.sa_len),
                           &hostname, socklen_t(hostname.count), nil, 0, NI_NUMERICHOST) == 0 {
                addresses.append(String(cString: hostname))
            }
            ptr = current.pointee.ifa_next
        }
        return addresses
    }
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
}
