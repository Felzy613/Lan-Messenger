import XCTest
import Carbon.HIToolbox
@testable import LanMessenger

// The ways a session ends without anybody clicking Stop.
//
// The indicator's Stop button is the obvious exit and the weakest one: once
// control is granted the host's mouse is contested, so pressing a button is a
// race with the person you are trying to stop. These are the exits that are not
// a race.
final class RemoteSessionStopTests: XCTestCase {

    // MARK: - The kill shortcut

    func testTheKillShortcutIsCarbonEscapeWithThreeModifiers() {
        // Escape is never a text character, and three modifiers put it well out
        // of reach of an accidental press.
        let shortcut = RemoteKillSwitch.shortcut
        XCTAssertEqual(shortcut.keyCode, UInt32(kVK_Escape))
        XCTAssertEqual(shortcut.modifiers, UInt32(controlKey | optionKey | cmdKey))
        XCTAssertEqual(shortcut.displayName, "⌃⌥⌘⎋")
    }

    func testTheShortcutIsNotForceQuitButSitsNextToIt() {
        // Deliberate adjacency: ⌘⌥⎋ is Force Quit, so a host groping for the
        // escape hatch under stress lands on either this — a clean stop — or on
        // Force Quit, which kills the app and therefore the session. Both exits
        // work. It must not *be* Force Quit, though, or we would be fighting the
        // window server for it.
        let forceQuit = UInt32(cmdKey | optionKey)
        XCTAssertNotEqual(RemoteKillSwitch.shortcut.modifiers, forceQuit)
        XCTAssertTrue(RemoteKillSwitch.shortcut.modifiers & UInt32(controlKey) != 0,
                      "Control is what separates this from Force Quit")
    }

    func testTheKillShortcutIsReservedFromForwarding() {
        // The protocol requirement is that it is *never forwarded*. On the
        // viewer side that means the capture layer swallows it locally, so a
        // viewer can always escape their own session — and so a host's kill
        // switch can never be triggered remotely by the peer it exists to stop.
        //
        // WS7 does not exist yet. This test exists now so the rule is in place
        // before the code that has to obey it.
        XCTAssertTrue(RemoteKillSwitch.isReserved(
            keyCode: RemoteKillSwitch.shortcut.keyCode,
            modifiers: RemoteKillSwitch.shortcut.modifiers))
    }

    func testOrdinaryKeystrokesAreNotReserved() {
        // A reserved set that swallows too much is an input path with holes in
        // it that nobody can explain.
        XCTAssertFalse(RemoteKillSwitch.isReserved(keyCode: UInt32(kVK_Escape), modifiers: 0))
        XCTAssertFalse(RemoteKillSwitch.isReserved(keyCode: UInt32(kVK_ANSI_A),
                                                   modifiers: UInt32(cmdKey)))
        XCTAssertFalse(RemoteKillSwitch.isReserved(
            keyCode: UInt32(kVK_Escape), modifiers: UInt32(cmdKey | optionKey)),
            "Force Quit is the system's, not ours")
    }

    // MARK: - Stop reasons

    func testReasonsThatCanStillReachThePeerDoSo() {
        // Not politeness: a peer that gets no `remote_end` waits out its own
        // watchdog before believing the session is over, and shows a frozen last
        // frame the whole time.
        for reason in [RemoteStopReason.userStopped, .killSwitch, .screenLocked,
                       .userSwitched, .systemSleep, .appQuit, .watchdog] {
            XCTAssertTrue(reason.canNotifyPeer, "\(reason) should send remote_end")
        }
        for reason in [RemoteStopReason.networkLost, .peerEnded, .error] {
            XCTAssertFalse(reason.canNotifyPeer, "\(reason) has no working link to send on")
        }
    }

    func testOnlyTheHostsOwnChoicesCountAsDeliberate() {
        // Shapes whether anything is worth surfacing afterwards: a session that
        // ended because somebody pressed Stop needs no explanation; one that
        // ended because a watchdog fired does.
        XCTAssertTrue(RemoteStopReason.userStopped.isDeliberate)
        XCTAssertTrue(RemoteStopReason.killSwitch.isDeliberate)
        XCTAssertTrue(RemoteStopReason.appQuit.isDeliberate)

        XCTAssertFalse(RemoteStopReason.watchdog.isDeliberate)
        XCTAssertFalse(RemoteStopReason.screenLocked.isDeliberate)
        XCTAssertFalse(RemoteStopReason.networkLost.isDeliberate)
        XCTAssertFalse(RemoteStopReason.peerEnded.isDeliberate)
    }

