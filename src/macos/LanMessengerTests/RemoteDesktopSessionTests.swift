import XCTest
import CryptoKit
@testable import LanMessenger

// The object that ties everything together, and the first thing in this feature
// that can be started.
@MainActor
final class RemoteDesktopSessionTests: XCTestCase {

    // MARK: - Shape

    func testAViewerDoesNotCaptureAndAHostDoesNotPresent() {
        // The asymmetry the whole session depends on. A viewer that captured
        // would be sharing its own screen without being asked; a host that
        // presented would be watching itself.
        let host = RemoteDesktopSession.Mode.host(peerName: "Dave", peerIP: "10.0.0.5")
        XCTAssertTrue(host.capturesLocally)
        XCTAssertFalse(host.presentsLocally)

        let viewer = RemoteDesktopSession.Mode.viewer(peerName: "Dave", peerIP: "10.0.0.5")
        XCTAssertFalse(viewer.capturesLocally)
        XCTAssertTrue(viewer.presentsLocally)
    }

    func testANewSessionIsIdleAndGrantsNothing() {
        let session = RemoteDesktopSession()
        XCTAssertFalse(session.isRunning)
        XCTAssertNil(session.mode)
        XCTAssertNil(session.videoLayer)
        XCTAssertEqual(session.grant.grant, .none)
        XCTAssertFalse(session.grant.acceptsInput)
    }

    func testControlCannotBeGrantedBeforeAnythingIsRunning() {
        // The two-stage rule reaching all the way up: there is no path to input
        // on a session nobody started, let alone agreed to.
        let session = RemoteDesktopSession()
        XCTAssertFalse(session.grantControl())
        XCTAssertFalse(session.revokeControl())
        XCTAssertEqual(session.grant.grant, .none)
    }

    func testStoppingAnIdleSessionIsHarmless() {
        // Every teardown path funnels into stop(), including ones that can fire
        // after the session already ended — a guard notification racing a Stop
        // click. It must not emit a second audit record or fire onEnded again.
        let session = RemoteDesktopSession(appendAudit: { _ in
            XCTFail("an idle session wrote an audit record")
        })
        var ended = 0
        session.onEnded = { _ in ended += 1 }

        session.stop(.userStopped)
        session.stop(.watchdog)
        XCTAssertEqual(ended, 0)
    }

    // MARK: - Inbound routing

    private func makeMediaSession() throws -> MediaSession {
        let a = Curve25519.KeyAgreement.PrivateKey()
        let b = Curve25519.KeyAgreement.PrivateKey()
        let ae = Curve25519.KeyAgreement.PrivateKey()
        let be = Curve25519.KeyAgreement.PrivateKey()
        let keys = try RemoteSessionCrypto.deriveKeys(
            myRole: .responder, myEphemeralPrivate: ae, myStaticPrivate: a,
            peerEphemeralPublicKeyB64: be.publicKey.rawRepresentation.base64EncodedString(),
            peerStaticPublicKeyB64: b.publicKey.rawRepresentation.base64EncodedString(),
            sessionID: "9f2c4a6e8b0d1f3a5c7e9b1d3f5a7c9e",
            params: RemoteHandshakeParams(["protocol": .int(1)]))
        return MediaSession(sessionID: "9f2c4a6e8b0d1f3a5c7e9b1d3f5a7c9e",
                            peerPublicKeyB64: b.publicKey.rawRepresentation.base64EncodedString(),
                            peerIP: "10.0.0.10", role: .responder, keys: keys,
                            link: BlockingMediaLink())
    }

    func testAdoptingATransportWiresInboundRoutingBeforeAnythingStarts() throws {
        // The bug: `onFrame` was wired inside `start`'s `presentsLocally`
        // block, so a HOST never set it at all and silently ignored every
        // control_request, every input record and every keyframe request its
        // viewer sent. Nothing logged it — a host has nothing to say about
        // frames it was never handed — and video is one-way, so both ends
        // looked healthy. It presented as "Request Control does nothing".
        //
        // Routing is now adopted with the transport rather than derived from
        // the role, so there is no role that can miss it.
        let session = RemoteDesktopSession()
        let media = try makeMediaSession()
        XCTAssertNil(media.onFrame, "the transport arrives unwired")

        session.adopt(media)
        XCTAssertNotNil(media.onFrame,
                        "a session that has taken a transport must route what arrives on it")
    }

    func testAHostWiresRoutingEvenWhenItsCaptureNeverStarts() throws {
        // Adoption happens before `start`, which is what makes it independent
        // of the role AND of whether capture is available. Asserted through
        // `startHosting` rather than `adopt` so the host entry point itself is
        // covered: this is the call site that was wrong.
        let session = RemoteDesktopSession()
        let media = try makeMediaSession()

        let done = expectation(description: "startHosting returned")
        Task { @MainActor in
            // Succeeds on a machine with the Screen Recording grant and throws
            // without one. Either way the transport must already be wired.
            try? await session.startHosting(peerName: "Dell", peerIP: "10.0.0.10", media: media)
            done.fulfill()
        }
        wait(for: [done], timeout: 10)

        XCTAssertNotNil(media.onFrame, "a host ignored everything its viewer sent")
        session.stop(.userStopped)
    }

    // MARK: - Reuse

    func testASecondSessionGetsAFreshGrantLadder() async throws {
        // The bug this exists for: RemoteGrantState is deliberately one-way —
        // "a session that has ended stays ended" — and the session object is a
        // long-lived singleton. Carrying the old state into the next session
        // left `ended` true, so `accept()` refused and the whole ladder stayed
        // at `.none`. The host would grant control, the viewer would log that it
        // had been granted, and nothing would be captured: the first session
        // after launch worked and every one after it silently did not.
        var state = RemoteGrantState()
        XCTAssertTrue(state.accept())
        XCTAssertTrue(state.grantControl())
        state.end()

        // The same value reused is dead, by design.
        XCTAssertFalse(state.accept(), "an ended ladder must not climb again")
        XCTAssertEqual(state.grant, .none)

        // Which is exactly why a new session must build a new one.
        var fresh = RemoteGrantState()
        XCTAssertTrue(fresh.accept())
        XCTAssertTrue(fresh.grantControl())
        XCTAssertEqual(fresh.grant, .control)
    }

}
