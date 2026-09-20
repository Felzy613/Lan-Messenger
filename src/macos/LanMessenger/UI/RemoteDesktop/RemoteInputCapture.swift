import AppKit
import CoreGraphics

// Turns the viewer's own mouse and keyboard into wire records.
//
// The mirror of the injector, and the easier half: nothing here is dangerous on
// its own, because the host decides what it will act on. What it must get right
// is geometry and the one shortcut it must never send.
//
// **Coordinates are normalized to the video surface, not this view.** The layer
// is aspect-fitted, so there are black bars whenever the window's shape does not
// match the remote screen's, and a click in a bar is not a click on the remote
// machine at all. Normalizing against the view would make every coordinate wrong
// by the width of those bars — subtly, and worse the further the window is from
// the right aspect.
//
// **The kill shortcut is never forwarded.** It is how a host stops a session
// they have lost control of, and a viewer that could press it remotely could
// stop the host stopping them. It is dropped here, and refused again on the host
// — see `RemoteInputInjector`.
//
// Key repeat is forwarded rather than synthesised. Neither platform auto-repeats
// injected key-downs, so a host waiting for repeats it will never generate shows
// one character where the user held a key.

class RemoteInputCaptureView: NSView {

    /// Where records go. Set by the window controller.
    var onRecords: (([RemoteInputRecord]) -> Void)?

    /// The remote screen's pixel size, for the aspect-fit arithmetic. Nil until
    /// the first frame has told us.
    var remoteSize: CGSize? {
        didSet { needsLayout = true }
    }

    /// Whether to capture at all. False until control is granted, so a viewer
    /// that is only watching never sends anything.
    var isCapturing = false {
        didSet {
            if isCapturing { window?.makeFirstResponder(self) }
            else { releaseHeldKeys() }
        }
    }

    private var heldUsages: Set<UInt16> = []

    override var acceptsFirstResponder: Bool { isCapturing }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    // MARK: - Geometry

    /// The rectangle the video actually occupies inside this view.
    ///
    /// Aspect-fitted, which is what `AVSampleBufferDisplayLayer`'s
    /// `.resizeAspect` does, so this has to agree with it exactly — a
    /// disagreement is a pointer offset that grows towards the edges.
    private var videoRect: CGRect {
        guard let remote = remoteSize, remote.width > 0, remote.height > 0 else { return bounds }
        let scale = min(bounds.width / remote.width, bounds.height / remote.height)
        let size = CGSize(width: remote.width * scale, height: remote.height * scale)
        return CGRect(x: bounds.midX - size.width / 2,
                      y: bounds.midY - size.height / 2,
                      width: size.width, height: size.height)
    }

    /// Normalized position inside the video, or nil for a point in the letterbox.
    ///
    /// Nil rather than clamped: a click in the black bar is not a click at the
    /// edge of the remote screen, and pretending otherwise puts the pointer
    /// somewhere the user did not aim.
    private func normalized(_ event: NSEvent) -> (Float, Float)? {
        let point = convert(event.locationInWindow, from: nil)
        let rect = videoRect
        guard rect.width > 0, rect.height > 0, rect.contains(point) else { return nil }

        let x = (point.x - rect.minX) / rect.width
        // AppKit's origin is bottom-left; the wire's is top-left, like every
        // screen coordinate system the host will resolve against.
        let y = 1 - (point.y - rect.minY) / rect.height
        return (Float(x), Float(y))
    }

    // MARK: - Mouse

    override func mouseMoved(with event: NSEvent)      { sendPointer(event) }
    override func mouseDragged(with event: NSEvent)    { sendPointer(event) }
    override func rightMouseDragged(with event: NSEvent) { sendPointer(event) }
    override func otherMouseDragged(with event: NSEvent) { sendPointer(event) }

