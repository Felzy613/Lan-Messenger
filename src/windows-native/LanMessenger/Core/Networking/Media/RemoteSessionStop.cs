namespace LanMessenger.Core.Networking.Media;

// Every way a session ends, and the shortcut that must always be able to end it.
// Mirror of RemoteSessionStop.swift.
//
// Both exist for the same reason. Once control has been granted the host's own
// mouse is contested — the viewer is moving it too — so a Stop button is not a
// guarantee, it is a race. The kill shortcut is the guarantee, and the auto-stop
// triggers are the cases where nobody is present to press anything.

public enum RemoteStopReason
{
    UserStopped, KillSwitch, ScreenLocked, UserSwitched, SystemSleep,
    NetworkLost, AppQuit, Watchdog, PeerEnded, Error,
}

public static class RemoteStopReasonExtensions
{
    /// The wire token. Travels in remote_end and lands in stored history, so
    /// camelCase leaking out would be permanent.
    public static string ToToken(this RemoteStopReason reason) => reason switch
    {
        RemoteStopReason.UserStopped  => "user_stopped",
        RemoteStopReason.KillSwitch   => "kill_switch",
        RemoteStopReason.ScreenLocked => "screen_locked",
        RemoteStopReason.UserSwitched => "user_switched",
        RemoteStopReason.SystemSleep  => "system_sleep",
        RemoteStopReason.NetworkLost  => "network_lost",
        RemoteStopReason.AppQuit      => "app_quit",
        RemoteStopReason.Watchdog     => "watchdog",
        RemoteStopReason.PeerEnded    => "peer_ended",
        _                             => "error",
    };

    /// Whether a remote_end can still be put on the wire. Not politeness: a peer
    /// that gets none waits out its own watchdog before believing the session is
    /// over, showing a frozen last frame the whole time.
    public static bool CanNotifyPeer(this RemoteStopReason reason) => reason switch
    {
        RemoteStopReason.NetworkLost or RemoteStopReason.PeerEnded or RemoteStopReason.Error => false,
        _ => true,
    };

    /// Whether the host chose this. Shapes whether anything is worth surfacing
    /// afterwards: a session ended by a Stop click needs no explanation; one
    /// ended by a watchdog does.
    public static bool IsDeliberate(this RemoteStopReason reason) => reason switch
    {
        RemoteStopReason.UserStopped or RemoteStopReason.KillSwitch or RemoteStopReason.AppQuit => true,
        _ => false,
    };

    /// One line for the conversation's audit trail, in the past tense, naming the
    /// cause rather than the mechanism.
    public static string AuditDescription(this RemoteStopReason reason) => reason switch
    {
        RemoteStopReason.UserStopped  => "You stopped sharing your screen.",
        RemoteStopReason.KillSwitch   => "You stopped sharing your screen with the emergency shortcut.",
        RemoteStopReason.ScreenLocked => "Screen sharing stopped because this PC was locked.",
        RemoteStopReason.UserSwitched => "Screen sharing stopped because the user account was switched.",
        RemoteStopReason.SystemSleep  => "Screen sharing stopped because this PC went to sleep.",
        RemoteStopReason.NetworkLost  => "Screen sharing stopped because the network connection was lost.",
        RemoteStopReason.AppQuit      => "Screen sharing stopped because LAN Messenger quit.",
        RemoteStopReason.Watchdog     => "Screen sharing stopped because the other side stopped responding.",
        RemoteStopReason.PeerEnded    => "The other side ended the session.",
        _                             => "Screen sharing stopped because of an error.",
    };
}

/// A key combination, in the terms RegisterHotKey speaks.
public readonly record struct RemoteShortcut(uint Modifiers, uint VirtualKey, string DisplayName);

/// The shortcut that always stops a session.
public static class RemoteKillSwitch
{
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004;
    private const uint VK_ESCAPE = 0x1B;

    /// Ctrl+Alt+Shift+Esc. The Windows counterpart of the Mac's ⌃⌥⌘⎋: Escape is
    /// never a text character, and three modifiers put it well out of reach of
    /// an accidental press.
    ///
    /// Ctrl+Shift+Esc alone is Task Manager, which is why Alt is in there — and
    /// the adjacency is useful rather than a problem, since a host groping for
    /// the escape hatch under stress lands on either this or on Task Manager,
    /// and both are a way out.
    ///
    /// RegisterHotKey needs no elevation and no special permission, which
    /// matters: this has to work when other things have gone wrong.
    public static readonly RemoteShortcut Shortcut =
        new(MOD_CONTROL | MOD_ALT | MOD_SHIFT, VK_ESCAPE, "Ctrl+Alt+Shift+Esc");

    /// Combinations a viewer's input capture must never put on the wire, so a
    /// viewer can always escape their own session and a host's kill switch can
    /// never be triggered remotely by the peer it exists to stop.
    public static readonly RemoteShortcut[] Reserved = [Shortcut];

    public static bool IsReserved(uint modifiers, uint virtualKey)
    {
        foreach (var s in Reserved)
        {
            if (s.Modifiers == modifiers && s.VirtualKey == virtualKey) return true;
        }
        return false;
    }
}
