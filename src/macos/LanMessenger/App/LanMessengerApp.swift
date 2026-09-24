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
        // Before anything else: a crash during launch is exactly the one we
        // have no other record of.
        CrashReporter.install()
        CrashReporter.reportPreviousRunIfCrashed()
        NetLogger.ui(event: "app_launch")

        let hideFromDock = ConfigStore.shared.config.hideFromDock
        NSApp.setActivationPolicy(DockPolicyGuard.desiredPolicy(hideFromDock: hideFromDock))
    }

    // Two things have to happen once AppKit is up, and neither can be left to
    // SwiftUI's defaults:
    //
    //  1. Surface the main window and pull the app to the front. A plain launch
    //     otherwise lands *behind* whatever was already on screen, and a launch
    //     at login can come up with the Window scene never materialised at all —
    //     which used to be unrecoverable, because `showMainWindow()` had no
    //     window to raise and no captured `openWindow` action to create one.
    //  2. Start the Dock-policy guard, so an AppKit promotion back to .regular
    //     can't leave a Dock icon on screen the user has switched off.
    func applicationDidFinishLaunching(_ notification: Notification) {
        DockPolicyGuard.shared.start()
        WindowController.showMainWindow()
    }

    // Don't quit when the last window closes — we live in the menu bar.
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        false
    }

    // Announce our departure so peers flip us offline instantly instead of
    // waiting out the silence timeout. This must be synchronous — the process
    // exits right after, so a Task/async hop would never run. The send itself
    // is a non-blocking UDP sendto handed to the kernel.
    func applicationWillTerminate(_ notification: Notification) {
        AppModel.shared?.sendGoodbyeOnTerminate()
        NetLogger.ui(event: "app_terminate")
        CrashReporter.noteCleanShutdown()
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

    /// SwiftUI's main window, as opposed to the MenuBarExtra's status-item
    /// window and any media-preview panel.
    private static func isMainWindow(_ w: NSWindow) -> Bool {
        w.canBecomeMain && !(w is NSPanel)
    }

    static func showMainWindow() {
        // Only promote to .regular (dock visible) if the user has chosen to show
        // the dock icon. When hideFromDock is true, stay in .accessory mode —
        // windows can still be shown and focused without a dock tile.
        if !ConfigStore.shared.config.hideFromDock {
            NSApp.setActivationPolicy(.regular)
        }
        NSApp.activate(ignoringOtherApps: true)

        // Bring any existing main window to the front right away. A miniaturised
        // window needs deminiaturize() first — makeKeyAndOrderFront alone leaves
        // it in the Dock, which reads to the user as "clicking does nothing".
        var foundExisting = false
        for w in NSApp.windows where isMainWindow(w) {
            if w.isMiniaturized { w.deminiaturize(nil) }
            w.makeKeyAndOrderFront(nil)
            foundExisting = true
            break
        }

        // Also ask SwiftUI to open/resurface the window scene so a fresh window
        // is created if the previous one was destroyed via the red-X button.
        requestSwiftUIWindow(retries: 40)

        // When no window existed, SwiftUI creates one asynchronously.  Poll for
        // it over the next ~2 s and raise it once it appears so it lands on
        // top rather than behind the previously-active app.
        if !foundExisting {
            bringNewWindowToFront(retries: 40)
        }

        // SwiftUI's Window scene can implicitly promote the app to .regular as
        // a side effect of materializing/activating its window, independent of
        // the explicit policy call above. Re-assert the preference afterwards so
        // a stray Dock icon doesn't reappear when the user has hidden it.
        reassertDockPolicy()
    }

    /// Invokes the captured `openWindow` action, waiting for it if the scene
    /// that publishes it hasn't rendered yet.
    ///
    /// `openWindow` is a SwiftUI environment value, so it can only be captured
    /// from inside a View. At a cold launch — and especially a launch at login,
    /// where the main window may never be materialised — no view has rendered
    /// yet, so the action is still nil. Giving up at that point is what left the
    /// app permanently windowless: the Dock icon was live but every click ran
    /// this code, found nothing to open, and returned.
    private static func requestSwiftUIWindow(retries: Int) {
        if let open = openWindow {
            open("main")
            return
        }
        guard retries > 0 else {
            NetLogger.ui(event: "window_open_unavailable")
            return
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) {
            requestSwiftUIWindow(retries: retries - 1)
        }
    }

    // Re-applies the user's dock preference after window operations that might
    // have caused AppKit to implicitly change the activation policy.
    private static func reassertDockPolicy() {
        DispatchQueue.main.async {
            DockPolicyGuard.shared.reassert()
        }
    }

    // Polls NSApp.windows until a main-eligible window appears, then raises it.
    private static func bringNewWindowToFront(retries: Int) {
        guard retries > 0 else { return }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) {
            for w in NSApp.windows where isMainWindow(w) {
                if w.isMiniaturized { w.deminiaturize(nil) }
                w.makeKeyAndOrderFront(nil)
                NSApp.activate(ignoringOtherApps: true)
                reassertDockPolicy()
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
            // Lets a running session (started from a contact's chat) be stopped
            // from the menu bar, without hunting for the indicator.
            CommandMenu("Remote Desktop") {
                if appModel.remoteSessionRunning {
                    Button("Stop Sharing") { appModel.stopRemoteSession() }
                        .keyboardShortcut(".", modifiers: [.command, .shift])
                    if let summary = appModel.remoteSessionSummary {
                        Text(summary)
                    }
                }
            }
        }

        MenuBarExtra {
            TrayMenuView()
                .environmentObject(appModel)
        } label: {
            MenuBarIcon()
                .environmentObject(appModel)
                // The status-item label always renders, even when the main
                // window scene never materialises. Capturing `openWindow` here
                // as well as in ContentView is what guarantees there is always
                // a way back to a window.
                .captureOpenWindow()
        }
        .menuBarExtraStyle(.menu)
    }
}

