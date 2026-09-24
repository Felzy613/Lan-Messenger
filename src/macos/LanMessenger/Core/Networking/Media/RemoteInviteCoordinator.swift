import CryptoKit
import Foundation

// The exchange that gets two machines to agree to share a screen.
//
// Everything underneath this was built and proven first: the handshake crypto,
// the media transport, the capture and encode path, the decoder, the presenter,
// the consent prompt, the audit trail. What was missing was the short
// conversation that turns all of it on, and the state that has to be held while
// that conversation is in flight.
//
// That state is the whole reason this object exists. Between a user asking to
// see a screen and pictures appearing there is a window — seconds long, and
// spanning two machines — in which an ephemeral private key, a session id and a
// set of negotiated parameters have to survive, be findable by session id when
// the answer arrives, and be discarded if it never does. Nothing else in the
// app has anywhere to keep them: `RemoteSessionRegistry` holds accept windows
// for sessions already agreed, and `RemoteDesktopSession` only exists once
// there is something to show.
//
// The shape of the exchange, from PROTOCOL.md:
//
//   initiator                               responder
//   ---------                               ---------
//   remote_invite  ----------------------->  policy gate, then consent prompt
//                  <----- remote_accept ---  opens a ~10s accept window
//   media_attach   ----------------------->  matched against that window
//   [binary media framing from here on]
//
//   ...or  <----- remote_decline ---  with a machine-readable reason.
//
// Two asymmetries are deliberate and easy to get backwards:
//
//  * **The initiator is the viewer.** The peer that asks is the one that
//    watches; the peer that agrees is the one whose screen is shared. Roles in
//    the key schedule follow from this, not from who opened the socket.
//  * **Accepting grants viewing only.** Control is a second consent, later, over
//    the media channel's control sub-channel. Nothing here may grant it.
//
// Every outside dependency arrives as a closure rather than a singleton, for the
// same reason `RemoteDesktopPolicy` takes a context struct: the interesting
// states here are refusals, races and timeouts, and none of them are reachable
// in a test that needs a live network stack and a real screen.

@MainActor
final class RemoteInviteCoordinator {

    /// What this needs from the rest of the app.
    struct Environment {
        /// Writes one JSON frame to a peer, one-shot.
        var send: (Data, String) -> Void
        /// Connects, writes `media_attach`, and returns the still-open
        /// descriptor — or -1. The socket speaks binary media framing after it.
        var attachOutbound: (String, Data) -> Int32
        var ownPublicKeyB64: () -> String
        var ownUsername: () -> String
        var privateKey: () throws -> Curve25519.KeyAgreement.PrivateKey
        var mode: () -> RemoteDesktopMode
        var contacts: () -> [KnownContact]
        /// Whether a session is already live locally, whichever side started it.
        var hasLiveSession: () -> Bool
        var registry: () -> RemoteSessionRegistry
        /// Raise the consent prompt. Fires exactly once.
        var presentConsent: (RemoteConsentRequest, @escaping (RemoteConsentOutcome) -> Void) -> Void
        /// The peer accepted and the media channel is up: show their screen.
        var startViewing: (String, String, MediaSession) -> Void
        /// We accepted: be ready to share this screen when they attach.
        var armHosting: (String, String, String) -> Void
        var now: () -> Date = Date.init
    }

    /// How long an initiator waits for any answer at all.
    ///
    /// Longer than the responder's 45s consent countdown on purpose: the peer
    /// declines on its own at 45s and tells us, and this only has to cover the
    /// case where that message never arrives. Expiring first would turn a
    /// perfectly normal slow decision into a reported network failure.
    static let inviteTimeout: TimeInterval = 60

    private struct PendingInvite {
        let sessionID: String
        let peerKey: String
        let peerIP: String
        let peerName: String
        let ephemeral: Curve25519.KeyAgreement.PrivateKey
        var timer: Task<Void, Never>?
    }

    private let env: Environment
    private var pending: PendingInvite?

    /// Progress for the interface. Empty string means "nothing in flight".
    var onStateChange: ((String) -> Void)?

    init(environment: Environment) {
        self.env = environment
    }

    var hasInviteInFlight: Bool { pending != nil }
    var pendingSessionID: String? { pending?.sessionID }

    // MARK: - Initiator

