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
                    Image(systemName: "person.2.slash")
                        .font(.system(size: 36))
                        .foregroundStyle(.secondary)
                    Text("No contacts")
                        .font(.headline)
                        .foregroundStyle(.secondary)
                    Text("Add a contact or wait for peers to appear on the LAN.")
                        .font(.caption)
                        .foregroundStyle(.tertiary)
                        .multilineTextAlignment(.center)
                        .frame(maxWidth: 200)
                    Button("Add Contact") { showContacts = true }
                        .buttonStyle(.borderedProminent)
                        .padding(.top, 4)
                }
                .padding()
            }
        }
    }
}
