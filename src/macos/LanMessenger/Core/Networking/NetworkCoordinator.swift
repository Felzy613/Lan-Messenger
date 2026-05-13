import Foundation

// Owns all PeerSessions and the DiscoveryService.
// Routes validated packets to MessagingService / FileTransferService via the delegate.
// Manages the TCP listener for inbound connections from peers.
//
// All delegate callbacks fire on the main queue.

protocol NetworkCoordinatorDelegate: AnyObject {
    @MainActor func coordinator(_ c: NetworkCoordinator, didReceivePacket packet: ValidatedPacket)
    @MainActor func coordinator(_ c: NetworkCoordinator, didDiscoverPeer packet: DiscoveryPacket, fromIP: String)
}

final class NetworkCoordinator: NSObject {

    weak var delegate: NetworkCoordinatorDelegate?

    let discovery = DiscoveryService()
    private var sessions: [String: PeerSession] = [:]   // keyed by peerIP
    private var listenerSocket: Int32 = -1
    private let listenerQueue = DispatchQueue(label: "com.dave.lanmessenger.listener", qos: .utility)
    private let tcpPort: UInt16 = 54232
    private var running = false

    private var ownPublicKeyB64: String { KeyManager.shared.publicKeyB64 }

    func start(username: String, localIPs: Set<String>) {
        guard !running else { return }
        running = true

        // Configure discovery
        discovery.ownPublicKeyB64 = ownPublicKeyB64
        discovery.ownIPs = localIPs
        discovery.delegate = self
        discovery.buildPayload = { [weak self] in
            DiscoveryPacket(
                type: "discovery",
                username: username,
                port: 54232,
                publicKeyB64: self?.ownPublicKeyB64 ?? "",
                ips: Array(localIPs)
            )
        }
        discovery.extraTargets = { [weak self] in
            self?.sessions.keys.map { $0 } ?? []
        }
        discovery.start()

        startTCPListener()
    }

    func stop() {
        running = false
        discovery.stop()
        sessions.values.forEach { $0.stop() }
        sessions.removeAll()
        if listenerSocket >= 0 { Darwin.close(listenerSocket); listenerSocket = -1 }
    }

    // Send a pre-encoded frame to a peer by IP. Opens a one-shot connection if needed.
    func send(frame: Data, toIP: String, port: Int = 54232) {
        // Use existing session if available; otherwise fire-and-forget
        if let session = sessions[toIP] {
            session.send(frame)
        } else {
            // One-shot TCP for messages to known peers without a persistent session
            DispatchQueue.global(qos: .utility).async {
                guard let socket = self.openSocket(ip: toIP, port: port) else { return }
                defer { Darwin.close(socket) }
                _ = frame.withUnsafeBytes { ptr in
                    Darwin.send(socket, ptr.baseAddress, frame.count, 0)
                }
            }
        }
    }

    func send(frames: [Data], toIP: String, port: Int = 54232) {
        for frame in frames { send(frame: frame, toIP: toIP, port: port) }
    }

    // Ensure a persistent session exists for this peer.
    func ensureSession(ip: String, port: Int) {
        guard sessions[ip] == nil else { return }
        let session = PeerSession(ip: ip, port: port)
        session.ownPublicKeyB64 = ownPublicKeyB64
        session.onPacket = { [weak self] pkt in
            guard let self else { return }
            Task { @MainActor [weak self] in
                guard let self else { return }
                self.delegate?.coordinator(self, didReceivePacket: pkt)
            }
        }
        session.onDisconnect = { [weak self] s in
            self?.sessions.removeValue(forKey: s.peerIP)
        }
        sessions[ip] = session
        session.start()
    }

    // MARK: - TCP Listener (inbound connections)

    private func startTCPListener() {
        listenerSocket = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP)
        guard listenerSocket >= 0 else { return }

        var enable: Int32 = 1
        setsockopt(listenerSocket, SOL_SOCKET, SO_REUSEADDR, &enable, socklen_t(MemoryLayout<Int32>.size))

        var addr = sockaddr_in()
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = tcpPort.bigEndian
        addr.sin_addr.s_addr = INADDR_ANY
        withUnsafePointer(to: &addr) { ptr in
            ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockPtr in
                _ = Darwin.bind(listenerSocket, sockPtr, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        _ = listen(listenerSocket, 16)

        listenerQueue.async { [weak self] in
            while self?.running == true {
                var clientAddr = sockaddr_in()
                var addrLen = socklen_t(MemoryLayout<sockaddr_in>.size)
                let clientSocket = withUnsafeMutablePointer(to: &clientAddr) { ptr in
                    ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockPtr in
                        accept(self?.listenerSocket ?? -1, sockPtr, &addrLen)
                    }
                }
                guard clientSocket >= 0 else { continue }
                let ip = String(cString: inet_ntoa(clientAddr.sin_addr))
                self?.handleInbound(socket: clientSocket, fromIP: ip)
            }
        }
    }

    private func handleInbound(socket: Int32, fromIP: String) {
        DispatchQueue.global(qos: .utility).async { [weak self] in
            defer { Darwin.close(socket) }
            var cfRead: Unmanaged<CFReadStream>?
            var cfWrite: Unmanaged<CFWriteStream>?
            CFStreamCreatePairWithSocket(nil, socket, &cfRead, &cfWrite)
            guard let inputRef = cfRead?.takeRetainedValue() else { return }
            let input = inputRef as InputStream
            input.open()
            defer { input.close() }

            while true {
                guard let frameData = try? FrameCodec.readFrame(from: input),
                      let json = try? FrameCodec.parseJSON(from: frameData) else { break }
                let result = PacketValidator.validate(
                    json: json,
                    senderIP: fromIP,
                    ownPublicKeyB64: self?.ownPublicKeyB64 ?? ""
                )
                if case .success(let pkt) = result {
                    DispatchQueue.main.async { [weak self] in
                        guard let self else { return }
                        self.delegate?.coordinator(self, didReceivePacket: pkt)
                    }
                }
            }
        }
    }

    // MARK: - Helpers

    private func openSocket(ip: String, port: Int) -> Int32? {
        let fd = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP)
        guard fd >= 0 else { return nil }
        var addr = sockaddr_in()
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = UInt16(port).bigEndian
        addr.sin_addr.s_addr = inet_addr(ip)
        let result = withUnsafePointer(to: &addr) { ptr in
            ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockPtr in
                Darwin.connect(fd, sockPtr, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        if result != 0 { Darwin.close(fd); return nil }
        return fd
    }
}

// MARK: - DiscoveryServiceDelegate

extension NetworkCoordinator: DiscoveryServiceDelegate {
    func discoveryService(_ service: DiscoveryService, didDiscoverPeer packet: DiscoveryPacket, fromIP: String) {
        Task { @MainActor [weak self] in
            guard let self else { return }
            self.delegate?.coordinator(self, didDiscoverPeer: packet, fromIP: fromIP)
        }
    }
}
