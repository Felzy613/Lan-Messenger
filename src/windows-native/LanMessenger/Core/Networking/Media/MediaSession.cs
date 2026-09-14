using LanMessenger.Core.Crypto;
using LanMessenger.Core.Services;

namespace LanMessenger.Core.Networking.Media;

// Owns the three execution contexts of a live media session and nothing else of
// substance. Mirror of the macOS MediaSession.swift.
//
// THREADING — this is the part that has already gone wrong twice in the macOS
// half of this codebase, in DiscoveryService, and the rule is not negotiable:
//
//   read task    the blocking ReadFrame() loop. It never returns while running,
//                so it gets a dedicated long-running thread, never a pool thread.
//   writer task  drains the scheduler. Owns the sequence counter, which is why
//                MediaFrameWriter is documented single-caller.
//   timers       keepalive and watchdog, on System.Threading.Timer, which fires
//                on the thread pool and is therefore structurally independent of
//                both loops.
//
// Windows avoids the macOS starvation trap by construction because
// System.Threading.Timer does not run on a caller-chosen serial queue — that is
// exactly why the Windows DiscoveryService never had the bug its macOS
// counterpart shipped twice. RemoteDesktopQueueTests asserts it anyway, so a
// future refactor onto a shared scheduler breaks loudly.
public sealed class MediaSession
{
    public string SessionId { get; }
    public string PeerPublicKeyB64 { get; }
    public string PeerIP { get; }
    public RemoteSessionRole Role { get; }

    private readonly IMediaLink _link;
    private readonly MediaFrameReader _reader;
    private readonly MediaFrameWriter _writer;
    private readonly MediaWriteScheduler _scheduler = new();

    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeSignal = new(0);
    private readonly CancellationTokenSource _cts = new();

    private Thread? _readThread;
    private Task? _writerTask;
    private Timer? _keepaliveTimer;
    private Timer? _watchdogTimer;
    private bool _running;
    private DateTime _lastInboundAt = DateTime.UtcNow;

    // ---- tunables. Settable so tests can shrink them; production defaults are
    // the protocol's. Same idiom as DiscoveryService's intervals, and for the
    // same reason: at the real values these paths are untestable in practice.
    public TimeSpan KeepaliveInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan WatchdogInterval  { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan WatchdogTimeout   { get; set; } = TimeSpan.FromSeconds(30);

    // ---- counted seams, reachable ONLY from the timer callbacks.
    //
    // Deliberately not anything the read loop also touches inline: the macOS
    // discovery regression test documents why that distinction decides whether a
    // test can detect starvation at all. A counter the read path also bumps keeps
    // ticking against broken code and the test passes while the bug is live.
    public Action? OnKeepaliveTick { get; set; }
    public Action? OnWatchdogTick { get; set; }

    /// <summary>Delivered from the read thread. Callers that touch UI must marshal themselves.</summary>
    public Action<MediaInboundFrame>? OnFrame { get; set; }
    /// <summary>Delivered once, from whichever context detected the end.</summary>
    public Action<MediaFaultKind?>? OnClosed { get; set; }

    public MediaSession(
        string sessionId, string peerPublicKeyB64, string peerIP,
        RemoteSessionRole role, RemoteSessionKeys keys, IMediaLink link)
    {
        SessionId = sessionId;
        PeerPublicKeyB64 = peerPublicKeyB64;
        PeerIP = peerIP;
        Role = role;
        _link = link;
        _reader = new MediaFrameReader(link, keys.OpeningKey(role), keys.OpeningSalt(role));
        _writer = new MediaFrameWriter(link, keys.SealingKey(role), keys.SealingSalt(role));
    }

    public bool InputArmed
    {
        get => _reader.InputArmed;
        set => _reader.InputArmed = value;
    }

    public (ulong FramesRead, ulong BytesRead, ulong FramesWritten, ulong BytesWritten,
            int DroppedVideo, int DroppedKeyframes) Statistics =>
        (_reader.FramesRead, _reader.BytesRead, _writer.FramesWritten, _writer.BytesWritten,
         _scheduler.DroppedVideoFrames, _scheduler.DroppedKeyframes);

    private bool IsRunning { get { lock (_gate) return _running; } }

    // ---- Lifecycle ---------------------------------------------------------

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
        }

        LanLogger.Remote("session_start", peer: PeerIP, sessionId: SessionId, role: Role.ToString());

        _keepaliveTimer = new Timer(_ => KeepaliveTick(), null, KeepaliveInterval, KeepaliveInterval);
        _watchdogTimer  = new Timer(_ => WatchdogTick(), null, WatchdogInterval, WatchdogInterval);
        _writerTask = Task.Run(WriterLoopAsync);

