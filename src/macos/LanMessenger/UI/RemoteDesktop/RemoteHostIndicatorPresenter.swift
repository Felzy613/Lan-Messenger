import AppKit
import SwiftUI

// Keeps the host indicator on screen for as long as a session is live.
//
// Almost every line here is a window-level or collection-behaviour flag, and
// each one is load-bearing. The indicator is a safety device: a host who cannot
// see it has no way to know their screen is being watched, and every way that
// can happen is a way this feature hurts somebody.
//
//  * **Non-activating.** Clicking Stop must not pull the app forward and
//    interrupt whatever the host is doing. `.nonactivatingPanel` plus
//    `becomesKeyOnlyIfNeeded` gets a working button without a focus change.
//  * **Above everything, including full-screen apps.** `.statusBar` level with
//    `.fullScreenAuxiliary`, so a host working in full-screen Xcode still sees
//    it. An indicator that hides behind the thing you are doing is not one.
//  * **On every space, and stationary.** `.canJoinAllSpaces` so switching desk
//    does not leave it behind; `.stationary` so Mission Control does not sweep
//    it into a corner.
//  * **Not movable, by anyone.** `isMovable = false`, and the position is
//    re-asserted on every screen change. See `IndicatorPlacement.origin` — a
//    viewer holding the mouse could otherwise drag the host's own warning off
//    the edge of the screen, and nothing can tell an injected drag from a real
//    one.
//  * **Ignores the session's own capture.** Nothing here excludes the panel from
//    `SCStream`, so the viewer sees the indicator too. That is the right way
//    round: it costs a strip of transmitted pixels and it means a host can
//    confirm the viewer is seeing what they think they are.

@MainActor
final class RemoteHostIndicatorPresenter {

    static let shared = RemoteHostIndicatorPresenter()

    private var panel: IndicatorPanel?
    private var model: RemoteHostIndicatorModel?
    private var ticker: Timer?
    private var screenObserver: NSObjectProtocol?

    private let now: () -> Date

    init(now: @escaping () -> Date = Date.init) {
        self.now = now
    }

    var isShowing: Bool { panel != nil }

    /// Shows the indicator, or updates it in place when the grant changes.
    /// Calling this on every grant transition is the intended usage — the panel
    /// is created once and then only re-rendered.
    func show(peerName: String,
              grant: RemoteGrant,
              startedAt: Date,
              onRevokeControl: @escaping () -> Void,
              onStop: @escaping () -> Void) {
        guard grant != .none else { hide(); return }

        let model = RemoteHostIndicatorModel(peerName: peerName, grant: grant,
                                             startedAt: startedAt)
        self.model = model

        let panel = self.panel ?? makePanel()
        self.panel = panel
        panel.update(model: model, elapsed: model.elapsed(at: now()),
                     onRevokeControl: onRevokeControl, onStop: onStop)
        reposition()
        panel.orderFrontRegardless()

        startTicking()
        observeScreenChanges()

        NetLogger.remote(event: "indicator_shown",
                         reason: "\(grant.rawValue) peer=\(peerName)")
    }

    /// Takes it down. The session is over, or was never live.
    func hide() {
        ticker?.invalidate(); ticker = nil
        if let screenObserver {
            NotificationCenter.default.removeObserver(screenObserver)
            self.screenObserver = nil
        }
        panel?.orderOut(nil)
        panel = nil
        model = nil
    }

    // MARK: - Private

    private func makePanel() -> IndicatorPanel {
        let panel = IndicatorPanel(
            contentRect: NSRect(x: 0, y: 0, width: 360, height: 44),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false)

        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.isMovable = false
        panel.isMovableByWindowBackground = false
        panel.level = .statusBar
        panel.hidesOnDeactivate = false
        panel.becomesKeyOnlyIfNeeded = true
        panel.collectionBehavior = [.canJoinAllSpaces, .stationary,
                                    .fullScreenAuxiliary, .ignoresCycle]
        return panel
    }

    /// Re-asserts the position rather than remembering one. Called on every
    /// update and on every screen-configuration change, so a display being
    /// unplugged cannot leave the indicator stranded off-screen — which would be
    /// indistinguishable, to the host, from not sharing at all.
    private func reposition() {
        guard let panel else { return }
        let screen = NSScreen.main ?? NSScreen.screens.first
        guard let visible = screen?.visibleFrame else { return }

        panel.setContentSize(panel.fittingContentSize)
        let size = panel.frame.size
        let origin = IndicatorPlacement.clamped(
            origin: IndicatorPlacement.origin(panelSize: size, in: visible),
            panelSize: size, in: visible)
        panel.setFrameOrigin(origin)
    }

    /// One tick a second, in `.common` mode so the elapsed time does not freeze
    /// while a menu is open — the same trap the consent countdown carries, and
    /// here it would make a live session look stopped.
    private func startTicking() {
        guard ticker == nil else { return }
        let timer = Timer(timeInterval: 1.0, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.tick() }
        }
        RunLoop.main.add(timer, forMode: .common)
        ticker = timer
    }

    private func tick() {
        guard let panel, let model else { return }
        panel.updateElapsed(model.elapsed(at: now()))
    }

    private func observeScreenChanges() {
        guard screenObserver == nil else { return }
        screenObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification,
            object: nil, queue: .main
        ) { [weak self] _ in
            Task { @MainActor in self?.reposition() }
        }
    }
}

// MARK: - The panel

/// Borderless and non-activating. `canBecomeKey` is overridden so the buttons
/// work at all — a borderless panel refuses key status by default, and a Stop
/// button nobody can click is worse than no Stop button, because it looks like
/// one.
private final class IndicatorPanel: NSPanel {

    private var hosting: NSHostingView<RemoteHostIndicatorView>?
    private var model: RemoteHostIndicatorModel?
    private var onRevokeControl: () -> Void = {}
    private var onStop: () -> Void = {}

    override var canBecomeKey: Bool { true }
    /// Never the main window. It has no business owning the menu bar.
    override var canBecomeMain: Bool { false }

    var fittingContentSize: NSSize {
        hosting?.fittingSize ?? NSSize(width: 360, height: 44)
    }

    func update(model: RemoteHostIndicatorModel,
                elapsed: String,
                onRevokeControl: @escaping () -> Void,
                onStop: @escaping () -> Void) {
        self.model = model
        self.onRevokeControl = onRevokeControl
        self.onStop = onStop
        render(elapsed: elapsed)
    }

    func updateElapsed(_ elapsed: String) {
        render(elapsed: elapsed)
    }

    private func render(elapsed: String) {
        guard let model else { return }
        let view = RemoteHostIndicatorView(
            model: model, elapsed: elapsed,
            onRevokeControl: { [weak self] in self?.onRevokeControl() },
            onStop: { [weak self] in self?.onStop() })

        if let hosting {
            hosting.rootView = view
        } else {
            let hosting = NSHostingView(rootView: view)
            contentView = hosting
            self.hosting = hosting
        }
    }

    /// Escape must not dismiss it. The indicator is persistent by requirement:
    /// the only ways out are Stop, or the session ending.
    override func cancelOperation(_ sender: Any?) {}
}
