import AppKit
import AVFoundation

// The window a viewer watches in.
//
// It is an `NSPanel` hosting the presenter's `AVSampleBufferDisplayLayer`
// directly, rather than a SwiftUI view wrapping it. That is deliberate: SwiftUI
// has no way to hand a `CALayer` to a view without an `NSViewRepresentable`
// anyway, and putting one in the middle adds a layout pass per frame to the only
// path in the app where per-frame cost matters.
//
// Not `openWindow`, for the reason recorded in CLAUDE.md: the SwiftUI action
// takes only a String id and cannot carry a session, and the app may be running
// with no window scene materialised at all.

@MainActor
final class RemoteViewerWindowController {

    private var panel: NSPanel?
    private var hostView: VideoHostView?
    private var onClose: (() -> Void)?

    init() {}

    var isOpen: Bool { panel != nil }

    /// Opens the window around `layer`.
    ///
    /// `aspect` sizes the window to the picture rather than to a guess, and is
    /// re-applied when the host's resolution changes — a window whose shape does
    /// not match the stream letterboxes forever, and the user has no way to know
    /// whether that is the app or the remote screen.
    /// Where captured input goes. Set before `show` so no event can be produced
    /// before there is somewhere to send it.
    var onInput: (([RemoteInputRecord]) -> Void)?

    /// Whether the viewer is driving the remote machine. False until the host
    /// grants control, so a viewer that is only watching never sends anything.
    var isControlling: Bool = false {
        didSet {
            hostView?.isCapturing = isControlling
            updateControlButton()
        }
    }

    /// Asks the host for the keyboard and mouse. Nil while viewing self.
    var onRequestControl: (() -> Void)?

    private var controlButton: NSButton?

