import SwiftUI

struct ContactsView: View {
    @EnvironmentObject var model: AppModel
    @Environment(\.dismiss) var dismiss
    @State private var contacts = ConfigStore.shared.config.contacts
    @State private var showPeerScanner = false
    @State private var searchQuery = ""

    private var filtered: [ContactConfig] {
        guard !searchQuery.trimmingCharacters(in: .whitespaces).isEmpty else { return contacts }
        let q = searchQuery.lowercased()
        return contacts.filter { $0.username.lowercased().contains(q) || $0.lastIP.contains(q) }
    }

    private func isOnline(_ contact: ContactConfig) -> Bool {
        model.peers.values.contains { $0.ip == contact.lastIP && $0.isOnline }
    }

    var body: some View {
        NavigationStack {
            VStack(spacing: 0) {
                searchBar
                Divider()

                // "New contact" row, WhatsApp-style.
                Button {
                    showPeerScanner = true
                } label: {
                    HStack(spacing: 14) {
                        ZStack {
                            Circle()
                                .fill(Theme.accent)
                                .frame(width: 44, height: 44)
                            Image(systemName: "person.fill.badge.plus")
                                .font(.system(size: 18, weight: .semibold))
                                .foregroundStyle(.white)
                        }
                        Text("New contact")
                            .font(.system(size: 15, weight: .medium))
                        Spacer()
                    }
                    .padding(.horizontal, 18)
                    .padding(.vertical, 10)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)

                Divider()

                if filtered.isEmpty {
                    emptyState
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                } else {
                    List {
                        Section {
                            ForEach(filtered) { contact in
                                contactRow(contact)
                            }
                            .onDelete { indices in
                                let toRemove = indices.map { filtered[$0].publicKeyB64 }
                                contacts.removeAll { toRemove.contains($0.publicKeyB64) }
                                persist()
                            }
                        } header: {
                            Text("Contacts")
                                .font(.system(size: 11, weight: .semibold))
                                .foregroundStyle(.secondary)
                        }
                    }
                    .listStyle(.plain)
                }
            }
            .navigationTitle("Contacts")
            .toolbar {
                ToolbarItem(placement: .primaryAction) {
                    Button { showPeerScanner = true } label: {
                        Image(systemName: "person.badge.plus")
                    }
                    .help("Add from nearby peers")
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done") { dismiss() }
                }
            }
            .sheet(isPresented: $showPeerScanner) {
                PeerScannerView(savedContacts: $contacts, onSave: persist)
                    .environmentObject(model)
            }
        }
        .frame(minWidth: 380, minHeight: 460)
    }

    // MARK: - Subviews

    private var searchBar: some View {
        HStack(spacing: 8) {
            Image(systemName: "magnifyingglass")
                .foregroundStyle(.secondary)
            TextField("Search contacts", text: $searchQuery)
                .textFieldStyle(.plain)
            if !searchQuery.isEmpty {
                Button { searchQuery = "" } label: {
                    Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary)
                }
                .buttonStyle(.plain)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .background(.quaternary, in: RoundedRectangle(cornerRadius: 10))
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
    }

    private func contactRow(_ contact: ContactConfig) -> some View {
        let online = isOnline(contact)
        return Button {
            model.selectedPeerIP = contact.lastIP
            dismiss()
        } label: {
            HStack(spacing: 12) {
                AvatarView(name: contact.username, size: 44)
                    .overlay(alignment: .bottomTrailing) {
                        Circle()
                            .fill(online ? Color.green : Color.gray.opacity(0.4))
                            .frame(width: 11, height: 11)
                            .offset(x: 2, y: 2)
                    }
                VStack(alignment: .leading, spacing: 2) {
                    Text(contact.username)
                        .font(.system(size: 14, weight: .semibold))
                        .foregroundStyle(.primary)
                    Text(online ? "Online" : contact.lastIP)
                        .font(.system(size: 12))
                        .foregroundStyle(online ? Theme.accent : .secondary)
                }
                Spacer()
                Image(systemName: "chevron.right")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(.tertiary)
            }
            .padding(.vertical, 6)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }

    private var emptyState: some View {
        VStack(spacing: 14) {
            Image(systemName: "person.2.slash")
                .font(.system(size: 44))
                .foregroundStyle(.secondary)
            Text(searchQuery.isEmpty ? "No saved contacts" : "No matches")
                .font(.headline)
                .foregroundStyle(.secondary)
            if searchQuery.isEmpty {
                Text("Tap “New contact” to scan for peers on your network.")
                    .font(.callout)
                    .foregroundStyle(.tertiary)
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 240)
            }
        }
        .padding()
    }

    private func persist() {
        ConfigStore.shared.config.contacts = contacts
        ConfigStore.shared.save()
    }
}

// MARK: - Peer Scanner sheet

struct PeerScannerView: View {
    @EnvironmentObject var model: AppModel
    @Environment(\.dismiss) var dismiss
    @Binding var savedContacts: [ContactConfig]
    var onSave: () -> Void

