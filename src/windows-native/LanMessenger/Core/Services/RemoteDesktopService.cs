using LanMessenger.Core.Crypto;
using LanMessenger.Core.Networking.Media;
using LanMessenger.Core.Protocol;
using System.Net.Sockets;

namespace LanMessenger.Core.Services;

// The remote-desktop transport's app-facing surface, and the owner of the
// session registry. Mirror of the macOS RemoteDesktopService.swift.
//
// Deliberately NOT UI-thread affine. AttachInbound is called synchronously from
// the inbound connection's own task, and it has to be: the decision to detach
// must be made before control returns to the JSON read loop, because by the time
// a DispatcherQueue.TryEnqueue landed, that loop would already have consumed the
// first 22 binary header bytes as a JSON length prefix and closed the connection.
public sealed class RemoteDesktopService
{
    public static RemoteDesktopService Shared { get; } = new();

    public RemoteSessionRegistry Registry { get; } = new();

    public enum AttachOutcome { Detached, Refused }

    private RemoteDesktopService() { }

    /// <summary>Called synchronously from NetworkCoordinator.HandleInbound's task.</summary>
    /// <remarks>
    /// On <see cref="AttachOutcome.Detached"/> the service owns the TcpClient: it
    /// has had its inherited timeouts cleared, NoDelay set, been wrapped in a
    /// SocketMediaLink, registered so Stop() can reach it, and started reading.
    /// The caller must NOT dispose it.
    /// </remarks>
    public AttachOutcome AttachInbound(RemoteControlPacket packet, TcpClient client, string fromIP)
    {
        var result = Registry.Admit(packet.SessionId, packet.SenderPublicKeyB64);
        if (result.Kind != RemoteAdmitKind.Admitted || result.Window is not { } window)
        {
            string reason = result.Kind switch
            {
                RemoteAdmitKind.Unknown         => "no matching accept window",
                RemoteAdmitKind.Expired         => "accept window expired",
                RemoteAdmitKind.AlreadyAttached => "session already attached",
                _                               => "refused",
            };
            LanLogger.Remote("attach_refused", peer: fromIP, sessionId: packet.SessionId, reason: reason);
            return AttachOutcome.Refused;
        }

        var link = new SocketMediaLink(client, fromIP);
        var session = new MediaSession(
            window.SessionId, window.PeerPublicKeyB64, fromIP, window.Role, window.Keys, link);
        session.OnClosed = _ => Registry.Remove(window.SessionId);
        Registry.Register(session);
        session.Start();

        LanLogger.Remote("attach", peer: fromIP, sessionId: window.SessionId, role: window.Role.ToString());

        // Only a responder hosts. An initiator's own attach is the connection it
        // just opened outbound, and its viewer is already being set up by the
        // coordinator that opened it.
        if (window.Role == RemoteSessionRole.Responder)
        {
            OnHostAttached?.Invoke(window.SessionId, session);
        }
        return AttachOutcome.Detached;
    }

    /// <summary>
    /// Routes the four JSON control packets. Session setup (consent, key
    /// derivation, invite/accept exchange) is a later work stream's job; this
    /// exists now so the transport is reachable and so an unhandled case is a
    /// compile error there rather than a silent drop.
    /// </summary>
    /// <summary>
    /// Set by AppModel. The invite exchange lives with the model because it
    /// needs the consent prompt, the peer list and the session; this object
    /// stays free of all three so AttachInbound can keep running on the socket
    /// thread.
    /// </summary>
    public RemoteInviteCoordinator? Invites { get; set; }

    /// <summary>A viewer we agreed to has attached. Hosting begins here.</summary>
    public Action<string, MediaSession>? OnHostAttached { get; set; }

    public void HandleControlPacket(ValidatedPacket packet)
    {
        switch (packet)
        {
            case ValidatedRemoteInvite invite:
                LanLogger.Remote("invite_received", peer: invite.SenderIP, sessionId: invite.Packet.SessionId);
                Invites?.HandleInvite(invite.Packet, invite.SenderIP);
                break;
            case ValidatedRemoteAccept accept:
                LanLogger.Remote("accepted", peer: accept.SenderIP, sessionId: accept.Packet.SessionId);
                Invites?.HandleAccept(accept.Packet, accept.SenderIP);
                break;
            case ValidatedRemoteDecline decline:
                LanLogger.Remote("declined", peer: decline.SenderIP, sessionId: decline.Packet.SessionId,
                                 reason: decline.Packet.Reason);
                Invites?.HandleDecline(decline.Packet, decline.SenderIP);
                Registry.Cancel(decline.Packet.SessionId);
                break;
            case ValidatedRemoteEnd end:
                LanLogger.Remote("session_end", peer: end.SenderIP, sessionId: end.Packet.SessionId,
                                 reason: end.Packet.Reason);
                Registry.Session(end.Packet.SessionId)?.Stop();
                Registry.Cancel(end.Packet.SessionId);
                break;
        }
    }

    /// <summary>
    /// Reached from NetworkCoordinator.Stop(). A detached socket must not outlive
    /// the network stack that produced it.
    /// </summary>
    public void StopAll() => Registry.CloseAll();
}