    func show(title: String,
              layer: CALayer,
              aspect: H264VideoDimensions?,
              onClose: @escaping () -> Void) {
        self.onClose = onClose

        let panel = self.panel ?? makePanel()
        self.panel = panel
        panel.title = title

        if hostView == nil {
            let view = VideoHostView()
            view.attach(layer)
            view.onRecords = { [weak self] records in self?.onInput?(records) }
            view.isCapturing = isControlling
            panel.contentView = view
            hostView = view
        }

        // The capture view needs the remote picture's size for its aspect-fit
        // arithmetic — without it every coordinate is normalized against the
        // window including its letterbox bars.
        if let aspect {
            hostView?.remoteSize = CGSize(width: CGFloat(aspect.width),
                                          height: CGFloat(aspect.height))
        }

        if onRequestControl != nil { installControlButton(on: panel) }
        if let aspect { applyAspect(aspect, to: panel) }
        panel.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    /// The picture changed size. Keeps the window's aspect honest without
    /// yanking it out from under the user — position and rough size are
    /// preserved, only the shape is corrected.
    func updateAspect(_ dimensions: H264VideoDimensions) {
        hostView?.remoteSize = CGSize(width: CGFloat(dimensions.width),
                                      height: CGFloat(dimensions.height))
        guard let panel else { return }
        applyAspect(dimensions, to: panel)
    }

    func close() {
        onClose = nil
        controlButton = nil
        onRequestControl = nil
        // Lifts anything still held, so the host does not keep a key down
        // because the window went away mid-chord.
        hostView?.isCapturing = false
        hostView = nil
        panel?.orderOut(nil)
        panel = nil
    }

    /// A titlebar accessory rather than an overlay on the picture.
    ///
    /// Anything drawn over the video is something the user will eventually click
    /// while trying to click the remote machine — and once control is granted
    /// that click goes to the remote machine instead, which is the worst of both.
    private func installControlButton(on panel: NSPanel) {
        guard controlButton == nil else { return }

        let button = NSButton(title: "", target: nil, action: nil)
        button.bezelStyle = .rounded
        button.controlSize = .small
        button.target = self
        button.action = #selector(controlButtonPressed)
        controlButton = button
        updateControlButton()

        let accessory = NSTitlebarAccessoryViewController()
        let container = NSView(frame: NSRect(x: 0, y: 0, width: 150, height: 28))
        button.frame = NSRect(x: 4, y: 2, width: 142, height: 24)
        container.addSubview(button)
        accessory.view = container
        accessory.layoutAttribute = .right
        panel.addTitlebarAccessoryViewController(accessory)
    }

    private func updateControlButton() {
        controlButton?.title = isControlling ? "Controlling" : "Request Control"
        controlButton?.isEnabled = !isControlling
    }

    @objc private func controlButtonPressed() {
        onRequestControl?()
    }

    // MARK: - Private

    private func makePanel() -> NSPanel {
        let panel = ViewerPanel(
            contentRect: NSRect(x: 0, y: 0, width: 960, height: 540),
            styleMask: [.titled, .closable, .resizable, .miniaturizable],
            backing: .buffered,
            defer: false)

        panel.isReleasedWhenClosed = false

        // NSPanel hides itself when its app is deactivated — that is the
        // default, and for a utility palette it is the right one. For a window
        // showing another machine's screen it is not: clicking any other app
        // makes the thing you are watching vanish, and the only way back is to
        // re-activate this app, which is precisely what somebody driving a
        // remote machine is not doing.
        panel.hidesOnDeactivate = false

        panel.backgroundColor = .black
        panel.collectionBehavior = [.fullScreenPrimary]
        panel.onWillClose = { [weak self] in
            // Closing the window ends the session. A viewer window that is gone
            // while the host's screen is still being captured is exactly the
            // state the watchdog exists to catch, and there is no reason to
            // reach it deliberately.
            let close = self?.onClose
            self?.onClose = nil
            self?.panel = nil
            self?.hostView = nil
            close?()
        }
        panel.center()
        return panel
    }

    private func applyAspect(_ dimensions: H264VideoDimensions, to panel: NSPanel) {
        guard dimensions.width > 0, dimensions.height > 0 else { return }
        let ratio = NSSize(width: dimensions.width, height: dimensions.height)
        panel.contentAspectRatio = ratio

        // Fit the current frame to the new ratio without moving the window or
        // growing it off screen.
        let current = panel.contentRect(forFrameRect: panel.frame)
        let scale = min(current.width / ratio.width, current.height / ratio.height)
        let fitted = NSSize(width: (ratio.width * scale).rounded(),
                            height: (ratio.height * scale).rounded())
        if abs(fitted.width - current.width) > 1 || abs(fitted.height - current.height) > 1 {
            panel.setContentSize(fitted)
        }
    }
}

/// A layer-backed view that hosts the display layer and keeps it filling the
/// window. The layer does not resize itself with its superlayer, so without
/// `layout()` the picture stays 960x540 in the corner of a resized window.
private final class VideoHostView: RemoteInputCaptureView {

    private var videoLayer: CALayer?

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        layer?.backgroundColor = NSColor.black.cgColor
    }

    required init?(coder: NSCoder) { fatalError("not used") }

    func attach(_ videoLayer: CALayer) {
        self.videoLayer = videoLayer
        videoLayer.frame = bounds
        if let display = videoLayer as? AVSampleBufferDisplayLayer {
            display.videoGravity = .resizeAspect
        }
        layer?.addSublayer(videoLayer)
    }

    override func layout() {
        super.layout()
        // No implicit animation. The default would make every resize a
        // quarter-second crossfade of live video, which reads as lag.
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        videoLayer?.frame = bounds
        CATransaction.commit()
    }
}

/// Escape closes it, and closing ends the session.
private final class ViewerPanel: NSPanel {
    var onWillClose: (() -> Void)?

    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }

    override func cancelOperation(_ sender: Any?) { close() }

    override func close() {
        let handler = onWillClose
        onWillClose = nil
        super.close()
        handler?()
    }
}
