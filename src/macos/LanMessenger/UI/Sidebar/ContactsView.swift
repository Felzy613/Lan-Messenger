import SwiftUI
import AppKit

struct ContactsView: View {
    @EnvironmentObject var model: AppModel
    @Environment(\.dismiss) var dismiss
    @State private var contacts = ConfigStore.shared.config.contacts
    @State private var showPeerScanner = false
    @State private var searchQuery = ""
    @State private var editingContact: ContactConfig? = nil

    private var filtered: [ContactConfig] {
        guard !searchQuery.trimmingCharacters(in: .whitespaces).isEmpty else { return contacts }
        let q = searchQuery.lowercased()
        return contacts.filter { $0.username.lowercased().contains(q) || $0.lastIP.contains(q) }
    }

    private func isOnline(_ contact: ContactConfig) -> Bool {
        model.peers.values.contains { $0.publicKeyB64 == contact.publicKeyB64 && $0.isOnline }
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
                                for key in toRemove { model.deleteContact(publicKeyB64: key) }
                                contacts = ConfigStore.shared.config.contacts
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
            .sheet(item: $editingContact) { contact in
                ContactEditorView(
                    contact: contact,
                    onSave: { name, photo in
                        model.updateContact(publicKeyB64: contact.publicKeyB64, username: name, photoB64: photo)
                        contacts = ConfigStore.shared.config.contacts
                        editingContact = nil
                    },
                    onCancel: { editingContact = nil }
                )
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
        return HStack(spacing: 12) {
            AvatarView(name: contact.username, size: 44, photoB64: contact.photoB64)
                .overlay(alignment: .bottomTrailing) {
                    Circle()
                        .fill(online ? Color.green : Color.gray.opacity(0.4))
                        .frame(width: 11, height: 11)
                        .offset(x: 2, y: 2)
                }
            Button {
                model.selectedPeerIP = contact.lastIP
                dismiss()
            } label: {
                VStack(alignment: .leading, spacing: 2) {
                    Text(contact.username)
                        .font(.system(size: 14, weight: .semibold))
                        .foregroundStyle(.primary)
                    Text(online ? "Online" : contact.lastIP)
                        .font(.system(size: 12))
                        .foregroundStyle(online ? Theme.accent : .secondary)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            Spacer()
            Menu {
                Button {
                    editingContact = contact
                } label: { Label("Edit", systemImage: "pencil") }
                Divider()
                Button(role: .destructive) {
                    model.deleteContact(publicKeyB64: contact.publicKeyB64)
                    contacts = ConfigStore.shared.config.contacts
                } label: { Label("Remove", systemImage: "trash") }
            } label: {
                Image(systemName: "ellipsis")
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(.secondary)
                    .frame(width: 22, height: 22)
                    .contentShape(Rectangle())
            }
            .menuStyle(.borderlessButton)
            .menuIndicator(.hidden)
            .fixedSize()
        }
        .padding(.vertical, 6)
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

// MARK: - Contact editor (name + photo)

struct ContactEditorView: View {
    let contact: ContactConfig
    let onSave: (String, String?) -> Void
    let onCancel: () -> Void

    @State private var name: String
    @State private var photoB64: String?

    init(contact: ContactConfig, onSave: @escaping (String, String?) -> Void, onCancel: @escaping () -> Void) {
        self.contact = contact
        self.onSave = onSave
        self.onCancel = onCancel
        self._name = State(initialValue: contact.username)
        self._photoB64 = State(initialValue: contact.photoB64)
    }

    var body: some View {
        NavigationStack {
            Form {
                Section("Photo") {
                    HStack(spacing: 16) {
                        AvatarView(name: name, size: 88, photoB64: photoB64)
                        VStack(alignment: .leading, spacing: 8) {
                            Button("Choose Image…") { pickPhoto() }
                            if photoB64 != nil {
                                Button("Remove Photo", role: .destructive) { photoB64 = nil }
                            }
                        }
                    }
                    .padding(.vertical, 6)
                }
                Section("Name") {
                    TextField("Contact name", text: $name)
                }
                Section("Details") {
                    LabeledContent("Last IP", value: contact.lastIP.isEmpty ? "—" : contact.lastIP)
                    LabeledContent("Device ID") {
                        Text(contact.publicKeyB64.prefix(16) + "…")
                            .font(.system(size: 11, design: .monospaced))
                            .foregroundStyle(.secondary)
                    }
                }
            }
            .formStyle(.grouped)
            .navigationTitle("Edit Contact")
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { onCancel() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Save") { onSave(name.trimmingCharacters(in: .whitespaces), photoB64) }
                        .buttonStyle(.borderedProminent)
                        .tint(Theme.accent)
                        .disabled(name.trimmingCharacters(in: .whitespaces).isEmpty)
                }
            }
        }
        .frame(minWidth: 420, minHeight: 380)
    }

    private func pickPhoto() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        panel.allowedContentTypes = [.png, .jpeg, .image]
        if panel.runModal() == .OK, let url = panel.url, let img = NSImage(contentsOf: url) {
            if let data = Self.compressedJPEG(from: img, maxDim: 256) {
                photoB64 = data.base64EncodedString()
            }
        }
    }

    // Re-encodes the chosen image as a small square JPEG so we don't bloat config.json
    // with multi-megabyte photos. ~256x256 keeps file size under ~30KB.
    static func compressedJPEG(from image: NSImage, maxDim: CGFloat) -> Data? {
        let size = image.size
        guard size.width > 0, size.height > 0 else { return nil }
        let scale = min(1.0, maxDim / max(size.width, size.height))
        let newSize = NSSize(width: size.width * scale, height: size.height * scale)
        let bitmap = NSBitmapImageRep(
            bitmapDataPlanes: nil,
            pixelsWide: Int(newSize.width),
            pixelsHigh: Int(newSize.height),
            bitsPerSample: 8,
            samplesPerPixel: 4,
            hasAlpha: true,
            isPlanar: false,
            colorSpaceName: .deviceRGB,
            bytesPerRow: 0,
            bitsPerPixel: 0
        )
        guard let rep = bitmap else { return nil }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        image.draw(in: NSRect(origin: .zero, size: newSize))
        NSGraphicsContext.restoreGraphicsState()
        return rep.representation(using: .jpeg, properties: [.compressionFactor: 0.85])
    }
}

// MARK: - Peer Scanner sheet
//
// Flow: search for peers -> multi-select -> Save -> per-peer name prompt -> contact saved.

struct PeerScannerView: View {
    @EnvironmentObject var model: AppModel
    @Environment(\.dismiss) var dismiss
    @Binding var savedContacts: [ContactConfig]
    var onSave: () -> Void

    @State private var isScanning = false
    @State private var selectedKeys: Set<String> = []
    @State private var namingQueue: [PeerInfo] = []
    @State private var currentlyNaming: PeerInfo? = nil
    @State private var nameInput: String = ""

    private var discoverablePeers: [PeerInfo] {
        // Filter out peers already saved as contacts.
        let savedKeys = Set(savedContacts.map(\.publicKeyB64))
        return model.peers.values
            .filter { !savedKeys.contains($0.publicKeyB64) }
            .sorted { $0.username < $1.username }
    }

    var body: some View {
        NavigationStack {
            Group {
                if discoverablePeers.isEmpty {
                    VStack(spacing: 16) {
                        if isScanning {
                            ProgressView().scaleEffect(1.2)
                            Text("Scanning for peers…").foregroundStyle(.secondary)
                        } else {
                            Image(systemName: "antenna.radiowaves.left.and.right")
                                .font(.system(size: 40))
                                .foregroundStyle(.secondary)
                            Text("No peers found").font(.headline).foregroundStyle(.secondary)
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
                    VStack(spacing: 0) {
                        List(discoverablePeers) { peer in
                            let selected = selectedKeys.contains(peer.publicKeyB64)
                            Button {
                                toggle(peer.publicKeyB64)
                            } label: {
                                HStack(spacing: 10) {
                                    Image(systemName: selected ? "checkmark.circle.fill" : "circle")
                                        .font(.system(size: 20))
                                        .foregroundStyle(selected ? Theme.accent : .secondary)
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
                                }
                                .padding(.vertical, 4)
                                .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                        }
                        .listStyle(.inset)
                        Divider()
                        Button {
                            triggerScan()
                        } label: {
                            HStack(spacing: 6) {
                                if isScanning {
                                    ProgressView().controlSize(.small)
                                } else {
                                    Image(systemName: "arrow.clockwise")
                                }
                                Text(isScanning ? "Scanning…" : "Scan Again")
                            }
                        }
                        .buttonStyle(.bordered)
                        .disabled(isScanning)
                        .padding(10)
                    }
                }
            }
            .navigationTitle("Find Contacts")
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
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button(selectedKeys.isEmpty ? "Save" : "Save (\(selectedKeys.count))") {
                        beginNamingFlow()
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled(selectedKeys.isEmpty)
                }
            }
        }
        .frame(minWidth: 360, minHeight: 420)
        .onAppear { triggerScan() }
        .sheet(item: $currentlyNaming) { peer in
            NameContactView(
                peer: peer,
                nameText: $nameInput,
                onSave: {
                    let trimmed = nameInput.trimmingCharacters(in: .whitespacesAndNewlines)
                    let final = trimmed.isEmpty ? peer.username : trimmed
                    model.addContact(peer, customName: final)
                    savedContacts = ConfigStore.shared.config.contacts
                    advanceNamingQueue()
                },
                onSkip: {
                    model.addContact(peer, customName: nil)
                    savedContacts = ConfigStore.shared.config.contacts
                    advanceNamingQueue()
                }
            )
        }
    }

    private func toggle(_ key: String) {
        if selectedKeys.contains(key) { selectedKeys.remove(key) } else { selectedKeys.insert(key) }
    }

    private func beginNamingFlow() {
        namingQueue = discoverablePeers.filter { selectedKeys.contains($0.publicKeyB64) }
        advanceNamingQueue()
    }

    private func advanceNamingQueue() {
        currentlyNaming = nil
        if namingQueue.isEmpty {
            onSave()
            dismiss()
            return
        }
        let next = namingQueue.removeFirst()
        nameInput = next.username
        // Defer to next runloop tick so the previous sheet fully dismisses
        // before the new one is presented — chained sheets can otherwise be lost.
        DispatchQueue.main.async {
            currentlyNaming = next
        }
    }

    private func triggerScan() {
        isScanning = true
        model.scan()
        DispatchQueue.main.asyncAfter(deadline: .now() + 3) {
            isScanning = false
        }
    }
}

// PeerInfo needs Identifiable conformance for `.sheet(item:)` — id is already publicKeyB64.

struct NameContactView: View {
    let peer: PeerInfo
    @Binding var nameText: String
    let onSave: () -> Void
    let onSkip: () -> Void

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    HStack(spacing: 14) {
                        AvatarView(name: nameText.isEmpty ? peer.username : nameText, size: 64)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(peer.username).font(.headline)
                            Text(peer.ip).font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    .padding(.vertical, 4)
                }
                Section("Save as") {
                    TextField("Contact name", text: $nameText)
                }
            }
            .formStyle(.grouped)
            .navigationTitle("Name Contact")
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Use \"\(peer.username)\"") { onSkip() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Save") { onSave() }
                        .buttonStyle(.borderedProminent)
                        .tint(Theme.accent)
                }
            }
        }
        .frame(minWidth: 360, minHeight: 280)
    }
}