    /// Asks a peer to share their screen.
    ///
    /// `peerIP` must be where the peer IS, resolved from its identity key at the
    /// moment of the click — never an address a conversation remembered. LAN
    /// addresses are recycled between machines by DHCP: the Dell's address on
    /// the day this was written had previously belonged to Ari, and a
    /// remembered one sent the Dell's invite to Ari's screen.
    ///
    /// **A new request supersedes a pending one** unless it is the same peer at
    /// the same address. This used to refuse everything while any invite was
    /// outstanding — for up to a minute, with only a log line to say so — which
    /// meant one click in the wrong conversation, or one invite aimed at a stale
    /// address, made the button do nothing at all for everyone else.
    func invite(peerKey: String, peerIP: String, peerName: String) {
        if let current = pending {
            if current.peerKey == peerKey && current.peerIP == peerIP {
                // Genuinely already asking this device at this address. Say so
                // again rather than nothing: a repeated click is a user who
                // cannot tell whether the first one worked.
                NetLogger.remote(event: "invite_already_pending", peer: peerIP,
                                 sessionID: current.sessionID)
                onStateChange?("Waiting for \(current.peerName) to accept…")
                return
            }
            // A different device, or the same device at a new address — either
            // way the outstanding invite is not wanted or cannot arrive. Withdraw
            // it properly, so a prompt that did reach somebody closes.
            NetLogger.remote(event: "invite_superseded", peer: current.peerIP,
                             sessionID: current.sessionID,
                             reason: current.peerKey == peerKey ? "peer moved" : "different peer")
            cancelInvite()
        }

        let sessionID = RemoteSessionCrypto.newSessionID()
        let ephemeral = Curve25519.KeyAgreement.PrivateKey()

        // Ours to state, theirs to answer. Canonicalised into the handshake
        // transcript, so a peer that rewrites a parameter in flight produces
        // different keys on each side — the tamper surfaces as a failure to
        // connect rather than as a silent downgrade.
        var params = RemoteHandshakeParams()
        params["protocol"] = .int(1)
        params["video"] = .string("h264")

        guard let frame = sealedFrame(type: "remote_invite", sessionID: sessionID,
                                      ephemeral: ephemeral, params: params, peerKey: peerKey)
        else {
            NetLogger.remote(event: "error", peer: peerIP, sessionID: sessionID,
                             reason: "could not seal the invite")
            return
        }

        var invite = PendingInvite(sessionID: sessionID, peerKey: peerKey, peerIP: peerIP,
                                   peerName: peerName, ephemeral: ephemeral, timer: nil)
        invite.timer = Task { [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(Self.inviteTimeout * 1_000_000_000))
            guard !Task.isCancelled else { return }
            self?.inviteTimedOut(sessionID: sessionID)
        }
        pending = invite

        env.send(frame, peerIP)
        NetLogger.remote(event: "invite_sent", peer: peerIP, sessionID: sessionID)
        onStateChange?("Waiting for \(peerName) to accept…")
    }

    /// The peer agreed. Derive the keys, then connect and upgrade.
    func handleAccept(_ packet: RemoteSessionPacket, from ip: String) {
        guard let invite = pending, invite.sessionID == packet.sessionId else {
            // An accept for a session we are not waiting on. Nothing to tear
            // down, and answering would confirm to an unknown sender that we
            // are here.
            NetLogger.remote(event: "accept_ignored", peer: ip, sessionID: packet.sessionId,
                             reason: "no invite in flight for this session")
            return
        }
        clearPending()

        do {
            let body = try openSealedBody(packet, peerKey: invite.peerKey)
            let keys = try RemoteSessionCrypto.deriveKeys(
                myRole: .initiator,
                myEphemeralPrivate: invite.ephemeral,
                myStaticPrivate: try env.privateKey(),
                peerEphemeralPublicKeyB64: body.ephemeralPublicKeyB64,
                peerStaticPublicKeyB64: invite.peerKey,
                sessionID: invite.sessionID,
                // Their parameters, not ours. They answer with what they
                // actually agreed to, and the transcript binds that answer.
                params: body.params)

            // Attach to where the answer came FROM. It opened under this peer's
            // identity key, so its source address is proof of where that
            // device is right now — fresher than the address the invite went
            // to, which DHCP may have handed to someone else since.
            try attachAndView(invite: invite, keys: keys, address: ip)
        } catch {
            NetLogger.remote(event: "error", peer: ip, sessionID: packet.sessionId,
                             reason: "accept handling failed: \(error)")
            onStateChange?("Could not start the session.")
            sendEnd(sessionID: packet.sessionId, to: ip, reason: .error)
        }
    }

