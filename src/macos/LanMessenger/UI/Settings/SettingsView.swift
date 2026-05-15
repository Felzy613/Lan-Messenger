import SwiftUI

struct SettingsView: View {
    @EnvironmentObject var model: AppModel
    @Environment(\.dismiss) var dismiss

    @State private var username = ConfigStore.shared.config.username
    @State private var updateRepo = ConfigStore.shared.config.updateRepo
    @State private var inboxDir = ConfigStore.shared.config.inboxDir
    @State private var hideFromDock = ConfigStore.shared.config.hideFromDock
    @State private var updateStatus = ""
    @State private var isCheckingUpdates = false

    var body: some View {
        NavigationStack {
            Form {
                Section("Identity") {
                    TextField("Display name", text: $username)
                }

                Section("Appearance") {
                    Toggle("Hide from Dock (menu bar only)", isOn: $hideFromDock)
                }

                Section("Files") {
                    LabeledContent("Save location") {
                        HStack(spacing: 8) {
                            Text(inboxDir.isEmpty ? "Default" : inboxDir)
                                .foregroundStyle(inboxDir.isEmpty ? .secondary : .primary)
                                .lineLimit(1)
                                .truncationMode(.middle)
                            Spacer(minLength: 0)
                            if !inboxDir.isEmpty {
                                Button("Reset") { inboxDir = "" }
                                    .buttonStyle(.bordered)
                            }
                            Button("Choose…") { pickInboxDir() }
                                .buttonStyle(.bordered)
                        }
                    }
                }

                Section("Updates") {
                    LabeledContent("Source") {
                        TextField("GitHub repo (owner/repo)", text: $updateRepo)
                            .textFieldStyle(.roundedBorder)
                    }
                    HStack {
                        Button {
                            checkForUpdates()
                        } label: {
                            if isCheckingUpdates {
                                ProgressView()
                                    .controlSize(.small)
                                    .padding(.trailing, 4)
                            }
                            Text("Check Now")
                        }
                        .buttonStyle(.bordered)
                        .disabled(isCheckingUpdates)

                        if !updateStatus.isEmpty {
                            Text(updateStatus)
                                .font(.system(size: 12))
                                .foregroundStyle(.secondary)
                        }
                    }

                    if let info = model.availableUpdate {
                        VStack(alignment: .leading, spacing: 6) {
                            HStack {
                                Image(systemName: "arrow.down.circle.fill")
                                    .foregroundStyle(Theme.accent)
                                Text("Version \(info.version) available")
                                    .font(.system(size: 13, weight: .semibold))
                                Spacer()
                                installButton(info: info)
                            }
                            if !info.notes.isEmpty {
                                Text(info.notes)
                                    .font(.system(size: 11))
                                    .foregroundStyle(.secondary)
                                    .lineLimit(8)
                            }
                            progressView()
                        }
                        .padding(8)
                        .background(Theme.accent.opacity(0.07), in: RoundedRectangle(cornerRadius: 8))
                    }
                }

                Section {
                    HStack {
                        Image(systemName: "info.circle")
                            .foregroundStyle(.secondary)
                        Text("LAN Messenger \(UpdateService.appVersion)")
                            .foregroundStyle(.secondary)
                            .font(.system(size: 12))
                    }
                } header: {
                    Text("About")
                }
            }
            .formStyle(.grouped)
            .navigationTitle("Settings")
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Save") { save(); dismiss() }
                        .buttonStyle(.borderedProminent)
                        .tint(Theme.accent)
                }
            }
        }
        .frame(minWidth: 460, minHeight: 380)
    }

    // MARK: - Update UI helpers

    private func installButton(info: UpdateInfo) -> some View {
        Group {
            switch model.updateProgress {
            case .downloading, .installing:
                EmptyView()
            default:
                Button("Install") { model.installUpdate() }
                    .buttonStyle(.borderedProminent)
                    .tint(Theme.accent)
            }
        }
    }

    @ViewBuilder
    private func progressView() -> some View {
        switch model.updateProgress {
        case .downloading(let p):
            ProgressView(value: p) {
                Text("Downloading… \(Int(p * 100))%")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
            }
        case .installing:
            HStack {
                ProgressView()
                    .controlSize(.small)
                Text("Installing… the app will relaunch shortly.")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
            }
        case .failed(let m):
            Text("Failed: \(m)")
                .font(.system(size: 11))
                .foregroundStyle(.red)
        case .idle:
            EmptyView()
        }
    }

    // MARK: - Actions

    private func save() {
        ConfigStore.shared.config.username = username
        ConfigStore.shared.config.updateRepo = updateRepo.trimmingCharacters(in: .whitespaces)
        ConfigStore.shared.config.inboxDir = inboxDir
        ConfigStore.shared.config.hideFromDock = hideFromDock
        ConfigStore.shared.save()
        model.applyDockPolicy()
    }

    private func pickInboxDir() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = false
        panel.prompt = "Select"
        if panel.runModal() == .OK, let url = panel.url {
            inboxDir = url.path
        }
    }

    private func checkForUpdates() {
        isCheckingUpdates = true
        updateStatus = ""
        Task { @MainActor in
            let result = await UpdateService.shared.check(repo: updateRepo.trimmingCharacters(in: .whitespaces))
            isCheckingUpdates = false
            switch result {
            case .upToDate:
                updateStatus = "You're up to date ✓"
                model.availableUpdate = nil
            case .available(let info):
                updateStatus = "v\(info.version) available"
                model.availableUpdate = info
            case .error(let msg):
                updateStatus = "Error: \(msg)"
            }
        }
    }
}
