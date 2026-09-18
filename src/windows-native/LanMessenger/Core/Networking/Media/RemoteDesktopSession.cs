using LanMessenger.Core.Services;
using System.Diagnostics;

namespace LanMessenger.Core.Networking.Media;

// The Windows mirror of RemoteDesktopSession.swift: the object that ties
// everything together.
//
//   host    duplicate → convert → encode → (wire)
//   viewer  (wire) → decode → present
//
// The single most important thing it owns is the **teardown ordering**, and it
// owns it in one place. Capture is released first and always, before anything
// that could throw or block. Spread across event handlers that order would be
// subtly different every time, and the version that gets it wrong leaves a
// screen being read after somebody pressed Stop.
//
// The capture loop is its own thread rather than a timer. Desktop Duplication's
// AcquireNextFrame blocks until something changes, so a timer would either poll
// pointlessly or sit inside a blocking call it was supposed to schedule around —
// the same family of mistake as putting a media session's timers on its read
// loop.

public sealed class RemoteDesktopSession : IDisposable
{
    public enum Mode { SelfView, Host, Viewer }

    private readonly Action<RemoteAuditRecord> _appendAudit;
    private readonly object _gate = new();

    private DesktopDuplicator? _capture;
    private H264Encoder? _encoder;
    private H264Decoder? _decoder;
    private IVideoPresenter? _presenter;
    private Thread? _captureThread;
    private volatile bool _capturing;

    private RemoteGrantState _grant = new();
    private DateTime? _startedAt;
    private string _peerName = "";
    private bool _lastSecureDesktop;

    public Mode? CurrentMode { get; private set; }
    public bool IsRunning => CurrentMode is not null;
    public RemoteGrant Grant => _grant.Grant;
    public int Width => _capture?.Width ?? _decoder?.Width ?? 0;
    public int Height => _capture?.Height ?? _decoder?.Height ?? 0;

    /// Frames ready for the wire. Null in self-view, where they short-circuit
    /// straight into the decoder.
    public Action<EncodedVideoFrame>? OnEncodedFrame { get; set; }
    /// Control messages this session wants sent — keyframe requests,
    /// video_config, host_state.
    public Action<MediaControlMessage>? OnControlMessage { get; set; }
    public Action<RemoteStopReason>? OnEnded { get; set; }
    public Action? OnChanged { get; set; }

    public RemoteDesktopSession(Action<RemoteAuditRecord>? appendAudit = null)
    {
        _appendAudit = appendAudit ?? (_ => { });
    }

    // ---- Start -------------------------------------------------------------

    /// Captures this screen and shows it back locally. No peer, no socket —
    /// the diagnostic path, and the fastest way to prove the whole chain works
    /// on one machine.
    public void StartSelfView(IVideoPresenter presenter, string? display = null)
        => Start(Mode.SelfView, "This PC", presenter, display);

    /// Shares this screen with a peer.
    public void StartHosting(string peerName, string? display = null)
        => Start(Mode.Host, peerName, presenter: null, display);

    /// Watches a peer's screen.
    public void StartViewing(string peerName, IVideoPresenter presenter)
        => Start(Mode.Viewer, peerName, presenter, display: null);