    @State private var isScanning = false
    @State private var addedKeys: Set<String> = []

    private var discoverablePeers: [PeerInfo] {
        // Only filter contacts that existed before this sheet opened (not ones added this session).
        let preExistingIPs = Set(savedContacts.filter { !addedKeys.contains($0.publicKeyB64) }.map(\.lastIP))
        return model.peers.values
            .filter { !preExistingIPs.contains($0.ip) }
            .sorted { $0.username < $1.username }
    }

    var body: some View {
        NavigationStack {
            Group {
                if discoverablePeers.isEmpty {
                    VStack(spacing: 16) {
                        if isScanning {
                            ProgressView()
                                .scaleEffect(1.2)
                            Text("Scanning for peers…")
                                .foregroundStyle(.secondary)
                        } else {
                            Image(systemName: "antenna.radiowaves.left.and.right")
                                .font(.system(size: 40))
                                .foregroundStyle(.secondary)
                            Text("No peers found")
                                .font(.headline)
                                .foregroundStyle(.secondary)
                            Text("Make sure other devices are on the same network and running LAN Messenger.")
                                .font(.caption)
                                .foregroundStyle(.tertiary)
                                .multilineTextAlignment(.center)
                                .frame(maxWidth: 240)
                            Button("Scan Again") { triggerScan() }
                                .buttonStyle(.borderedProminent)
                        }
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                } else {
                    List(discoverablePeers) { peer in
                        let alreadyAdded = addedKeys.contains(peer.publicKeyB64)
                        Button {
                            if !alreadyAdded { addContact(peer) }
                        } label: {
                            HStack(spacing: 10) {
                                AvatarView(name: peer.username, size: 36)
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(peer.username)
                                        .font(.headline)
                                        .foregroundStyle(.primary)
                                    Text(peer.ip)
                                        .font(.caption)
                                        .foregroundStyle(.secondary)
                                }
                                Spacer()
                                if alreadyAdded {
                                    HStack(spacing: 4) {
                                        Image(systemName: "checkmark.circle.fill")
                                            .foregroundStyle(.green)
                                            .font(.system(size: 20))
                                        Text("Added")
                                            .font(.caption)
                                            .foregroundStyle(.green)
                                    }
                                } else {
                                    Text("Add")
                                        .font(.system(size: 12, weight: .semibold))
                                        .foregroundStyle(.white)
                                        .padding(.horizontal, 12)
                                        .padding(.vertical, 5)
                                        .background(Theme.accent, in: Capsule())
                                }
                            }
                            .padding(.vertical, 4)
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        .disabled(alreadyAdded)
                    }
                    .listStyle(.inset)
                }
            }
            .navigationTitle("Nearby Peers")
            .toolbar {
                ToolbarItem(placement: .navigation) {
                    Button {
                        triggerScan()
                    } label: {
                        Image(systemName: "arrow.clockwise")
                    }
                    .help("Scan for peers")
                    .disabled(isScanning)
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done") { dismiss() }
                }
            }
        }
        .frame(minWidth: 320, minHeight: 380)
        .onAppear { triggerScan() }
    }

    private func triggerScan() {
        isScanning = true
        model.scan()
        DispatchQueue.main.asyncAfter(deadline: .now() + 3) {
            isScanning = false
        }
    }

    private func addContact(_ peer: PeerInfo) {
        let contact = ContactConfig(
            publicKeyB64: peer.publicKeyB64,
            username: peer.username,
            lastIP: peer.ip
        )
        savedContacts.append(contact)
        addedKeys.insert(peer.publicKeyB64)
        onSave()
    }
}
