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
                model.sendTyping(!newValue.isEmpty, toPeerIP: peerIP)
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
    }

    private func send() {
        let trimmed = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
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
