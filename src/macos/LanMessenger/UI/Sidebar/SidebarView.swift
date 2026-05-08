import SwiftUI

struct SidebarView: View {
    @EnvironmentObject var model: AppModel
    @State private var showSettings = false
    @State private var showContacts = false

    var body: some View {
        List(model.conversations, selection: $model.selectedPeerIP) { conv in
            ConversationRowView(conv: conv)
                .tag(conv.peerIP)
        }
        .listStyle(.sidebar)
        .navigationTitle("Messenger")
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button { showContacts = true } label: {
                    Image(systemName: "person.2")
                }
                .help("Contacts")
            }
            ToolbarItem(placement: .primaryAction) {
                Button { showSettings = true } label: {
                    Image(systemName: "gear")
                }
                .help("Settings")
            }
        }
        .sheet(isPresented: $showSettings) {
            SettingsView()
                .environmentObject(model)
        }
        .sheet(isPresented: $showContacts) {
            ContactsView()
                .environmentObject(model)
        }
        .overlay {
            if model.conversations.isEmpty {
                VStack(spacing: 12) {
                    Image(systemName: "antenna.radiowaves.left.and.right")
                        .font(.system(size: 36))
                        .foregroundStyle(.secondary)
                    Text("No peers online")
                        .font(.headline)
                        .foregroundStyle(.secondary)
                    Text("Waiting for peers on the LAN…")
                        .font(.caption)
                        .foregroundStyle(.tertiary)
                }
            }
        }
    }
}