    override func mouseDown(with event: NSEvent)       { sendButton(event, .left, down: true) }
    override func mouseUp(with event: NSEvent)         { sendButton(event, .left, down: false) }
    override func rightMouseDown(with event: NSEvent)  { sendButton(event, .right, down: true) }
    override func rightMouseUp(with event: NSEvent)    { sendButton(event, .right, down: false) }
    override func otherMouseDown(with event: NSEvent)  { sendButton(event, .middle, down: true) }
    override func otherMouseUp(with event: NSEvent)    { sendButton(event, .middle, down: false) }

    override func scrollWheel(with event: NSEvent) {
        guard isCapturing, let (x, y) = normalized(event) else { return }
        // AppKit reports lines for a wheel and points for a trackpad; the wire
        // carries lines, so a trackpad's pixel deltas are scaled down rather
        // than sent as hundreds of lines.
        let divisor: CGFloat = event.hasPreciseScrollingDeltas ? 16 : 1
        let dx = Float(event.scrollingDeltaX / divisor)
        let dy = Float(event.scrollingDeltaY / divisor)
        guard dx != 0 || dy != 0 else { return }
        onRecords?([.pointerScroll(dx: dx, dy: dy, x: x, y: y)])
    }

    private func sendPointer(_ event: NSEvent) {
        guard let (x, y) = accepted(event, "move") else { return }
        onRecords?([.pointerMove(x: x, y: y)])
    }

    private func sendButton(_ event: NSEvent, _ button: RemotePointerButton, down: Bool) {
        guard let (x, y) = accepted(event, down ? "button_down" : "button_up") else { return }
        onRecords?([.pointerButton(button: button, down: down, x: x, y: y)])
    }

    /// The two reasons a pointer event produces nothing, said out loud.
    ///
    /// Both are silent by design in the hot path — a viewer that is only
    /// watching must send nothing, and a click in a letterbox bar is not a click
    /// on the remote machine. But "the pointer does nothing" has exactly these
    /// two causes and no way to tell them apart from outside, so the first few
    /// of each are logged.
    private func accepted(_ event: NSEvent, _ what: String) -> (Float, Float)? {
        guard isCapturing else {
            if noteRejection("not capturing") { }
            return nil
        }
        guard let point = normalized(event) else {
            if noteRejection("outside the video rectangle") { }
            return nil
        }
        if accepted < 3 {
            accepted += 1
            NetLogger.remote(event: "input_captured",
                             reason: "\(what) at \(point.0), \(point.1)")
        }
        return point
    }

    private var accepted = 0
    private var rejected: [String: Int] = [:]

    private func noteRejection(_ reason: String) -> Bool {
        let seen = (rejected[reason] ?? 0) + 1
        rejected[reason] = seen
        if seen <= 3 {
            NetLogger.remote(event: "input_not_captured", reason: reason)
        }
        return true
    }

    // MARK: - Keyboard

    override func keyDown(with event: NSEvent) {
        forward(event, down: true, repeating: event.isARepeat)
    }

    override func keyUp(with event: NSEvent) {
        forward(event, down: false, repeating: false)
    }

    override func flagsChanged(with event: NSEvent) {
        // Modifiers arrive as a flags change rather than key events, so their
        // down/up has to be inferred from whether the flag is now set.
        guard isCapturing,
              let usage = RemoteHidUsage.usage(forVirtualKey: event.keyCode) else { return }
        let down = isModifierDown(usage, flags: event.modifierFlags)
        emitKey(usage: usage, down: down, repeating: false, flags: event.modifierFlags)
    }

    private func isModifierDown(_ usage: UInt16, flags: NSEvent.ModifierFlags) -> Bool {
        switch usage {
        case 0xE0, 0xE4: return flags.contains(.control)
        case 0xE1, 0xE5: return flags.contains(.shift)
        case 0xE2, 0xE6: return flags.contains(.option)
        case 0xE3, 0xE7: return flags.contains(.command)
        case 0x39:       return flags.contains(.capsLock)
        default:         return false
        }
    }

