import Foundation

// The remote-desktop transport's app-facing surface, and the owner of the
// session registry.
//
// Deliberately NOT @MainActor. `attachInbound` is called synchronously from the
// inbound socket's own queue, and it has to be: the decision to detach must be
// made before control returns to the JSON read loop, because by the time an
// async main-actor hop landed, that loop would already have consumed the first
// 22 binary header bytes as a JSON length prefix and closed the connection.
final class RemoteDesktopService {

    static let shared = RemoteDesktopService()

    let registry = RemoteSessionRegistry()

    enum AttachOutcome: Equatable {
        /// The service now owns the descriptor. The caller must NOT close it.
        case detached
        case refused(String)
    }

    private init() {}

    /// Called synchronously from `NetworkCoordinator.handleInbound`'s queue.
    ///
    /// On `.detached` the descriptor has already been adopted, had its inherited
    /// read timeout cleared, had TCP_NODELAY set, been wrapped in a
    /// `SocketMediaLink`, registered so `stop()` can reach it, and started
    /// reading.
    nonisolated func attachInbound(packet: RemoteControlPacket,
                                   socket: Int32,
                                   fromIP: String) -> AttachOutcome {
        let result = registry.admit(sessionID: packet.sessionId,
                                    peerPublicKeyB64: packet.senderPublicKeyB64)
        guard case .admitted(let window) = result else {
            let reason: String
            switch result {
            case .unknown:         reason = "no matching accept window"
            case .expired:         reason = "accept window expired"
            case .alreadyAttached: reason = "session already attached"
            case .admitted:        reason = ""
            }
            NetLogger.remote(event: "attach_refused", peer: fromIP,
                             sessionID: packet.sessionId, reason: reason)
            return .refused(reason)
        }

        let link = SocketMediaLink(adopting: socket, peerIP: fromIP)
        let session = MediaSession(sessionID: window.sessionID,
                                   peerPublicKeyB64: window.peerPublicKeyB64,
                                   peerIP: fromIP,
                                   role: window.role,
                                   keys: window.keys,
                                   link: link)
        session.onClosed = { [weak self] _ in
            self?.registry.remove(sessionID: window.sessionID)
        }
        registry.register(session)
        session.start()

        NetLogger.remote(event: "attach", peer: fromIP, sessionID: window.sessionID,
                         role: window.role.rawValue)
        return .detached
    }

    /// Routes the four JSON control packets. Session setup (consent, key
    /// derivation, invite/accept exchange) is WS8's job; this exists now so the
    /// transport is reachable and so an unhandled case is a compile error there
    /// rather than a silent drop.
    @MainActor
    func handleControlPacket(_ packet: ValidatedPacket) {
        switch packet {
        case .remoteInvite(let pkt, let ip):
            NetLogger.remote(event: "invite_received", peer: ip, sessionID: pkt.sessionId)
        case .remoteAccept(let pkt, let ip):
            NetLogger.remote(event: "accepted", peer: ip, sessionID: pkt.sessionId)
        case .remoteDecline(let pkt, let ip):
            NetLogger.remote(event: "declined", peer: ip, sessionID: pkt.sessionId,
                             reason: pkt.reason)
            registry.cancel(sessionID: pkt.sessionId)
        case .remoteEnd(let pkt, let ip):
            NetLogger.remote(event: "session_end", peer: ip, sessionID: pkt.sessionId,
                             reason: pkt.reason)
            registry.session(id: pkt.sessionId)?.stop()
            registry.cancel(sessionID: pkt.sessionId)
        default:
            break
        }
    }

    /// Reached from `NetworkCoordinator.stop()`. A detached socket must not
    /// outlive the network stack that produced it.
    func stopAll() {
        registry.closeAll()
    }
}
