using LanMessenger.Core.Networking.Media;
using LanMessenger.Core.Crypto;
using System.Net.Sockets;
using LanMessenger.Core.Services;
using Microsoft.UI.Dispatching;
using System;

namespace LanMessenger.UI.RemoteDesktop;

// What the interface talks to. One object owning the session, its windows and
// the guard, so no call site has to know the order those come up and go down in.
//
// The macOS side puts this ordering inside RemoteDesktopSession itself; here the
// session is deliberately free of WinUI, so that the capture, encode and decode
// path stays compilable and testable without a UI thread. The windows are this
// class's business instead.
//
// Everything that touches a Window happens on the dispatcher it was created on.
// A WinUI window created on one thread and closed from another does not fail
// cleanly — it corrupts the XAML island's state and takes the process with it,
// usually somewhere unrelated.

public sealed class RemoteDesktopController
{
    public static RemoteDesktopController Shared { get; } = new();

    private readonly object _gate = new();
    private RemoteDesktopSession? _session;
    private RemoteViewerWindow? _viewer;
    private RemoteHostIndicatorWindow? _indicator;
    private RemoteConsentWindow? _consent;
    private RemoteSessionGuard? _guard;
    /// The transport, for the two modes that have a peer. Null for self-view.
    private MediaSession? _media;
    private DispatcherQueue? _ui;

    private string _peerName = "";
    private DateTime _startedAt;

    /// Where audit records go. Set by AppModel so a session can be exercised
    /// without a history store.
    public Action<RemoteAuditRecord>? AppendAudit { get; set; }

    /// <summary>Tells the peer why a session ended. Set by AppModel.</summary>
    public Action<string, string, RemoteStopReason>? AnnounceEnd { get; set; }
    public Action? OnChanged { get; set; }

    public bool IsRunning => _session?.IsRunning ?? false;
    public string Summary => IsRunning
        ? $"{_peerName} · {_session!.Width}x{_session.Height}"
        : "";

    private RemoteDesktopController() { }

    /// Captures this screen and shows it back in a local window. No peer, no
    /// socket — the fastest way to prove the whole chain works on one machine,
    /// and the only thing that can be tested before the invite exchange exists.
    public void StartSelfView(DispatcherQueue ui)
    {
        lock (_gate)
        {
            if (IsRunning) return;
            _ui = ui;
            _peerName = "This PC";
            _startedAt = DateTime.UtcNow;

            try
            {
                var viewer = new RemoteViewerWindow(_peerName);
                _viewer = viewer;

                var session = new RemoteDesktopSession(record => AppendAudit?.Invoke(record));
                _session = session;

                viewer.OnClosed = () => Stop(RemoteStopReason.UserStopped);
                session.OnEnded = _ => CloseWindows();
                session.OnChanged = () => OnChanged?.Invoke();

                session.StartSelfView(viewer);
                viewer.Activate();

                ShowIndicator();
                ArmGuard();
            }
            catch (Exception ex)
            {
                LanLogger.Remote("error", reason: $"self view failed: {ex.Message}");
                CloseWindows();
                _session?.Dispose();
                _session = null;
                throw;
            }
        }
        OnChanged?.Invoke();
    }

    /// What a host has agreed to share but not yet started sharing.
    ///
    /// The gap between remote_accept and the peer's media_attach is real time —
    /// a network round trip plus a connect — and capture must not begin until
    /// they actually arrive. A viewer that changes its mind therefore never
    /// causes this screen to be read at all.
    private (string SessionId, string PeerName, string PeerIP)? _armedHosting;

    /// <summary>
    /// Raises the consent prompt on the UI thread and answers exactly once.
    /// </summary>
    /// <remarks>
    /// The coordinator runs on whatever thread the packet arrived on, and a
    /// WinUI Window may only be created on the thread that owns the dispatcher
    /// — so the hop is not optional. The outcome is funnelled back through
    /// RemoteConsentWindow.Finish, which is idempotent, so a prompt that is
    /// closed while the session dies underneath it still answers once.
    /// </remarks>
    public void PresentConsent(DispatcherQueue ui, RemoteConsentRequest request,
                               Action<RemoteConsentOutcome> onOutcome)
    {
        _ui = ui;
        ui.TryEnqueue(() =>
        {
            try
            {
                var window = new RemoteConsentWindow(
                    request.SessionId,
                    request.Kind,
                    request.PeerName,
                    request.PeerIP,
                    request.PeerPublicKeyB64,
                    request.Trust,
                    outcome =>
                    {
                        lock (_gate) { _consent = null; }
                        onOutcome(outcome);
                    });
                lock (_gate) { _consent = window; }
                window.Activate();
            }
            catch (Exception ex)
            {
                // A prompt that could not be shown is a decline, not a hang.
                // Leaving the peer waiting on a dialog that never appeared is
                // the one outcome with no way out on either side.
                LanLogger.Remote("error", peer: request.PeerIP, sessionId: request.SessionId,
                                 reason: $"consent prompt failed: {ex.Message}");
                onOutcome(RemoteConsentOutcome.Declined());
            }
        });
    }

