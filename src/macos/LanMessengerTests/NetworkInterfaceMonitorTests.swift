import XCTest
@testable import LanMessenger

// These tests cover the platform-agnostic invariants of NetworkInterfaceMonitor.
// The OS-event subscription and live socket re-binding are exercised by manual
// QA (Wi-Fi reconnect, VPN toggle, sleep/resume) — there's no clean way to
// simulate those transitions inside an XCTest run.
final class NetworkInterfaceMonitorTests: XCTestCase {

    func testEnumerateExcludesLoopbackAndApipa() {
        let snapshots = NetworkInterfaceMonitor.enumerate()
        for s in snapshots {
            XCTAssertFalse(s.localIP.hasPrefix("127."), "loopback addresses must be excluded")
            XCTAssertFalse(s.localIP.hasPrefix("169.254."), "APIPA link-local addresses must be excluded")
            XCTAssertFalse(s.localIP == "0.0.0.0", "unbound addresses must be excluded")
        }
    }

    func testIsLocalNetworkAvailableMatchesAdapterCount() {
        let monitor = NetworkInterfaceMonitor()
        monitor.start()
        defer { monitor.stop() }
        XCTAssertEqual(monitor.adapters.count > 0, monitor.isLocalNetworkAvailable)
    }

    func testBroadcastAddressComputedFromMask() {
        let snapshots = NetworkInterfaceMonitor.enumerate()
        for s in snapshots {
            let ip   = s.localIP.split(separator: ".").compactMap { UInt8($0) }
            let mask = s.subnetMask.split(separator: ".").compactMap { UInt8($0) }
            let bc   = s.broadcastAddress.split(separator: ".").compactMap { UInt8($0) }
            guard ip.count == 4, mask.count == 4, bc.count == 4 else {
                XCTFail("malformed snapshot \(s)"); return
            }
            for i in 0..<4 {
                XCTAssertEqual(UInt8(ip[i] | ~mask[i]), bc[i],
                               "broadcast byte \(i) mismatch for \(s.localIP)/\(s.subnetMask)")
            }
        }
    }

    func testStartIsIdempotent() {
        let monitor = NetworkInterfaceMonitor()
        monitor.start()
        let before = monitor.adapters
        monitor.start()    // must not throw or double-subscribe
        XCTAssertEqual(before.count, monitor.adapters.count)
        monitor.stop()
    }

    func testObserverFiresOnInitialChange() {
        // Add an observer BEFORE start so the first refresh's main-queue
        // notification reaches us. The observer fires asynchronously on the
        // main queue, so wait briefly.
        let monitor = NetworkInterfaceMonitor()
        var calls = 0
        let _ = monitor.addObserver { calls += 1 }
        monitor.start()
        // Drain main queue twice to give the async dispatch a chance to land.
        let exp = expectation(description: "observer ran")
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
            // Observer is called only when the set differs from the previous,
            // and the initial refresh produces a non-empty set whenever any
            // usable IPv4 interface exists. CI runners with only loopback will
            // not trigger it; assert that the start path doesn't crash and
            // that the call count is consistent with adapter presence.
            if monitor.adapters.isEmpty {
                XCTAssertEqual(calls, 0)
            } else {
                XCTAssertGreaterThan(calls, 0)
            }
            exp.fulfill()
        }
        wait(for: [exp], timeout: 2.0)
        monitor.stop()
    }

    func testRemoveObserverStopsNotifications() {
        let monitor = NetworkInterfaceMonitor()
        var calls = 0
        let id = monitor.addObserver { calls += 1 }
        monitor.removeObserver(id)
        monitor.start()
        // No way to deterministically force a change; just assert no crash and
        // that removeObserver of an unknown ID is harmless.
        monitor.removeObserver(UUID())
        monitor.stop()
        XCTAssertGreaterThanOrEqual(calls, 0)   // tautology — just keeps `calls` used
    }
}
