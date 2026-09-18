using LanMessenger.Core.Networking.Media;
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
    private DispatcherQueue? _ui;

    private string _peerName = "";
    private DateTime _startedAt;

    /// Where audit records go. Set by AppModel so a session can be exercised
    /// without a history store.
    public Action<RemoteAuditRecord>? AppendAudit { get; set; }
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

        _guard?.Disarm();
        _guard?.Dispose();
        _guard = null;

        CloseWindows();
        OnChanged?.Invoke();
    }

    /// The second consent prompt's result.
    public void GrantControl()
    {
        if (_session?.GrantControl() == true) RefreshIndicator();
    }

    public void RevokeControl()
    {
        if (_session?.RevokeControl() == true) RefreshIndicator();
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

            try { _consent?.Finish(RemoteConsentOutcome.Declined); } catch { }
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