    /// Connects, upgrades the socket, and opens the viewer, at `address` — the
    /// authenticated source of the accept, not the address the invite used.
    private func attachAndView(invite: PendingInvite, keys: RemoteSessionKeys,
                               address: String) throws {
        let attach = RemoteControlPacket(type: "media_attach",
                                         sessionId: invite.sessionID,
                                         sender: env.ownUsername(),
                                         senderPublicKeyB64: env.ownPublicKeyB64(),
                                         port: 54232)
        let frame = try FrameCodec.encode(attach)

        let socket = env.attachOutbound(address, frame)
        guard socket >= 0 else {
            onStateChange?("Could not reach \(invite.peerName).")
            return
        }

        // From here the descriptor is ours and speaks binary media framing.
        let link = SocketMediaLink(adopting: socket, peerIP: address)
        let media = MediaSession(sessionID: invite.sessionID,
                                 peerPublicKeyB64: invite.peerKey,
                                 peerIP: address,
                                 role: .initiator,
                                 keys: keys,
                                 link: link)
        let registry = env.registry()
        registry.register(media)
        media.onClosed = { [weak self] _ in
            Task { @MainActor [weak self] in
                registry.remove(sessionID: invite.sessionID)
                self?.onStateChange?("")
            }
        }
        media.start()

        NetLogger.remote(event: "attach_sent", peer: address, sessionID: invite.sessionID)
        onStateChange?("Connected to \(invite.peerName).")
        env.startViewing(invite.peerName, address, media)
    }

    /// The peer refused, or could not.
    func handleDecline(_ packet: RemoteControlPacket, from ip: String) {
        guard let invite = pending, invite.sessionID == packet.sessionId else { return }
        clearPending()

        // An unknown token is a newer peer saying something sensible we have not
        // learned yet. The spec requires tolerating it, so it gets the generic
        // sentence rather than its raw text.
        let reason = RemoteDeclineReason(rawValue: packet.reason ?? "") ?? .declined
        onStateChange?(Self.declineMessage(reason, peerName: invite.peerName))
        NetLogger.remote(event: "declined", peer: ip, sessionID: packet.sessionId,
                         reason: packet.reason ?? "declined")
    }

    static func declineMessage(_ reason: RemoteDeclineReason, peerName: String) -> String {
        switch reason {
        case .declined:    return "\(peerName) declined."
        case .busy:        return "\(peerName) is already in a session."
        case .unsupported: return "\(peerName)'s version does not support screen sharing."
        case .disabled:    return "\(peerName) has screen sharing switched off."
        case .noEncoder:   return "\(peerName)'s machine has no video encoder available."
        case .timeout:     return "\(peerName) did not answer."
        }
    }

    private func inviteTimedOut(sessionID: String) {
        guard let invite = pending, invite.sessionID == sessionID else { return }
        clearPending()
        NetLogger.remote(event: "invite_timeout", peer: invite.peerIP, sessionID: sessionID)
        onStateChange?("\(invite.peerName) did not answer.")
        sendEnd(sessionID: sessionID, to: invite.peerIP, reason: .watchdog)
    }

    /// Withdraws an invite the user gave up on.
    func cancelInvite() {
        guard let invite = pending else { return }
        clearPending()
        sendEnd(sessionID: invite.sessionID, to: invite.peerIP, reason: .userStopped)
        onStateChange?("")
    }

    private func clearPending() {
        pending?.timer?.cancel()
        pending = nil
    }

    // MARK: - Responder

