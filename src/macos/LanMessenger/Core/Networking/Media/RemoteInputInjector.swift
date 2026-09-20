import ApplicationServices
import CoreGraphics
import Foundation

// Turns decoded input records into real events on this Mac.
// Mirror of RemoteInputInjector.cs.
//
// This is the most dangerous object in the feature: everything it does was asked
// for by another computer. Three rules are enforced here rather than trusted from
// the caller, because a bug upstream must not become keyboard access:
//
//  1. **Nothing is injected without a control grant.**
//  2. **The kill shortcut is never injected.** A host being actively controlled
//     has to be able to stop the session, and a viewer that can press the stop
//     combination remotely can prevent exactly that.
//  3. **Coordinates are clamped, never trusted.** They are floats from a peer.
//
// Unlike screen capture, this needs the **Accessibility** grant, not Screen
// Recording — they are separate TCC permissions and being granted one says
// nothing about the other. `CGEvent.post` fails silently without it: no error, no
// exception, simply nothing happening, which is indistinguishable from a dead
// network. `isTrusted` is therefore checked before the first injection of a
// session and the answer is logged, so "control does nothing" has an entry
// explaining itself.

@MainActor
final class RemoteInputInjector {

    /// Read at every injection, never cached.
    private let grant: () -> RemoteGrant
    /// The rectangle being shared, in global display points. Normalized
    /// coordinates resolve against this and nothing else — a viewer sees the
    /// shared surface, so that is the only thing its 0..1 can mean.
    private let surface: () -> CGRect

    private var keysDown: Set<UInt16> = []
    private var unmappedSeen: Set<UInt16> = []
    private var refused = 0
    private var checkedTrust = false

    /// The event source. One per injector rather than one per event: a source
    /// carries the modifier state CGEvent uses to decide what a key means, and a
    /// fresh source per event forgets that a modifier is held.
    private let source = CGEventSource(stateID: .privateState)

    init(grant: @escaping () -> RemoteGrant,
         surface: @escaping () -> CGRect = RemoteInputInjector.mainDisplayBounds) {
        self.grant = grant
        self.surface = surface
    }

    static func mainDisplayBounds() -> CGRect {
        CGDisplayBounds(CGMainDisplayID())
    }

    /// Whether this process may post events at all.
    ///
    /// Separate from Screen Recording, and commonly not granted when that one is
    /// — the session will capture perfectly and then ignore every click.
    static var hasAccessibilityGrant: Bool {
        AXIsProcessTrusted()
    }

    // MARK: - Injection

    /// Injects a decoded burst. Silently does nothing without control.
    func inject(_ records: [RemoteInputRecord]) {
        guard grant() == .control else {
            refused += records.count
            return
        }

        if !checkedTrust {
            checkedTrust = true
            let trusted = Self.hasAccessibilityGrant
            NetLogger.remote(event: trusted ? "input_ready" : "error",
                             reason: trusted
                                ? "accessibility granted"
                                : "no Accessibility grant — CGEvent.post will do nothing silently")
        }

        for record in records { injectOne(record) }
    }

    private func injectOne(_ record: RemoteInputRecord) {
        switch record {
        case .pointerMove(let x, let y):
            postMouse(.mouseMoved, at: point(x, y), button: .left)

        case .pointerButton(let button, let down, let x, let y):
            let location = point(x, y)
            let type: CGEventType
            switch (button, down) {
            case (.left, true):    type = .leftMouseDown
            case (.left, false):   type = .leftMouseUp
            case (.right, true):   type = .rightMouseDown
            case (.right, false):  type = .rightMouseUp
            case (.middle, true):  type = .otherMouseDown
            case (.middle, false): type = .otherMouseUp
            }
            postMouse(type, at: location, button: cgButton(button))

        case .pointerScroll(let dx, let dy, _, _):
            let vertical = Int32(RemoteInputGeometry.clampScroll(Double(dy)))
            let horizontal = Int32(RemoteInputGeometry.clampScroll(Double(dx)))
            guard vertical != 0 || horizontal != 0 else { return }
            let event = CGEvent(scrollWheelEvent2Source: source,
                                units: .line,
                                wheelCount: 2,
                                wheel1: vertical,
                                wheel2: horizontal,
                                wheel3: 0)
            event?.post(tap: .cghidEventTap)

        case .key(let usage, let down, _, let modifiers):
            postKey(usage: usage, down: down, modifiers: modifiers)

        case .text(let string):
            postText(string)
        }
    }

    /// Normalized to the shared surface, clamped, and converted to global points.
    private func point(_ normalizedX: Float, _ normalizedY: Float) -> CGPoint {
        let rect = surface()
        let x = RemoteInputGeometry.clampUnit(Double(normalizedX))
        let y = RemoteInputGeometry.clampUnit(Double(normalizedY))
        return CGPoint(x: rect.origin.x + x * max(rect.width - 1, 0),
                       y: rect.origin.y + y * max(rect.height - 1, 0))
    }

    private func cgButton(_ button: RemotePointerButton) -> CGMouseButton {
        switch button {
        case .left:   return .left
        case .right:  return .right
        case .middle: return .center
        }
    }

