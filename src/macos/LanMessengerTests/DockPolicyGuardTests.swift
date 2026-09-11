import XCTest
import AppKit
@testable import LanMessenger

// Cover for "the Dock icon randomly appears even though it's toggled off".
//
// The app sets its activation policy once at launch, but AppKit promotes an
// .accessory process back to .regular on its own — materialising a Window
// scene, running a modal panel, being re-activated after the updater relaunches
// the bundle. There is no notification for "the activation policy changed", so
// the only way to keep the preference honoured is to re-assert it: on the
// events that tend to precede a promotion, and on a slow safety-net tick.
//
// DockPolicyGuard's three closures (read preference / read policy / apply
// policy) exist precisely so this correction logic is exercisable without a
// running NSApplication.
final class DockPolicyGuardTests: XCTestCase {

    /// A guard wired to in-memory state instead of NSApp.
    private func makeGuard(hidden: Bool,
                           policy: NSApplication.ActivationPolicy) -> (DockPolicyGuard, () -> NSApplication.ActivationPolicy, () -> Int) {
        let g = DockPolicyGuard()
        var current = policy
        var applyCount = 0
        g.hideFromDock = { hidden }
        g.currentPolicy = { current }
        g.applyPolicy = { current = $0; applyCount += 1 }
        g.onCorrection = { _ in }
        return (g, { current }, { applyCount })
    }

    func testDesiredPolicyFollowsPreference() {
        XCTAssertEqual(DockPolicyGuard.desiredPolicy(hideFromDock: true), .accessory)
        XCTAssertEqual(DockPolicyGuard.desiredPolicy(hideFromDock: false), .regular)
    }

    // The actual bug: something promoted us to .regular behind our back while
    // the user has the Dock icon switched off.
    func testReassertCorrectsAppKitPromotionToRegular() {
        let (g, policy, applyCount) = makeGuard(hidden: true, policy: .regular)

        XCTAssertTrue(g.reassert(), "a drifted policy should report that it was corrected")
        XCTAssertEqual(policy(), .accessory)
        XCTAssertEqual(applyCount(), 1)
    }

    // The guard ticks every few seconds for the life of the process. Calling
    // setActivationPolicy on every tick would flicker the Dock and steal focus,
    // so a policy that already matches must be left completely alone.
    func testReassertIsANoOpWhenPolicyAlreadyMatches() {
        let (g, _, applyCount) = makeGuard(hidden: true, policy: .accessory)

        XCTAssertFalse(g.reassert())
        XCTAssertFalse(g.reassert())
        XCTAssertEqual(applyCount(), 0, "no setActivationPolicy call when nothing drifted")
    }

    // The inverse case: the user wants the Dock icon, so being demoted to
    // .accessory is the drift to correct.
    func testReassertRestoresRegularWhenUserWantsDockIcon() {
        let (g, policy, applyCount) = makeGuard(hidden: false, policy: .accessory)

        XCTAssertTrue(g.reassert())
        XCTAssertEqual(policy(), .regular)
        XCTAssertEqual(applyCount(), 1)
    }

    // Repeated drift must keep being corrected — the guard holds no "already
    // fixed this once" state that would let a second promotion stick.
    func testRepeatedDriftIsCorrectedEachTime() {
        let g = DockPolicyGuard()
        var current: NSApplication.ActivationPolicy = .accessory
        var applyCount = 0
        g.hideFromDock = { true }
        g.currentPolicy = { current }
        g.applyPolicy = { current = $0; applyCount += 1 }
        g.onCorrection = { _ in }

        for _ in 0..<3 {
            current = .regular              // AppKit promotes us again
            XCTAssertTrue(g.reassert())
            XCTAssertEqual(current, .accessory)
        }
        XCTAssertEqual(applyCount, 3)
    }

    func testCorrectionIsReportedForLogging() {
        let (g, _, _) = makeGuard(hidden: true, policy: .regular)
        var reported: [NSApplication.ActivationPolicy] = []
        g.onCorrection = { reported.append($0) }

        g.reassert()
        g.reassert()   // already correct — must not log again

        XCTAssertEqual(reported, [.accessory])
    }

    // start() is called from applicationDidFinishLaunching; a second call (a
    // re-entrant launch path, a future re-init) must not stack duplicate
    // observers and timers that would each fire a correction.
    @MainActor
    func testStartIsIdempotent() {
        let g = DockPolicyGuard()
        g.hideFromDock = { true }
        g.currentPolicy = { .accessory }
        g.applyPolicy = { _ in }
        g.onCorrection = { _ in }

        g.start()
        g.start()
        g.stop()
        // Stopping once must fully detach: a stopped guard that still held an
        // observer would keep touching NSApp after teardown.
        g.start()
        g.stop()
    }
}