    /// <summary>The peer accepted and their media channel is open. Show it.</summary>
    public void StartViewing(DispatcherQueue ui, string peerName, string peerIP,
                             RemoteAttachedChannel channel)
    {
        if (channel.Connection is not TcpClient client)
        {
            LanLogger.Remote("error", peer: peerIP, sessionId: channel.SessionId,
                             reason: "attached channel carried no connection");
            return;
        }

        var link = new SocketMediaLink(client, peerIP);
        var media = new MediaSession(channel.SessionId, channel.PeerPublicKeyB64, peerIP,
                                     RemoteSessionRole.Initiator, channel.Keys, link);

        lock (_gate)
        {
            if (IsRunning) { media.Stop(); return; }
            _ui = ui;
            _peerName = peerName;
            _startedAt = DateTime.UtcNow;

            try
            {
                var viewer = new RemoteViewerWindow(peerName);
                _viewer = viewer;

                var session = new RemoteDesktopSession(record => AppendAudit?.Invoke(record));
                _session = session;

                viewer.OnClosed = () => Stop(RemoteStopReason.UserStopped);
                session.OnEnded = _ => CloseWindows();
                session.OnChanged = () => OnChanged?.Invoke();

                // Pictures arrive from the socket rather than a local encoder.
                // Delivered on the media session's read thread; AcceptVideo
                // decodes there and the presenter marshals to the UI itself.
                // Routed by channel. Sending everything to AcceptVideo worked
                // only while nothing else was being sent: a control message
                // handed to the decoder is not an error anywhere, it simply
                // produces no picture, so this would have failed silently the
                // moment input existed.
                media.OnFrame = frame => Route(session, frame);
                // Composed, not replaced. AttachInbound already puts a handler
                // here that frees the registry entry, and overwriting it leaves
                // the peer marked in-flight forever — the next invite is then
                // refused as busy with no session actually running, which is
                // exactly the "it will not start again" symptom.
                var previous = media.OnClosed;
                media.OnClosed = error =>
                {
                    previous?.Invoke(error);
                    RemoteDesktopService.Shared.Registry.Remove(channel.SessionId);
                    Stop(RemoteStopReason.Error);
                };

                RemoteDesktopService.Shared.Registry.Register(media);
                _media = media;
                media.Start();

                viewer.OnInput = records =>
                {
                    // Silently dropped without a grant, so a keystroke already
                    // in flight when control is revoked cannot still arrive.
                    if (session.Grant != RemoteGrant.Control) return;
                    byte[] payload = RemoteInputCodec.Encode(records);
                    if (payload.Length == 0) return;
                    media.Submit(new MediaOutboundFrame(MediaChannel.Input, payload, 0));
                };
                viewer.OnRequestControl = () =>
                {
                    // Asking is all a viewer may do. The host's second consent
                    // prompt decides, and nothing here can pre-empt it.
                    media.Submit(new MediaOutboundFrame(
                        MediaChannel.Control,
                        MediaControlCodec.Encode(MediaControlMessage.ControlRequest()), 0));
                    LanLogger.Remote("control_request_sent", peer: peerIP);
                };
                session.OnChanged = () =>
                {
                    OnUi(() => viewer.IsCapturing = session.Grant == RemoteGrant.Control);
                    OnChanged?.Invoke();
                };

                session.StartViewing(peerName, viewer);
                viewer.Activate();
                ArmGuard();
            }
            catch (Exception ex)
            {
                LanLogger.Remote("error", peer: peerIP, sessionId: channel.SessionId,
                                 reason: $"viewer start failed: {ex.Message}");
                media.Stop();
                CloseWindows();
                _session?.Dispose();
                _session = null;
                _media = null;
                return;
            }
        }
        OnChanged?.Invoke();
    }

