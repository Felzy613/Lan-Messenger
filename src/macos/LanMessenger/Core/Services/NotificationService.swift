import Foundation
import UserNotifications

// Wraps UNUserNotificationCenter for inbound message and file notifications.
// Request authorization on first launch.
final class NotificationService: NSObject, UNUserNotificationCenterDelegate {

    static let shared = NotificationService()
    private override init() {}

    /// False when the process has no app bundle around it — which is exactly
    /// what `swift run` produces.
    ///
    /// `UNUserNotificationCenter.current()` does not fail gracefully there. It
    /// raises `NSInternalInconsistencyException` with "bundleProxyForCurrentProcess
    /// is nil", from inside a `dispatch_once`, during
    /// `applicationWillFinishLaunching` — so the app dies before its first
    /// window and the backtrace points at SwiftUI rather than at the cause.
    ///
    /// Guarding here rather than at the call sites keeps `swift run` usable as a
    /// development loop, which matters: the alternative is packaging a signed
    /// bundle to check any UI change.
    private var isBundled: Bool { Bundle.main.bundleIdentifier != nil }

    func requestAuthorization() {
        guard isBundled else {
            NetLogger.info("Notify", "unbundled process — notifications disabled")
            return
        }
        let center = UNUserNotificationCenter.current()
        // Must set delegate before requesting auth so willPresent fires correctly.
        center.delegate = self
        center.requestAuthorization(options: [.alert, .sound, .badge]) { _, _ in }
    }

    // Without this, macOS suppresses banners whenever the app is "active" —
    // which includes window-closed and minimized states, not just foreground.
    func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        willPresent notification: UNNotification,
        withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void
    ) {
        completionHandler([.banner, .sound])
    }

    func showMessage(from sender: String, text: String) {
        let content = UNMutableNotificationContent()
        content.title = sender
        content.body = String(text.prefix(80))
        content.sound = .default
        deliver(content: content, id: "msg-\(UUID().uuidString)")
    }

    func showFileReceived(from sender: String, filename: String) {
        let content = UNMutableNotificationContent()
        content.title = "File from \(sender)"
        content.body = filename
        content.sound = .default
        deliver(content: content, id: "file-\(UUID().uuidString)")
    }

    private func deliver(content: UNMutableNotificationContent, id: String) {
        let request = UNNotificationRequest(
            identifier: id,
            content: content,
            trigger: nil   // deliver immediately
        )
        guard isBundled else { return }
        UNUserNotificationCenter.current().add(request, withCompletionHandler: nil)
    }
}