// Captures the SwiftUI openWindow action once the root view appears so the
// menu-bar tray and AppDelegate can re-surface the main window.
//
// Applied to both the main window's root view and the MenuBarExtra label. The
// main window's copy is the one that never runs when it matters: if the app
// relaunches with its window scene unmaterialised (a login-item start, or a
// restore of a session whose window had been closed with the red X), ContentView
// never appears and the action stays nil — leaving the Dock icon live but inert.
// The status-item label always renders, so it always supplies the action.
private struct CaptureOpenWindow: ViewModifier {
    @Environment(\.openWindow) private var openWindow
    func body(content: Content) -> some View {
        content.onAppear {
            WindowController.openWindow = { id in openWindow(id: id) }
        }
    }
}
extension View {
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

    /// The EmptyState recipe: a clear-glass disc holding the icon, a pane
    /// title, and one line saying what to do next.
    private var emptyState: some View {
        VStack(spacing: 12) {
            Image(systemName: "bubble.left.and.bubble.right")
                .font(.system(size: 26))
                .foregroundStyle(Theme.inkSecondary)
                .frame(width: 64, height: 64)
                .glassSurface(.clear, in: Circle())
            VStack(spacing: 4) {
                Text("No conversation selected")
                    .font(GlassTokens.Typography.titlePane)
                    .foregroundStyle(Theme.ink)
                Text("Pick a peer from the sidebar, or wait for one to appear on the LAN.")
                    .font(GlassTokens.Typography.label)
                    .foregroundStyle(Theme.inkSecondary)
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 240)
            }
        }
    }
}

// MARK: - Menu bar extra

// Custom label so we can overlay a small red dot on the glyph when there are
// unread messages — MenuBarExtra's plain `systemImage:` initializer has no way
// to composite a badge onto the status item icon.
struct MenuBarIcon: View {
    @EnvironmentObject var model: AppModel

    private var hasUnread: Bool {
        model.conversations.contains { $0.unreadCount > 0 }
    }

    var body: some View {
        ZStack(alignment: .topTrailing) {
            Image(systemName: "message.fill")
            if hasUnread {
                Circle()
                    .fill(Color.red)
                    .frame(width: 7, height: 7)
                    .offset(x: 3, y: -3)
            }
        }
        .accessibilityLabel(Text("LAN Messenger"))
    }
}

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

        // Remote desktop lives here rather than only in the app menu bar.
        // `hideFromDock` defaults to true, which makes this an `.accessory`
        // process — and an accessory app has no menu bar at all, so a
        // `CommandMenu` is invisible to most users. The MenuBarExtra always
        // renders, which is the same reason CLAUDE.md insists `openWindow` be
        // captured here.
        RemoteDesktopTrayItems()

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

// MARK: - Remote desktop tray items

/// Shows a running remote-desktop session, if any, in the one menu that is
/// always present — sessions themselves start from a contact's chat.
private struct RemoteDesktopTrayItems: View {

    @EnvironmentObject var model: AppModel

    var body: some View {
        if model.remoteSessionRunning {
            if let summary = model.remoteSessionSummary {
                Text("Sharing: \(summary)")
            }
            Button("Stop Sharing") { model.stopRemoteSession() }

            Divider()
        }
    }
}
