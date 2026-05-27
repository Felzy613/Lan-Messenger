import SwiftUI
import UniformTypeIdentifiers
import AppKit

struct ComposerView: View {
    @EnvironmentObject var model: AppModel
    let peerIP: String
    @Binding var replyTarget: MessageEntry?

    @State private var draft = ""
    @State private var isDragTargeted = false
    @State private var measuredHeight: CGFloat = 36
    @State private var typingTimer: Task<Void, Never>?

    // Screenshot flow ─────────────────────────────────────────────────────────
    // Step 1 → camera button tapped → fetch window list → show picker sheet
    // Step 2 → user picks a window → capture → show preview sheet
    // Step 3 → user clicks Send in preview → sendFile() → dismiss
    @State private var screenshotBusy = false          // spinner on camera button
    @State private var screenshotError: String? = nil  // surfaced in alert
    @State private var showWindowPicker = false
    @State private var windowPickerItems: [ScreenshotWindowItem] = []
    @State private var showScreenshotPreview = false
    @State private var capturedScreenshotPath: String? = nil

    private let minHeight: CGFloat = 36
    private let maxHeight: CGFloat = 120

    private var clampedHeight: CGFloat {
        min(max(measuredHeight, minHeight), maxHeight)
    }

    var body: some View {
        HStack(alignment: .bottom, spacing: 8) {
            Button { openFilePicker() } label: {
                Image(systemName: "paperclip")
                    .font(.system(size: 16))
                    .frame(width: 32, height: 32)
            }
            .buttonStyle(.plain)
            .foregroundStyle(.secondary)
            .padding(.bottom, 4)
            .help("Send file")

            // Screenshot capture button.  Disabled while a capture is in
            // progress so a double-tap can't enqueue two PNGs in a row.
            Button { startScreenshotFlow() } label: {
                ZStack {
                    if screenshotBusy {
                        ProgressView().controlSize(.small)
                    } else {
                        Image(systemName: "camera.viewfinder")
                            .font(.system(size: 16))
                    }
                }
                .frame(width: 32, height: 32)
            }
            .buttonStyle(.plain)
            .foregroundStyle(.secondary)
            .padding(.bottom, 4)
            .disabled(screenshotBusy)
            .help("Capture and send a screenshot")

            ZStack(alignment: .topLeading) {
                // Hidden, off-screen text used to measure ideal height for the draft string.
                Text(draft.isEmpty ? " " : draft)
                    .font(.system(size: 14))
                    .padding(.horizontal, 12)
                    .padding(.vertical, 8)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(GeometryReader { geo in
                        Color.clear
                            .preference(key: ComposerHeightKey.self, value: geo.size.height)
                    })
                    .hidden()

                if draft.isEmpty {
                    Text("Message")
                        .font(.system(size: 14))
                        .foregroundStyle(.tertiary)
                        .padding(.horizontal, 12)
                        .padding(.vertical, 8)
                        .allowsHitTesting(false)
                }

                ComposerTextEditor(text: $draft, onSubmit: send)
            }
            .frame(height: clampedHeight)
            .onPreferenceChange(ComposerHeightKey.self) { newValue in
                measuredHeight = newValue
            }
            .background(.quaternary, in: RoundedRectangle(cornerRadius: 18))
            .overlay(
                RoundedRectangle(cornerRadius: 18)
                    .stroke(isDragTargeted ? Theme.accent : Color.clear, lineWidth: 2)
            )
            .onDrop(of: [.fileURL], isTargeted: $isDragTargeted) { providers in
                guard let provider = providers.first else { return false }
                provider.loadItem(forTypeIdentifier: UTType.fileURL.identifier, options: nil) { item, _ in
                    guard let data = item as? Data,
                          let url = URL(dataRepresentation: data, relativeTo: nil) else { return }
                    DispatchQueue.main.async { model.sendFile(path: url.path, toPeerIP: peerIP) }
                }
                return true
            }
            .onChange(of: draft) { newValue in
                typingTimer?.cancel()
                if newValue.isEmpty {
                    model.sendTyping(false, toPeerIP: peerIP)
                } else {
                    model.sendTyping(true, toPeerIP: peerIP)
                    typingTimer = Task {
                        try? await Task.sleep(for: .seconds(3))
                        guard !Task.isCancelled else { return }
                        model.sendTyping(false, toPeerIP: peerIP)
                    }
                }
            }

            Button(action: send) {
                Image(systemName: "arrow.up.circle.fill")
                    .font(.system(size: 28))
                    .foregroundStyle(draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                                     ? AnyShapeStyle(.tertiary)
                                     : AnyShapeStyle(Theme.accent))
            }
            .buttonStyle(.plain)
            .disabled(draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            .padding(.bottom, 2)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        // Screenshot error alert
        .alert("Screenshot failed",
               isPresented: Binding(get: { screenshotError != nil },
                                    set: { if !$0 { screenshotError = nil } })) {
            Button("OK", role: .cancel) { }
        } message: {
            Text(screenshotError ?? "")
        }
        // Step 1 – window picker
        .sheet(isPresented: $showWindowPicker) {
            WindowPickerView(items: windowPickerItems, onSelect: { item in
                handleWindowSelected(item)
            }, onCancel: {
                showWindowPicker = false
            })
        }
        // Step 2 – preview before sending
        .sheet(isPresented: $showScreenshotPreview) {
            if let path = capturedScreenshotPath {
                ScreenshotPreviewView(
                    imagePath: path,
                    onSend: { handleSendScreenshot() },
                    onCancel: {
                        // Clean up the file – user chose not to send it.
                        if let p = capturedScreenshotPath {
                            try? FileManager.default.removeItem(atPath: p)
                        }
                        capturedScreenshotPath = nil
                        showScreenshotPreview = false
                    }
                )
            }
        }
    }

    // MARK: - Screenshot flow

    /// Step 1: fetch the list of capturable windows, then show the picker.
    private func startScreenshotFlow() {
        guard !screenshotBusy else { return }
        screenshotBusy = true
        Task {
            do {
                let windows = try await ScreenshotService.getShareableWindows()
                var items: [ScreenshotWindowItem] = [.fullScreen]
                items += windows.map { .window($0) }
                windowPickerItems = items
                screenshotBusy = false
                showWindowPicker = true
            } catch let err as ScreenshotError {
                screenshotBusy = false
                screenshotError = err.errorDescription
            } catch {
                screenshotBusy = false
                screenshotError = error.localizedDescription
            }
        }
    }

    /// Step 2: user chose a window (or Full Screen) from the picker.
    /// Dismiss the picker, wait for its animation to finish, then capture.
    private func handleWindowSelected(_ item: ScreenshotWindowItem) {
        showWindowPicker = false
        screenshotBusy = true
        Task {
            // Allow the picker sheet to finish its dismiss animation before
            // capturing.  For Full Screen this also ensures the picker is gone
            // from the screenshot.
            try? await Task.sleep(nanoseconds: 380_000_000)
            do {
                let path: String
                switch item {
                case .fullScreen:
                    path = try await ScreenshotService.capturePrimaryDisplay()
                case .window(let info):
                    path = try await ScreenshotService.captureWindow(id: info.id)
                }
                capturedScreenshotPath = path
                screenshotBusy = false
                showScreenshotPreview = true
            } catch let err as ScreenshotError {
                screenshotBusy = false
                screenshotError = err.errorDescription
            } catch {
                screenshotBusy = false
                screenshotError = error.localizedDescription
            }
        }
    }

    /// Step 3: user clicked Send in the preview sheet.
    private func handleSendScreenshot() {
        guard let path = capturedScreenshotPath else { return }
        showScreenshotPreview = false
        capturedScreenshotPath = nil
        // Hand off through the same path drag-drop and the file picker use.
        model.sendFile(path: path, toPeerIP: peerIP)
    }

    // MARK: - Text message

    private func send() {
        let trimmed = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        typingTimer?.cancel()
        typingTimer = nil
        model.sendMessage(trimmed, toPeerIP: peerIP, replyTo: replyTarget)
        draft = ""
        replyTarget = nil
        model.sendTyping(false, toPeerIP: peerIP)
    }

    private func openFilePicker() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        // Non-blocking — uses an async completion so the UI doesn't freeze if
        // the system dialog takes time to render or appears off-screen.
        panel.begin { response in
            guard response == .OK, let url = panel.url else { return }
            model.sendFile(path: url.path, toPeerIP: peerIP)
        }
    }
}

// MARK: - NSTextView wrapper: Return=send, Shift+Return=newline, auto-scroll

struct ComposerTextEditor: NSViewRepresentable {
    @Binding var text: String
    var onSubmit: () -> Void

