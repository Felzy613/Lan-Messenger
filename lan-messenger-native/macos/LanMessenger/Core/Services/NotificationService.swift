import Foundation
import UserNotifications

// Wraps UNUserNotificationCenter for inbound message and file notifications.
// Request authorization on first launch.
final class NotificationService {

    static let shared = NotificationService()
    private init() {}

    func requestAuthorization() {
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound, .badge]) { _, _ in }
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
        UNUserNotificationCenter.current().add(request, withCompletionHandler: nil)
    }
}
