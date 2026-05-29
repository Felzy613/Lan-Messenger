import SwiftUI
import AppKit

// MARK: - App delegate
// We use a tiny AppDelegate so the app keeps running when the user closes the
// main window with the red "X". The menu-bar item provides a way back in.

final class LanMessengerAppDelegate: NSObject, NSApplicationDelegate {
    // Apply the dock policy as early as possible — before any NSWindow gets a
    // chance to materialize. Flipping to .accessory only after the main
    // SwiftUI window appears is unreliable: AppKit has already promoted the
    // app to .regular, and macOS sometimes leaves a vestigial Dock icon
    // until the user toggles the setting twice. Reading the config here also
    // means a relaunch picks up the persisted preference automatically.
    func applicationWillFinishLaunching(_ notification: Notification) {
        let hideFromDock = ConfigStore.shared.config.hideFromDock
        NSApp.setActivationPolicy(hideFromDock ? .accessory : .regular)
    }

    // Don't quit when the last window closes — we live in the menu bar.
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        false
    }

    // When the user clicks the dock icon (if visible) or relaunches, surface the window again.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows: Bool) -> Bool {
        if !hasVisibleWindows {
            WindowController.showMainWindow()
        }
        return true
    }
}

// MARK: - Window helper
// Holds the action used to surface the main window. SwiftUI's `openWindow`
// environment value is only available inside a View, so we capture it once
// at root-view appear time and re-use it from menu-bar buttons / AppDelegate.

enum WindowController {
    static var openWindow: ((String) -> Void)?

    static func showMainWindow() {
        // Only promote to .regular (dock visible) if the user has chosen to show
        // the dock icon. When hideFromDock is true, stay in .accessory mode —
        // windows can still be shown and focused without a dock tile.
        if !ConfigStore.shared.config.hideFromDock {
            NSApp.setActivationPolicy(.regular)
        }
        NSApp.activate(ignoringOtherApps: true)

        // Bring any existing main window to the front right away.
        var foundExisting = false
        for w in NSApp.windows where w.canBecomeMain && !(w is NSPanel) {
            w.makeKeyAndOrderFront(nil)
            foundExisting = true
            break
        }

        // Also ask SwiftUI to open/resurface the window scene so a fresh window
        // is created if the previous one was destroyed via the red-X button.
        if let open = openWindow {
            open("main")
        }

        // When no window existed, SwiftUI creates one asynchronously.  Poll for
        // it over the next ~400 ms and raise it once it appears so it lands on
        // top rather than behind the previously-active app.
        if !foundExisting {
            bringNewWindowToFront(retries: 8)
        }
    }

    // Polls NSApp.windows until a main-eligible window appears, then raises it.
    private static func bringNewWindowToFront(retries: Int) {
        guard retries > 0 else { return }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) {
            for w in NSApp.windows where w.canBecomeMain && !(w is NSPanel) {
                w.makeKeyAndOrderFront(nil)
                NSApp.activate(ignoringOtherApps: true)
                return
            }
            bringNewWindowToFront(retries: retries - 1)
        }
    }
}

@main
struct LanMessengerApp: App {

    @NSApplicationDelegateAdaptor(LanMessengerAppDelegate.self) var appDelegate
    @StateObject private var appModel = AppModel()

    var body: some Scene {
        Window("LAN Messenger", id: "main") {
            ContentView()
                .environmentObject(appModel)
                .frame(minWidth: 720, minHeight: 500)
                .captureOpenWindow()
        }
        .windowStyle(.titleBar)
        .windowToolbarStyle(.unified)
        .commands {
            CommandGroup(replacing: .newItem) {}   // no "New Window"
            CommandGroup(after: .windowArrangement) {
                Button("Show Main Window") { WindowController.showMainWindow() }
                    .keyboardShortcut("1", modifiers: [.command])
            }
        }

        MenuBarExtra("LAN Messenger", systemImage: "message.fill") {
            TrayMenuView()
                .environmentObject(appModel)
        }
        .menuBarExtraStyle(.menu)
    }
}

