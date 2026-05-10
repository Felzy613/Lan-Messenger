import SwiftUI

@main
struct LanMessengerApp: App {

    @StateObject private var appModel = AppModel()

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environmentObject(appModel)
                .frame(minWidth: 720, minHeight: 500)
        }
        .windowStyle(.titleBar)
        .windowToolbarStyle(.unified)
        .commands {
            CommandGroup(replacing: .newItem) {}   // no "New Window"
        }

        MenuBarExtra("LAN Messenger", systemImage: "message.fill") {
            TrayMenuView()
                .environmentObject(appModel)
        }
        .menuBarExtraStyle(.menu)
    }
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

    var body: some View {
        Text("LAN Messenger").font(.headline)
        Divider()
        ForEach(model.conversations) { conv in
            Button {
                model.selectedPeerIP = conv.peerIP
                NSApp.activate(ignoringOtherApps: true)
            } label: {
                HStack {
                    Text(conv.peerName)
                    if conv.unreadCount > 0 {
                        Spacer()
                        Text("●")
                            .foregroundStyle(Theme.accent)
                    }
                }
            }
        }
        if model.conversations.isEmpty {
            Text("No contacts")
                .foregroundStyle(.secondary)
        }
        Divider()
        Button("Quit LAN Messenger") { NSApp.terminate(nil) }
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
