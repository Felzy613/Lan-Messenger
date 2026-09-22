import AppKit
import SwiftUI

// Puts the consent prompt on screen, counts it down, and answers `timeout` if
// nobody does.
//
// It is an `NSPanel` + `NSHostingView`, following `MediaBubbleView`'s preview
// panel, and **not** SwiftUI's `openWindow`. That is not a style preference:
// `WindowController.openWindow` is a single slot taking only a String id, so it
// cannot carry a session payload — and the app may be running with no window
// scene materialised at all (a login-item start, or a session whose window was
// closed with the red X), in which case there is nothing for a scene-based
// presentation to attach to. A panel needs neither.
//
// The countdown is the part worth reading twice. A prompt that waits forever is
// worse than one that gives up: the screen it guards may be on a desk nobody is
// sitting at, and the peer is meanwhile staring at a spinner with no way to tell
// a slow human from a dead one. So it declines itself, with the `timeout` token
// PROTOCOL.md already reserves.

enum RemoteConsentOutcome: Equatable {
    case accepted
    case declined(RemoteDeclineReason)
}

@MainActor
final class RemoteConsentPresenter {

    static let shared = RemoteConsentPresenter()

    private var panels: [String: ConsentPanel] = [:]   // keyed by request id

    init() {}

    /// Whether a prompt for this session is already on screen. A peer that can
    /// stack prompts can paper a screen with them, and the second one would in
    /// any case be answering for a session the first already decided.
    func isPresenting(sessionID: String) -> Bool {
        panels.keys.contains { $0.hasPrefix(sessionID) }
    }

    /// Shows the prompt. `onOutcome` fires exactly once, whatever happens —
    /// clicked, escaped, timed out, or dismissed because the session died.
    func present(_ request: RemoteConsentRequest,
                 now: @escaping () -> Date = Date.init,
                 onOutcome: @escaping (RemoteConsentOutcome) -> Void) {
        guard panels[request.id] == nil else { return }

        let panel = ConsentPanel(request: request, now: now) { [weak self] outcome in
            self?.close(request.id)
            onOutcome(outcome)
        }
        panels[request.id] = panel
        panel.show()

        NetLogger.remote(event: "consent_prompt", peer: request.peerIP,
                         sessionID: request.sessionID,
                         reason: "\(request.kind == .viewing ? "viewing" : "control") "
                               + "fp=\(request.fingerprint)")
    }

    /// Takes a prompt down without an answer — the session ended underneath it,
    /// or the peer withdrew. The outcome handler is still called, because a
    /// caller waiting on one exactly once must not be left waiting.
    func dismiss(sessionID: String, reason: RemoteDeclineReason) {
        for (id, panel) in panels where id.hasPrefix(sessionID) {
            panel.finish(.declined(reason))
        }
    }

    private func close(_ id: String) {
        panels.removeValue(forKey: id)
    }
}

// MARK: - The panel

/// `cancelOperation` is what Escape reaches. Declining on Escape is the point:
/// every accidental way out of this dialog has to land on "no".
private final class ConsentPanel: NSPanel {

    private let request: RemoteConsentRequest
    private let now: () -> Date
    private var onOutcome: ((RemoteConsentOutcome) -> Void)?
    private var ticker: Timer?
    private var secondsRemaining: Int

    init(request: RemoteConsentRequest,
         now: @escaping () -> Date,
         onOutcome: @escaping (RemoteConsentOutcome) -> Void) {
        self.request = request
        self.now = now
        self.onOutcome = onOutcome
        self.secondsRemaining = request.secondsRemaining(at: now())

        super.init(contentRect: NSRect(x: 0, y: 0, width: 420, height: 260),
                   styleMask: [.titled, .closable],
                   backing: .buffered,
                   defer: false)

        title = request.kind == .viewing ? "Screen Sharing Request" : "Control Request"
        isFloatingPanel = true
        // Above ordinary windows. A consent prompt that opens behind the
        // full-screen app somebody is working in will simply time out, and the
        // user's account of it is that the feature does not work.
        level = .floating
        hidesOnDeactivate = false
        isReleasedWhenClosed = false
        collectionBehavior = [.moveToActiveSpace, .fullScreenAuxiliary]

        contentView = NSHostingView(rootView: body())
        setContentSize(NSHostingView(rootView: body()).fittingSize)
        center()
    }

    private func body() -> some View {
        RemoteConsentView(
            request: request,
            secondsRemaining: secondsRemaining,
            onAccept: { [weak self] in self?.finish(.accepted) },
            onDecline: { [weak self] in self?.finish(.declined(.declined)) })
    }

    func show() {
        makeKeyAndOrderFront(nil)
        orderFrontRegardless()
        // Bounces the Dock icon. Deliberately not `NSApp.activate` — yanking
        // focus out of whatever somebody is typing into is how a stray Return
        // lands on a dialog they have not read. Return is wired to Decline for
        // the same reason, but not provoking the keystroke is better than
        // catching it.
        NSApp.requestUserAttention(.criticalRequest)
        startTicking()
    }

    /// `.common` run-loop mode, not the default. A plain scheduled timer stops
    /// firing while a menu is open or a window is being dragged, which would
    /// freeze the countdown exactly when a user has wandered off mid-gesture —
    /// and a countdown that silently pauses is worse than none, because the
    /// caller's own expiry still arrives on time.
    private func startTicking() {
        let timer = Timer(timeInterval: 0.5, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.tick() }
        }
        RunLoop.main.add(timer, forMode: .common)
        ticker = timer
    }

    private func tick() {
        let moment = now()
        if request.hasExpired(at: moment) {
            finish(.declined(.timeout))
            return
        }
        let remaining = request.secondsRemaining(at: moment)
        guard remaining != secondsRemaining else { return }
        secondsRemaining = remaining
        contentView = NSHostingView(rootView: body())
    }

    override func cancelOperation(_ sender: Any?) { finish(.declined(.declined)) }

    /// Also the close button: an X on a consent dialog means no.
    override func close() {
        finish(.declined(.declined))
        super.close()
    }

    /// Idempotent, and the only exit. Everything that can end this dialog — a
    /// click, Escape, the close button, the countdown, the session dying
    /// underneath it — arrives here, so the outcome fires exactly once.
    func finish(_ outcome: RemoteConsentOutcome) {
        guard let handler = onOutcome else { return }
        onOutcome = nil
        ticker?.invalidate()
        ticker = nil

        NetLogger.remote(event: "consent_outcome", peer: request.peerIP,
                         sessionID: request.sessionID,
                         reason: outcome == .accepted ? "accepted" : declineToken(outcome))
        orderOut(nil)
        handler(outcome)
    }

    private func declineToken(_ outcome: RemoteConsentOutcome) -> String {
        if case .declined(let reason) = outcome { return "declined:\(reason.rawValue)" }
        return "declined"
    }
}