    private void Start(Mode mode, string peerName, IVideoPresenter? presenter, string? display)
    {
        lock (_gate)
        {
            if (IsRunning) throw new InvalidOperationException("a session is already running");

            _peerName = peerName;
            CurrentMode = mode;
            _startedAt = DateTime.UtcNow;

            // Presentation first. A viewer that starts capturing before it has
            // anywhere to put frames spends its first second discarding them.
            if (mode is Mode.SelfView or Mode.Viewer)
            {
                _presenter = presenter
                    ?? throw new ArgumentNullException(nameof(presenter),
                            "a presenting session needs somewhere to present");

                _decoder = new H264Decoder
                {
                    OnKeyframeNeeded = reason => RequestKeyframe(reason),
                    OnDimensionsChanged = (w, h) => OnChanged?.Invoke(),
                };
                _presenter.OnKeyframeNeeded = reason => RequestKeyframe(reason);
            }

            if (mode is Mode.SelfView or Mode.Host)
            {
                _capture = new DesktopDuplicator(display);
                _encoder = new H264Encoder(_capture.Width, _capture.Height);
                _encoder.OnEncodedFrame = HandleEncodedFrame;

                // An encoder that has stopped producing frames is the end of
                // the session, not a log line. Without this the viewer sits on
                // "waiting for first frame" with nothing on screen to say why.
                //
                // Off the calling thread on purpose: this fires from the
                // encoder's own event pump, and Stop disposes the encoder,
                // which joins that pump. Tearing down from inside it would be
                // a thread joining itself.
                _encoder.OnFatalError = detail => Task.Run(() =>
                {
                    LanLogger.Remote("session_fault", reason: $"encoder: {detail}");
                    Stop(RemoteStopReason.Error);
                });

                // A viewer has no other source for the picture size, and needs
                // it before the first frame.
                OnControlMessage?.Invoke(MediaControlMessage.Video(new VideoConfig
                {
                    Width = _capture.Width, Height = _capture.Height,
                    Scale = 1, DisplayId = 0, Codec = "h264",
                }));

                _capturing = true;
                _captureThread = new Thread(CaptureLoop)
                {
                    IsBackground = true,
                    Name = "remote-desktop-capture",
                };
                _captureThread.Start();
            }

            _grant.Accept();
            _appendAudit(new RemoteAuditRecord(RemoteAuditEvent.SessionStarted, _peerName));
            LanLogger.Remote("session_started", reason: $"{mode} {Width}x{Height}");
        }
        OnChanged?.Invoke();
    }

    /// Its own thread, because AcquireNextFrame blocks until the screen changes.
    /// Frames per second the capture loop will encode at most.
    ///
    /// The encoder is configured for this rate, so exceeding it is not a bonus:
    /// it is CPU spent producing frames the timeline has no room for.
    private const int TargetFrameRate = 30;

    private void CaptureLoop()
    {
        long attempts = 0, delivered = 0;
        var clock = Stopwatch.StartNew();
        long nextSlotMs = 0;
        long intervalMs = 1000 / TargetFrameRate;

        while (_capturing)
        {
            // Pace *before* asking for a frame, not after taking one.
            //
            // Desktop Duplication always hands back the newest complete desktop
            // image, so waiting costs nothing but staleness bounded by one
            // interval — while TryCapture does a full-screen colour conversion,
            // which is the most expensive thing in the loop. Skipping the frame
            // after that work is done saves nothing at all.
            //
            // Without this the loop ran as fast as the GPU allowed. Self-view
            // measured 4,252 frames in 98 seconds — about 42fps into an encoder
            // told to expect 30, with every capture attempt delivering, because
            // presenting each frame changed the screen and so produced the next
            // one. The pacing is what stops that becoming a treadmill.
            long now = clock.ElapsedMilliseconds;
            if (now < nextSlotMs)
            {
                Thread.Sleep((int)Math.Min(intervalMs, nextSlotMs - now));
                continue;
            }
            nextSlotMs = now + intervalMs;

            try
            {
                var capture = _capture;
                var encoder = _encoder;
                if (capture is null || encoder is null) return;

                // Null means nothing changed. That is the ordinary case on a
                // still screen and is not a failure of any kind.
                var frame = capture.TryCapture(timeoutMs: 100);
                attempts++;
                if (frame is not null) delivered++;

                // The capture side of the same question the encoder stats
                // answer. A screen with nothing moving on it legitimately
                // delivers nothing at all, so "no frames" is only meaningful
                // next to the number of times we asked.
                if (attempts % 150 == 0)
                {
                    LanLogger.Remote("capture_stats",
                        reason: $"attempts={attempts} delivered={delivered}");
                }

                // The secure desktop coming and going is the one capture state a
                // viewer needs told about, or they see a frozen picture and no
                // reason for it.
                if (capture.SecureDesktopActive != _lastSecureDesktop)
                {
                    _lastSecureDesktop = capture.SecureDesktopActive;
                    OnControlMessage?.Invoke(MediaControlMessage.Host(
                        new RemoteHostState { SecureDesktop = _lastSecureDesktop }));
                }

                if (frame is null) continue;
                encoder.Encode(frame.Nv12, frame.Stride, frame.CaptureUs);
            }
            catch (Exception ex)
            {
                LanLogger.Remote("error", reason: $"capture loop: {ex.Message}");
                Stop(RemoteStopReason.Error);
                return;
            }
        }
    }