        // A dedicated thread, not a pool thread: the loop blocks indefinitely and
        // would otherwise occupy a pool slot for the life of the session.
        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = $"media-read-{SessionId[..8]}" };
        _readThread.Start();
    }

    public void Stop(MediaFaultKind? reason = null, string reasonText = "")
    {
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
        }

        _keepaliveTimer?.Dispose(); _keepaliveTimer = null;
        _watchdogTimer?.Dispose();  _watchdogTimer = null;
        try { _cts.Cancel(); } catch { }
        _writeSignal.Release();
        _link.ShutdownAndClose();
        _scheduler.Reset();

        var stats = Statistics;
        LanLogger.Remote(
            reason is null ? "session_end" : "session_fault",
            peer: PeerIP, sessionId: SessionId, role: Role.ToString(),
            bytes: (int)Math.Min(int.MaxValue, stats.BytesRead + stats.BytesWritten),
            reason: string.IsNullOrEmpty(reasonText) ? null : reasonText);
        OnClosed?.Invoke(reason);
    }

    // ---- Read loop (read thread only) --------------------------------------

    private void ReadLoop()
    {
        while (IsRunning)
        {
            var outcome = _reader.ReadFrame();
            switch (outcome.Kind)
            {
                case MediaReadKind.Frame:
                    NoteInbound();
                    OnFrame?.Invoke(outcome.Frame);
                    break;
                case MediaReadKind.Partial:
                    NoteInbound();
                    break;
                case MediaReadKind.Dropped:
                    NoteInbound();
                    LanLogger.Remote("frame_dropped", peer: PeerIP, sessionId: SessionId,
                                     channel: outcome.DroppedChannel, reason: outcome.Reason);
                    break;
                case MediaReadKind.Closed:
                    Stop();
                    return;
                case MediaReadKind.Fault:
                    LanLogger.Remote(FaultEvent(outcome.Fault), peer: PeerIP, sessionId: SessionId,
                                     reason: outcome.Reason);
                    Stop(outcome.Fault, outcome.Reason);
                    return;
            }
        }
    }

    private static string FaultEvent(MediaFaultKind kind) => kind switch
    {
        MediaFaultKind.SequenceNotIncreasing => "sequence_violation",
        MediaFaultKind.DecryptFailed         => "handshake_failed",
        _                                    => "error",
    };

    private void NoteInbound() { lock (_gate) _lastInboundAt = DateTime.UtcNow; }

    // ---- Write path --------------------------------------------------------

    /// <summary>Thread-safe. Never discard the verdict — an overflow means the socket is wedged.</summary>
    public MediaSubmission Submit(MediaOutboundFrame frame)
    {
        var verdict = _scheduler.Submit(frame);
        if (verdict.Kind == MediaSubmissionKind.Overflow)
        {
            LanLogger.Remote("error", peer: PeerIP, sessionId: SessionId,
                             channel: (int)verdict.Channel, reason: "send queue overflow");
            Stop(MediaFaultKind.LinkFailed, "send queue overflow");
            return verdict;
        }
        _writeSignal.Release();
        return verdict;
    }

    private async Task WriterLoopAsync()
    {
        while (IsRunning)
        {
            try { await _writeSignal.WaitAsync(_cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            while (IsRunning && _scheduler.NextSegment() is { } segment)
            {
                if (!_writer.WriteSegment(segment))
                {
                    LanLogger.Remote("error", peer: PeerIP, sessionId: SessionId,
                                     reason: "media write failed");
                    Stop(MediaFaultKind.LinkFailed, "media write failed");
                    return;
                }
            }
        }
    }

    // ---- Timers (thread pool) ----------------------------------------------

    private void KeepaliveTick()
    {
        if (!IsRunning) return;
        Submit(new MediaOutboundFrame(MediaChannel.Control, [], 0));
        OnKeepaliveTick?.Invoke();
    }

    private void WatchdogTick()
    {
        if (!IsRunning) return;
        OnWatchdogTick?.Invoke();
        TimeSpan idle;
        lock (_gate) idle = DateTime.UtcNow - _lastInboundAt;
        // A crashed viewer must never leave a host's screen being captured
        // indefinitely. That is the single worst failure this feature can have,
        // so the watchdog is not optional.
        if (idle > WatchdogTimeout)
        {
            LanLogger.Remote("watchdog_fired", peer: PeerIP, sessionId: SessionId,
                             reason: $"no inbound frames for {(int)idle.TotalSeconds}s");
            Stop(MediaFaultKind.LinkFailed, "watchdog timeout");
        }
    }
}
