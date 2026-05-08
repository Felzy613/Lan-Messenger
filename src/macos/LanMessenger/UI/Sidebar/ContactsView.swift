import SwiftUI

struct ContactsView: View {
    @EnvironmentObject var model: AppModel
    @Environment(\.dismiss) var dismiss
    @State private var contacts = ConfigStore.shared.config.contacts
    @State private var showAddContact = false

    var body: some View {
        NavigationStack {
            List {
                ForEach(contacts) { contact in
                    HStack(spacing: 10) {
                        AvatarView(name: contact.username, size: 36)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(contact.username).font(.headline)
                            Text(contact.lastIP)
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                    }
                    .padding(.vertical, 2)
                }
                .onDelete { indices in
                    contacts.remove(atOffsets: indices)
                    persist()
                }
            }
            .navigationTitle("Contacts")
            .toolbar {
                ToolbarItem(placement: .primaryAction) {
                    Button { showAddContact = true } label: {
                        Image(systemName: "person.badge.plus")
                    }
                    .help("Add contact")
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done") { dismiss() }
                }
            }
            .sheet(isPresented: $showAddContact) {
                AddContactView { newContact in
                    contacts.append(newContact)
                    persist()
                }
            }
            .overlay {
                if contacts.isEmpty {
                    VStack(spacing: 12) {
                        Image(systemName: "person.2.slash")
                            .font(.system(size: 36))
                            .foregroundStyle(.secondary)
                        Text("No saved contacts")
                            .font(.headline)
                            .foregroundStyle(.secondary)
                        Text("Tap + to add a contact by name and IP address.")
                            .font(.caption)
                            .foregroundStyle(.tertiary)
                            .multilineTextAlignment(.center)
                    }
                    .padding()
                }
            }
        }
        .frame(minWidth: 320, minHeight: 400)
    }

    private func persist() {
        ConfigStore.shared.config.contacts = contacts
        ConfigStore.shared.save()
    }
}

// MARK: - Add Contact sheet

struct AddContactView: View {
    @Environment(\.dismiss) var dismiss
    var onSave: (ContactConfig) -> Void

    @State private var username = ""
    @State private var ip = ""

    private var isValid: Bool {
        !username.trimmingCharacters(in: .whitespaces).isEmpty &&
        !ip.trimmingCharacters(in: .whitespaces).isEmpty
    }

    var body: some View {
        NavigationStack {
            Form {
                Section("Contact details") {
                    TextField("Name", text: $username)
                    TextField("IP address (e.g. 192.168.1.42)", text: $ip)
                        .textContentType(.none)
                }
                Section {
                    Text("The app will ping this IP during discovery, which helps reach peers on different subnets. The public key will be learned automatically when they come online.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            .formStyle(.grouped)
            .navigationTitle("Add Contact")
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Add") {
                        let contact = ContactConfig(
                            publicKeyB64: UUID().uuidString,   // placeholder until seen on LAN
                            username: username.trimmingCharacters(in: .whitespaces),
                            lastIP: ip.trimmingCharacters(in: .whitespaces)
                        )
                        onSave(contact)
                        dismiss()
                    }
                    .disabled(!isValid)
                    .buttonStyle(.borderedProminent)
                    .tint(Theme.accent)
                }
            }
        }
        .frame(minWidth: 320, minHeight: 260)
    }
}
