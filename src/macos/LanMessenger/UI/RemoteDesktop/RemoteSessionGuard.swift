import AppKit
import Carbon.HIToolbox

// Everything that can stop a live session without anyone clicking Stop.
//
// The indicator's Stop button is the obvious exit and the weakest one: once
// control is granted the host's mouse is contested, so pressing a button is a
// race with the person you are trying to stop. This class owns the exits that
// are not a race — a global hotkey, and the system events that mean nobody is
// there to press anything.
//
// It deliberately owns no session state. It observes, and it calls back. What
// actually happens on a stop is the caller's business, which keeps the ordering
// of teardown (release capture, take down the indicator, send `remote_end`) in
// one place instead of smeared across notification handlers.

@MainActor
final class RemoteSessionGuard {

    private var onStop: ((RemoteStopReason) -> Void)?
    private var observers: [(NotificationCenter, NSObjectProtocol)] = []
    private var hotKey: EventHotKeyRef?
    private var hotKeyHandler: EventHandlerRef?

    /// Set while armed so the C event callback, which cannot capture context,
    /// can find its way back. Only ever one session at a time — the registry
    /// enforces one in flight per peer and the host shares one screen.
    fileprivate static weak var armed: RemoteSessionGuard?

    init() {}

    var isArmed: Bool { onStop != nil }

    /// Starts watching. Idempotent in the sense that re-arming replaces the
    /// handler rather than stacking a second set of observers.
    func arm(onStop: @escaping (RemoteStopReason) -> Void) {
        disarm()
        self.onStop = onStop
        Self.armed = self

        observeWorkspace()
        observeScreenLock()
        observeAppTermination()
        registerHotKey()

        NetLogger.remote(event: "guard_armed",
                         reason: "kill=\(RemoteKillSwitch.shortcut.displayName)")
    }

    /// Stops watching. Safe to call when not armed, and called from the stop
    /// path itself — so it must not fire the callback it is tearing down.
    func disarm() {
        onStop = nil
        if Self.armed === self { Self.armed = nil }

        for (center, token) in observers { center.removeObserver(token) }
        observers.removeAll()
        unregisterHotKey()
    }

    /// Single funnel. Everything below arrives here, and the first reason wins:
    /// a lid closing produces sleep *and* a screen lock, and the session should
    /// stop once with the first cause rather than twice with both.
    fileprivate func trigger(_ reason: RemoteStopReason) {
        guard let handler = onStop else { return }
        disarm()
        NetLogger.remote(event: "guard_triggered", reason: reason.rawValue)
        handler(reason)
    }

    // MARK: - System events

    private func observeWorkspace() {
        let center = NSWorkspace.shared.notificationCenter
        add(center, NSWorkspace.willSleepNotification, .systemSleep)
        // Fast user switching. The session belongs to the account that agreed to
        // it, not to whoever sits down next.
        add(center, NSWorkspace.sessionDidResignActiveNotification, .userSwitched)
    }

    /// Screen lock is only published on the *distributed* notification centre,
    /// under a name with no public constant. There is no AppKit equivalent, and
    /// without it a locked Mac keeps streaming a lock screen — and then whatever
    /// is behind it when the host comes back and types their password.
    private func observeScreenLock() {
        let center = DistributedNotificationCenter.default()
        let token = center.addObserver(
            forName: Notification.Name("com.apple.screenIsLocked"),
            object: nil, queue: .main
        ) { [weak self] _ in
            Task { @MainActor in self?.trigger(.screenLocked) }
        }
        observers.append((center, token))
    }

    private func observeAppTermination() {
        add(NotificationCenter.default, NSApplication.willTerminateNotification, .appQuit)
    }

    private func add(_ center: NotificationCenter, _ name: Notification.Name,
                     _ reason: RemoteStopReason) {
        let token = center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.trigger(reason) }
        }
        observers.append((center, token))
    }

    /// Called by the network layer when every usable interface has gone. Not a
    /// notification of its own because `NetworkInterfaceMonitor` already owns
    /// that signal and a second observer would be a second opinion.
    func networkBecameUnavailable() { trigger(.networkLost) }

    // MARK: - The kill shortcut

    /// Carbon's `RegisterEventHotKey`, not an `NSEvent` global monitor.
    ///
    /// The monitor route needs the Accessibility grant, and the whole point of
    /// this shortcut is to work when other things have gone wrong — including a
    /// grant that was never given or has been revoked. Carbon needs no
    /// permission at all, and it fires whatever app is frontmost, which is the
    /// requirement: a host being actively controlled is by definition not
    /// looking at our window.
    private func registerHotKey() {
        var eventType = EventTypeSpec(eventClass: OSType(kEventClassKeyboard),
                                      eventKind: UInt32(kEventHotKeyPressed))

        InstallEventHandler(GetApplicationEventTarget(), { _, event, _ -> OSStatus in
            // No context pointer: the callback is a C function and cannot
            // capture. `armed` is the way back, and there is only ever one.
            var id = EventHotKeyID()
            GetEventParameter(event, EventParamName(kEventParamDirectObject),
                              EventParamType(typeEventHotKeyID), nil,
                              MemoryLayout<EventHotKeyID>.size, nil, &id)
            guard id.signature == RemoteSessionGuard.hotKeySignature else { return noErr }
            DispatchQueue.main.async {
                MainActor.assumeIsolated { RemoteSessionGuard.armed?.trigger(.killSwitch) }
            }
            return noErr
        }, 1, &eventType, nil, &hotKeyHandler)

        let id = EventHotKeyID(signature: Self.hotKeySignature, id: 1)
        let status = RegisterEventHotKey(RemoteKillSwitch.shortcut.keyCode,
                                         RemoteKillSwitch.shortcut.modifiers,
                                         id, GetApplicationEventTarget(), 0, &hotKey)
        if status != noErr {
            // Another app already owns the combination. The session is still
            // stoppable from the indicator, but the guarantee is gone, so this
            // is worth an error rather than a shrug.
            NetLogger.remote(event: "error",
                             reason: "kill switch unavailable (\(status)) — "
                                   + "\(RemoteKillSwitch.shortcut.displayName) is taken")
        }
    }

    private func unregisterHotKey() {
        if let hotKey { UnregisterEventHotKey(hotKey) }
        hotKey = nil
        if let hotKeyHandler { RemoveEventHandler(hotKeyHandler) }
        hotKeyHandler = nil
    }

    /// 'LMRD' — LAN Messenger Remote Desktop.
    fileprivate static let hotKeySignature: OSType = 0x4C4D5244
}