    /// <summary>The second consent prompt: the peer wants keyboard and mouse.</summary>
    /// <remarks>
    /// Shows the same fingerprint the first prompt did, because the question is
    /// the same one — is this who you think it is — asked about a much larger
    /// permission. Anything other than an explicit yes leaves the grant where
    /// it was.
    /// </remarks>
    private void PresentControlConsent(DispatcherQueue ui, string peerName, string peerIP,
                                       string sessionId)
    {
        var request = new RemoteConsentRequest(
            sessionId, RemoteConsentKind.Control, peerName, peerIP,
            _media?.PeerPublicKeyB64 ?? "", PeerKeyTrust.Unknown,
            DateTime.UtcNow.AddSeconds(RemoteConsentRequest.DefaultTimeoutSeconds));

        PresentConsent(ui, request, outcome =>
        {
            if (outcome.Kind != RemoteConsentOutcomeKind.Accepted)
            {
                LanLogger.Remote("control_refused", peer: peerIP, sessionId: sessionId);
                return;
            }
            GrantControl();
        });
    }

    private static void Route(RemoteDesktopSession session, MediaInboundFrame frame)
    {
        switch (frame.Channel)
        {
            case MediaChannel.Video:
                session.AcceptVideo(frame.Payload, frame.CaptureUs);
                break;

            case MediaChannel.Control:
                try { session.AcceptControl(MediaControlCodec.Decode(frame.Payload)); }
                catch (Exception ex)
                {
                    LanLogger.Remote("error", reason: $"undecodable control message: {ex.Message}");
                }
                break;

            case MediaChannel.Input:
                session.AcceptInput(frame.Payload);
                break;
        }
    }

    /// <summary>We accepted an invite. Be ready, but do not capture yet.</summary>
    public void ArmHosting(string sessionId, string peerName, string peerIP)
    {
        lock (_gate) { _armedHosting = (sessionId, peerName, peerIP); }
        LanLogger.Remote("hosting_armed", peer: peerIP, sessionId: sessionId);
    }

    /// <summary>
    /// The viewer we agreed to has attached. Start reading this screen.
    ///
    /// Called from the media channel's arrival, not from the accept — which is
    /// the point: everything before this moment is an agreement, and nothing
    /// before it captures a pixel.
    /// </summary>
    public void BeginHosting(DispatcherQueue ui, string sessionId, MediaSession media)
    {
        (string SessionId, string PeerName, string PeerIP) armed;
        lock (_gate)
        {
            if (_armedHosting is not { } pending || pending.SessionId != sessionId)
            {
                LanLogger.Remote("host_ignored", sessionId: sessionId,
                                 reason: "attach with no armed accept");
                return;
            }
            armed = pending;
            _armedHosting = null;

            if (IsRunning) { media.Stop(); return; }
            _ui = ui;
            _peerName = armed.PeerName;
            _startedAt = DateTime.UtcNow;

            try
            {
                var session = new RemoteDesktopSession(record => AppendAudit?.Invoke(record));
                _session = session;
                _media = media;

                session.OnEnded = _ => CloseWindows();
                session.OnChanged = () => OnChanged?.Invoke();

                // Encoded frames go to the peer instead of looping back.
                session.OnEncodedFrame = frame => media.Submit(new MediaOutboundFrame(
                    MediaChannel.Video, frame.AnnexB, frame.CaptureUs, frame.IsKeyframe));
                session.OnControlMessage = message => media.Submit(new MediaOutboundFrame(
                    MediaChannel.Control, MediaControlCodec.Encode(message), 0));
                var previous = media.OnClosed;
                media.OnFrame = frame => Route(session, frame);
                media.OnClosed = error =>
                {
                    previous?.Invoke(error);       // frees the registry entry
                    Stop(RemoteStopReason.Error);
                };

                // The peer asked for the keyboard and mouse. A second prompt,
                // never an escalation of the first — PROTOCOL.md makes the
                // two-stage grant a requirement, not an interface nicety.
                session.OnControlRequested = () => PresentControlConsent(ui, armed.PeerName,
                                                                         armed.PeerIP, sessionId);

                session.StartHosting(armed.PeerName);

                ShowIndicator();
                ArmGuard();
            }
            catch (Exception ex)
            {
                LanLogger.Remote("error", peer: armed.PeerIP, sessionId: sessionId,
                                 reason: $"host start failed: {ex.Message}");
                media.Stop();
                CloseWindows();
                _session?.Dispose();
                _session = null;
                _media = null;
                return;
            }
        }
        OnChanged?.Invoke();
    }

