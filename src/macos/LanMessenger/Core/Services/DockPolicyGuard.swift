import Foundation
import AppKit

/// Keeps the process's Dock presence in sync with the `hideFromDock` preference.
///
/// Setting the activation policy once at launch is not enough. AppKit promotes
/// an `.accessory` process back to `.regular` as a side effect of ordinary UI
/// work — materialising a `Window` scene, running a modal panel or open/save
/// dialog, being re-activated after the updater relaunches the bundle. Every
/// promotion puts a Dock icon back on screen even though the user turned it
/// off, which is the "icon randomly appears in the Dock" report.
///
/// There is no notification for "the activation policy changed", so the guard
/// re-asserts the preference on the events that tend to precede a promotion
/// (app activation, a window becoming key/main) plus a slow safety-net tick for
/// the promotions that happen with no observable event at all.
///
/// The three closures below are the only contact with `NSApp`, so the decision
/// logic is exercised in tests without a running `NSApplication`.
final class DockPolicyGuard {
    static let shared = DockPolicyGuard()

    /// Reads the user's preference. Overridden in tests.
    var hideFromDock: () -> Bool = { ConfigStore.shared.config.hideFromDock }
    /// Reads the process's current activation policy. Overridden in tests.
    var currentPolicy: () -> NSApplication.ActivationPolicy = { NSApp.activationPolicy() }
    /// Applies an activation policy. Overridden in tests.
    var applyPolicy: (NSApplication.ActivationPolicy) -> Void = { NSApp.setActivationPolicy($0) }
    /// Emits a log line when a drift was corrected. Overridden in tests.
    var onCorrection: (NSApplication.ActivationPolicy) -> Void = { policy in
        NetLogger.ui(event: "dock_policy_corrected",
                     detail: policy == .accessory ? "accessory" : "regular")
    }

    /// Safety-net interval. Long enough to be free, short enough that a stray
    /// Dock icon disappears before the user reaches for the mouse.
    static let tickInterval: TimeInterval = 3

    private var observers: [NSObjectProtocol] = []
    private var timer: Timer?

    /// The policy the preference asks for.
    static func desiredPolicy(hideFromDock: Bool) -> NSApplication.ActivationPolicy {
        hideFromDock ? .accessory : .regular
    }

    /// Re-applies the preference. Returns true when the policy had drifted and
    /// was corrected, false when it was already right.
    @discardableResult
    func reassert() -> Bool {
        let want = Self.desiredPolicy(hideFromDock: hideFromDock())
        guard currentPolicy() != want else { return false }
        applyPolicy(want)
        onCorrection(want)
        return true
    }

    /// Starts observing. Safe to call more than once — a second call is a no-op
    /// so a re-entrant launch path cannot stack duplicate observers or timers.
    @MainActor
    func start() {
        guard observers.isEmpty, timer == nil else { return }

        let center = NotificationCenter.default
        let names: [Notification.Name] = [
            NSApplication.didBecomeActiveNotification,
            NSApplication.didResignActiveNotification,
            NSApplication.didUnhideNotification,
            NSWindow.didBecomeKeyNotification,
            NSWindow.didBecomeMainNotification,
        ]
        for name in names {
            observers.append(center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                // The promotion lands after AppKit finishes the current event,
                // so correct on the next runloop turn rather than inside it.
                DispatchQueue.main.async { self?.reassert() }
            })
        }

        let t = Timer(timeInterval: Self.tickInterval, repeats: true) { [weak self] _ in
            DispatchQueue.main.async { self?.reassert() }
        }
        // .common so the tick keeps firing while a menu or a resize loop is up —
        // exactly the moments AppKit is most likely to have promoted us.
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    @MainActor
    func stop() {
        for observer in observers { NotificationCenter.default.removeObserver(observer) }
        observers.removeAll()
        timer?.invalidate()
        timer = nil
    }
}
