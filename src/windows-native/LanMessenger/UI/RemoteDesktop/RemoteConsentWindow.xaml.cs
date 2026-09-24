using LanMessenger.Core.Crypto;
using LanMessenger.Core.Networking.Media;
using LanMessenger.Core.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System;

namespace LanMessenger.UI.RemoteDesktop;

// The consent prompt. Mirror of RemoteConsentView.swift.
//
// This dialog is the whole security model made visible, so it is written to be
// read under the worst conditions it will actually meet: somebody mid-task, who
// did not expect it, deciding in about two seconds. Four things follow, and each
// is a deliberate choice rather than styling:
//
//  * **The peer is named in the headline.** "Someone wants to view your screen"
//    is not a sentence anybody can act on.
//  * **The fingerprint is always shown, monospaced.** A display name is
//    trivially spoofable by anyone on the LAN; the pinned key is not. One shown
//    only when something is wrong is one nobody has ever seen before and cannot
//    compare against anything.
//  * **No reassuring badge on the safe path.** A green tick on every prompt
//    trains people to look for the tick instead of the words.
//  * **Every accidental way out lands on "no."** Enter declines, Escape
//    declines, the close button declines, and the countdown declines with the
//    `timeout` token PROTOCOL.md already reserves.

public sealed partial class RemoteConsentWindow : Window
{
    /// How long a prompt waits before declining on the user's behalf.
    ///
    /// A dialog that waits forever is worse than one that gives up: the screen
    /// it guards may be on a desk nobody is sitting at, and the peer is
    /// meanwhile staring at a spinner with no way to tell a slow human from a
    /// dead one.
    public const int DefaultTimeoutSeconds = RemoteConsentRequest.DefaultTimeoutSeconds;

    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly Action<RemoteConsentOutcome> _onOutcome;
    private readonly string _sessionId;
    private int _secondsRemaining = DefaultTimeoutSeconds;
    private bool _answered;

    public RemoteConsentWindow(string sessionId,
                               RemoteConsentKind kind,
                               string peerName,
                               string peerIp,
                               string peerPublicKeyB64,
                               PeerKeyTrust trust,
                               Action<RemoteConsentOutcome> onOutcome)
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _onOutcome = onOutcome;
        _sessionId = sessionId;

        Title = kind == RemoteConsentKind.Viewing ? "Screen Sharing Request" : "Control Request";

        TitleText.Text = kind == RemoteConsentKind.Viewing
            ? $"{peerName} wants to view your screen"
            : $"{peerName} wants to control your screen";

        ExplanationText.Text = kind == RemoteConsentKind.Viewing
            ? "They will see everything on your display, including notifications and "
              + "anything you open, until you stop sharing."
            : "They will be able to use your keyboard and mouse as if they were sitting "
              + "at this PC. You can take back control at any time.";

        AcceptButton.Content = kind == RemoteConsentKind.Viewing ? "Allow Viewing" : "Allow Control";

        AddressText.Text = peerIp;
        // Never falls back to something reassuring: a key that cannot be parsed
        // has no fingerprint, and the dialog says so rather than showing a
        // plausible-looking blank.
        FingerprintText.Text = RemoteSessionCrypto.Fingerprint(peerPublicKeyB64) ?? "unreadable key";

        var warning = WarningFor(trust);
        if (warning is not null)
        {
            WarningText.Text = warning;
            WarningPanel.Visibility = Visibility.Visible;
        }

        // Above whatever the host is working in. A prompt that opens behind a
        // full-screen app simply times out, and the user's account of it is that
        // the feature does not work.
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 360));

        // Real glass over the desktop; the root grid is transparent for it.
        // Falls back to a solid colour by itself when transparency is off.
        try { SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop(); }
        catch (Exception ex) { LanLogger.Remote("error", reason: $"consent backdrop: {ex.Message}"); }

        // Focus starts on Decline, so Enter declines. Only the first
        // activation: after that, focus is wherever the user put it.
        var focused = false;
        Activated += (_, _) =>
        {
            if (focused) return;
            focused = true;
            DeclineButton.Focus(FocusState.Programmatic);
        };

        // The close button is a way out, so it means no.
        Closed += (_, _) => Finish(RemoteConsentOutcome.Declined());

        UpdateCountdown();
        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) =>
        {
            _secondsRemaining--;
            if (_secondsRemaining <= 0) Finish(RemoteConsentOutcome.TimedOut);
            else UpdateCountdown();
        };
        _timer.Start();

        LanLogger.Remote("consent_prompt", peer: peerIp, sessionId: sessionId,
            reason: $"{(kind == RemoteConsentKind.Viewing ? "viewing" : "control")} "
                  + $"fp={FingerprintText.Text}");
    }

    /// Absent is meaningful: a pinned key gets a plain dialog with no warning
    /// chrome at all.
    private static string? WarningFor(PeerKeyTrust trust) => trust.Kind switch
    {
        PeerKeyTrustKind.Pinned => null,
        PeerKeyTrustKind.ChangedAtKnownAddress =>
            $"This is not the key you have saved for {trust.Username}. "
            + "They may have reinstalled — or this may not be them.",
        _ => "This device is not in your contacts.",
    };

    private void UpdateCountdown() =>
        CountdownText.Text = $"Declines automatically in {_secondsRemaining}s";

    private void Accept_Click(object sender, RoutedEventArgs e) => Finish(RemoteConsentOutcome.Accepted);
    private void Decline_Click(object sender, RoutedEventArgs e) => Finish(RemoteConsentOutcome.Declined());

    /// Idempotent, and the only exit. Everything that can end this dialog — a
    /// click, the close button, the countdown, the session dying underneath it —
    /// arrives here, so the outcome fires exactly once.
    public void Finish(RemoteConsentOutcome outcome)
    {
        if (_answered) return;
        _answered = true;

        try { _timer.Stop(); } catch { /* already stopped */ }

        LanLogger.Remote("consent_outcome", sessionId: _sessionId,
                         reason: outcome.Kind.ToString().ToLowerInvariant());

        // Nothing inside a WinUI callback may throw.
        try { _onOutcome(outcome); }
        catch (Exception ex) { LanLogger.Remote("error", reason: $"consent handler: {ex.Message}"); }

        try { Close(); } catch { /* already closing */ }
    }
}
