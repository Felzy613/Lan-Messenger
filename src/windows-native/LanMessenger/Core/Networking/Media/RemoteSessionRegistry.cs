using LanMessenger.Core.Crypto;

namespace LanMessenger.Core.Networking.Media;

// The ~10 s accept window, one-in-flight-session-per-peer, and single-shot
// session_id lookup. Mirror of the macOS RemoteSessionRegistry.swift.
//
// The clock is injected, so the entire window-expiry behaviour is testable in
// zero wall-clock time. Without that seam the only way to test a 10 s window is
// to wait 10 s, which means in practice it never gets tested.

/// <summary>An invite that has been accepted and is waiting for its media connection.</summary>
public sealed class RemoteAcceptWindow(
    string sessionId, string peerPublicKeyB64, string peerIP,
    RemoteSessionRole role, RemoteSessionKeys keys, DateTime openedAt)
{
    public string SessionId { get; } = sessionId;
    public string PeerPublicKeyB64 { get; } = peerPublicKeyB64;
    public string PeerIP { get; } = peerIP;
    public RemoteSessionRole Role { get; } = role;
    public RemoteSessionKeys Keys { get; } = keys;
    public DateTime OpenedAt { get; } = openedAt;
    public bool Attached { get; internal set; }
}

public enum RemoteOpenKind { Opened, Busy }
public enum RemoteAdmitKind { Admitted, Unknown, Expired, AlreadyAttached }

public readonly record struct RemoteOpenResult(RemoteOpenKind Kind, string ExistingSessionId = "");
public readonly record struct RemoteAdmitResult(RemoteAdmitKind Kind, RemoteAcceptWindow? Window = null);

public sealed class RemoteSessionRegistry(Func<DateTime>? now = null, TimeSpan? acceptWindow = null)
{
    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);
    private readonly TimeSpan _acceptWindow = acceptWindow ?? TimeSpan.FromSeconds(10);
    private readonly object _gate = new();

    private readonly Dictionary<string, RemoteAcceptWindow> _windows = [];   // by sessionId
    private readonly Dictionary<string, string> _inFlightByPeer = [];        // peerKey -> sessionId
    private readonly Dictionary<string, MediaSession> _liveSessions = [];    // sessionId -> session

    // ---- Accept windows ----------------------------------------------------

    /// <summary>Opens a window after an invite is accepted.</summary>
    /// <remarks>
    /// One session in flight per peer: a peer that can open windows without bound
    /// can make a host hold derived key material for every one of them.
    /// </remarks>
    public RemoteOpenResult Open(
        string sessionId, string peerPublicKeyB64, string peerIP,
        RemoteSessionRole role, RemoteSessionKeys keys)
    {
        lock (_gate)
        {
            PruneLocked();
            if (_inFlightByPeer.TryGetValue(peerPublicKeyB64, out var existing) && existing != sessionId)
                return new RemoteOpenResult(RemoteOpenKind.Busy, existing);

            _windows[sessionId] = new RemoteAcceptWindow(
                sessionId, peerPublicKeyB64, peerIP, role, keys, _now());
            _inFlightByPeer[peerPublicKeyB64] = sessionId;
            return new RemoteOpenResult(RemoteOpenKind.Opened);
        }
    }

    /// <summary>Consumes a window for an inbound media_attach.</summary>
    /// <remarks>
    /// The peer key is checked as well as the id: a session id travels in
    /// plaintext on the wire, so any LAN host that sees one could otherwise
    /// attach to a window it did not open.
    /// </remarks>
    public RemoteAdmitResult Admit(string sessionId, string peerPublicKeyB64)
    {
        lock (_gate)
        {
            PruneLocked();
            if (!_windows.TryGetValue(sessionId, out var window)
                || window.PeerPublicKeyB64 != peerPublicKeyB64)
                return new RemoteAdmitResult(RemoteAdmitKind.Unknown);
            if (window.Attached)
                return new RemoteAdmitResult(RemoteAdmitKind.AlreadyAttached);
            if (_now() - window.OpenedAt > _acceptWindow)
            {
                _windows.Remove(sessionId);
                _inFlightByPeer.Remove(window.PeerPublicKeyB64);
                return new RemoteAdmitResult(RemoteAdmitKind.Expired);
            }
            window.Attached = true;
            return new RemoteAdmitResult(RemoteAdmitKind.Admitted, window);
        }
    }

    public void Cancel(string sessionId)
    {
        lock (_gate)
        {
            if (_windows.Remove(sessionId, out var window))
                _inFlightByPeer.Remove(window.PeerPublicKeyB64);
        }
    }

    public bool HasWindow(string sessionId) { lock (_gate) return _windows.ContainsKey(sessionId); }

    public string? InFlightSessionId(string peerPublicKeyB64)
    {
        lock (_gate)
        {
            PruneLocked();
            return _inFlightByPeer.TryGetValue(peerPublicKeyB64, out var id) ? id : null;
        }
    }

    /// <summary>
    /// Drops windows that were never used. Called on every registry operation
    /// rather than from a timer — an expired window costs nothing to hold for a
    /// few extra seconds, and a timer here would be one more thing to starve.
    /// </summary>
    private void PruneLocked()
    {
        DateTime cutoff = _now() - _acceptWindow;
        foreach (var id in _windows.Where(kv => !kv.Value.Attached && kv.Value.OpenedAt < cutoff)
                                   .Select(kv => kv.Key).ToList())
        {
            if (_windows.Remove(id, out var window))
                _inFlightByPeer.Remove(window.PeerPublicKeyB64);
        }
    }

    // ---- Live sessions -----------------------------------------------------

    public void Register(MediaSession session) { lock (_gate) _liveSessions[session.SessionId] = session; }

    public void Remove(string sessionId)
    {
        lock (_gate)
        {
            if (_windows.Remove(sessionId, out var window))
                _inFlightByPeer.Remove(window.PeerPublicKeyB64);
            _liveSessions.Remove(sessionId);
        }
    }

    public MediaSession? Session(string sessionId)
    {
        lock (_gate) return _liveSessions.TryGetValue(sessionId, out var s) ? s : null;
    }

    public int LiveSessionCount { get { lock (_gate) return _liveSessions.Count; } }

    /// <summary>
    /// Closes everything. Reached from NetworkCoordinator.Stop() so a detached
    /// socket can never outlive the network stack that produced it.
    /// </summary>
    public void CloseAll()
    {
        List<MediaSession> sessions;
        lock (_gate)
        {
            sessions = [.. _liveSessions.Values];
            _liveSessions.Clear();
            _windows.Clear();
            _inFlightByPeer.Clear();
        }
        foreach (var session in sessions) session.Stop();
    }
}