    /// An invite arrived. Judge it, then ask the user.
    func handleInvite(_ packet: RemoteSessionPacket, from ip: String) {
        let sessionID = packet.sessionId
        let peerKey = packet.senderPublicKeyB64

        // The gate first, and before the prompt. A peer who is not a saved
        // contact must produce no interface at all: an unsolicited dialog from
        // a stranger is itself the attack, whatever the user then clicks.
        let decision = RemoteDesktopPolicy.decide(RemoteInviteContext(
            peerPublicKeyB64: peerKey,
            peerIP: ip,
            ownPublicKeyB64: env.ownPublicKeyB64(),
            mode: env.mode(),
            contacts: env.contacts(),
            hasSessionInFlight: hasInviteInFlight
                || env.registry().hasWindow(sessionID: sessionID)
                || env.hasLiveSession()))

        let trust: PeerKeyTrust
        switch decision {
        case .ignore(let why):
            NetLogger.remote(event: "invite_dropped", peer: ip, sessionID: sessionID,
                             reason: why.rawValue)
            return
        case .decline(let reason):
            NetLogger.remote(event: "invite_refused", peer: ip, sessionID: sessionID,
                             reason: reason.rawValue)
            sendDecline(sessionID: sessionID, to: ip, reason: reason)
            return
        case .prompt(let t):
            trust = t
        }

        // Only now does the peer get to cost us an X25519 agreement or a dialog.
        guard let body = try? openSealedBody(packet, peerKey: peerKey) else {
            NetLogger.remote(event: "invite_dropped", peer: ip, sessionID: sessionID,
                             reason: "sealed body did not open")
            return
        }

        let request = RemoteConsentRequest(
            sessionID: sessionID,
            kind: .viewing,
            peerName: packet.sender,
            peerIP: ip,
            peerPublicKeyB64: peerKey,
            trust: trust,
            expiresAt: env.now().addingTimeInterval(RemoteConsentRequest.defaultTimeout))

        // Answered inline rather than through a Task hop. The prompt is a
        // main-actor object and AppKit delivers its callbacks on the main
        // thread, so hopping only delays the answer — and it delays it past the
        // point where a test can observe that an accept window was opened
        // before the accept was written, which is the one ordering here that
        // has to be provable.
        env.presentConsent(request) { [weak self] outcome in
            MainActor.assumeIsolated {
                self?.finishConsent(outcome, packet: packet, body: body, ip: ip)
            }
        }
    }

    private func finishConsent(_ outcome: RemoteConsentOutcome,
                               packet: RemoteSessionPacket,
                               body: SealedBody,
                               ip: String) {
        let sessionID = packet.sessionId
        let peerKey = packet.senderPublicKeyB64

        if case .declined(let reason) = outcome {
            sendDecline(sessionID: sessionID, to: ip, reason: reason)
            return
        }

        do {
            let ephemeral = Curve25519.KeyAgreement.PrivateKey()

            // What we actually agreed to, which is what gets sealed back and
            // what both sides hash into the transcript. Echoing their params
            // unchanged would make the field decorative; naming the display we
            // will share is the point of answering at all.
            var params = body.params
            params["display"] = .int(0)

            let keys = try RemoteSessionCrypto.deriveKeys(
                myRole: .responder,
                myEphemeralPrivate: ephemeral,
                myStaticPrivate: try env.privateKey(),
                peerEphemeralPublicKeyB64: body.ephemeralPublicKeyB64,
                peerStaticPublicKeyB64: peerKey,
                sessionID: sessionID,
                params: params)

            // The window opens BEFORE the accept goes out. The peer may attach
            // the instant it reads our answer, and a media_attach arriving
            // against a window that does not exist yet is dropped and the
            // connection closed — a race that would present as an intermittent
            // failure to connect and would reproduce only on a fast network.
            let opened = env.registry().open(sessionID: sessionID,
                                             peerPublicKeyB64: peerKey,
                                             peerIP: ip,
                                             role: .responder,
                                             keys: keys)
            guard case .opened = opened else {
                sendDecline(sessionID: sessionID, to: ip, reason: .busy)
                return
            }

            guard let frame = sealedFrame(type: "remote_accept", sessionID: sessionID,
                                          ephemeral: ephemeral, params: params, peerKey: peerKey)
            else {
                env.registry().cancel(sessionID: sessionID)
                sendDecline(sessionID: sessionID, to: ip, reason: .declined)
                return
            }

            env.send(frame, ip)
            NetLogger.remote(event: "accept_sent", peer: ip, sessionID: sessionID)

            // Capture does not start here. It starts when they attach, so a
            // viewer that never arrives never causes this screen to be read:
            // the window simply expires.
            env.armHosting(sessionID, packet.sender, ip)
        } catch {
            NetLogger.remote(event: "error", peer: ip, sessionID: sessionID,
                             reason: "accept failed: \(error)")
            env.registry().cancel(sessionID: sessionID)
            sendDecline(sessionID: sessionID, to: ip, reason: .declined)
        }
    }

    // MARK: - Outbound helpers

    func sendDecline(sessionID: String, to ip: String, reason: RemoteDeclineReason) {
        guard let frame = controlFrame(type: "remote_decline", sessionID: sessionID,
                                       reason: reason.rawValue) else { return }
        env.send(frame, ip)
        NetLogger.remote(event: "decline_sent", peer: ip, sessionID: sessionID,
                         reason: reason.rawValue)
    }

