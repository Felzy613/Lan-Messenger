import SwiftUI
import AppKit

struct SettingsView: View {
    @EnvironmentObject var model: AppModel
    @Environment(\.dismiss) var dismiss

    @State private var username = ConfigStore.shared.config.username
    @State private var updateRepo = ConfigStore.shared.config.updateRepo
    @State private var inboxDir = ConfigStore.shared.config.inboxDir
    @State private var hideFromDock = ConfigStore.shared.config.hideFromDock
    @State private var launchAtLogin = ConfigStore.shared.config.launchAtLogin
    @State private var verboseLogging = ConfigStore.shared.config.verboseLogging
    @State private var loginItemStatusText = ""
    @State private var loginItemNeedsApproval = false
    @State private var updateStatus = ""
    @State private var isCheckingUpdates = false
    @State private var logExportMessage = ""

    var body: some View {
        NavigationStack {
            Form {
                Section("Identity") {
                    TextField("Display name", text: $username)
                }

                Section("Appearance") {
                    // Toggle is "Don't hide icon in dock" — ON means show in dock,
                    // OFF (default) means hide from dock (menu-bar-only mode).
                    Toggle("Don't hide icon in dock", isOn: Binding(
                        get: { !hideFromDock },
                        set: { hideFromDock = !$0 }
                    ))
                }

                Section("Startup") {
                    Toggle("Launch at login", isOn: $launchAtLogin)
                        .onChange(of: launchAtLogin) { newValue in
                            applyLoginItem(enabled: newValue)
                        }
                    if !loginItemStatusText.isEmpty {
                        HStack(spacing: 8) {
                            Image(systemName: loginItemNeedsApproval ? "exclamationmark.triangle.fill" : "checkmark.circle.fill")
                                .foregroundStyle(loginItemNeedsApproval ? .orange : Theme.accent)
                            Text(loginItemStatusText)
                                .font(.system(size: 11))
                                .foregroundStyle(.secondary)
                            Spacer()
                            if loginItemNeedsApproval {
                                Button("Open Login Items…") {
                                    LoginItemService.openSystemLoginItemsPane()
                                }
                                .buttonStyle(.bordered)
                            }
                        }
                    }
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

                Section("Logging") {
                    Toggle("Verbose logging", isOn: $verboseLogging)
                    Text("Logs file transfers, connections, and protocol events to a file. Useful for diagnosing transfer failures.")
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                    HStack(spacing: 8) {
                        Button("Open Logs Folder") { openLogsFolder() }
                            .buttonStyle(.bordered)
                        Button("Export Log…") { exportLog() }
                            .buttonStyle(.bordered)
                        if !logExportMessage.isEmpty {
                            Text(logExportMessage)
                                .font(.system(size: 11))
                                .foregroundStyle(.secondary)
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
        .frame(minWidth: 460, minHeight: 500)
        .onAppear { refreshLoginItemStatus() }
    }

    private func applyLoginItem(enabled: Bool) {
        let status = LoginItemService.setEnabled(enabled)
        ConfigStore.shared.config.launchAtLogin = enabled
        ConfigStore.shared.save()
        renderLoginItemStatus(status, requested: enabled)
    }

    private func refreshLoginItemStatus() {
        renderLoginItemStatus(LoginItemService.currentStatus, requested: launchAtLogin)
    }

    private func renderLoginItemStatus(_ status: LoginItemService.Status, requested: Bool) {
        switch status {
        case .enabled:
            loginItemStatusText = "Will start when you log in."
            loginItemNeedsApproval = false
        case .disabled:
            loginItemStatusText = requested
                ? "Couldn't register — try moving the app to /Applications."
                : ""
            loginItemNeedsApproval = false
        case .requiresApproval:
            loginItemStatusText = "Approval needed in System Settings → Login Items."
            loginItemNeedsApproval = true
        case .notSupported:
            loginItemStatusText = "Not supported on this macOS version."
            loginItemNeedsApproval = false
        case .error(let msg):
            loginItemStatusText = "Couldn't update login item: \(msg)"
            loginItemNeedsApproval = false
        }
    }

    // MARK: - Update UI helpers

    private func installButton(info: UpdateInfo) -> some View {
        Group {
            switch model.updateProgress {
            case .downloading, .verifying, .installing:
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
        case .verifying:
            HStack {
                ProgressView()
                    .controlSize(.small)
                Text("Verifying integrity…")
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
        ConfigStore.shared.config.verboseLogging = verboseLogging
        ConfigStore.shared.save()
        model.applyDockPolicy()
    }

    private func openLogsFolder() {
        NSWorkspace.shared.open(NetLogger.logsDirectory)
    }

    private func exportLog() {
        let src = NetLogger.logURL
        guard FileManager.default.fileExists(atPath: src.path) else {
            logExportMessage = "No log file yet."
            return
        }
        let panel = NSSavePanel()
        let fmt = DateFormatter()
        fmt.dateFormat = "yyyy-MM-dd_HHmmss"
        panel.nameFieldStringValue = "LanMessenger-\(fmt.string(from: Date())).log"
        panel.allowedContentTypes = [.plainText]
        guard panel.runModal() == .OK, let dest = panel.url else { return }
        do {
            if FileManager.default.fileExists(atPath: dest.path) {
                try FileManager.default.removeItem(at: dest)
            }
            try FileManager.default.copyItem(at: src, to: dest)
            logExportMessage = "Exported ✓"
        } catch {
            logExportMessage = "Export failed: \(error.localizedDescription)"
        }
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
