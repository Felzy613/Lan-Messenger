import Foundation
import Network
import Darwin

// UDP discovery on port 54231.
//
// Architecture: one shared receive socket bound to INADDR_ANY:54231 with the
// 239.255.42.99 multicast group joined explicitly per interface (so multi-homed
// Macs — VPN + Wi-Fi + Ethernet — actually receive multicast on the real LAN
// adapter, not just the OS default-route interface). For sending, one
// dedicated socket per interface is bound to that interface's local IP, with
// IP_MULTICAST_IF set so multicast and limited-broadcast (255.255.255.255)
// beacons leave the box via every interface, not just the one chosen by the
// routing table.
//
// On network change (Wi-Fi reconnect, sleep/resume, VPN toggle), the
// NetworkInterfaceMonitor fires onChange and this service tears down stale
// sockets and rebuilds the per-interface set.
//
// Wire-protocol invariants from PROTOCOL.md are preserved exactly: port 54231,
// multicast group 239.255.42.99, TTL 1, 1.5 s beacon interval, JSON shape.

protocol DiscoveryServiceDelegate: AnyObject {
    func discoveryService(_ service: DiscoveryService, didDiscoverPeer packet: DiscoveryPacket, fromIP: String)
}

final class DiscoveryService {

    weak var delegate: DiscoveryServiceDelegate?

    // Provide these before calling start()
    var buildPayload: (() -> DiscoveryPacket)?
    var extraTargets: (() -> [String])?     // contact + peer IPs for unicast hints
    var ownPublicKeyB64: String = ""

    private let monitor: NetworkInterfaceMonitor
    private let discoveryPort: UInt16 = 54231
    private let multicastGroup = "239.255.42.99"
    private let interval: TimeInterval = 1.5

    // Each beacon is emitted to three targets (subnet-bcast, multicast, limited-bcast) per
    // interface, so the receive socket sees 2–3 copies per peer per cycle. Suppress
    // duplicates within a window shorter than the 1.5 s beacon interval.
    // Safe to access without a lock: handleReceivedData is only ever called from
    // the serial `queue` dispatch loop.
    private var lastSeen: [String: Date] = [:]
    private let dedupWindow: TimeInterval = 1.2

    private var sendSockets: [String: Int32] = [:]   // keyed by interface localIP
    private var recvSocket: Int32 = -1
    private var sendTimer: DispatchSourceTimer?
    private let queue = DispatchQueue(label: "com.dave.lanmessenger.discovery", qos: .utility)
    private var running = false
    private let socketLock = NSLock()
    private var monitorObserverID: UUID?

    init(monitor: NetworkInterfaceMonitor) {
        self.monitor = monitor
    }

    var ownIPs: Set<String> { monitor.localIPs }

    func start() {
        guard !running else { return }
        running = true

        rebuildSockets(reason: "start")
        monitorObserverID = monitor.addObserver { [weak self] in self?.onInterfacesChanged() }

        startBeaconTimer()
        startReceiveLoop()

        NetLogger.info("Discovery",
            "started port=\(discoveryPort) group=\(multicastGroup) interval=\(Int(interval*1000))ms " +
            "interfaces=\(monitor.adapters.count)")
    }

    func stop() {
        running = false
        sendTimer?.cancel(); sendTimer = nil
        if let id = monitorObserverID { monitor.removeObserver(id); monitorObserverID = nil }
        teardownSockets()
        NetLogger.info("Discovery", "stopped")
    }

    // MARK: - Socket lifecycle

    private func onInterfacesChanged() {
        queue.async { [weak self] in self?.rebuildSockets(reason: "iface-change") }
    }

    private func rebuildSockets(reason: String) {
        socketLock.lock(); defer { socketLock.unlock() }
        guard running || reason == "start" else { return }
        teardownSocketsLocked()
        setupReceiveSocketLocked()
        setupSendSocketsLocked()
        NetLogger.info("Discovery",
            "sockets rebuilt (\(reason)): send=\(sendSockets.count) recv=\(recvSocket >= 0 ? 1 : 0)")
    }

    private func teardownSockets() {
        socketLock.lock(); defer { socketLock.unlock() }
        teardownSocketsLocked()
    }

    private func teardownSocketsLocked() {
        for (_, fd) in sendSockets { Darwin.close(fd) }
        sendSockets.removeAll()
        if recvSocket >= 0 { Darwin.close(recvSocket); recvSocket = -1 }
    }

    private func setupReceiveSocketLocked() {
        let fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        guard fd >= 0 else {
            NetLogger.error("Discovery", "recv socket() failed: \(String(cString: strerror(errno)))")
            return
        }

        var enable: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &enable, socklen_t(MemoryLayout<Int32>.size))
        setsockopt(fd, SOL_SOCKET, SO_REUSEPORT, &enable, socklen_t(MemoryLayout<Int32>.size))

