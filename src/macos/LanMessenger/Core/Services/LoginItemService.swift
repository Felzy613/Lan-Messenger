import Foundation
import ServiceManagement

// Manages "Launch at Login" using the modern SMAppService API (macOS 13+).
//
// SMAppService.mainApp registers the running .app bundle as a per-user Login Item,
// which is the same mechanism System Settings → General → Login Items exposes. The
// system handles the actual launch — there is no LaunchAgent plist to install and
// no privileged helper to maintain. The user can also disable us from System
// Settings; we resync our stored preference from the live status on every read.
//
// Not marked @MainActor — SMAppService is thread-safe; callers in this app
// happen to invoke from view-modifier closures (already on the main actor),
// but we don't force that contract here.
enum LoginItemService {

    enum Status: Equatable {
        case enabled
        case disabled
        case requiresApproval   // user must approve in System Settings → Login Items
        case notSupported       // running on a build that can't be a login item (e.g. unsigned in /tmp)
        case error(String)
    }

    static var isSupported: Bool {
        // SMAppService requires macOS 13+. Our deployment target is 13.0, so this
        // is always true at runtime — but keep the flag so future deployment-target
        // shifts have a single chokepoint.
        if #available(macOS 13.0, *) { return true }
        return false
    }

    static var currentStatus: Status {
        guard #available(macOS 13.0, *) else { return .notSupported }
        switch SMAppService.mainApp.status {
        case .enabled:           return .enabled
        case .notRegistered:     return .disabled
        case .notFound:          return .disabled
        case .requiresApproval:  return .requiresApproval
        @unknown default:        return .disabled
        }
    }

    // Toggle: register or unregister depending on `enabled`.
    // Returns the resulting status so the caller can update the UI in one step.
    @discardableResult
    static func setEnabled(_ enabled: Bool) -> Status {
        guard #available(macOS 13.0, *) else { return .notSupported }
        let svc = SMAppService.mainApp
        do {
            if enabled {
                try svc.register()
            } else {
                try svc.unregister()
            }
            return currentStatus
        } catch {
            // Common failure modes:
            //  - App is not in /Applications and unsigned → SMAppService refuses
            //  - User previously disabled us from System Settings (requires re-approval)
            return .error(error.localizedDescription)
        }
    }

    // Open System Settings → Login Items so the user can re-enable us after the
    // OS has put us in "requires approval" state. There is no API for the system
    // to grant the approval programmatically — only the user can.
    static func openSystemLoginItemsPane() {
        guard #available(macOS 13.0, *) else { return }
        SMAppService.openSystemSettingsLoginItems()
    }
}
