using LanMessenger.Core.Services;
using System.Collections.Generic;
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

    /// <summary>
    /// How far the peer's media clock is from ours, measured rather than
    /// assumed. Until this is synced a viewer's latency figure is out by the gap
    /// between two boot times — 32.5 days, on the first real session.
    /// </summary>
    public RemoteClockSync ClockSync { get; private set; } = new();

    /// <summary>Fires when the estimate improves, so a presenter can start
    /// reporting a latency that means something.</summary>
    public Action<RemoteClockSync>? OnClockSynced { get; set; }

    /// Pings sent and not yet answered, by id. Bounded: a peer that never
    /// answers must not grow this forever.
    private readonly Dictionary<ulong, ulong> _outstandingPings = new();
    private ulong _nextPingId = 1;
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

            // Same reasoning as the grant ladder: the next session may be
            // against a different machine, and a carried-over offset would
            // subtract one peer's boot time from another peer's timestamps —
            // worse than no estimate, because it produces a plausible number.
            ClockSync = new RemoteClockSync();
            _outstandingPings.Clear();

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
                // Host only. Built here rather than at grant time so the
                // surface it resolves coordinates against is the one actually
                // being captured.
                if (mode == Mode.Host)
                {
                    // The display's real position, not an assumed (0,0): on a
                    // multi-monitor host the shared display may start anywhere,
                    // including at a negative coordinate.
                    int width = _capture.Width, height = _capture.Height;
                    int originX = _capture.OriginX, originY = _capture.OriginY;
                    _injector = new RemoteInputInjector(
                        () => _grant.Grant, () => (originX, originY, width, height));
                }

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
            _appendAudit(new RemoteAuditRecord(RemoteAuditEvent.SessionStarted, _peerName,
                                               viewing: mode == Mode.Viewer));
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
        long lastStatsAttempts = 0, captureUsTotal = 0, encodeUsTotal = 0;
        var clock = Stopwatch.StartNew();
        long nextSlotMs = 0;
        long intervalMs = 1000 / TargetFrameRate;

        while (_capturing)
        {
            // Pace by declining to convert, never by sleeping.
            //
            // The first version slept until the next slot. On paper that is a
            // 33ms lap; in practice Thread.Sleep rounds up to the system timer
            // granularity, which is 15.6ms by default, so a requested 21ms
            // became about 31ms and the loop settled at 21fps with only 12ms of
            // real work in it. The measurement said so plainly: capture 6-8ms,
            // encode 1-4ms, lap 47ms.
            //
            // AcquireNextFrame is already the right wait. It blocks until the
            // desktop actually changes, costs nothing while it waits, and has
            // no rounding. So the loop now always acquires — duplication has to
            // be drained anyway, since an unreleased frame blocks the next
            // acquire — and only pays for the colour conversion when a slot is
            // due. Frames arrive as fresh as the compositor can make them.
            long now = clock.ElapsedMilliseconds;
            bool due = now >= nextSlotMs;
            if (due) nextSlotMs = now + intervalMs;

            try
            {
                var capture = _capture;
                var encoder = _encoder;
                if (capture is null || encoder is null) return;

                // Null means nothing changed. That is the ordinary case on a
                // still screen and is not a failure of any kind.
                long t0 = Stopwatch.GetTimestamp();
                var frame = capture.TryCapture(timeoutMs: 100, convert: due);
                long t1 = Stopwatch.GetTimestamp();
                captureUsTotal += (t1 - t0) / (Stopwatch.Frequency / 1_000_000L);
                attempts++;
                if (frame is not null) delivered++;

                // The capture side of the same question the encoder stats
                // answer. A screen with nothing moving on it legitimately
                // delivers nothing at all, so "no frames" is only meaningful
                // next to the number of times we asked.
                if (attempts % 150 == 0)
                {
                    long laps = Math.Max(1, attempts - lastStatsAttempts);
                    LanLogger.Remote("capture_stats",
                        reason: $"attempts={attempts} delivered={delivered} "
                              + $"accum={capture.AccumulatedFrames} "
                              + $"age_ms={capture.FrameAgeUs / 1000} "
                              + $"convert_us={capture.ConvertUs} "
                              + $"capture_ms_avg={captureUsTotal / laps / 1000} "
                              + $"encode_ms_avg={encodeUsTotal / laps / 1000}");
                    lastStatsAttempts = attempts;
                    captureUsTotal = 0;
                    encodeUsTotal = 0;
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
                encodeUsTotal += (Stopwatch.GetTimestamp() - t1) / (Stopwatch.Frequency / 1_000_000L);
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
    /// <summary>Host side only. A viewer has none, which is the first of the two
    /// gates on injection — the second is the grant check inside the injector.</summary>
    private RemoteInputInjector? _injector;

    /// <summary>Raised when the peer asks for control. The second consent prompt
    /// is the interface's business, so this only reports the request.</summary>
    public Action? OnControlRequested { get; set; }

    /// <summary>An input burst arrived from the viewer.</summary>
    /// <remarks>
    /// Gated twice over: a viewer has no injector at all, and the injector
    /// re-checks the grant at every call rather than trusting this one.
    /// </remarks>
    public void AcceptInput(ReadOnlySpan<byte> payload)
    {
        if (CurrentMode != Mode.Host || _injector is null) return;

        var records = RemoteInputCodec.Decode(payload);
        if (records is null)
        {
            LanLogger.Remote("error", reason: "malformed input payload");
            return;
        }
        _injector.Inject(records);
    }

    /// <summary>A control message arrived from the peer.</summary>
    public void AcceptControl(MediaControlMessage message)
    {
        switch (message.Type)
        {
            case "control_request":
                // The second consent. Never granted here — this only asks.
                if (CurrentMode != Mode.Host) return;
                LanLogger.Remote("control_requested", reason: _peerName);
                OnControlRequested?.Invoke();
                break;

            case "control_grant":
                // We are the viewer and the host said yes.
                if (CurrentMode != Mode.Viewer) return;
                _grant.GrantControl();
                LanLogger.Remote("control_granted", reason: "by the host");
                OnChanged?.Invoke();
                break;

            case "control_revoke":
                if (CurrentMode != Mode.Viewer) return;
                _grant.RevokeControl();
                LanLogger.Remote("control_revoked", reason: "by the host");
                OnChanged?.Invoke();
                break;

            case "ping":
                // Answered with OUR clock, not by echoing theirs. The whole
                // point of the exchange is to learn the difference between the
                // two, so a pong that parroted the ping's timestamp would
                // measure nothing.
                OnControlMessage?.Invoke(MediaControlMessage.Pong(message.Id, MediaClock.NowUs()));
                break;

            case "pong":
                AcceptPong(message.Id, message.SentUs);
                break;

            case "keyframe_request":
                _encoder?.LatchKeyframe();
                break;
        }
    }

    private void AcceptPong(ulong id, ulong theirSendUs)
    {
        ulong ourSendUs;
        lock (_gate)
        {
            // An id we never sent, or one already answered. Not fatal, but it
            // would corrupt the estimate, so it is dropped rather than folded in.
            if (!_outstandingPings.Remove(id, out ourSendUs)) return;
        }

        if (!ClockSync.Record(ourSendUs, theirSendUs, MediaClock.NowUs())) return;
        LanLogger.Remote("clock_sync", reason: ClockSync.Summary());
        OnClockSynced?.Invoke(ClockSync);
    }

    /// <summary>
    /// Asks the peer what time it is. Both roles ping: the host wants the
    /// round-trip figure for its own stats.
    /// </summary>
    /// <remarks>
    /// Driven from the media session's keepalive tick, which already runs on the
    /// timer context — the one context guaranteed not to be the read loop. A
    /// ping scheduled onto the read loop would never be dequeued, which is the
    /// failure this project has now had three times.
    /// </remarks>
    public void SendPing()
    {
        ulong id, now;
        lock (_gate)
        {
            if (CurrentMode is null) return;
            // A peer that answers nothing must not grow this without bound.
            // Sixteen is already far more history than the best-sample rule
            // can use.
            if (_outstandingPings.Count >= 16)
            {
                _outstandingPings.Clear();
                LanLogger.Remote("clock_sync", reason: "peer is not answering pings");
            }
            id = _nextPingId++;
            now = MediaClock.NowUs();
            _outstandingPings[id] = now;
        }
        OnControlMessage?.Invoke(MediaControlMessage.Ping(id, now));
    }

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
    /// <summary>The transport's own input gate. Set by the controller.</summary>
    /// <remarks>
    /// MediaFrameReader drops every input frame until this is armed — a
    /// deliberate belt-and-braces check on the receiving side, built in WS3 and
    /// left unconnected until now, so input was refused at the socket with
    /// "input before control_grant" while both apps agreed control was granted.
    /// </remarks>
    public Action<bool>? OnInputArmedChanged { get; set; }

    public bool GrantControl()
    {
        if (!_grant.GrantControl()) return false;
        OnInputArmedChanged?.Invoke(true);
        _appendAudit(new RemoteAuditRecord(RemoteAuditEvent.ControlGranted, _peerName,
                                           viewing: CurrentMode == Mode.Viewer));
        OnControlMessage?.Invoke(MediaControlMessage.ControlGrant());
        OnChanged?.Invoke();
        return true;
    }

    public bool RevokeControl()
    {
        OnInputArmedChanged?.Invoke(false);
        _injector?.ReleaseEverything();
        if (!_grant.RevokeControl()) return false;
        _appendAudit(new RemoteAuditRecord(RemoteAuditEvent.ControlRevoked, _peerName,
                                           viewing: CurrentMode == Mode.Viewer));
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
        DesktopDuplicator? capture;
        H264Encoder? encoder;
        H264Decoder? decoder;

        lock (_gate)
        {
            if (CurrentMode is null) return;
            mode = CurrentMode;
            duration = _startedAt is { } start ? (DateTime.UtcNow - start).TotalSeconds : 0;

            // Before anything else: a session that ends mid-chord must not
            // leave the host holding keys. A machine with Alt stuck behaves as
            // if possessed, and the user's first instinct is to blame their
            // keyboard.
            _injector?.ReleaseEverything();
            _injector = null;
            OnInputArmedChanged?.Invoke(false);

            // Capture stops FIRST — a host whose screen is still being read
            // after they pressed Stop is the worst possible ordering bug — but
            // the things it reads are disposed LAST, after the thread has
            // actually come back. Disposing them here, with the loop possibly
            // mid-frame, is what produced "VideoProcessorBlt failed
            // E_INVALIDARG" followed by a NullReferenceException in the colour
            // converter at the end of every session: the loop was still using
            // what had just been freed.
            _capturing = false;

            capture = _capture; _capture = null;
            encoder = _encoder; _encoder = null;
            decoder = _decoder; _decoder = null;

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

        // The loop has returned; now nothing is reading these.
        capture?.Dispose();
        encoder?.Dispose();
        decoder?.Dispose();

        _appendAudit(new RemoteAuditRecord(RemoteAuditEvent.SessionEnded, _peerName,
                                           reason.ToToken(), duration,
                                           viewing: mode == Mode.Viewer));
        LanLogger.Remote("session_stopped",
            reason: $"{reason.ToToken()} after {(int)duration}s ({mode})");

        OnEnded?.Invoke(reason);
        OnChanged?.Invoke();
    }

    public void Dispose() => Stop(RemoteStopReason.AppQuit);
}