    func testEveryReasonHasAnAuditLineThatNamesTheCause() {
        // The audit trail is read weeks later by somebody asking "why was my
        // screen shared at 11pm". A reason token is not an answer.
        //
        // Both roles, because a viewer writes these lines too and a missing
        // case there would be an empty sentence in somebody's history.
        for reason in RemoteStopReason.allCases {
            for viewing in [false, true] {
                let line = reason.auditDescription(viewing: viewing)
                XCTAssertFalse(line.isEmpty, "\(reason) has no audit line")
                XCTAssertTrue(line.hasSuffix("."), "\(reason): audit lines are sentences")
                // A leaked token is what this is actually guarding against, and
                // it would show up as snake_case in a sentence. `.error`'s line
                // legitimately contains the word "error", so matching the raw
                // value outright would be a cleverness that fails on the honest
                // case.
                XCTAssertFalse(line.contains("_"),
                               "\(reason): a wire token leaked into the audit line")
                XCTAssertNotEqual(line, reason.rawValue)
            }
        }
    }

    func testAViewerNeverClaimsItsOwnScreenWasShared() {
        // The bug: every one of these sentences was written from the host's
        // chair, so a Mac that had spent ten minutes WATCHING somebody else's
        // screen ended the session and recorded "You stopped sharing your
        // screen." That is not a wording slip — it is a false entry in the one
        // record a user consults to find out whether their screen was ever
        // shared.
        for reason in RemoteStopReason.allCases {
            let line = reason.auditDescription(viewing: true)
            XCTAssertFalse(line.lowercased().contains("sharing your screen"),
                           "\(reason): a viewer's line claims its own screen was shared")
            XCTAssertFalse(line.lowercased().contains("screen sharing stopped"),
                           "\(reason): a viewer's line is written from the host's chair")
        }

        XCTAssertEqual(RemoteStopReason.userStopped.auditDescription(viewing: false),
                       "You stopped sharing your screen.")
        XCTAssertEqual(RemoteStopReason.userStopped.auditDescription(viewing: true),
                       "You stopped viewing their screen.")

        // The one cause that reads identically from both chairs: the peer's
        // decision is the peer's decision whichever end we are.
        XCTAssertEqual(RemoteStopReason.peerEnded.auditDescription(viewing: false),
                       RemoteStopReason.peerEnded.auditDescription(viewing: true))
    }

    func testTheWireTokensAreSnakeCase() {
        // They travel in `remote_end` and land in stored history, so camelCase
        // leaking out would be permanent.
        for reason in RemoteStopReason.allCases {
            XCTAssertEqual(reason.rawValue, reason.rawValue.lowercased(),
                           "\(reason) has uppercase in its token")
        }
        XCTAssertEqual(RemoteStopReason.userStopped.rawValue, "user_stopped")
        XCTAssertEqual(RemoteStopReason.killSwitch.rawValue, "kill_switch")
        XCTAssertEqual(RemoteStopReason.networkLost.rawValue, "network_lost")
    }

    // MARK: - The guard

    @MainActor
    func testTheGuardFiresOnceAndDisarmsItself() {
        // A lid closing produces sleep *and* a screen lock. The session should
        // stop once, with the first cause, rather than twice with both — and a
        // second callback would arrive after teardown had already run.
        let guardian = RemoteSessionGuard()
        var reasons: [RemoteStopReason] = []

        guardian.arm { reasons.append($0) }
        XCTAssertTrue(guardian.isArmed)

        guardian.networkBecameUnavailable()
        guardian.networkBecameUnavailable()

        XCTAssertEqual(reasons, [.networkLost])
        XCTAssertFalse(guardian.isArmed, "the guard must disarm as it fires")
        guardian.disarm()
    }

    @MainActor
    func testDisarmingStopsItFiringAtAll() {
        let guardian = RemoteSessionGuard()
        var fired = false
        guardian.arm { _ in fired = true }
        guardian.disarm()
        guardian.networkBecameUnavailable()
        XCTAssertFalse(fired, "a disarmed guard must not stop anything")
    }

    @MainActor
    func testReArmingReplacesRatherThanStacks() {
        // Arming twice without disarming would otherwise leave two sets of
        // observers, and the second session's stop would fire the first
        // session's teardown as well.
        let guardian = RemoteSessionGuard()
        var first = 0, second = 0
        guardian.arm { _ in first += 1 }
        guardian.arm { _ in second += 1 }
        guardian.networkBecameUnavailable()

        XCTAssertEqual(first, 0, "the replaced handler must not run")
        XCTAssertEqual(second, 1)
        guardian.disarm()
    }
}
