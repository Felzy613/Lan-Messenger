import Foundation
import AppKit
import CoreMedia
import CoreVideo

// The object the plan forgot.
//
// Twelve workstreams built capture, encode, transport, decode, presentation,
// consent, the host indicator, the kill switch and the audit trail — and nothing
// that connects any of them. Each piece is tested in isolation and none of them
// had a caller, which is why the feature was ninety per cent complete and zero
// per cent usable.
//
// This is that caller. It owns one session's whole life:
//
//   host    capture → encode → Annex-B → (wire)
//   viewer  (wire) → decode → sample buffers → presenter
//
// and everything around the edges: the grant ladder, the guard that stops the
// session when nobody is there to press Stop, the indicator, and the audit
// entries. The ordering of teardown lives here and only here — release capture,
// take the indicator down, disarm the guard, write the audit line — because
// smeared across notification handlers it would be subtly different every time.
//
// **Self-view** is the first mode to work, and it is not a toy. It runs the real
// capture source, the real encoder and the real decoder in one process and puts
// the result in a real window, so the whole pipeline becomes runnable and
// visible on a single machine with no peer. What it deliberately skips is the
// socket, because `VideoPipelineEndToEndTests` already proves two real
// `MediaSession`s carry these exact frames — re-proving that was not what was
// missing.

@MainActor
final class RemoteDesktopSession {

    enum Mode: Equatable {
        /// Host and viewer in one process. The development and diagnostic path.
        case selfView
        /// We are sharing our screen with `peerName`.
        case host(peerName: String, peerIP: String)
        /// We are watching `peerName`'s screen.
        case viewer(peerName: String, peerIP: String)

        var peerName: String {
            switch self {
            case .selfView: return "This Mac"
            case .host(let name, _), .viewer(let name, _): return name
            }
        }

        var capturesLocally: Bool {
            switch self {
            case .selfView, .host: return true
            case .viewer: return false
            }
        }

        var presentsLocally: Bool {
            switch self {
            case .selfView, .viewer: return true
            case .host: return false
            }
        }
    }

    enum StartFailure: Error, CustomStringConvertible {
        case alreadyRunning
        case capture(ScreenCaptureError)
        case encoder(Error)

        var description: String {
            switch self {
            case .alreadyRunning: return "a remote desktop session is already running"
            case .capture(let e):  return e.description
            case .encoder(let e):  return "could not start the encoder: \(e)"
            }
        }
    }

    // MARK: - Observable state

    private(set) var mode: Mode?
    private(set) var grant = RemoteGrantState()
    private(set) var startedAt: Date?
    private(set) var dimensions: H264VideoDimensions?

    var isRunning: Bool { mode != nil }

    /// Fires whenever the session starts, changes grant, or ends, so the UI can
    /// follow without polling.
    var onChange: (() -> Void)?
    /// Fires with the reason a session ended, for anything that wants to say so.
    var onEnded: ((RemoteStopReason) -> Void)?

    // MARK: - Machinery

    private var capture: ScreenCaptureSource?
    private var sendPipeline: VideoSendPipeline?
    private var receivePipeline: VideoReceivePipeline?

    /// The transport, for the two modes that have a peer. Nil for self-view,
    /// which is the whole difference between them: everything else in this
    /// object behaves identically whether the frames cross a socket or not.
    private var media: MediaSession?
    private var presenter: SampleBufferVideoPresenter?
    private let guardian = RemoteSessionGuard()

    /// Where decoded frames go. Set by the owner before starting, so the window
    /// is the caller's business rather than this object's.
    var onSample: ((CMSampleBuffer) -> Void)?
    /// The layer a viewer window should host. Present only while a session that
    /// presents locally is running.
    var videoLayer: CALayer? { presenter?.layer }

    private let appendAudit: (RemoteAuditEntry) -> Void

    /// Tells the peer why this ended, over TCP 54232 rather than the media
    /// channel — so it still works when the media channel is what broke.
    /// Injected, because the session knows nothing about the invite exchange.
    var announceEnd: ((String, String, RemoteStopReason) -> Void)?

    /// - Parameter appendAudit: writes a record into the conversation. Injected
    ///   rather than reached for, so a session can be exercised without a
    ///   history store — and so self-view, which has no conversation, simply
    ///   passes a sink that drops them.
    init(appendAudit: @escaping (RemoteAuditEntry) -> Void = { _ in }) {
        self.appendAudit = appendAudit
    }

    // MARK: - Start

    /// Starts a session that captures this screen and shows it back in a local
    /// window. No peer, no socket.
    func startSelfView(configuration: ScreenCaptureConfiguration = ScreenCaptureConfiguration())
    async throws {
        try await start(mode: .selfView, configuration: configuration)
    }