        var addr = sockaddr_in()
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = discoveryPort.bigEndian
        addr.sin_addr.s_addr = INADDR_ANY
        let bindResult = withUnsafePointer(to: &addr) { ptr -> Int32 in
            ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockPtr in
                Darwin.bind(fd, sockPtr, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard bindResult == 0 else {
            NetLogger.error("Discovery", "recv bind() failed: \(String(cString: strerror(errno)))")
            Darwin.close(fd)
            return
        }

        // 1 s timeout so the receive loop can check `running` between packets.
        var tv = timeval(tv_sec: 1, tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))

        recvSocket = fd
        joinMulticastOnAllInterfacesLocked()
    }

    private func joinMulticastOnAllInterfacesLocked() {
        guard recvSocket >= 0 else { return }
        var joined = 0
        for adapter in monitor.adapters {
            var mreq = ip_mreq()
            mreq.imr_multiaddr.s_addr = inet_addr(multicastGroup)
            mreq.imr_interface.s_addr = inet_addr(adapter.localIP)
            let rc = setsockopt(recvSocket, IPPROTO_IP, IP_ADD_MEMBERSHIP,
                                &mreq, socklen_t(MemoryLayout<ip_mreq>.size))
            if rc == 0 {
                joined += 1
            } else {
                NetLogger.warn("Discovery",
                    "multicast join failed on \(adapter.localIP) (\(adapter.name)): \(String(cString: strerror(errno)))")
            }
        }
        NetLogger.info("Discovery", "multicast joined on \(joined)/\(monitor.adapters.count) interfaces")
    }

