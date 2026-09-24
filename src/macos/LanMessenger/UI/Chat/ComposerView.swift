import SwiftUI
import AppKit

struct ComposerView: View {
    @EnvironmentObject var model: AppModel
    let peerIP: String
    @Binding var replyTarget: MessageEntry?
    /// Non-nil while editing an already-sent message. The composer swaps its
    /// draft for that message's text and Send applies the edit.
    @Binding var editTarget: MessageEntry?

    @State private var draft = ""
    /// The in-progress draft that edit mode displaced, restored on cancel or
    /// after the edit is sent — entering edit mode must not eat what the user
    /// had already typed.
    @State private var draftBeforeEdit = ""
    @State private var measuredHeight: CGFloat = 36
    @State private var typingTimer: Task<Void, Never>?

    // Screenshot flow ─────────────────────────────────────────────────────────
    // Step 1 → camera button tapped → `screencapture -i` overlay (drag a region
    //          or click a window, like Cmd+Shift+4 / Cmd+Shift+5's "Selected
    //          Portion"). Esc cancels silently (no file written, no error shown).
    // Step 2 → on success, show the preview sheet with the captured PNG.
    // Step 3 → user clicks Send in preview → sendFile() → dismiss.
    @State private var screenshotBusy = false          // spinner on camera button
    @State private var screenshotError: String? = nil  // surfaced in alert
    @State private var showScreenshotPreview = false
    @State private var capturedScreenshotPath: String? = nil

    private let minHeight: CGFloat = 36
    private let maxHeight: CGFloat = 130

    private var clampedHeight: CGFloat {
        min(max(measuredHeight, minHeight), maxHeight)
    }

