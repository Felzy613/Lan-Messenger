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
    @State private var relayEnabled = ConfigStore.shared.config.relayEnabled
    @State private var relayWorkerURL = ConfigStore.shared.config.relayWorkerURL
    @State private var loginItemStatusText = ""
    @State private var loginItemNeedsApproval = false
    @State private var updateStatus = ""
    @State private var isCheckingUpdates = false
    @State private var logExportMessage = ""
    @State private var notesExpanded = false

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

                Section("Cloud Relay") {
                    Toggle("Enable cloud relay", isOn: $relayEnabled)
                    Text("When enabled, messages sent to offline contacts are stored in the cloud and delivered when they reconnect. Deploy your own Cloudflare Worker (see README) and paste its URL below.")
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                    if relayEnabled {
                        TextField("Worker URL", text: $relayWorkerURL,
                                  prompt: Text("https://your-worker.workers.dev"))
                    }
                }

                Section("Updates") {
                    TextField("Source", text: $updateRepo,
                              prompt: Text("owner/repo"))
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
                                releaseNotesView(notes: info.notes)
                            }
                            progressView()
                        }
                        .padding(8)
                        .background(Theme.accent.opacity(0.07), in: RoundedRectangle(cornerRadius: 8))
                        .onChange(of: model.availableUpdate) { _ in notesExpanded = false }
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

    // Strips the "Downloads / Install" section that belongs in CI release pages,
    // not in an in-app changelog. Everything from the first "---" separator or
    // a "## Downloads" / "## Install" heading is removed.
    private func trimmedNotes(_ raw: String) -> String {
        var result: [String] = []
        for line in raw.components(separatedBy: "\n") {
            let t = line.trimmingCharacters(in: .whitespaces)
            if t == "---" || t.hasPrefix("## Downloads") || t.hasPrefix("## Install") {
                break
            }
            result.append(line)
        }
        while result.last?.trimmingCharacters(in: .whitespaces).isEmpty == true {
            result.removeLast()
        }
        return result.joined(separator: "\n")
    }

    // Parses inline markdown (bold, code, links) within a single line of text.
    private func inlineText(_ s: String) -> Text {
        let attr = (try? AttributedString(
            markdown: s,
            options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace)
        )) ?? AttributedString(s)
        return Text(attr)
    }

    // Renders a single line with the appropriate visual treatment based on its
    // Markdown prefix (heading, bullet, blockquote, or body text).
    @ViewBuilder
    private func noteLineView(_ line: String) -> some View {
        let t = line.trimmingCharacters(in: .whitespaces)
        if t.isEmpty {
            Color.clear.frame(height: 4)
        } else if t.hasPrefix("### ") {
            inlineText(String(t.dropFirst(4)))
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(.primary.opacity(0.85))
        } else if t.hasPrefix("## ") {
            inlineText(String(t.dropFirst(3)))
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(.primary)
        } else if t.hasPrefix("- ") || t.hasPrefix("* ") {
            HStack(alignment: .top, spacing: 5) {
                Text("•")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .frame(width: 8, alignment: .leading)
                inlineText(String(t.dropFirst(2)))
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        } else if t.hasPrefix("> ") {
            inlineText(String(t.dropFirst(2)))
                .font(.system(size: 11))
                .italic()
                .foregroundStyle(.tertiary)
        } else {
            inlineText(t)
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
        }
    }

    // Renders release notes with proper heading hierarchy and bullet formatting.
    // Collapses to 8 lines; a "Show more" toggle reveals the rest.
    @ViewBuilder
    private func releaseNotesView(notes: String) -> some View {
        let trimmed = trimmedNotes(notes)
        let lines = trimmed.components(separatedBy: "\n")
        let isLong = lines.count > 8
        let visibleLines = isLong && !notesExpanded ? Array(lines.prefix(8)) : lines

        VStack(alignment: .leading, spacing: 2) {
            VStack(alignment: .leading, spacing: 1) {
                ForEach(Array(visibleLines.enumerated()), id: \.offset) { _, line in
                    noteLineView(line)
                }
            }
            if isLong {
                Button(notesExpanded ? "Show less" : "Show more") {
                    withAnimation(.easeInOut(duration: 0.15)) { notesExpanded.toggle() }
                }
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(Theme.accent)
                .buttonStyle(.plain)
                .padding(.top, 2)
            }
        }
    }

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
        ConfigStore.shared.config.relayEnabled = relayEnabled
        ConfigStore.shared.config.relayWorkerURL = relayWorkerURL.trimmingCharacters(in: .whitespaces)
        ConfigStore.shared.save()
        model.applyDockPolicy()
    }

    private func openLogsFolder() {
        NSWorkspace.shared.open(NetLogger.logsDirectory)
    }

    private func exportLog() {
        let sources = NetLogger.archivedLogURLs()
        guard !sources.isEmpty else {
            logExportMessage = "No log file yet."
            return
        }
        let panel = NSSavePanel()
        let fmt = DateFormatter()
        fmt.dateFormat = "yyyy-MM-dd_HHmmss"
        // Bundle the active log + every rotated archive into a single zip so
        // the user can attach one file to a bug report.
        panel.nameFieldStringValue = "LanMessenger-Logs-\(fmt.string(from: Date())).zip"
        panel.allowedContentTypes = [.zip]
        guard panel.runModal() == .OK, let dest = panel.url else { return }

        // Stage everything into a temp directory then run `ditto -c -k` to make
        // a Finder-friendly zip.  Avoids pulling in a third-party zip library.
        let staging = URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true)
            .appendingPathComponent("LanMessenger-LogExport-\(UUID().uuidString)", isDirectory: true)
        do {
            try FileManager.default.createDirectory(at: staging, withIntermediateDirectories: true)
            defer { try? FileManager.default.removeItem(at: staging) }

            for src in sources {
                let copy = staging.appendingPathComponent(src.lastPathComponent)
                try? FileManager.default.removeItem(at: copy)
                try FileManager.default.copyItem(at: src, to: copy)
            }

            if FileManager.default.fileExists(atPath: dest.path) {
                try FileManager.default.removeItem(at: dest)
            }

            let task = Process()
            task.launchPath = "/usr/bin/ditto"
            task.arguments = ["-c", "-k", "--sequesterRsrc", "--keepParent", staging.path, dest.path]
            try task.run()
            task.waitUntilExit()
            logExportMessage = task.terminationStatus == 0 ? "Exported ✓" : "Export failed."
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