    private func forward(_ event: NSEvent, down: Bool, repeating: Bool) {
        guard isCapturing else { return super.keyDown(with: event) }

        // Never forwarded. This is the host's way out.
        if RemoteKillSwitch.isReserved(keyCode: UInt32(event.keyCode),
                                       modifiers: carbonModifiers(event.modifierFlags)) {
            NetLogger.remote(event: "input_withheld", reason: "kill shortcut not forwarded")
            return
        }

        guard let usage = RemoteHidUsage.usage(forVirtualKey: event.keyCode) else { return }
        emitKey(usage: usage, down: down, repeating: repeating, flags: event.modifierFlags)
    }

    private func emitKey(usage: UInt16, down: Bool, repeating: Bool,
                         flags: NSEvent.ModifierFlags) {
        var modifiers: RemoteInputModifiers = []
        if flags.contains(.shift)    { modifiers.insert(.shift) }
        if flags.contains(.control)  { modifiers.insert(.control) }
        if flags.contains(.option)   { modifiers.insert(.option) }
        if flags.contains(.command)  { modifiers.insert(.meta) }
        if flags.contains(.capsLock) { modifiers.insert(.capsLock) }

        if down { heldUsages.insert(usage) } else { heldUsages.remove(usage) }
        onRecords?([.key(usage: usage, down: down, repeating: repeating, modifiers: modifiers)])
    }

    /// Lifts anything this view believes is held.
    ///
    /// Called when capture stops, when the window loses focus, and at teardown.
    /// Without it, releasing a key outside the window leaves the host holding it
    /// — the viewer never sees the key-up, so it never sends one.
    func releaseHeldKeys() {
        guard !heldUsages.isEmpty else { return }
        let records = heldUsages.map {
            RemoteInputRecord.key(usage: $0, down: false, repeating: false, modifiers: [])
        }
        heldUsages.removeAll()
        onRecords?(records)
        NetLogger.remote(event: "input_released",
                         reason: "\(records.count) key(s) lifted by the viewer")
    }

    override func resignFirstResponder() -> Bool {
        // Focus leaving is the common way a key-up goes missing.
        releaseHeldKeys()
        return super.resignFirstResponder()
    }

    private func carbonModifiers(_ flags: NSEvent.ModifierFlags) -> UInt32 {
        var value: UInt32 = 0
        if flags.contains(.command) { value |= 0x0100 }
        if flags.contains(.shift)   { value |= 0x0200 }
        if flags.contains(.option)  { value |= 0x0800 }
        if flags.contains(.control) { value |= 0x1000 }
        return value
    }

    // MARK: - Tracking

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        for area in trackingAreas { removeTrackingArea(area) }
        // Without a tracking area there are no mouseMoved events at all, only
        // drags — the pointer would appear to teleport between clicks.
        addTrackingArea(NSTrackingArea(
            rect: bounds,
            options: [.activeInKeyWindow, .mouseMoved, .inVisibleRect],
            owner: self))
    }
}

/// macOS virtual key code to USB HID usage — the inverse of `HidKeyMap`.
///
/// Built by inverting that table rather than written out again, so the two can
/// never disagree: a second hand-written table is a second place for the same
/// typo, and this direction is the one that decides what gets sent.
enum RemoteHidUsage {

    private static let byVirtualKey: [CGKeyCode: UInt16] = {
        var map: [CGKeyCode: UInt16] = [:]
        // Every usage the forward table knows, inverted. The forward table is
        // the single source of truth for which keys exist at all.
        for usage in UInt16(0x00)...UInt16(0xFF) {
            if let code = HidKeyMap.virtualKey(for: usage) {
                map[code] = usage
            }
        }
        return map
    }()

    static func usage(forVirtualKey keyCode: UInt16) -> UInt16? {
        byVirtualKey[CGKeyCode(keyCode)]
    }
}
