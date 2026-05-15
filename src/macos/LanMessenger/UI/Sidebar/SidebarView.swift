import SwiftUI

struct SidebarView: View {
    @EnvironmentObject var model: AppModel
    @State private var showSettings = false
    @State private var showContacts = false
    @State private var showArchived = false
    @State private var showNewMessage = false

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
                Button { showNewMessage = true } label: {
                    Image(systemName: "square.and.pencil")
                }
                .help("New message")
            }
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
        .sheet(isPresented: $showNewMessage) {
            NewMessageView(onAddContact: {
                showNewMessage = false
                showContacts = true
            })
            .environmentObject(model)
        }
        .overlay {
            if model.conversations.isEmpty && model.archivedConversations.isEmpty {
                VStack(spacing: 12) {
                    Image(systemName: "bubble.left.and.bubble.right")
                        .font(.system(size: 36))
                        .foregroundStyle(.secondary)
                    Text("No conversations")
                        .font(.headline)
                        .foregroundStyle(.secondary)
                    Text("Tap “New message” to chat with one of your saved contacts, or add a new contact.")
                        .font(.caption)
                        .foregroundStyle(.tertiary)
                        .multilineTextAlignment(.center)
                        .frame(maxWidth: 220)
                    HStack(spacing: 8) {
                        Button("New Message") { showNewMessage = true }
                            .buttonStyle(.borderedProminent)
                        Button("Add Contact") { showContacts = true }
                            .buttonStyle(.bordered)
                    }
                    .padding(.top, 4)
                }
                .padding()
            }
        }
    }
}

// MARK: - New message picker — choose from saved contacts to start/resume a thread

struct NewMessageView: View {
    @EnvironmentObject var model: AppModel
    @Environment(\.dismiss) var dismiss
    let onAddContact: () -> Void
    @State private var searchQuery = ""

    private var contacts: [ContactConfig] {
        let q = searchQuery.trimmingCharacters(in: .whitespaces).lowercased()
        let all = ConfigStore.shared.config.contacts
        guard !q.isEmpty else { return all.sorted { $0.username.lowercased() < $1.username.lowercased() } }
        return all
            .filter { $0.username.lowercased().contains(q) || $0.lastIP.contains(q) }
            .sorted { $0.username.lowercased() < $1.username.lowercased() }
    }

    private func isOnline(_ contact: ContactConfig) -> Bool {
        model.peers.values.contains { $0.publicKeyB64 == contact.publicKeyB64 && $0.isOnline }
    }

    var body: some View {
        NavigationStack {
            VStack(spacing: 0) {
                HStack(spacing: 8) {
                    Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                    TextField("Search contacts", text: $searchQuery)
                        .textFieldStyle(.plain)
                }
                .padding(.horizontal, 12).padding(.vertical, 8)
                .background(.quaternary, in: RoundedRectangle(cornerRadius: 10))
                .padding(.horizontal, 14).padding(.vertical, 10)
                Divider()

                if contacts.isEmpty {
                    VStack(spacing: 12) {
                        Image(systemName: "person.crop.circle.badge.plus")
                            .font(.system(size: 40))
                            .foregroundStyle(.secondary)
                        Text(searchQuery.isEmpty ? "No saved contacts" : "No matches")
                            .font(.headline)
                            .foregroundStyle(.secondary)
                        if searchQuery.isEmpty {
                            Button("Add a contact") { onAddContact() }
                                .buttonStyle(.borderedProminent)
                        }
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                } else {
                    List(contacts) { contact in
                        Button {
                            model.startConversation(withContact: contact.publicKeyB64)
                            dismiss()
                        } label: {
                            HStack(spacing: 10) {
                                AvatarView(name: contact.username, size: 40, photoB64: contact.photoB64)
                                    .overlay(alignment: .bottomTrailing) {
                                        Circle()
                                            .fill(isOnline(contact) ? Color.green : Color.gray.opacity(0.4))
                                            .frame(width: 11, height: 11)
                                            .offset(x: 2, y: 2)
                                    }
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(contact.username)
                                        .font(.system(size: 14, weight: .semibold))
                                        .foregroundStyle(.primary)
                                    Text(isOnline(contact) ? "Online" : (contact.lastIP.isEmpty ? "—" : contact.lastIP))
                                        .font(.system(size: 12))
                                        .foregroundStyle(.secondary)
                                }
                                Spacer()
                            }
                            .contentShape(Rectangle())
                            .padding(.vertical, 4)
                        }
                        .buttonStyle(.plain)
                    }
                    .listStyle(.inset)
                }
            }
            .navigationTitle("New Message")
            .toolbar {
                ToolbarItem(placement: .navigation) {
                    Button("Add Contact") { onAddContact() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done") { dismiss() }
                }
            }
        }
        .frame(minWidth: 360, minHeight: 420)
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