    public void Stop(RemoteStopReason reason)
    {
        RemoteDesktopSession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }
        if (session is null) return;

        // Capture first, always. A host whose screen is still being read after
        // they pressed Stop is the worst possible ordering bug.
        session.Stop(reason);
        session.Dispose();

        // The channel goes with the session. A media socket left reading after
        // the session it fed has gone is exactly what the watchdog exists to
        // catch, and closing it here means the watchdog never has to.
        MediaSession? media;
        lock (_gate) { media = _media; _media = null; _armedHosting = null; }

        if (media is not null)
        {
            // remote_end goes over TCP 54232 rather than the media channel, so
            // it still works when the media channel is what broke. The socket
            // closing is the signal that always arrives; this supplies the
            // reason alongside it.
            try { AnnounceEnd?.Invoke(media.SessionId, media.PeerIP, reason); }
            catch (Exception ex)
            {
                LanLogger.Remote("error", reason: $"announcing the end failed: {ex.Message}");
            }
            media.Stop();
            RemoteDesktopService.Shared.Registry.Remove(media.SessionId);
            RemoteDesktopService.Shared.Registry.Cancel(media.SessionId);
        }

        _guard?.Disarm();
        _guard?.Dispose();
        _guard = null;

        CloseWindows();
        OnChanged?.Invoke();
    }

    /// The second consent prompt's result.
    public void GrantControl()
    {
        if (_session?.GrantControl() != true) return;
        RefreshIndicator();
        SendControl(MediaControlMessage.ControlGrant());
    }

    public void RevokeControl()
    {
        if (_session?.RevokeControl() != true) return;
        RefreshIndicator();
        SendControl(MediaControlMessage.ControlRevoke());
    }

    private void SendControl(MediaControlMessage message)
    {
        MediaSession? media;
        lock (_gate) { media = _media; }
        if (media is null) return;
        try
        {
            media.Submit(new MediaOutboundFrame(
                MediaChannel.Control, MediaControlCodec.Encode(message), 0));
        }
        catch (Exception ex)
        {
            LanLogger.Remote("error", reason: $"sending {message.Type} failed: {ex.Message}");
        }
    }

    // ---- Windows -----------------------------------------------------------

    private void ShowIndicator()
    {
        OnUi(() =>
        {
            _indicator = new RemoteHostIndicatorWindow(_peerName, RemoteGrant.Viewing, _startedAt)
            {
                OnStopSharing = () => Stop(RemoteStopReason.UserStopped),
                OnStopControl = RevokeControl,
            };
            _indicator.Activate();
        });
    }

    private void RefreshIndicator()
    {
        var grant = _session?.Grant ?? RemoteGrant.None;
        OnUi(() => _indicator?.ApplyGrant(_peerName, grant));
    }

    private void CloseWindows()
    {
        OnUi(() =>
        {
            try { _indicator?.Hide(); } catch { }
            _indicator = null;

            // The viewer's own close handler calls Stop; null it first so a
            // programmatic close does not re-enter.
            var viewer = _viewer;
            _viewer = null;
            if (viewer is not null)
            {
                viewer.OnClosed = null;
                try { viewer.Close(); } catch { }
            }

            try { _consent?.Finish(RemoteConsentOutcome.Declined()); } catch { }
            _consent = null;
        });
    }

    /// A WinUI window created on one thread and closed from another does not
    /// fail cleanly — it corrupts the XAML island's state and takes the process
    /// with it, usually somewhere unrelated.
    private void OnUi(Action work)
    {
        var ui = _ui;
        if (ui is null) return;
        if (ui.HasThreadAccess) work();
        else ui.TryEnqueue(() => work());
    }

    private void ArmGuard()
    {
        _guard = new RemoteSessionGuard();
        _guard.Arm(reason => Stop(reason));
        if (!_guard.HotkeyRegistered)
        {
            LanLogger.Remote("error",
                reason: $"{RemoteKillSwitch.Shortcut.DisplayName} unavailable; "
                      + "the indicator's Stop button is the only exit");
        }
    }
}
