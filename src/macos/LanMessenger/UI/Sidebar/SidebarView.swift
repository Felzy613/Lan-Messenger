import SwiftUI

struct SidebarView: View {
    @EnvironmentObject var model: AppModel
    @State private var showSettings = false
    @State private var showContacts = false
    @State private var showArchived = false

    var body: some View {
        List(selection: $model.selectedPeerIP) {
            ForEach(model.conversations) { conv in
                ConversationRowView(conv: conv)
                    .tag(conv.peerIP)
            }
            if !model.archivedConversations.isEmpty {
                Section {
                    Button {
                        showArchived = true
                    } label: {
                        HStack(spacing: 10) {
                            Image(systemName: "archivebox.fill")
                                .foregroundStyle(Theme.accent)
                                .frame(width: 32, height: 32)
                                .background(Theme.accent.opacity(0.12), in: Circle())
                            VStack(alignment: .leading, spacing: 2) {
                                Text("Archived")
                                    .font(.system(size: 14, weight: .semibold))
                                Text("\(model.archivedConversations.count) conversation\(model.archivedConversations.count == 1 ? "" : "s")")
                                    .font(.system(size: 12))
                                    .foregroundStyle(.secondary)
                            }
                            Spacer()
                            Image(systemName: "chevron.right")
                                .font(.system(size: 11, weight: .semibold))
                                .foregroundStyle(.tertiary)
                        }
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                }
            }
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
        .sheet(isPresented: $showArchived) {
            ArchivedConversationsView()
                .environmentObject(model)
        }
        .overlay {
            if model.conversations.isEmpty && model.archivedConversations.isEmpty {
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

// MARK: - Archived sheet

struct ArchivedConversationsView: View {
    @EnvironmentObject var model: AppModel
    @Environment(\.dismiss) var dismiss

    var body: some View {
        NavigationStack {
            Group {
                if model.archivedConversations.isEmpty {
                    VStack(spacing: 10) {
                        Image(systemName: "archivebox")
                            .font(.system(size: 36))
                            .foregroundStyle(.secondary)
                        Text("Nothing archived")
                            .font(.headline)
                            .foregroundStyle(.secondary)
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                } else {
                    List(model.archivedConversations) { conv in
                        ConversationRowView(conv: conv)
                            .contentShape(Rectangle())
                            .onTapGesture {
                                model.selectedPeerIP = conv.peerIP
                                dismiss()
                            }
                    }
                    .listStyle(.inset)
                }
            }
            .navigationTitle("Archived")
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done") { dismiss() }
                }
            }
        }
        .frame(minWidth: 360, minHeight: 420)
    }
}
