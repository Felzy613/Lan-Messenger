import XCTest
import CryptoKit
@testable import LanMessenger

// The media transport's queue-starvation cover, and the direct descendant of
// DiscoveryServiceQueueTests.
//
// That bug has now been found twice in this codebase, both times in
// DiscoveryService and both times invisible to code review, because starvation
// is a queue-occupancy property rather than anything visible at a call site:
//
//   1. The blocking recvfrom() loop shared a serial queue with the beacon timer,
//      so the Mac answered peers but never announced itself.
//   2. The health-summary timer was then created on `recvQueue` — the queue the
//      receive loop never releases — so it never fired once in production. The
//      diagnostic built to catch starvation was itself dead from starvation.
//
// MediaSession has exactly the same shape: a readFrame() loop that never returns
// while running, plus a keepalive timer and a watchdog. If any of those three
// ever share a serial queue, a session whose peer has gone quiet stops sending
// keepalives and never notices it is dead — which is the failure mode the
// watchdog exists to prevent.
//
// Counting signal: these tests count `onKeepaliveTick` / `onWatchdogTick`, which
// are reachable ONLY from the timer queue. That distinction is the lesson from
// the discovery tests, which count `extraTargets` rather than `buildPayload`
// precisely because `buildPayload` is also reached from inside the receive loop
// and therefore keeps ticking against broken code.
final class RemoteDesktopQueueTests: XCTestCase {

    private func makeSession(link: MediaLink) throws -> MediaSession {
        let a = Curve25519.KeyAgreement.PrivateKey()
        let b = Curve25519.KeyAgreement.PrivateKey()
        let ae = Curve25519.KeyAgreement.PrivateKey()
        let be = Curve25519.KeyAgreement.PrivateKey()
        let keys = try RemoteSessionCrypto.deriveKeys(
            myRole: .initiator, myEphemeralPrivate: ae, myStaticPrivate: a,
            peerEphemeralPublicKeyB64: be.publicKey.rawRepresentation.base64EncodedString(),
            peerStaticPublicKeyB64: b.publicKey.rawRepresentation.base64EncodedString(),
            sessionID: "9f2c4a6e8b0d1f3a5c7e9b1d3f5a7c9e",
            params: RemoteHandshakeParams(["protocol": .int(1)]))

        return MediaSession(sessionID: "9f2c4a6e8b0d1f3a5c7e9b1d3f5a7c9e",
                            peerPublicKeyB64: b.publicKey.rawRepresentation.base64EncodedString(),
                            peerIP: link.peerIP, role: .initiator, keys: keys, link: link)
    }

    // The core regression. Against a session whose timers shared the read queue
    // this sees zero ticks and times out: the read loop parks in readExact and
    // never yields the queue.
    func testKeepaliveKeepsFiringWhileTheReadLoopIsParked() throws {
        let link = BlockingMediaLink()
        let session = try makeSession(link: link)
        session.keepaliveInterval = 0.2
        session.watchdogInterval = 60          // keep the watchdog out of this test
        session.watchdogTimeout = 600

        let ticks = expectation(description: "keepalive timer fires repeatedly")
        ticks.expectedFulfillmentCount = 3
        ticks.assertForOverFulfill = false
        session.onKeepaliveTick = { ticks.fulfill() }

        session.start()
        defer { session.stop(); link.release() }

        wait(for: [ticks], timeout: 20.0)
    }

    // The watchdog is the thing that stops a crashed viewer leaving a host's
    // screen captured indefinitely, so it must survive the same parked read loop.
    func testWatchdogKeepsFiringWhileTheReadLoopIsParked() throws {
        let link = BlockingMediaLink()
        let session = try makeSession(link: link)
        session.keepaliveInterval = 60
        session.watchdogInterval = 0.2
        session.watchdogTimeout = 600          // observe ticks without tearing down

        let ticks = expectation(description: "watchdog timer fires repeatedly")
        ticks.expectedFulfillmentCount = 3
        ticks.assertForOverFulfill = false
        session.onWatchdogTick = { ticks.fulfill() }

        session.start()
        defer { session.stop(); link.release() }

        wait(for: [ticks], timeout: 20.0)
    }

    // Guards against a future regression that gives the read loop a single
    // shared or static queue instead of a per-session one: the second session's
    // timers must not be blocked by the first session's parked reader.
    func testConcurrentSessionsBothKeepTicking() throws {
        let firstLink = BlockingMediaLink()
        let secondLink = BlockingMediaLink()
        let first = try makeSession(link: firstLink)
        let second = try makeSession(link: secondLink)
        for session in [first, second] {
            session.keepaliveInterval = 0.2
            session.watchdogInterval = 60
            session.watchdogTimeout = 600
        }

        let firstTicks = expectation(description: "first session keepalives")
        firstTicks.expectedFulfillmentCount = 2
        firstTicks.assertForOverFulfill = false
        let secondTicks = expectation(description: "second session keepalives")
        secondTicks.expectedFulfillmentCount = 2
        secondTicks.assertForOverFulfill = false

        first.onKeepaliveTick = { firstTicks.fulfill() }
        second.onKeepaliveTick = { secondTicks.fulfill() }

        first.start(); second.start()
        defer {
            first.stop(); second.stop()
            firstLink.release(); secondLink.release()
        }

        wait(for: [firstTicks, secondTicks], timeout: 20.0)
    }

    // The watchdog must actually tear the session down, not merely tick. A
    // viewer that crashes mid-session leaves the host capturing its screen
    // forever otherwise — the worst failure this feature can have.
    func testWatchdogClosesAnIdleSession() throws {
        let link = BlockingMediaLink()
        let session = try makeSession(link: link)
        session.keepaliveInterval = 60
        session.watchdogInterval = 0.2
        session.watchdogTimeout = 0.3

        let closed = expectation(description: "session closes on watchdog timeout")
        closed.assertForOverFulfill = false
        session.onClosed = { _ in closed.fulfill() }

        session.start()
        defer { link.release() }

        wait(for: [closed], timeout: 20.0)
        XCTAssertTrue(link.isClosed, "the watchdog must close the link, not just log")
    }
}