    private func setupSendSocketsLocked() {
        for adapter in monitor.adapters {
            let fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
            guard fd >= 0 else {
                NetLogger.warn("Discovery", "send socket() failed for \(adapter.localIP): \(String(cString: strerror(errno)))")
                continue
            }

            var enable: Int32 = 1
            setsockopt(fd, SOL_SOCKET, SO_BROADCAST, &enable, socklen_t(MemoryLayout<Int32>.size))

            var ttl: UInt8 = 1
            setsockopt(fd, IPPROTO_IP, IP_MULTICAST_TTL, &ttl, socklen_t(MemoryLayout<UInt8>.size))

            // Force multicast output to use THIS adapter. Without this,
            // macOS picks the default-route interface and peers on the real
            // LAN segment never see the beacon.
            var ifaceAddr = in_addr(s_addr: inet_addr(adapter.localIP))
            setsockopt(fd, IPPROTO_IP, IP_MULTICAST_IF, &ifaceAddr, socklen_t(MemoryLayout<in_addr>.size))

            // Bind to the adapter's IP so limited-broadcast and unicast packets
            // exit through this interface.
            var bindAddr = sockaddr_in()
            bindAddr.sin_family = sa_family_t(AF_INET)
            bindAddr.sin_port = 0
            bindAddr.sin_addr.s_addr = inet_addr(adapter.localIP)
            let bindResult = withUnsafePointer(to: &bindAddr) { ptr -> Int32 in
                ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockPtr in
                    Darwin.bind(fd, sockPtr, socklen_t(MemoryLayout<sockaddr_in>.size))
                }
            }
            if bindResult != 0 {
                NetLogger.warn("Discovery", "send bind() failed for \(adapter.localIP): \(String(cString: strerror(errno)))")
                Darwin.close(fd)
                continue
            }

            sendSockets[adapter.localIP] = fd
            NetLogger.info("Discovery",
                "send socket bound on \(adapter.localIP) mask=\(adapter.subnetMask) bcast=\(adapter.broadcastAddress) (\(adapter.name))")
        }
    }

    // MARK: - Beacon

    private func startBeaconTimer() {
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now(), repeating: interval)
        timer.setEventHandler { [weak self] in self?.sendBeacon() }
        timer.resume()
        sendTimer = timer
    }

    func sendBeacon() {
        guard running, let payload = buildPayload?() else { return }
        guard let data = try? JSONEncoder().encode(payload) else { return }
        let extras = extraTargets?() ?? []

        socketLock.lock(); defer { socketLock.unlock() }
        guard !sendSockets.isEmpty else { return }

        for (localIP, fd) in sendSockets {
            let adapter = monitor.adapters.first { $0.localIP == localIP }
            if let bcast = adapter?.broadcastAddress {
                sendUDPLocked(data: data, fd: fd, toIP: bcast, port: discoveryPort, label: "subnet-bcast")
            }
            sendUDPLocked(data: data, fd: fd, toIP: multicastGroup, port: discoveryPort, label: "multicast")
            sendUDPLocked(data: data, fd: fd, toIP: "255.255.255.255", port: discoveryPort, label: "limited-bcast")
            for target in extras {
                sendUDPLocked(data: data, fd: fd, toIP: target, port: discoveryPort, label: "unicast")
            }
        }
    }

    // Used by NetworkCoordinator for one-off unicast discovery replies.
    func sendUDP(data: Data, toIP: String, port: UInt16) {
        socketLock.lock(); defer { socketLock.unlock() }
        guard let fd = pickSocketForTargetLocked(toIP) else {
            NetLogger.warn("Discovery", "sendUDP: no send socket available for \(toIP)")
            return
        }
        sendUDPLocked(data: data, fd: fd, toIP: toIP, port: port, label: "reply")
    }

    private func pickSocketForTargetLocked(_ toIP: String) -> Int32? {
        guard !sendSockets.isEmpty else { return nil }
        // Prefer the socket on the same subnet as the target.
        let destParts = toIP.split(separator: ".").compactMap { UInt8($0) }
        if destParts.count == 4 {
            for adapter in monitor.adapters {
                let ipParts = adapter.localIP.split(separator: ".").compactMap { UInt8($0) }
                let mkParts = adapter.subnetMask.split(separator: ".").compactMap { UInt8($0) }
                guard ipParts.count == 4, mkParts.count == 4 else { continue }
                var sameSubnet = true
                for i in 0..<4 where (ipParts[i] & mkParts[i]) != (destParts[i] & mkParts[i]) {
                    sameSubnet = false; break
                }
                if sameSubnet, let fd = sendSockets[adapter.localIP] { return fd }
            }
        }
        return sendSockets.values.first
    }

    private func sendUDPLocked(data: Data, fd: Int32, toIP: String, port: UInt16, label: String) {
        var addr = sockaddr_in()
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = port.bigEndian
        addr.sin_addr.s_addr = inet_addr(toIP)
        if addr.sin_addr.s_addr == INADDR_NONE {
            NetLogger.info("Discovery", "send \(label): bad target IP '\(toIP)'")
            return
        }
        let sent = data.withUnsafeBytes { ptr -> Int in
            withUnsafePointer(to: &addr) { addrPtr in
                addrPtr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockPtr in
                    Darwin.sendto(fd, ptr.baseAddress, data.count, 0, sockPtr, socklen_t(MemoryLayout<sockaddr_in>.size))
                }
            }
        }
        if sent < 0 {
            NetLogger.info("Discovery", "send \(label) to \(toIP):\(port) failed: \(String(cString: strerror(errno)))")
        }
    }

    // MARK: - Receive loop

    private func startReceiveLoop() {
        queue.async { [weak self] in
            var buffer = [UInt8](repeating: 0, count: 8192)
            while let strong = self, strong.running {
                var addr = sockaddr_in()
                var addrLen = socklen_t(MemoryLayout<sockaddr_in>.size)

                strong.socketLock.lock()
                let fd = strong.recvSocket
                strong.socketLock.unlock()
                if fd < 0 {
                    Thread.sleep(forTimeInterval: 0.2)
                    continue
                }

                let n = withUnsafeMutablePointer(to: &addr) { addrPtr -> Int in
                    addrPtr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockPtr in
                        recvfrom(fd, &buffer, buffer.count, 0, sockPtr, &addrLen)
                    }
                }
                if n <= 0 {
                    // Timeout (EAGAIN/EWOULDBLOCK) or socket closed mid-rebuild —
                    // keep looping so the next iteration picks up the new fd.
                    continue
                }
                let data = Data(buffer[..<n])
                let sourceIP = String(cString: inet_ntoa(addr.sin_addr))
                strong.handleReceivedData(data, fromIP: sourceIP)
            }
        }
    }

    private func handleReceivedData(_ data: Data, fromIP: String) {
        guard let pkt = PacketValidator.validateDiscovery(
            data: data,
            senderIP: fromIP,
            ownPublicKeyB64: ownPublicKeyB64,
            ownIPs: ownIPs
        ) else { return }

        // Suppress duplicate copies of the same beacon (we send to three targets per
        // interface so the receive socket sees each peer's beacon 2-3 times per cycle).
        let dedupKey = "\(pkt.publicKeyB64):\(pkt.type)"
        let now = Date()
        if let last = lastSeen[dedupKey], now.timeIntervalSince(last) < dedupWindow {
            return
        }
        lastSeen[dedupKey] = now

        // Reply to "discovery" packets (not to "discovery_reply" — that would
        // create an infinite ping-pong).
        if pkt.type == "discovery", let replyPayload = buildPayload?() {
            if let replyData = try? JSONEncoder().encode(
                DiscoveryPacket(
                    type: "discovery_reply",
                    username: replyPayload.username,
                    port: replyPayload.port,
                    publicKeyB64: replyPayload.publicKeyB64,
                    ips: replyPayload.ips
                )
            ) {
                sendUDP(data: replyData, toIP: fromIP, port: discoveryPort)
            }
        }

        NetLogger.info("Discovery", "rx \(pkt.type) from \(fromIP) user='\(pkt.username)' port=\(pkt.port)")
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            self.delegate?.discoveryService(self, didDiscoverPeer: pkt, fromIP: fromIP)
        }
    }
}