    func makeNSView(context: Context) -> NSScrollView {
        let scrollView = NSTextView.scrollableTextView()
        guard let tv = scrollView.documentView as? NSTextView else { return scrollView }
        tv.isRichText = false
        tv.font = .systemFont(ofSize: 14)
        tv.delegate = context.coordinator
        tv.textContainerInset = NSSize(width: 4, height: 6)
        tv.isAutomaticQuoteSubstitutionEnabled = false
        tv.isAutomaticDashSubstitutionEnabled = false
        tv.drawsBackground = false
        scrollView.drawsBackground = false
        scrollView.hasVerticalScroller = false
        return scrollView
    }

    func updateNSView(_ scrollView: NSScrollView, context: Context) {
        guard let tv = scrollView.documentView as? NSTextView else { return }
        if tv.string != text { tv.string = text }
    }

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    final class Coordinator: NSObject, NSTextViewDelegate {
        var parent: ComposerTextEditor
        init(_ parent: ComposerTextEditor) { self.parent = parent }

        func textDidChange(_ notification: Notification) {
            guard let tv = notification.object as? NSTextView else { return }
            parent.text = tv.string
        }

        func textView(_ textView: NSTextView, doCommandBy commandSelector: Selector) -> Bool {
            if commandSelector == #selector(NSResponder.insertNewline(_:)) {
                if NSApp.currentEvent?.modifierFlags.contains(.shift) == true {
                    textView.insertNewlineIgnoringFieldEditor(nil)
                } else {
                    parent.onSubmit()
                }
                return true
            }
            return false
        }
    }
}