// Captures the SwiftUI openWindow action once the root view appears so the
// menu-bar tray and AppDelegate can re-surface the main window.
private struct CaptureOpenWindow: ViewModifier {
    @Environment(\.openWindow) private var openWindow
    func body(content: Content) -> some View {
        content.onAppear {
            WindowController.openWindow = { id in openWindow(id: id) }
        }
    }
}
private extension View {
    func captureOpenWindow() -> some View { modifier(CaptureOpenWindow()) }
}

// MARK: - Root split view

struct ContentView: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        NavigationSplitView {
            SidebarView()
                .environmentObject(model)
        } detail: {
            if let ip = model.selectedPeerIP {
                ChatView(peerIP: ip)
                    .environmentObject(model)
                    .id(ip)     // re-create the view when peer changes
            } else {
                emptyState
            }
        }
        .sheet(isPresented: $model.showMigrationPrompt) {
            MigrationView()
                .environmentObject(model)
        }
    }

    private var emptyState: some View {
        VStack(spacing: 16) {
            Image(systemName: "bubble.left.and.bubble.right")
                .font(.system(size: 52))
                .foregroundStyle(.quaternary)
            Text("No conversation selected")
                .font(.title3)
                .foregroundStyle(.secondary)
            Text("Pick a peer from the sidebar, or wait for one to appear on the LAN.")
                .font(.callout)
                .foregroundStyle(.tertiary)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 260)
        }
    }
}

// MARK: - Menu bar extra

struct TrayMenuView: View {
    @EnvironmentObject var model: AppModel

    private var totalUnread: Int {
        model.conversations.reduce(0) { $0 + $1.unreadCount }
    }

    var body: some View {
        Text(totalUnread > 0
             ? "LAN Messenger — \(totalUnread) unread"
             : "LAN Messenger")
            .font(.headline)

        Divider()

        Button("Open LAN Messenger") {
            WindowController.showMainWindow()
        }
        .keyboardShortcut("o", modifiers: [.command])

        Divider()

        if model.conversations.isEmpty {
            Text("No conversations")
                .foregroundStyle(.secondary)
        } else {
            Text("Conversations").font(.caption)
            ForEach(model.conversations.prefix(8)) { conv in
                Button {
                    model.selectedPeerIP = conv.peerIP
                    WindowController.showMainWindow()
                } label: {
                    HStack {
                        Text(conv.peerName)
                        if conv.unreadCount > 0 {
                            Spacer()
                            Text("\(conv.unreadCount)")
                                .foregroundStyle(Theme.accent)
                        }
                    }
                }
            }
        }

        Divider()
        Button("Quit LAN Messenger") { NSApp.terminate(nil) }
            .keyboardShortcut("q", modifiers: [.command])
    }
}

// MARK: - First-launch migration sheet

struct MigrationView: View {
    @EnvironmentObject var model: AppModel
    @Environment(\.dismiss) var dismiss

    var body: some View {
        VStack(spacing: 24) {
            Image(systemName: "arrow.triangle.2.circlepath.circle.fill")
                .font(.system(size: 52))
                .foregroundStyle(Theme.accent)

            Text("Import from Python App")
                .font(.title2.bold())

            Text("A LAN Messenger config was found at ~/.lan_messenger/. Would you like to import your existing identity and chat history, or start fresh with a new key?")
                .multilineTextAlignment(.center)
                .foregroundStyle(.secondary)
                .frame(maxWidth: 340)

            HStack(spacing: 16) {
                Button("Start Fresh") {
                    model.acceptMigrationWithFreshKey()
                    dismiss()
                }
                .buttonStyle(.bordered)

                Button("Import Existing Key & History") {
                    model.acceptMigrationWithExistingKey()
                    dismiss()
                }
                .buttonStyle(.borderedProminent)
                .tint(Theme.accent)
            }
        }
        .padding(32)
        .frame(width: 440)
    }
}
