import Foundation

// Owns all PeerSessions, the DiscoveryService, and the NetworkInterfaceMonitor.
// Routes validated packets to MessagingService / FileTransferService via the
// delegate. Manages the TCP listener for inbound connections from peers.
//
// All delegate callbacks fire on the main queue.

protocol NetworkCoordinatorDelegate: AnyObject {
    @MainActor func coordinator(_ c: NetworkCoordinator, didReceivePacket packet: ValidatedPacket)
    @MainActor func coordinator(_ c: NetworkCoordinator, didDiscoverPeer packet: DiscoveryPacket, fromIP: String)
    @MainActor func coordinatorNetworkAvailabilityChanged(_ c: NetworkCoordinator)
}

// Default no-op for the availability hook so existing implementers don't need to add it.
extension NetworkCoordinatorDelegate {
    @MainActor func coordinatorNetworkAvailabilityChanged(_ c: NetworkCoordinator) {}
}

final class NetworkCoordinator: NSObject {

    weak var delegate: NetworkCoordinatorDelegate?

    let network: NetworkInterfaceMonitor
    let discovery: DiscoveryService

    private var sessions: [String: PeerSession] = [:]   // keyed by peerIP
    private var listenerSocket: Int32 = -1
    private let listenerQueue = DispatchQueue(label: "com.dave.lanmessenger.listener", qos: .utility)
    private let tcpPort: UInt16 = 54232
    private var running = false
    private var monitorObserverID: UUID?

    var isLocalNetworkAvailable: Bool { network.isLocalNetworkAvailable }
    var localIPs: Set<String> { network.localIPs }

    private var ownPublicKeyB64: String { KeyManager.shared.publicKeyB64 }

    override init() {
        let monitor = NetworkInterfaceMonitor()
        self.network = monitor
        self.discovery = DiscoveryService(monitor: monitor)
        super.init()
    }

    func start() {
        guard !running else { return }
        running = true

        // Monitor must be running BEFORE discovery so the first beacon sees a
        // populated interface set.
        network.start()
        monitorObserverID = network.addObserver { [weak self] in
            Task { @MainActor [weak self] in
                guard let self else { return }
                self.delegate?.coordinatorNetworkAvailabilityChanged(self)
            }
        }

        discovery.ownPublicKeyB64 = ownPublicKeyB64
        discovery.delegate = self
        // Read the username fresh on every beacon so changes in Settings
        // propagate without restarting the network stack.
        discovery.buildPayload = { [weak self] in
            DiscoveryPacket(
                type: "discovery",
                username: ConfigStore.shared.config.username,
                port: 54232,
                publicKeyB64: self?.ownPublicKeyB64 ?? "",
                ips: self?.network.localIPs.map { $0 } ?? []
            )
        }
        discovery.extraTargets = { [weak self] in
            self?.sessions.keys.map { $0 } ?? []
        }
        discovery.start()

        startTCPListener()

        NetLogger.info("Net",
            "coordinator started user='\(ConfigStore.shared.config.username)' tcp=\(tcpPort) udp=54231 " +
            "interfaces=\(network.adapters.count) available=\(network.isLocalNetworkAvailable)")
    }

    func stop() {
        running = false
        discovery.stop()
        if let id = monitorObserverID { network.removeObserver(id); monitorObserverID = nil }
        network.stop()
        sessions.values.forEach { $0.stop() }
        sessions.removeAll()
        if listenerSocket >= 0 { Darwin.close(listenerSocket); listenerSocket = -1 }
    }

    // Send a pre-encoded frame to a peer by IP. Opens a one-shot connection if needed.
    func send(frame: Data, toIP: String, port: Int = 54232) {
        if let session = sessions[toIP] {
            session.send(frame)
        } else {
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
        guard listenerSocket >= 0 else {
            NetLogger.error("Net", "listener socket() failed: \(String(cString: strerror(errno)))")
            return
        }

        var enable: Int32 = 1
        setsockopt(listenerSocket, SOL_SOCKET, SO_REUSEADDR, &enable, socklen_t(MemoryLayout<Int32>.size))

        var addr = sockaddr_in()
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = tcpPort.bigEndian
        addr.sin_addr.s_addr = INADDR_ANY
        let bindResult = withUnsafePointer(to: &addr) { ptr -> Int32 in
            ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockPtr in
                Darwin.bind(listenerSocket, sockPtr, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        if bindResult != 0 {
            NetLogger.error("Net", "listener bind() failed: \(String(cString: strerror(errno)))")
        }
        _ = listen(listenerSocket, 16)
        NetLogger.info("Net", "TCP listener bound on 0.0.0.0:\(tcpPort)")

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

            // 30 s read timeout — kills stuck readers without losing fresh data
            var tv = timeval(tv_sec: 30, tv_usec: 0)
            setsockopt(socket, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))

            while true {
                guard let frameData = Self.recvFrame(socket: socket) else { break }
                guard let json = try? JSONSerialization.jsonObject(with: frameData) as? [String: Any] else { break }
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

    // Reads one length-prefixed frame directly from the socket using recv().
    // CFStream wrappers around accepted sockets have been unreliable in practice;
    // raw recv() with a socket-level read timeout works deterministically.
    private static func recvFrame(socket: Int32) -> Data? {
        var header = [UInt8](repeating: 0, count: 4)
        guard recvExact(socket: socket, buffer: &header, count: 4) else { return nil }
        let length = Int(UInt32(bigEndian: header.withUnsafeBytes { $0.load(as: UInt32.self) }))
        guard length > 0, length <= FrameCodec.maxFrameSize else { return nil }
        var body = [UInt8](repeating: 0, count: length)
        guard recvExact(socket: socket, buffer: &body, count: length) else { return nil }
        return Data(body)
    }

    private static func recvExact(socket: Int32, buffer: inout [UInt8], count: Int) -> Bool {
        var total = 0
        while total < count {
            let n = buffer.withUnsafeMutableBytes { ptr in
                Darwin.recv(socket, ptr.baseAddress!.advanced(by: total), count - total, 0)
            }
            if n <= 0 { return false }
            total += n
        }
        return true
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
