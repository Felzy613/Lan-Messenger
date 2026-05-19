import Foundation
import Network
import Darwin

// One usable IPv4 interface on this machine. Snapshot value — never mutated
// after construction; the monitor publishes a fresh set on every change.
struct NetworkAdapterSnapshot: Equatable, Hashable {
    let name: String              // BSD interface name, e.g. en0, en1, utun3
    let localIP: String           // "192.168.1.42"
    let subnetMask: String        // "255.255.255.0"
    let broadcastAddress: String  // "192.168.1.255"
}

// Tracks the set of IPv4 interfaces eligible for LAN discovery on macOS.
//
// "Eligible" means:
//   - IFF_UP set, IFF_LOOPBACK clear
//   - Has at least one IPv4 unicast address
//   - The address is not in the APIPA link-local range (169.254/16) — those
//     indicate "no real network" when DHCP fails on macOS
//
// Publishes a snapshot of the current adapter set and fires `onChange`
// whenever the set differs from the previous one. Listens to NWPathMonitor
// for OS-level network transitions (Wi-Fi reconnect, sleep/resume, VPN
// toggle) and runs a 5-second polling safety net.
//
// IsLocalNetworkAvailable is true whenever the snapshot is non-empty. The UI
// should treat that as "the app has a usable local network", not "internet is
// reachable" — LAN messaging doesn't need internet.
final class NetworkInterfaceMonitor {

    // Observers receive notifications on the main queue whenever the adapter
    // set changes. Returned token is opaque; pass it to removeObserver to stop.
    typealias Observer = () -> Void

    private(set) var adapters: [NetworkAdapterSnapshot] = []

    var isLocalNetworkAvailable: Bool { !adapters.isEmpty }

    var localIPs: Set<String> { Set(adapters.map { $0.localIP }) }

    private let pathMonitor = NWPathMonitor()
    private let pathQueue = DispatchQueue(label: "com.dave.lanmessenger.netmonitor", qos: .utility)
    private var pollTimer: DispatchSourceTimer?
    private var started = false

    private var observers: [(id: UUID, fn: Observer)] = []
    private let observerLock = NSLock()

    @discardableResult
    func addObserver(_ fn: @escaping Observer) -> UUID {
        observerLock.lock(); defer { observerLock.unlock() }
        let id = UUID()
        observers.append((id, fn))
        return id
    }

    func removeObserver(_ id: UUID) {
        observerLock.lock(); defer { observerLock.unlock() }
        observers.removeAll { $0.id == id }
    }

    private func notifyObservers() {
        observerLock.lock()
        let snapshot = observers
        observerLock.unlock()
        DispatchQueue.main.async {
            for (_, fn) in snapshot { fn() }
        }
    }

    func start() {
        guard !started else { return }
        started = true

        // Initial snapshot synchronously so callers see populated state immediately.
        refresh(reason: "initial")

        pathMonitor.pathUpdateHandler = { [weak self] _ in
            self?.refresh(reason: "nwpath")
        }
        pathMonitor.start(queue: pathQueue)

        // 5 s safety-net poll — NWPathMonitor occasionally misses transitions
        // on Wi-Fi roams and VPN bring-up; this catches the strays.
        let timer = DispatchSource.makeTimerSource(queue: pathQueue)
        timer.schedule(deadline: .now() + .seconds(5), repeating: .seconds(5))
        timer.setEventHandler { [weak self] in self?.refresh(reason: "poll") }
        timer.resume()
        pollTimer = timer
    }

    func stop() {
        guard started else { return }
        started = false
        pollTimer?.cancel()
        pollTimer = nil
        pathMonitor.cancel()
    }

    // MARK: - Refresh

    private let refreshLock = NSLock()

    private func refresh(reason: String) {
        refreshLock.lock()
        let fresh = Self.enumerate()
        let prev = adapters
        let equal = fresh.count == prev.count &&
            Set(fresh).isSubset(of: Set(prev)) &&
            Set(prev).isSubset(of: Set(fresh))
        if !equal { adapters = fresh }
        refreshLock.unlock()

        if equal { return }
        NetLogger.info("NetMonitor",
            "interfaces changed (\(reason)): was=\(prev.count) now=\(fresh.count) " +
            "ips=[\(fresh.map { $0.localIP }.joined(separator: ","))] " +
            "available=\(isLocalNetworkAvailable)")
        notifyObservers()
    }

    // MARK: - Interface enumeration

    static func enumerate() -> [NetworkAdapterSnapshot] {
        var result: [NetworkAdapterSnapshot] = []

        var ifaddr: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&ifaddr) == 0, let first = ifaddr else { return [] }
        defer { freeifaddrs(ifaddr) }

        var ptr: UnsafeMutablePointer<ifaddrs>? = first
        while let current = ptr {
            defer { ptr = current.pointee.ifa_next }

            let flags = Int32(current.pointee.ifa_flags)
            guard (flags & IFF_UP) != 0,
                  (flags & IFF_LOOPBACK) == 0,
                  let addrPtr = current.pointee.ifa_addr,
                  addrPtr.pointee.sa_family == UInt8(AF_INET) else { continue }

            let name = current.pointee.ifa_name.map { String(cString: $0) } ?? ""

            let localIP = Self.numericHost(addrPtr)
            guard !localIP.isEmpty,
                  !localIP.hasPrefix("169.254."),
                  localIP != "0.0.0.0" else { continue }

            let mask: String = {
                guard let maskPtr = current.pointee.ifa_netmask else { return "255.255.255.0" }
                let m = Self.numericHost(maskPtr)
                return m.isEmpty ? "255.255.255.0" : m
            }()

            let broadcast = Self.computeBroadcast(ip: localIP, mask: mask)

            result.append(NetworkAdapterSnapshot(
                name: name,
                localIP: localIP,
                subnetMask: mask,
                broadcastAddress: broadcast))
        }
        return result
    }

    private static func numericHost(_ addr: UnsafePointer<sockaddr>) -> String {
        var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
        let len = socklen_t(addr.pointee.sa_len)
        let rc = getnameinfo(addr, len, &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST)
        return rc == 0 ? String(cString: host) : ""
    }

    private static func computeBroadcast(ip: String, mask: String) -> String {
        let ipParts = ip.split(separator: ".").compactMap { UInt8($0) }
        let mkParts = mask.split(separator: ".").compactMap { UInt8($0) }
        guard ipParts.count == 4, mkParts.count == 4 else { return "255.255.255.255" }
        var b = [UInt8](repeating: 0, count: 4)
        for i in 0..<4 { b[i] = ipParts[i] | ~mkParts[i] }
        return "\(b[0]).\(b[1]).\(b[2]).\(b[3])"
    }
}