    private var canSend: Bool {
        !draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    var body: some View {
        Group {
            if LiquidGlass.isAvailable {
                glassComposer
            } else {
                classicComposer
            }
        }
        // Screenshot error alert
        .alert("Screenshot failed",
               isPresented: Binding(get: { screenshotError != nil },
                                    set: { if !$0 { screenshotError = nil } })) {
            Button("OK", role: .cancel) { }
        } message: {
            Text(screenshotError ?? "")
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

    // MARK: - Layouts

    /// macOS 26: one glass pill (banner, attach, screenshot, field) beside the
    /// send orb, merged as glass by the container.
    @ViewBuilder
    private var glassComposer: some View {
        #if compiler(>=6.2)
        if #available(macOS 26, *) {
            GlassEffectContainer(spacing: 8) {
                HStack(alignment: .bottom, spacing: 8) {
                    VStack(alignment: .leading, spacing: 0) {
                        banner
                        HStack(alignment: .bottom, spacing: 2) {
                            attachButton
                            screenshotButton
                            field
                        }
                    }
                    .padding(4)
                    .glassEffect(.regular, in: .rect(cornerRadius: 22))
                    sendOrb
                        .padding(.bottom, 4)
                }
            }
            .padding(.horizontal, 8)
            .padding(.top, 4)
            .padding(.bottom, 8)
        } else {
            classicComposer
        }
        #else
        classicComposer
        #endif
    }

    /// Before macOS 26: the strip ChatView puts on `.bar`, with the field on
    /// `.quaternary` as before. It still takes the banner and the orb, so the
    /// layout matches the glass one.
    private var classicComposer: some View {
        VStack(alignment: .leading, spacing: 0) {
            banner
            HStack(alignment: .bottom, spacing: 8) {
                attachButton
                screenshotButton
                field
                    .background(.quaternary, in: RoundedRectangle(cornerRadius: 18))
                sendOrb
                    .padding(.bottom, 2)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }

    // MARK: - Pieces

    private var attachButton: some View {
        Button { openFilePicker() } label: {
            Image(systemName: "paperclip")
                .font(.system(size: 16))
                .frame(width: GlassTokens.Size.iconButton, height: GlassTokens.Size.iconButton)
                .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .foregroundStyle(Theme.inkSecondary)
        .help("Send file")
    }

    // Screenshot capture button.  Disabled while a capture is in
    // progress so a double-tap can't enqueue two PNGs in a row.
    private var screenshotButton: some View {
        Button { startScreenshotFlow() } label: {
            ZStack {
                if screenshotBusy {
                    ProgressView().controlSize(.small)
                } else {
                    Image(systemName: "camera.viewfinder")
                        .font(.system(size: 16))
                }
            }
            .frame(width: GlassTokens.Size.iconButton, height: GlassTokens.Size.iconButton)
            .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .foregroundStyle(Theme.inkSecondary)
        .disabled(screenshotBusy)
        .help("Capture and send a screenshot")
    }

    private var field: some View {
        ZStack(alignment: .topLeading) {
            if draft.isEmpty {
                Text("Message")
                    .font(GlassTokens.Typography.message)
                    .foregroundStyle(Theme.inkSecondary)
                    .padding(.horizontal, 12)
                    .padding(.vertical, 8)
                    .allowsHitTesting(false)
            }

            // File drops are handled by ChatView, which covers the whole
            // thread rather than just this strip.
            ComposerTextEditor(text: $draft,
                               contentHeight: $measuredHeight,
                               onSubmit: send,
                               onCancel: cancelComposerMode,
                               onPasteAttachments: sendPastedAttachments)
        }
        .frame(height: clampedHeight)
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
            // While editing, the composer is showing someone's already-sent
            // message — persisting that as the draft would resurrect it as
            // an unsent message the next time the conversation is opened.
            if editTarget == nil {
                model.drafts[peerIP] = newValue.isEmpty ? nil : newValue
            }
        }
        // Keyed on id rather than the entry: MessageEntry isn't Equatable,
        // and the id is what changes when a different message is picked.
        .onChange(of: editTarget?.id) { _ in
            if let target = editTarget {
                draftBeforeEdit = draft
                draft = target.text
            } else {
                draft = draftBeforeEdit
                draftBeforeEdit = ""
            }
        }
        .onAppear {
            draft = model.drafts[peerIP] ?? ""
        }
    }

    /// The send orb: brand with an on-brand glyph while there is something to
    /// send (white on brand is 2:1), glass with an ink-secondary glyph while
    /// there is not. A checkmark while editing.
    @ViewBuilder
    private var sendOrb: some View {
        let glyph = Image(systemName: editTarget == nil ? "arrow.up" : "checkmark")
            .font(.system(size: 15, weight: .semibold))
            .frame(width: GlassTokens.Size.send, height: GlassTokens.Size.send)
        Button(action: send) {
            if canSend {
                glyph
                    .foregroundStyle(Theme.onBrand)
                    .background(Circle().fill(Theme.accent))
            } else {
                glyph
                    .foregroundStyle(Theme.inkSecondary)
                    .glassSurface(.regular, in: Circle())
            }
        }
        .buttonStyle(.plain)
        .contentShape(Circle())
        .disabled(!canSend)
        .help(editTarget == nil ? "Send" : "Save edit")
        .accessibilityLabel(Text(editTarget == nil ? "Send" : "Save edit"))
    }

    /// The reply or edit banner, grown inside the composer above the field.
    /// The strings and cancel actions are the ones ChatView used to own.
    @ViewBuilder
    private var banner: some View {
        if let editing = editTarget {
            bannerView(title: "Editing message",
                       preview: MessagingService.replyPreviewText(for: editing),
                       cancelHelp: "Cancel editing (Esc)") {
                withAnimation { editTarget = nil }
            }
            .transition(.opacity)
        } else if let reply = replyTarget {
            bannerView(title: "Replying to \(reply.incoming ? reply.sender : "yourself")",
                       preview: MessagingService.replyPreviewText(for: reply),
                       cancelHelp: nil) {
                withAnimation { replyTarget = nil }
            }
            .transition(.opacity)
        }
    }

    private func bannerView(title: String, preview: String, cancelHelp: String?,
                            cancel: @escaping () -> Void) -> some View {
        HStack(alignment: .center, spacing: 10) {
            RoundedRectangle(cornerRadius: 1.5)
                .fill(Theme.accent)
                .frame(width: 3)
            VStack(alignment: .leading, spacing: 0) {
                Text(title)
                    .font(GlassTokens.Typography.captionStrong)
                    .foregroundStyle(Theme.accentInk)
                Text(preview)
                    .font(GlassTokens.Typography.label)
                    .foregroundStyle(Theme.inkSecondary)
                    .lineLimit(1)
            }
            Spacer(minLength: 0)
            Button(action: cancel) {
                Image(systemName: "xmark.circle.fill")
                    .font(.system(size: 16))
                    .foregroundStyle(Theme.inkSecondary)
            }
            .buttonStyle(.plain)
            .help(cancelHelp ?? "Cancel")
        }
        .fixedSize(horizontal: false, vertical: true)
        .padding(.leading, 10)
        .padding(.trailing, 8)
        .padding(.vertical, 6)
        .background(Theme.insetFill, in: RoundedRectangle(cornerRadius: 18))
        .padding(EdgeInsets(top: 2, leading: 2, bottom: 4, trailing: 2))
    }

    // MARK: - Screenshot flow

    /// Step 1: launch the interactive screen capture overlay (`screencapture -i`),
    /// which lets the user drag a rectangle to capture a region or click a window
    /// to capture it — the same UX as Cmd+Shift+4 / Cmd+Shift+5's "Selected Portion".
    /// If the user presses Esc to cancel, `captureInteractive()` returns nil and
    /// we silently return to the composer with no error dialog.
    private func startScreenshotFlow() {
        guard !screenshotBusy else { return }
        screenshotBusy = true
        Task {
            do {
                guard let path = try await ScreenshotService.captureInteractive() else {
                    // User pressed Esc — no file was created, nothing to do.
                    screenshotBusy = false
                    return
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

        if let target = editTarget {
            // Leave the composer in edit mode if the message turned out not to
            // be editable, rather than silently discarding what was typed.
            guard model.editMessage(target, newText: trimmed, peerIP: peerIP) else {
                NetLogger.warn("Edit", "message no longer editable — keeping composer in edit mode")
                return
            }
            editTarget = nil        // onChange restores draftBeforeEdit
            model.sendTyping(false, toPeerIP: peerIP)
            return
        }

        model.sendMessage(trimmed, toPeerIP: peerIP, replyTo: replyTarget)
        draft = ""
        model.drafts[peerIP] = nil
        replyTarget = nil
        model.sendTyping(false, toPeerIP: peerIP)
    }

    /// Escape backs out of edit or reply mode. Editing shows an already-sent
    /// message in the composer, so there has to be a way out that doesn't send.
    private func cancelComposerMode() {
        if editTarget != nil {
            withAnimation { editTarget = nil }   // onChange restores draftBeforeEdit
        } else if replyTarget != nil {
            withAnimation { replyTarget = nil }
        }
    }

    /// ⌘V in the composer carried files or a bitmap rather than text.
    /// Routed through `sendFile` exactly like a drop or the file picker.
    private func sendPastedAttachments(_ paths: [String]) {
        guard !paths.isEmpty else { return }
        NetLogger.ui(event: "attachment_pasted_send", peer: peerIP, detail: "\(paths.count) file(s)")
        for path in paths {
            model.sendFile(path: path, toPeerIP: peerIP)
        }
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
    @Binding var contentHeight: CGFloat
    var onSubmit: () -> Void
    /// Called on Escape — backs out of edit/reply mode.
    var onCancel: () -> Void = { }
    /// Called when a paste carried attachments instead of text. See
    /// `AttachmentPasteboard.decide` for the precedence rules.
    var onPasteAttachments: ([String]) -> Void = { _ in }

    func makeNSView(context: Context) -> NSScrollView {
        // Assembled by hand rather than with NSTextView.scrollableTextView()
        // because the text view has to be our own subclass — intercepting ⌘V
        // needs an override, the delegate protocol has no paste hook.
        //
        // NSTextView.init(frame:) builds and owns its own text storage /
        // layout manager / container. Wiring that stack up manually instead
        // would leave the storage unreferenced — AppKit's ownership there runs
        // storage → layoutManager → container, not the other way round.
        let scrollView = NSScrollView()
        scrollView.borderType = .noBorder
        scrollView.drawsBackground = false
        scrollView.hasVerticalScroller = true
        scrollView.hasHorizontalScroller = false
        scrollView.autohidesScrollers = true
        scrollView.autoresizingMask = [.width, .height]

        let tv = PastingTextView(frame: NSRect(x: 0, y: 0, width: 100, height: 36))
        tv.onPasteAttachments = { paths in onPasteAttachments(paths) }
        tv.textContainer?.widthTracksTextView = true
        tv.textContainer?.containerSize = NSSize(width: 100, height: CGFloat.greatestFiniteMagnitude)
        tv.isRichText = false
        tv.font = .systemFont(ofSize: 14)
        tv.delegate = context.coordinator
        tv.textContainerInset = NSSize(width: 4, height: 6)
        tv.isAutomaticQuoteSubstitutionEnabled = false
        tv.isAutomaticDashSubstitutionEnabled = false
        tv.drawsBackground = false
        tv.isVerticallyResizable = true
        tv.isHorizontallyResizable = false
        tv.autoresizingMask = [NSView.AutoresizingMask.width]
        tv.minSize = NSSize(width: 0, height: 0)
        tv.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        // NSTextView registers for file drags itself and inserts the dropped
        // path as literal text. unregisterDraggedTypes() does NOT hold: AppKit
        // recomputes the text view's drag registration whenever it recomputes
        // editability or moves between windows, silently putting file-URL back.
        // So the text view owns the drop instead of trying to opt out of it —
        // see PastingTextView's dragging overrides.
        tv.registerForDraggedTypes([.fileURL])
        tv.onDropAttachments = { paths in onPasteAttachments(paths) }

        scrollView.documentView = tv
        return scrollView
    }

    func updateNSView(_ scrollView: NSScrollView, context: Context) {
        guard let tv = scrollView.documentView as? PastingTextView else { return }
        tv.onPasteAttachments = { paths in onPasteAttachments(paths) }
        tv.onDropAttachments  = { paths in onPasteAttachments(paths) }
        if tv.string != text {
            tv.string = text
            context.coordinator.invalidateHeight(tv)
        }
    }

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    final class Coordinator: NSObject, NSTextViewDelegate {
        var parent: ComposerTextEditor
        init(_ parent: ComposerTextEditor) { self.parent = parent }

        func textDidChange(_ notification: Notification) {
            guard let tv = notification.object as? NSTextView else { return }
            parent.text = tv.string
            invalidateHeight(tv)
        }

        func invalidateHeight(_ tv: NSTextView) {
            guard let lm = tv.layoutManager, let tc = tv.textContainer else { return }
            lm.ensureLayout(for: tc)
            let used = lm.usedRect(for: tc).height
            let total = used + tv.textContainerInset.height * 2
            DispatchQueue.main.async { [weak self] in
                self?.parent.contentHeight = total
            }
        }

        func textView(_ textView: NSTextView, doCommandBy commandSelector: Selector) -> Bool {
            if commandSelector == #selector(NSResponder.cancelOperation(_:)) {
                parent.onCancel()
                return true
            }
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

/// NSTextView that sends copied files and screenshots instead of pasting their
/// path (or nothing at all) into the draft.
final class PastingTextView: NSTextView {
    var onPasteAttachments: ([String]) -> Void = { _ in }
    /// Files dropped directly onto the text area. Handled here rather than
    /// letting the drop fall through to ChatView: AppKit picks a single drag
    /// destination by hit-testing, and a text view that declines does not
    /// reliably hand the drop to an ancestor.
    var onDropAttachments: ([String]) -> Void = { _ in }

    // MARK: - File drops

    private func droppedPaths(_ sender: NSDraggingInfo) -> [String] {
        AttachmentPasteboard.fileURLs(on: sender.draggingPasteboard).map(\.path)
    }

    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation {
        droppedPaths(sender).isEmpty ? super.draggingEntered(sender) : .copy
    }

    override func draggingUpdated(_ sender: NSDraggingInfo) -> NSDragOperation {
        droppedPaths(sender).isEmpty ? super.draggingUpdated(sender) : .copy
    }

    override func prepareForDragOperation(_ sender: NSDraggingInfo) -> Bool {
        droppedPaths(sender).isEmpty ? super.prepareForDragOperation(sender) : true
    }

    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        let paths = droppedPaths(sender)
        guard !paths.isEmpty else { return super.performDragOperation(sender) }
        onDropAttachments(paths)
        return true
    }

    override func paste(_ sender: Any?) {
        handlePaste { super.paste(sender) }
    }

    // ⌘⇧V and the Edit menu's "Paste and Match Style" land here; an attachment
    // has no style to match, so treat it identically.
    override func pasteAsPlainText(_ sender: Any?) {
        handlePaste { super.pasteAsPlainText(sender) }
    }

    /// `insertText` falls back to AppKit so the user always gets *some* paste
    /// rather than a swallowed keystroke.
    private func handlePaste(fallback: () -> Void) {
        let pasteboard = NSPasteboard.general
        switch AttachmentPasteboard.action(for: pasteboard) {
        case .insertText:
            fallback()

        case .attachFiles:
            let paths = AttachmentPasteboard.fileURLs(on: pasteboard).map(\.path)
            if paths.isEmpty { fallback() } else { onPasteAttachments(paths) }

        case .attachImage:
            // NSPasteboard is main-thread-only, so the bytes come out here —
            // but transcoding a 4K bitmap to PNG is hundreds of milliseconds,
            // and that does not belong on the main thread.
            guard let payload = AttachmentPasteboard.imagePayload(on: pasteboard) else {
                fallback()
                return
            }
            let directory = ConfigStore.shared.config.screenshotDir
            let handler = onPasteAttachments
            DispatchQueue.global(qos: .userInitiated).async {
                guard let path = AttachmentPasteboard.writeImage(data: payload.data,
                                                                 type: payload.type,
                                                                 customDirectory: directory) else { return }
                DispatchQueue.main.async { handler([path]) }
            }
        }
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
