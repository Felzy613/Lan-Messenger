import Foundation
import CoreGraphics

// What the host sees while somebody is watching, and where it sits.
//
// PROTOCOL.md requires it: "while a session is live the host must show a
// persistent indicator naming the viewer and the current grant level, with a
// stop control". The requirement is short and the reasoning behind each word is
// not, so it is written down here rather than rediscovered.
//
// **Persistent** means it cannot be dismissed, only stopped. An indicator with a
// close button is one people close.
//
// **Naming the viewer** means the name, not "a peer". The host has to be able to
// tell an expected session from an unexpected one at a glance, and "someone is
// viewing your screen" does not let them.
//
// **The current grant level** means viewing and control read differently and
// obviously. They are a single escalation apart and wildly different in
// consequence.
//
// The one design decision here that is not in the spec: **the indicator does not
// move.** It is not draggable, and it re-asserts its position whenever the
// screen configuration changes. That is deliberate and it is a security
// property rather than a layout preference — see `IndicatorPlacement`.

/// Everything the indicator shows, derived rather than stored, so the strip is a
/// pure function of session state and the clock.
struct RemoteHostIndicatorModel: Equatable {

    /// How loudly the strip presents itself. Viewing is a fact; control is a
    /// fact the host should not be able to overlook.
    enum Severity: Equatable {
        case watching
        case controlled
    }

    let peerName: String
    let grant: RemoteGrant
    let startedAt: Date

    init(peerName: String, grant: RemoteGrant, startedAt: Date) {
        self.peerName = peerName
        self.grant = grant
        self.startedAt = startedAt
    }

    /// Nothing to show when nothing is shared. The presenter hides the panel
    /// rather than drawing an empty one.
    var isVisible: Bool { grant != .none }

    var severity: Severity { grant == .control ? .controlled : .watching }

    /// Present tense and unhedged. "May be able to view" is the kind of phrasing
    /// that gets skimmed past.
    var headline: String {
        switch grant {
        case .none:    return ""
        case .viewing: return "\(peerName) is viewing your screen"
        case .control: return "\(peerName) is controlling your screen"
        }
    }

    /// Offered only when there is something to take back. Revoking control
    /// leaves the session running, which is usually what a host actually wants
    /// — "stop touching things" rather than "get out".
    var showsRevokeControl: Bool { grant == .control }

    /// `m:ss`, and `h:mm:ss` once it has been going long enough that the hour
    /// matters. Elapsed time is the quiet part of the indicator that catches the
    /// session somebody forgot they left open.
    func elapsed(at now: Date) -> String {
        let seconds = max(0, Int(now.timeIntervalSince(startedAt)))
        let (hours, minutes, remainder) = (seconds / 3600, (seconds % 3600) / 60, seconds % 60)
        return hours > 0
            ? String(format: "%d:%02d:%02d", hours, minutes, remainder)
            : String(format: "%d:%02d", minutes, remainder)
    }
}

/// Where the strip goes.
enum IndicatorPlacement {

    /// Gap from the top of the visible frame, clear of the menu bar and notch.
    static let topInset: CGFloat = 8

    /// Top centre of the screen's visible frame.
    ///
    /// **Fixed, and re-asserted rather than remembered.** A draggable indicator
    /// is a real hole once control has been granted: a viewer with the keyboard
    /// and mouse could drag the host's own warning off the edge of the screen
    /// and carry on working unobserved. There is no way to tell an injected drag
    /// from a real one — that is the entire point of input injection — so the
    /// answer is that neither can move it.
    ///
    /// The cost is a host who cannot shift it off something it covers. That is a
    /// small, visible annoyance; the alternative is an invisible one.
    static func origin(panelSize: CGSize, in visibleFrame: CGRect) -> CGPoint {
        let x = visibleFrame.midX - panelSize.width / 2
        // AppKit's origin is bottom-left, so "just below the top edge" subtracts.
        let y = visibleFrame.maxY - panelSize.height - topInset
        return CGPoint(x: x.rounded(), y: y.rounded())
    }

    /// Clamps the strip onto a screen it would otherwise hang off — a very
    /// narrow display, or a panel that grew with a long peer name.
    static func clamped(origin: CGPoint, panelSize: CGSize, in visibleFrame: CGRect) -> CGPoint {
        let x = min(max(origin.x, visibleFrame.minX), max(visibleFrame.maxX - panelSize.width,
                                                          visibleFrame.minX))
        let y = min(max(origin.y, visibleFrame.minY), max(visibleFrame.maxY - panelSize.height,
                                                          visibleFrame.minY))
        return CGPoint(x: x, y: y)
    }
}
