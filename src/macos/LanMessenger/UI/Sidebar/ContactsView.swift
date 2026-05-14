import SwiftUI

struct ContactsView: View {
    @EnvironmentObject var model: AppModel
    @Environment(\.dismiss) var dismiss
    @State private var contacts = ConfigStore.shared.config.contacts
    @State private var showPeerScanner = false

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
                        Spacer()
                        if model.peers.values.contains(where: { $0.ip == contact.lastIP }) {
                            Circle()
                                .fill(Color.green)
                                .frame(width: 8, height: 8)
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
            .overlay {
                if contacts.isEmpty {
                    VStack(spacing: 12) {
                        Image(systemName: "person.2.slash")
                            .font(.system(size: 36))
                            .foregroundStyle(.secondary)
                        Text("No saved contacts")
                            .font(.headline)
                            .foregroundStyle(.secondary)
                        Text("Scan for nearby peers to add them as contacts.")
                            .font(.caption)
                            .foregroundStyle(.tertiary)
                            .multilineTextAlignment(.center)
                        Button("Scan for Peers") { showPeerScanner = true }
                            .buttonStyle(.borderedProminent)
                            .padding(.top, 4)
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