    private func postMouse(_ type: CGEventType, at location: CGPoint, button: CGMouseButton) {
        // A drag is a move with a button held, and CGEvent needs to be told which
        // — posting `mouseMoved` while the left button is down makes the target
        // app see the pointer teleport rather than drag.
        var eventType = type
        if type == .mouseMoved {
            if heldMouseButtons.contains(.left) { eventType = .leftMouseDragged }
            else if heldMouseButtons.contains(.right) { eventType = .rightMouseDragged }
            else if heldMouseButtons.contains(.center) { eventType = .otherMouseDragged }
        }

        let event = CGEvent(mouseEventSource: source, mouseType: eventType,
                            mouseCursorPosition: location, mouseButton: button)
        event?.post(tap: .cghidEventTap)

        switch type {
        case .leftMouseDown:   heldMouseButtons.insert(.left)
        case .leftMouseUp:     heldMouseButtons.remove(.left)
        case .rightMouseDown:  heldMouseButtons.insert(.right)
        case .rightMouseUp:    heldMouseButtons.remove(.right)
        case .otherMouseDown:  heldMouseButtons.insert(.center)
        case .otherMouseUp:    heldMouseButtons.remove(.center)
        default: break
        }
    }

    private var heldMouseButtons: Set<CGMouseButton> = []

    private func postKey(usage: UInt16, down: Bool, modifiers: RemoteInputModifiers) {
        // Defence in depth. The viewer is required never to forward the kill
        // shortcut, but a host that relies on that is a host whose stop control
        // can be held down remotely.
        if down, completesKillShortcut(usage) {
            NetLogger.remote(event: "input_refused",
                             reason: "the kill shortcut may not be injected")
            return
        }

        guard let keyCode = HidKeyMap.virtualKey(for: usage) else {
            // Logged once per usage rather than per keystroke: a held key would
            // otherwise fill the log.
            if unmappedSeen.insert(usage).inserted {
                NetLogger.remote(event: "input_unmapped",
                                 reason: String(format: "HID usage 0x%02X", usage))
            }
            return
        }

        let event = CGEvent(keyboardEventSource: source, virtualKey: keyCode, keyDown: down)
        // Flags go on every event, not just the modifier's own key-down: a
        // key-down for `c` with no command flag is a literal `c`, however
        // recently command was pressed. That is the difference between copy and
        // typing a letter into the document.
        event?.flags = HidKeyMap.flags(for: modifiers)
        event?.post(tap: .cghidEventTap)

        if down { keysDown.insert(usage) } else { keysDown.remove(usage) }
    }

    /// Ctrl+Option+Command+Escape. Only the Escape completes it, so that is the
    /// one refused — refusing the modifiers would break every ordinary chord.
    private func completesKillShortcut(_ usage: UInt16) -> Bool {
        guard usage == 0x29 else { return false }      // Escape
        let control = keysDown.contains(0xE0) || keysDown.contains(0xE4)
        let option  = keysDown.contains(0xE2) || keysDown.contains(0xE6)
        let command = keysDown.contains(0xE3) || keysDown.contains(0xE7)
        return control && option && command
    }

    private func postText(_ string: String) {
        // The escape hatch for layout mismatch, emoji and IME. Posted as a
        // Unicode string rather than as key positions, because there is no key
        // position for most of what arrives here.
        for scalar in string.unicodeScalars {
            var units = Array(String(scalar).utf16)
            guard let down = CGEvent(keyboardEventSource: source, virtualKey: 0, keyDown: true),
                  let up = CGEvent(keyboardEventSource: source, virtualKey: 0, keyDown: false)
            else { continue }
            down.keyboardSetUnicodeString(stringLength: units.count, unicodeString: &units)
            up.keyboardSetUnicodeString(stringLength: units.count, unicodeString: &units)
            down.post(tap: .cghidEventTap)
            up.post(tap: .cghidEventTap)
        }
    }

    /// Lifts every key and button this injector is holding.
    ///
    /// A session that ends mid-chord otherwise leaves the host holding whatever
    /// was down, and a Mac with Command stuck behaves as if possessed. Called
    /// from every teardown path, including the ones that are themselves errors.
    func releaseEverything() {
        let held = keysDown
        keysDown.removeAll()
        for usage in held {
            guard let keyCode = HidKeyMap.virtualKey(for: usage) else { continue }
            let event = CGEvent(keyboardEventSource: source, virtualKey: keyCode, keyDown: false)
            event?.post(tap: .cghidEventTap)
        }

        let buttons = heldMouseButtons
        heldMouseButtons.removeAll()
        for button in buttons {
            let type: CGEventType = button == .left ? .leftMouseUp
                                  : button == .right ? .rightMouseUp : .otherMouseUp
            let event = CGEvent(mouseEventSource: source, mouseType: type,
                                mouseCursorPosition: .zero, mouseButton: button)
            event?.post(tap: .cghidEventTap)
        }

        if !held.isEmpty || !buttons.isEmpty {
            NetLogger.remote(event: "input_released",
                             reason: "\(held.count) key(s), \(buttons.count) button(s) lifted")
        }
    }
}