// MARK: - Height measurement preference key

private struct ComposerHeightKey: PreferenceKey {
    static var defaultValue: CGFloat = 36
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) {
        value = nextValue()
    }
}

// MARK: - Screenshot flow: window item model

enum ScreenshotWindowItem: Identifiable {
    case fullScreen
    case window(WindowInfo)

    var id: String {
        switch self {
        case .fullScreen:          return "__fullscreen__"
        case .window(let w):       return String(w.id)
        }
    }

    var iconName: String {
        switch self {
        case .fullScreen: return "display"
        case .window:     return "macwindow"
        }
    }

    var displayTitle: String {
        switch self {
        case .fullScreen:    return "Full Screen"
        case .window(let w): return w.title
        }
    }

    /// Secondary label shown below the title.  Nil when the subtitle would
    /// duplicate the title (e.g. when the window had no title so title == appName).
    var subtitle: String? {
        switch self {
        case .fullScreen:
            return "Capture the entire display"
        case .window(let w):
            guard !w.appName.isEmpty, w.appName != w.title else { return nil }
            return w.appName
        }
    }
}

// MARK: - Screenshot flow: window picker sheet

struct WindowPickerView: View {
    let items: [ScreenshotWindowItem]
    let onSelect: (ScreenshotWindowItem) -> Void
    let onCancel: () -> Void

    var body: some View {
        NavigationStack {
            Group {
                if items.count <= 1 {
                    // Only Full Screen is available (no other windows found).
                    VStack(spacing: 12) {
                        Image(systemName: "macwindow.badge.plus")
                            .font(.system(size: 40))
                            .foregroundStyle(.secondary)
                        Text("No other windows found")
                            .font(.headline)
                            .foregroundStyle(.secondary)
                        Text("Open another app's window and try again, or capture the full screen.")
                            .font(.caption)
                            .foregroundStyle(.tertiary)
                            .multilineTextAlignment(.center)
                            .frame(maxWidth: 280)
                        Button("Capture Full Screen") { onSelect(.fullScreen) }
                            .buttonStyle(.borderedProminent)
                            .padding(.top, 4)
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                } else {
                    List(items) { item in
                        Button { onSelect(item) } label: {
                            HStack(spacing: 12) {
                                Image(systemName: item.iconName)
                                    .font(.system(size: 18))
                                    .frame(width: 28)
                                    .foregroundStyle(Theme.accent)
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(item.displayTitle)
                                        .font(.system(size: 14, weight: .medium))
                                        .foregroundStyle(.primary)
                                    if let sub = item.subtitle {
                                        Text(sub)
                                            .font(.system(size: 12))
                                            .foregroundStyle(.secondary)
                                    }
                                }
                                Spacer()
                                Image(systemName: "chevron.right")
                                    .font(.system(size: 11, weight: .semibold))
                                    .foregroundStyle(.tertiary)
                            }
                            .contentShape(Rectangle())
                            .padding(.vertical, 4)
                        }
                        .buttonStyle(.plain)
                    }
                    .listStyle(.inset)
                }
            }
            .navigationTitle("Select Window")
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { onCancel() }
                }
            }
        }
        .frame(minWidth: 380, minHeight: 300)
    }
}

// MARK: - Screenshot flow: preview sheet

struct ScreenshotPreviewView: View {
    let imagePath: String
    let onSend: () -> Void
    let onCancel: () -> Void

    private var image: NSImage? { NSImage(contentsOfFile: imagePath) }

    var body: some View {
        NavigationStack {
            ZStack {
                Color(nsColor: .windowBackgroundColor)
                    .ignoresSafeArea()
                if let img = image {
                    Image(nsImage: img)
                        .resizable()
                        .aspectRatio(contentMode: .fit)
                        .padding(20)
                        .shadow(color: .black.opacity(0.15), radius: 8, x: 0, y: 2)
                } else {
                    VStack(spacing: 12) {
                        Image(systemName: "photo.slash")
                            .font(.system(size: 40))
                            .foregroundStyle(.secondary)
                        Text("Could not load screenshot")
                            .foregroundStyle(.secondary)
                    }
                }
            }
            .navigationTitle("Screenshot Preview")
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { onCancel() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Send") { onSend() }
                        .buttonStyle(.borderedProminent)
                }
            }
        }
        .frame(minWidth: 520, minHeight: 400)
    }
}
