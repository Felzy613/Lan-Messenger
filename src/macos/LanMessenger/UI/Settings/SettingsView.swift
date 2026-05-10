import SwiftUI

struct SettingsView: View {
    @EnvironmentObject var model: AppModel
    @Environment(\.dismiss) var dismiss

    @State private var username = ConfigStore.shared.config.username
    @State private var updateServerURL = ConfigStore.shared.config.updateServerURL
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
                    TextField("Update server URL", text: $updateServerURL)
                    HStack {
                        Button {
                            checkForUpdates()
                        } label: {
                            if isCheckingUpdates {
                                ProgressView()
                                    .controlSize(.small)
                                    .padding(.trailing, 4)
                            }
                            Text("Check for Updates")
                        }
                        .buttonStyle(.bordered)
                        .disabled(isCheckingUpdates)

                        if !updateStatus.isEmpty {
                            Text(updateStatus)
                                .font(.system(size: 12))
                                .foregroundStyle(.secondary)
                        }
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
        .frame(minWidth: 420, minHeight: 340)
    }

    // MARK: - Actions

    private func save() {
        ConfigStore.shared.config.username = username
        ConfigStore.shared.config.updateServerURL = updateServerURL
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
        UpdateService.shared.check(manifestURL: updateServerURL) { result in
            isCheckingUpdates = false
            switch result {
            case .upToDate:
                updateStatus = "You're up to date ✓"
            case .available(let info):
                updateStatus = "v\(info.version) available"
            case .error(let msg):
                updateStatus = "Error: \(msg)"
            }
        }
    }
}