    /// Shares this screen with a peer. The media channel is already attached and
    /// reading by the time this is called — the invite exchange owns that, and
    /// hands the running session in.
    func startHosting(peerName: String, peerIP: String, media: MediaSession,
                      configuration: ScreenCaptureConfiguration = ScreenCaptureConfiguration())
    async throws {
        self.media = media
        try await start(mode: .host(peerName: peerName, peerIP: peerIP),
                        configuration: configuration)
    }

    /// Watches a peer's screen. Captures nothing.
    func startViewing(peerName: String, peerIP: String, media: MediaSession) async throws {
        self.media = media
        try await start(mode: .viewer(peerName: peerName, peerIP: peerIP),
                        configuration: ScreenCaptureConfiguration())
    }

    private func start(mode: Mode,
                       configuration: ScreenCaptureConfiguration) async throws {
        guard !isRunning else { throw StartFailure.alreadyRunning }

        // Presentation first. A viewer that starts capturing before it has
        // anywhere to put the frames spends the first second discarding them,
        // and on a self-view that is the second where the window should have
        // appeared.
        if mode.presentsLocally {
            let presenter = SampleBufferVideoPresenter()
            presenter.onNeedsKeyframe = { [weak self] reason in
                self?.sendPipeline?.latchKeyframe()
                NetLogger.remote(event: "keyframe_request", reason: reason)
            }
            self.presenter = presenter

            let receive = VideoReceivePipeline()
            receive.onSample = { [weak self] sample in
                // The decode runs on whatever thread delivered the frame; the
                // presenter is main-actor work. This is the only hop in the
                // path, and it is here rather than inside the pipeline so the
                // pipeline stays testable without an actor.
                Task { @MainActor in
                    self?.presenter?.present(sample)
                    self?.onSample?(sample)
                }
            }
            receive.onKeyframeNeeded = { [weak self] _ in
                Task { @MainActor in self?.sendPipeline?.latchKeyframe() }
            }
            receive.onDimensionsChanged = { [weak self] size in
                Task { @MainActor in
                    self?.dimensions = size
                    self?.onChange?()
                }
            }
            self.receivePipeline = receive

            // A viewer's pictures arrive from the socket rather than from a
            // local encoder. Delivered on the media session's read thread, so
            // the hop to the main actor is not optional.
            if let media {
                media.onFrame = { [weak self] frame in
                    Task { @MainActor [weak self] in
                        self?.receivePipeline?.accept(frame)
                    }
                }
            }
        }

        if mode.capturesLocally {
            let source = ScreenCaptureSource(configuration: configuration)
            try await source.start()
            guard let size = source.currentSize else {
                source.stop()
                throw StartFailure.capture(.streamFailed("capture started without a size"))
            }

            let send: VideoSendPipeline
            do {
                send = try VideoSendPipeline(
                    dimensions: size,
                    submit: { [weak self] frame in
                        // A session torn down mid-frame: report the drop
                        // honestly rather than claiming it was queued.
                        guard let self else { return .droppedStaleVideo(wasKeyframe: false) }
                        // Self-view short-circuits the socket. The transport is
                        // already proven to carry these exact frames by
                        // VideoPipelineEndToEndTests; what was never proven is
                        // that a real capture reaches a real window.
                        guard let media = self.media else {
                            self.deliverLocally(frame)
                            return .queued
                        }
                        return media.submit(frame)
                    })
            } catch {
                source.stop()
                throw StartFailure.encoder(error)
            }

            source.onFrame = { pixelBuffer, captureUs in
                send.encode(pixelBuffer: pixelBuffer, captureUs: captureUs)
            }
            source.onRestarted = { _ in send.latchKeyframe() }
            source.onSizeChanged = { [weak self] size in
                Task { @MainActor in self?.handleCaptureResize(size) }
            }
            source.onFatalError = { [weak self] error in
                Task { @MainActor in
                    NetLogger.remote(event: "error", reason: error.description)
                    self?.stop(.error)
                }
            }

            self.capture = source
            self.sendPipeline = send
            self.dimensions = size
        }

        self.mode = mode
        self.startedAt = Date()
        _ = grant.accept()

        armGuard()
        showIndicator()
        appendAudit(RemoteAuditEntry(event: .sessionStarted, peerName: mode.peerName))

        NetLogger.remote(event: "session_started",
                         reason: "\(describe(mode)) \(dimensions.map { "\($0.width)x\($0.height)" } ?? "?")")
        onChange?()
    }

