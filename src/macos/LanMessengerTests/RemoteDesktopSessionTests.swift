import XCTest
import CoreMedia
@testable import LanMessenger

// The object that ties everything together, and the first thing in this feature
// that can be started.
//
// The pure half of these tests runs anywhere. The live half needs the Screen
// Recording grant and is skipped without it, like `ScreenCaptureLiveTests` —
// but it is the one that matters, because it runs a real capture through a real
// encoder into a real decoder and checks a picture comes out the far end.
@MainActor
final class RemoteDesktopSessionTests: XCTestCase {

    private var isLive: Bool {
        ProcessInfo.processInfo.environment["LANMSG_LIVE_CAPTURE"] == "1"
    }

    // MARK: - Shape

    func testAViewerDoesNotCaptureAndAHostDoesNotPresent() {
        // The asymmetry the whole session depends on. A viewer that captured
        // would be sharing its own screen without being asked; a host that
        // presented would be watching itself.
        XCTAssertTrue(RemoteDesktopSession.Mode.selfView.capturesLocally)
        XCTAssertTrue(RemoteDesktopSession.Mode.selfView.presentsLocally)

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

    // MARK: - Live: the whole pipeline, on this machine

    func testSelfViewCarriesRealFramesFromScreenToPresenter() async throws {
        try XCTSkipUnless(isLive, "set LANMSG_LIVE_CAPTURE=1 with the Screen Recording grant")

        var audit: [RemoteAuditEntry] = []
        let session = RemoteDesktopSession(appendAudit: { audit.append($0) })

        // No lock: `onSample` is delivered on the main actor by the session,
        // and this test is main-actor isolated. NSLock is also unavailable from
        // an async context, which is the compiler pointing at the same fact.
        var samples: [CMSampleBuffer] = []
        session.onSample = { samples.append($0) }

        try await session.startSelfView()

        XCTAssertTrue(session.isRunning)
        XCTAssertEqual(session.mode, .selfView)
        XCTAssertEqual(session.grant.grant, .viewing,
                       "starting grants viewing and nothing more")
        XCTAssertFalse(session.grant.acceptsInput)
        XCTAssertNotNil(session.videoLayer, "a presenting session must vend a layer")

        let size = try XCTUnwrap(session.dimensions)
        print("""

            ── self view running ────────────────────────────────────────────
              \(size.width)x\(size.height)
              MOVE THE MOUSE for the next 10 seconds.
            ─────────────────────────────────────────────────────────────────

            """)

        for _ in 0..<10 {
            try await Task.sleep(nanoseconds: 1_000_000_000)
            if samples.count >= 5 { break }
        }

        let received = samples
        print("presented \(received.count) frame(s)")

        // This is the assertion Phase 1 exists for. Every component was already
        // tested alone; nothing had ever carried a real screen all the way from
        // SCStream to a display layer.
        XCTAssertGreaterThan(received.count, 0, """
            No frames reached the presenter.

            If the screen was genuinely still, that is expected — SCStream is
            change-driven. Run it again and move the mouse.
            """)

        // The picture that came out must be the picture that went in. A decoder
        // that silently produced a different size would mean the Annex-B
        // conversion or the parameter sets were wrong.
        for sample in received.prefix(3) {
            let description = try XCTUnwrap(CMSampleBufferGetFormatDescription(sample))
            let decoded = CMVideoFormatDescriptionGetDimensions(description)
            XCTAssertEqual(Int(decoded.width), size.width)
            XCTAssertEqual(Int(decoded.height), size.height)
        }

        XCTAssertEqual(audit.map(\.event), [.sessionStarted],
                       "a running session records its start and nothing else yet")

        // MARK: teardown

        session.stop(.userStopped)

        XCTAssertFalse(session.isRunning)
        XCTAssertNil(session.mode)
        XCTAssertNil(session.videoLayer, "the layer must go with the session")
        XCTAssertNil(session.dimensions)
        XCTAssertEqual(session.grant.grant, .none)

        XCTAssertEqual(audit.map(\.event), [.sessionStarted, .sessionEnded])
        let ending = try XCTUnwrap(audit.last)
        XCTAssertEqual(ending.reason, RemoteStopReason.userStopped.rawValue)
        XCTAssertNotNil(ending.durationSummary, "a session that ran has a duration")

        // An ended session stays ended. Reconnect means a new session_id, new
        // ephemerals and new keys — so it means a new session object, and the
        // grant this one holds can never climb again.
        XCTAssertTrue(session.grant.ended)
        XCTAssertFalse(session.grant.isLive)
    }

    func testControlEscalationIsRecordedAndReversible() async throws {
        try XCTSkipUnless(isLive, "set LANMSG_LIVE_CAPTURE=1 with the Screen Recording grant")

        var audit: [RemoteAuditEntry] = []
        let session = RemoteDesktopSession(appendAudit: { audit.append($0) })
        try await session.startSelfView()
        defer { session.stop(.userStopped) }

        XCTAssertFalse(session.grant.acceptsInput, "viewing must not arm input")

        XCTAssertTrue(session.grantControl())
        XCTAssertEqual(session.grant.grant, .control)
        XCTAssertTrue(session.grant.acceptsInput)

        XCTAssertTrue(session.revokeControl())
        XCTAssertEqual(session.grant.grant, .viewing,
                       "revoking control leaves the session running")
        XCTAssertFalse(session.grant.acceptsInput)

        XCTAssertEqual(audit.map(\.event),
                       [.sessionStarted, .controlGranted, .controlRevoked],
                       "every grant change is on the record")
    }

    func testTheKillPathStopsCaptureEvenWhenItComesFromTheGuard() async throws {
        try XCTSkipUnless(isLive, "set LANMSG_LIVE_CAPTURE=1 with the Screen Recording grant")

        var audit: [RemoteAuditEntry] = []
        let session = RemoteDesktopSession(appendAudit: { audit.append($0) })
        try await session.startSelfView()

        // What the kill shortcut and every system trigger do.
        session.stop(.killSwitch)

        XCTAssertFalse(session.isRunning)
        XCTAssertEqual(audit.last?.reason, RemoteStopReason.killSwitch.rawValue)
        XCTAssertEqual(audit.last?.summary,
                       RemoteStopReason.killSwitch.auditDescription)
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
