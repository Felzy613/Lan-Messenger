import XCTest
@testable import LanMessenger

// Regression cover for the discovery queue-starvation bug.
//
// DiscoveryService runs a blocking `while running { recvfrom(...) }` loop. It
// once shared a serial DispatchQueue with the 1.5 s beacon timer and the
// interface-change socket rebuild. Because that loop never returns, every work
// item queued behind it was starved for the life of the process:
//
//   • the beacon timer never fired again, so the Mac answered peers' probes but
//     never announced itself — Windows peers could not discover it at all;
//   • sockets were never rebuilt after a DHCP/Wi-Fi change, so every send
//     failed EADDRNOTAVAIL ("Can't assign requested address") until restart.
//
// The wiring reads as correct at every call site, so this is invisible to code
// review — it only shows up as queue occupancy at runtime.
//
// Counting signal: these tests count `extraTargets` invocations, NOT
// `buildPayload`. `buildPayload` is also called by the reply path inside the
// receive loop (DiscoveryService.handleReceivedData), which is *not* starved by
// the bug — so on a live network, ambient beacons from real peers keep it
// ticking and a buildPayload-based counter passes even against the broken code.
// `extraTargets` is only reached from sendBeacon()/sendGoodbye(), so it isolates
// the timer-driven path that the bug actually killed.
final class DiscoveryServiceQueueTests: XCTestCase {

    /// A service whose `extraTargets` closure reports each beacon. Returns
    /// unusable targets deliberately: the test only cares that the timer's work
    /// item reached the queue, not that any packet left the machine (CI runners
    /// may have no usable interface, and sendBeacon's `sendSockets.isEmpty`
    /// guard sits *after* the extraTargets call).
    private func makeService(
        monitor: NetworkInterfaceMonitor,
        onBeacon: @escaping () -> Void
    ) -> DiscoveryService {
        let service = DiscoveryService(monitor: monitor)
        service.ownPublicKeyB64 = "queue-test-public-key"
        service.buildPayload = {
            DiscoveryPacket(
                type: "discovery",
                username: "QueueTest",
                port: 54232,
                publicKeyB64: "queue-test-public-key",
                ips: []
            )
        }
        service.extraTargets = {
            onBeacon()
            return []
        }
        return service
    }

    // The core regression. Against the shared-queue version this sees at most
    // one beacon — the immediate `deadline: .now()` fire, and only if it wins
    // the race against the receive loop claiming the queue — then silence for
    // the life of the process.
    func testBeaconTimerKeepsFiringAfterReceiveLoopStarts() {
        let monitor = NetworkInterfaceMonitor()
        monitor.start()
        defer { monitor.stop() }

        let beacons = expectation(description: "beacon timer fires repeatedly")
        beacons.expectedFulfillmentCount = 3
        beacons.assertForOverFulfill = false

        let service = makeService(monitor: monitor) { beacons.fulfill() }
        service.start()
        defer { service.stop() }

        // 3 fires at a 1.5 s interval need ~3 s; allow slack for a loaded CI
        // machine before calling it starvation.
        wait(for: [beacons], timeout: 20.0)
    }

    // Guards against a future regression that reintroduces sharing by giving
    // the receive loop a single shared/static queue instead of a per-instance
    // one: the second service's beacons must not be blocked by the first
    // service's loop.
    func testConcurrentServicesBothKeepBeaconing() {
        let monitor = NetworkInterfaceMonitor()
        monitor.start()
        defer { monitor.stop() }

        let firstBeacons = expectation(description: "first service beacons")
        firstBeacons.expectedFulfillmentCount = 2
        firstBeacons.assertForOverFulfill = false

        let secondBeacons = expectation(description: "second service beacons")
        secondBeacons.expectedFulfillmentCount = 2
        secondBeacons.assertForOverFulfill = false

        let first = makeService(monitor: monitor) { firstBeacons.fulfill() }
        first.start()
        defer { first.stop() }

        let second = makeService(monitor: monitor) { secondBeacons.fulfill() }
        second.start()
        defer { second.stop() }

        wait(for: [firstBeacons, secondBeacons], timeout: 20.0)
    }
}
