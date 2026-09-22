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
//
// The same starvation then claimed a second victim, for years and in silence:
// the per-minute health summary timer was created with `queue: recvQueue`, the
// very queue the receive loop never releases, so `emitHealthSummary()` was never
// once dequeued. Production logs showed 39,304 Discovery lines and zero health
// lines. The diagnostic built to catch beacon starvation was itself dead from
// queue starvation, and nothing said so — which is exactly the failure mode the
// summary exists to prevent. It now has its own `healthQueue`, and
// testHealthSummaryFiresWhileReceiveLoopRuns is the cover.
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

    // The health summary must keep firing while the receive loop is running.
    //
    // Against the pre-fix code (timer on `recvQueue`) this test times out: the
    // receive loop's block occupies that serial queue forever, so the handler is
    // never dequeued and `onHealthSummary` is never called. The interval is
    // shrunk from the production 60 s purely so the test finishes — at the real
    // interval, proving this at all would take a minute per assertion, which is
    // why the bug survived as long as it did.
    func testHealthSummaryFiresWhileReceiveLoopRuns() {
        let monitor = NetworkInterfaceMonitor()
        monitor.start()
        defer { monitor.stop() }

        let summaries = expectation(description: "health summary is emitted repeatedly")
        summaries.expectedFulfillmentCount = 2
        summaries.assertForOverFulfill = false

        let service = makeService(monitor: monitor) { }
        service.healthInterval = 0.25
        service.onHealthSummary = { summaries.fulfill() }
        service.start()
        defer { service.stop() }

        wait(for: [summaries], timeout: 20.0)
    }

    // The health timer must also stay off the beacon queue. Its whole purpose is
    // to report "tx_beacons=0" when the beacon path dies, so sharing a queue
    // with the thing it monitors would make it silent in the one case it exists
    // for. Here the beacon path is left completely unblocked and we simply assert
    // both keep ticking independently.
    func testHealthSummaryAndBeaconsBothTick() {
        let monitor = NetworkInterfaceMonitor()
        monitor.start()
        defer { monitor.stop() }

        let beacons = expectation(description: "beacons keep firing")
        beacons.expectedFulfillmentCount = 2
        beacons.assertForOverFulfill = false
        let summaries = expectation(description: "health summaries keep firing")
        summaries.expectedFulfillmentCount = 2
        summaries.assertForOverFulfill = false

        let service = makeService(monitor: monitor) { beacons.fulfill() }
        service.healthInterval = 0.25
        service.onHealthSummary = { summaries.fulfill() }
        service.start()
        defer { service.stop() }

        wait(for: [beacons, summaries], timeout: 20.0)
    }
}