    private void HandleEncodedFrame(EncodedVideoFrame frame)
    {
        if (CurrentMode == Mode.SelfView)
        {
            // Short-circuits the socket. The transport is already proven to
            // carry these frames; what self-view exercises is everything else.
            DecodeAndPresent(frame.AnnexB, frame.CaptureUs);
            return;
        }
        OnEncodedFrame?.Invoke(frame);
    }

    /// A video frame arriving from the wire.
    public void AcceptVideo(ReadOnlySpan<byte> annexB, ulong captureUs)
        => DecodeAndPresent(annexB, captureUs);

    private void DecodeAndPresent(ReadOnlySpan<byte> annexB, ulong captureUs)
    {
        var decoder = _decoder;
        var presenter = _presenter;
        if (decoder is null || presenter is null) return;

        try
        {
            decoder.Decode(annexB, captureUs, presenter.Present);
        }
        catch (Exception ex)
        {
            LanLogger.Remote("error", reason: $"decode failed: {ex.Message}");
            decoder.Reset("decode_failed");
        }
    }

    // ---- Grant -------------------------------------------------------------

    /// The second consent prompt's result. Viewing is already granted; this arms
    /// the input channel, and nothing else may.
    public bool GrantControl()
    {
        if (!_grant.GrantControl()) return false;
        _appendAudit(new RemoteAuditRecord(RemoteAuditEvent.ControlGranted, _peerName));
        OnControlMessage?.Invoke(MediaControlMessage.ControlGrant());
        OnChanged?.Invoke();
        return true;
    }

    public bool RevokeControl()
    {
        if (!_grant.RevokeControl()) return false;
        _appendAudit(new RemoteAuditRecord(RemoteAuditEvent.ControlRevoked, _peerName));
        OnControlMessage?.Invoke(MediaControlMessage.ControlRevoke());
        OnChanged?.Invoke();
        return true;
    }

    private void RequestKeyframe(string reason)
    {
        // A host asks its own encoder; a viewer asks the peer.
        if (_encoder is not null) _encoder.LatchKeyframe();
        else OnControlMessage?.Invoke(MediaControlMessage.KeyframeRequest(reason));
    }

    // ---- Stop --------------------------------------------------------------

    /// The single teardown path. Everything that can end a session arrives here,
    /// so the order is the same every time and nothing is left running because
    /// one caller forgot a step.
    public void Stop(RemoteStopReason reason)
    {
        Mode? mode;
        double duration;

        lock (_gate)
        {
            if (CurrentMode is null) return;
            mode = CurrentMode;
            duration = _startedAt is { } start ? (DateTime.UtcNow - start).TotalSeconds : 0;

            // Capture first, always. A host whose screen is still being read
            // after they pressed Stop is the worst possible ordering bug, so it
            // goes before anything that could throw or block.
            _capturing = false;
            _capture?.Dispose(); _capture = null;
            _encoder?.Dispose(); _encoder = null;
            _decoder?.Dispose(); _decoder = null;

            _presenter?.Clear();
            _presenter = null;

            _grant.End();
            CurrentMode = null;
            _startedAt = null;
        }

        // Outside the lock: the thread may be inside a blocking AcquireNextFrame
        // and joining while holding the gate would deadlock any concurrent Stop.
        var thread = _captureThread;
        _captureThread = null;
        if (thread is not null && thread.IsAlive && thread != Thread.CurrentThread)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }

        _appendAudit(new RemoteAuditRecord(RemoteAuditEvent.SessionEnded, _peerName,
                                           reason.ToToken(), duration));
        LanLogger.Remote("session_stopped",
            reason: $"{reason.ToToken()} after {(int)duration}s ({mode})");

        OnEnded?.Invoke(reason);
        OnChanged?.Invoke();
    }

    public void Dispose() => Stop(RemoteStopReason.AppQuit);
}
