import Foundation
import Network

// Owns the UDP discovery socket.
// Sends a discovery beacon every 1.5 s to:
//   - 255.255.255.255:54231 (broadcast)
//   - 239.255.42.99:54231   (multicast)
//   - x.x.x.255:54231       (per-subnet broadcast)
//   - last-known contact IPs (unicast)
//   - current peer IPs      (unicast)
//
// On receiving a "discovery" packet from a remote host, immediately sends
// a "discovery_reply" back to {source_ip}:54231 via UDP.
//
// Thread safety: all callbacks are delivered on the main actor.

protocol DiscoveryServiceDelegate: AnyObject {
    func discoveryService(_ service: DiscoveryService, didDiscoverPeer packet: DiscoveryPacket, fromIP: String)
}

final class DiscoveryService {

    weak var delegate: DiscoveryServiceDelegate?

    // Provide these before calling start()
    var buildPayload: (() -> DiscoveryPacket)?
    var extraTargets: (() -> [String])?     // contact + peer IPs for unicast
    var ownPublicKeyB64: String = ""
    var ownIPs: Set<String> = []

    private let discoveryPort: UInt16 = 54231
    private let multicastGroup = "239.255.42.99"
    private let interval: TimeInterval = 1.5

    private var sendSocket: Int32 = -1
    private var recvSocket: Int32 = -1
    private var sendTimer: DispatchSourceTimer?
    private let queue = DispatchQueue(label: "com.dave.lanmessenger.discovery", qos: .utility)
    private var running = false

    func start() {
        guard !running else { return }
        running = true
        setupReceiveSocket()
        setupSendSocket()
        startBeaconTimer()
        startReceiveLoop()
    }

    func stop() {
        running = false
        sendTimer?.cancel()
        sendTimer = nil
        if sendSocket >= 0 { Darwin.close(sendSocket); sendSocket = -1 }
        if recvSocket >= 0 { Darwin.close(recvSocket); recvSocket = -1 }
    }

    // MARK: - Send socket

    private func setupSendSocket() {
        sendSocket = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        guard sendSocket >= 0 else { return }
        var enable: Int32 = 1
        setsockopt(sendSocket, SOL_SOCKET, SO_BROADCAST, &enable, socklen_t(MemoryLayout<Int32>.size))
        var ttl: UInt8 = 1
        setsockopt(sendSocket, IPPROTO_IP, IP_MULTICAST_TTL, &ttl, socklen_t(MemoryLayout<UInt8>.size))
    }

    private func startBeaconTimer() {
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now(), repeating: interval)
        timer.setEventHandler { [weak self] in self?.sendBeacon() }
        timer.resume()
        sendTimer = timer
    }

    private func sendBeacon() {
        guard let payload = buildPayload?() else { return }
        guard let data = try? JSONEncoder().encode(payload) else { return }
        var targets = broadcastTargets()
        targets += extraTargets?() ?? []
        for target in Set(targets) {
            sendUDP(data: data, toIP: target, port: discoveryPort)
        }
    }

    private func broadcastTargets() -> [String] {
        var targets = ["255.255.255.255", multicastGroup]
        for ip in ownIPs {
            let octets = ip.split(separator: ".")
            if octets.count == 4 {
                targets.append("\(octets[0]).\(octets[1]).\(octets[2]).255")
            }
        }
        return targets
    }

    func sendUDP(data: Data, toIP: String, port: UInt16) {
        guard sendSocket >= 0 else { return }
        var addr = sockaddr_in()
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = port.bigEndian
        addr.sin_addr.s_addr = inet_addr(toIP)
        _ = data.withUnsafeBytes { ptr in
            withUnsafePointer(to: &addr) { addrPtr in
                addrPtr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockPtr in
                    Darwin.sendto(sendSocket, ptr.baseAddress, data.count, 0, sockPtr, socklen_t(MemoryLayout<sockaddr_in>.size))
                }
            }
        }
    }

    // MARK: - Receive socket

    private func setupReceiveSocket() {
        recvSocket = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        guard recvSocket >= 0 else { return }

        var enable: Int32 = 1
        setsockopt(recvSocket, SOL_SOCKET, SO_REUSEADDR, &enable, socklen_t(MemoryLayout<Int32>.size))
        setsockopt(recvSocket, SOL_SOCKET, SO_REUSEPORT, &enable, socklen_t(MemoryLayout<Int32>.size))

        var addr = sockaddr_in()
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = discoveryPort.bigEndian
        addr.sin_addr.s_addr = INADDR_ANY
        withUnsafePointer(to: &addr) { ptr in
            ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockPtr in
                _ = Darwin.bind(recvSocket, sockPtr, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }

        // Join multicast group
        var mreq = ip_mreq()
        mreq.imr_multiaddr.s_addr = inet_addr(multicastGroup)
        mreq.imr_interface.s_addr = INADDR_ANY
        setsockopt(recvSocket, IPPROTO_IP, IP_ADD_MEMBERSHIP, &mreq, socklen_t(MemoryLayout<ip_mreq>.size))

        // 1-second timeout so the receive loop can check `running`
        var tv = timeval(tv_sec: 1, tv_usec: 0)
        setsockopt(recvSocket, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
    }

    private func startReceiveLoop() {
        queue.async { [weak self] in
            var buffer = [UInt8](repeating: 0, count: 8192)
            while self?.running == true {
                var addr = sockaddr_in()
                var addrLen = socklen_t(MemoryLayout<sockaddr_in>.size)
                let n = withUnsafeMutablePointer(to: &addr) { addrPtr in
                    addrPtr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockPtr in
                        recvfrom(self?.recvSocket ?? -1, &buffer, buffer.count, 0, sockPtr, &addrLen)
                    }
                }
                guard n > 0 else { continue }
                let data = Data(buffer[..<n])
                let sourceIP = String(cString: inet_ntoa(addr.sin_addr))
                self?.handleReceivedData(data, fromIP: sourceIP)
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

        // Reply to discoveries
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

        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            self.delegate?.discoveryService(self, didDiscoverPeer: pkt, fromIP: fromIP)
        }
    }
}