    func sendEnd(sessionID: String, to ip: String, reason: RemoteStopReason) {
        guard let frame = controlFrame(type: "remote_end", sessionID: sessionID,
                                       reason: reason.rawValue) else { return }
        env.send(frame, ip)
        NetLogger.remote(event: "end_sent", peer: ip, sessionID: sessionID,
                         reason: reason.rawValue)
    }

    private func controlFrame(type: String, sessionID: String, reason: String) -> Data? {
        let packet = RemoteControlPacket(type: type,
                                         sessionId: sessionID,
                                         sender: env.ownUsername(),
                                         senderPublicKeyB64: env.ownPublicKeyB64(),
                                         port: 54232,
                                         reason: reason)
        return try? FrameCodec.encode(packet)
    }

    // MARK: - Sealed body

    /// The encrypted half of an invite or an accept.
    struct SealedBody: Equatable {
        let ephemeralPublicKeyB64: String
        let params: RemoteHandshakeParams
    }

    /// Builds a sealed `remote_invite` or `remote_accept` frame.
    ///
    /// The ephemeral key is sealed rather than sent in the clear. That does not
    /// stop an attacker who cannot complete the triple DH anyway — it stops an
    /// unauthenticated peer making us do X25519 work, and it authenticates
    /// `params` for free.
    private func sealedFrame(type: String,
                             sessionID: String,
                             ephemeral: Curve25519.KeyAgreement.PrivateKey,
                             params: RemoteHandshakeParams,
                             peerKey: String) -> Data? {
        do {
            var paramDict: [String: Any] = [:]
            for (key, value) in params.values {
                switch value {
                case .int(let i):    paramDict[key] = i
                case .string(let s): paramDict[key] = s
                }
            }
            let body: [String: Any] = [
                "eph_pub_b64": ephemeral.publicKey.rawRepresentation.base64EncodedString(),
                "params": paramDict,
            ]

            let plaintext = try JSONSerialization.data(withJSONObject: body,
                                                       options: [.sortedKeys])
            let sealed = try SessionCrypto.encryptForPeer(
                myPrivate: try env.privateKey(),
                peerPublicKeyB64: peerKey,
                plaintext: plaintext,
                aad: Data(sessionID.utf8))

            return try FrameCodec.encodeDict([
                "type": type,
                "session_id": sessionID,
                "sender": env.ownUsername(),
                "sender_public_key_b64": env.ownPublicKeyB64(),
                "port": 54232,
                "nonce": sealed.nonceB64,
                "ciphertext": sealed.ciphertextB64,
            ])
        } catch {
            NetLogger.remote(event: "error", sessionID: sessionID,
                             reason: "sealing \(type) failed: \(error)")
            return nil
        }
    }

    func openSealedBody(_ packet: RemoteSessionPacket, peerKey: String) throws -> SealedBody {
        let plaintext = try SessionCrypto.decryptFromPeer(
            myPrivate: try env.privateKey(),
            peerPublicKeyB64: peerKey,
            nonceB64: packet.nonce,
            ciphertextB64: packet.ciphertext,
            aad: Data(packet.sessionId.utf8))

        // A peer controls every byte here. JSONSerialization raises an
        // uncatchable ObjC exception for some inputs, so the shape is checked
        // before anything is read out of it.
        let parsed = try JSONSerialization.jsonObject(with: plaintext)
        guard let object = parsed as? [String: Any],
              let eph = object["eph_pub_b64"] as? String,
              Data(base64Encoded: eph)?.count == 32 else {
            throw RemoteSessionCryptoError.invalidPublicKey
        }

        var params = RemoteHandshakeParams()
        if let raw = object["params"] as? [String: Any] {
            for (key, value) in raw {
                // NSNumber bridges to both Int and Bool, and the order matters:
                // a JSON `true` read as Int 1 would hash differently on the two
                // sides. RemoteParamValue has no bool case, so a value we cannot
                // represent must fail the handshake rather than be dropped —
                // the peer hashed it into their transcript either way.
                if let i = value as? Int {
                    params[key] = .int(i)
                } else if let s = value as? String {
                    params[key] = .string(s)
                } else {
                    throw RemoteSessionCryptoError.transcriptMismatch
                }
            }
        }
        return SealedBody(ephemeralPublicKeyB64: eph, params: params)
    }
}