    /// Host → viewer inside one process. Converts to the wire format on the way
    /// through rather than handing the decoder AVCC directly, so self-view
    /// exercises the Annex-B conversion that a real peer depends on instead of
    /// quietly bypassing it.
    private nonisolated func deliverLocally(_ frame: MediaOutboundFrame) {
        Task { @MainActor [weak self] in
            guard let receive = self?.receivePipeline else { return }
            receive.accept(MediaInboundFrame(
                channel: frame.channel,
                flags: frame.keyframe ? .keyframe : [],
                sequence: 0,
                captureUs: frame.captureUs,
                payload: frame.payload,
                fragmentCount: 1))
        }
    }

    // MARK: - Grant

    /// The second consent prompt's result. Viewing is already granted; this arms
    /// the input channel, and nothing else may.
    @discardableResult
    func grantControl() -> Bool {
        guard grant.grantControl() else { return false }
        appendAudit(RemoteAuditEntry(event: .controlGranted,
                                     peerName: mode?.peerName ?? ""))
        showIndicator()
        onChange?()
        return true
    }

    @discardableResult
    func revokeControl() -> Bool {
        guard grant.revokeControl() else { return false }
        appendAudit(RemoteAuditEntry(event: .controlRevoked,
                                     peerName: mode?.peerName ?? ""))
        showIndicator()
        onChange?()
        return true
    }

    // MARK: - Stop

    /// The single teardown path. Everything that can end a session — the Stop
    /// button, the kill shortcut, a system event, a capture failure, the peer —
    /// arrives here, so the order is the same every time and nothing is left
    /// running because one caller forgot a step.
    func stop(_ reason: RemoteStopReason) {
        guard let mode else { return }
        let duration = startedAt.map { Date().timeIntervalSince($0) } ?? 0

        // Capture first, always. A host whose screen is still being read after
        // they pressed Stop is the worst possible ordering bug, so it goes
        // before anything that could throw or block.
        capture?.stop()
        capture = nil
        sendPipeline?.stop()
        sendPipeline = nil

        // Close the channel, do not merely forget it.
        //
        // Dropping the reference left the socket open, so the peer never
        // learned the session was over: their indicator stayed up, their
        // capture kept running, and the registry kept the session id in flight
        // so the next invite was refused as busy. The socket closing is the
        // signal that always arrives — a crashing peer sends nothing else —
        // and remote_end rides alongside it to supply the reason.
        if let media {
            announceEnd?(media.sessionID, media.peerIP, reason)
            media.onFrame = nil
            media.stop()
        }
        media = nil

        receivePipeline = nil
        presenter?.clear()
        presenter = nil

        guardian.disarm()
        RemoteHostIndicatorPresenter.shared.hide()
        RemoteConsentPresenter.shared.dismiss(sessionID: "", reason: .declined)

        grant.end()
        self.mode = nil
        startedAt = nil
        dimensions = nil

        appendAudit(RemoteAuditEntry(event: .sessionEnded,
                                     peerName: mode.peerName,
                                     reason: reason.rawValue,
                                     duration: duration))
        NetLogger.remote(event: "session_stopped",
                         reason: "\(reason.rawValue) after \(Int(duration))s")

        onEnded?(reason)
        onChange?()
    }

    // MARK: - Private

    private func armGuard() {
        guardian.arm { [weak self] reason in
            Task { @MainActor in self?.stop(reason) }
        }
    }

    /// The indicator is only ever shown by a host. A viewer is not sharing
    /// anything, and a strip claiming otherwise on their own screen would be
    /// alarming and wrong.
    private func showIndicator() {
        guard let mode, mode.capturesLocally else { return }
        RemoteHostIndicatorPresenter.shared.show(
            peerName: mode.peerName,
            grant: grant.grant,
            startedAt: startedAt ?? Date(),
            onRevokeControl: { [weak self] in self?.revokeControl() },
            onStop: { [weak self] in self?.stop(.userStopped) })
    }

    /// A display changing size mid-session. The encoder has to be rebuilt for
    /// the new geometry, and a real peer additionally needs a fresh
    /// `video_config` before the next frame — a viewer laying out against the
    /// old size shows a stretched picture and maps every click wrongly.
    private func handleCaptureResize(_ size: H264VideoDimensions) {
        dimensions = size
        do {
            try sendPipeline?.resize(to: size)
            NetLogger.remote(event: "video_config",
                             reason: "\(size.width)x\(size.height)")
        } catch {
            NetLogger.remote(event: "error", reason: "resize failed: \(error)")
            stop(.error)
        }
        onChange?()
    }

    private func describe(_ mode: Mode) -> String {
        switch mode {
        case .selfView:            return "self_view"
        case .host(_, let ip):     return "host peer=\(ip)"
        case .viewer(_, let ip):   return "viewer peer=\(ip)"
        }
    }
}
