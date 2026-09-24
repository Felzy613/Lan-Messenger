import CryptoKit
import XCTest
@testable import LanMessenger

/// The invite exchange, exercised through its injected environment.
///
/// Everything interesting here is a refusal, a race or a timeout, and none of
/// those are reachable in a test that needs two machines — which is exactly why
/// `RemoteInviteCoordinator` takes its dependencies as closures rather than
/// reaching for singletons. What is asserted below is mostly what does NOT
/// happen: no prompt for a stranger, no reply to a stranger, no accept window
/// left behind after a failure.
@MainActor
final class RemoteInviteCoordinatorTests: XCTestCase {

    // MARK: - Fixtures

    /// Both ends of a conversation, so a packet sealed by one really opens on
    /// the other. Nothing here is a mock of the crypto — a test that faked it
    /// would prove only that the test agrees with itself.
    private struct Peers {
        let localPrivate = Curve25519.KeyAgreement.PrivateKey()
        let remotePrivate = Curve25519.KeyAgreement.PrivateKey()

        var localKeyB64: String {
            localPrivate.publicKey.rawRepresentation.base64EncodedString()
        }
        var remoteKeyB64: String {
            remotePrivate.publicKey.rawRepresentation.base64EncodedString()
        }
    }

    /// Everything the coordinator did, in order.
    private final class Recorder {
        var sent: [(frame: Data, ip: String)] = []
        var prompts: [RemoteConsentRequest] = []
        var viewingStarts: [(String, String)] = []
        var hostingArmed: [(String, String, String)] = []
        var attachCalls = 0
        /// Where each media attach was dialled.
        var attachAddresses: [String] = []
        /// Everything the coordinator told the interface, in order.
        var statuses: [String] = []
        /// Set by the test to answer whatever prompt is raised.
        var consentAnswer: RemoteConsentOutcome = .declined(.declined)
        /// Session ids that had an accept window open at the moment the accept
        /// frame was written. The race this exists to catch is subtle: a peer
        /// can attach the instant it reads the accept, so the window must
        /// already be open when that frame goes out.
        var windowOpenWhenAccepted: [String: Bool] = [:]

        func decodedTypes() -> [String] {
            sent.compactMap { entry in
                guard entry.frame.count > 4 else { return nil }
                let body = entry.frame.dropFirst(4)
                guard let obj = try? JSONSerialization.jsonObject(with: body) as? [String: Any]
                else { return nil }
                return obj["type"] as? String
            }
        }

        func decodedReasons() -> [String] {
            sent.compactMap { entry in
                guard entry.frame.count > 4 else { return nil }
                let body = entry.frame.dropFirst(4)
                guard let obj = try? JSONSerialization.jsonObject(with: body) as? [String: Any]
                else { return nil }
                return obj["reason"] as? String
            }
        }
    }

    private func makeCoordinator(
        peers: Peers,
        mode: RemoteDesktopMode = .on,
        contacts: [KnownContact]? = nil,
        hasLiveSession: Bool = false,
        registry: RemoteSessionRegistry = RemoteSessionRegistry(),
        recorder: Recorder
    ) -> RemoteInviteCoordinator {
        let saved = contacts ?? [KnownContact(publicKeyB64: peers.remoteKeyB64,
                                              username: "Dell",
                                              lastIP: "10.0.0.9")]
        let env = RemoteInviteCoordinator.Environment(
            send: { frame, ip in
                recorder.sent.append((frame, ip))
                // Snapshot the window state at the moment an accept is written.
                if let obj = try? JSONSerialization.jsonObject(with: frame.dropFirst(4))
                    as? [String: Any],
                   obj["type"] as? String == "remote_accept",
                   let id = obj["session_id"] as? String {
                    recorder.windowOpenWhenAccepted[id] = registry.hasWindow(sessionID: id)
                }
            },
            attachOutbound: { ip, _ in
                recorder.attachCalls += 1
                recorder.attachAddresses.append(ip)
                return -1      // no real socket in a unit test
            },
            ownPublicKeyB64: { peers.localKeyB64 },
            ownUsername: { "Mac" },
            privateKey: { peers.localPrivate },
            mode: { mode },
            contacts: { saved },
            hasLiveSession: { hasLiveSession },
            registry: { registry },
            presentConsent: { request, onOutcome in
                recorder.prompts.append(request)
                onOutcome(recorder.consentAnswer)
            },
            startViewing: { name, ip, _ in recorder.viewingStarts.append((name, ip)) },
            armHosting: { id, name, ip in recorder.hostingArmed.append((id, name, ip)) })
        let coordinator = RemoteInviteCoordinator(environment: env)
        coordinator.onStateChange = { recorder.statuses.append($0) }
        return coordinator
    }

    /// A `remote_invite` as the Dell would actually send one — sealed with the
    /// real session key, so the coordinator has to genuinely decrypt it.
    private func makeInvite(peers: Peers,
                            sessionID: String = RemoteSessionCrypto.newSessionID(),
                            params: [String: Any] = ["protocol": 1, "video": "h264"],
                            senderKeyB64: String? = nil) throws -> RemoteSessionPacket {
        let ephemeral = Curve25519.KeyAgreement.PrivateKey()
        let body: [String: Any] = [
            "eph_pub_b64": ephemeral.publicKey.rawRepresentation.base64EncodedString(),
            "params": params,
        ]
        let plaintext = try JSONSerialization.data(withJSONObject: body, options: [.sortedKeys])
        let sealed = try SessionCrypto.encryptForPeer(
            myPrivate: peers.remotePrivate,
            peerPublicKeyB64: peers.localKeyB64,
            plaintext: plaintext,
            aad: Data(sessionID.utf8))

        return RemoteSessionPacket(type: "remote_invite",
                                   sessionId: sessionID,
                                   sender: "Dell",
                                   senderPublicKeyB64: senderKeyB64 ?? peers.remoteKeyB64,
                                   port: 54232,
                                   nonce: sealed.nonceB64,
                                   ciphertext: sealed.ciphertextB64)
    }

    // MARK: - The gate

    func testAnInviteFromAStrangerProducesNoPromptAndNoReply() throws {
        let peers = Peers()
        let recorder = Recorder()
        // No saved contacts at all.
        let coordinator = makeCoordinator(peers: peers, contacts: [], recorder: recorder)

        coordinator.handleInvite(try makeInvite(peers: peers), from: "10.0.0.9")

        XCTAssertTrue(recorder.prompts.isEmpty,
                      "a stranger must not be able to put a dialog on the screen")
        // Silence is the point. A decline would confirm this address is running
        // the app, which is exactly what an unsolicited invite is probing for.
        XCTAssertTrue(recorder.sent.isEmpty, "a stranger must not get an answer either")
    }

    func testAnInviteIsDeclinedAsDisabledWhenTheFeatureIsOff() throws {
        let peers = Peers()
        let recorder = Recorder()
        let coordinator = makeCoordinator(peers: peers, mode: .off, recorder: recorder)

        coordinator.handleInvite(try makeInvite(peers: peers), from: "10.0.0.9")

        XCTAssertTrue(recorder.prompts.isEmpty)
        XCTAssertEqual(recorder.decodedTypes(), ["remote_decline"])
        // `disabled` rather than `declined`, deliberately: somebody who switched
        // the feature off wants their peer told that, not left guessing whether
        // they were personally refused.
        XCTAssertEqual(recorder.decodedReasons(), ["disabled"])
    }

    func testAnInviteDuringALiveSessionIsDeclinedAsBusy() throws {
        let peers = Peers()
        let recorder = Recorder()
        let coordinator = makeCoordinator(peers: peers, hasLiveSession: true, recorder: recorder)

        coordinator.handleInvite(try makeInvite(peers: peers), from: "10.0.0.9")

        XCTAssertTrue(recorder.prompts.isEmpty)
        XCTAssertEqual(recorder.decodedReasons(), ["busy"])
    }

    func testAnInviteWhoseSealedBodyDoesNotOpenIsDroppedSilently() throws {
        let peers = Peers()
        let recorder = Recorder()
        let coordinator = makeCoordinator(peers: peers, recorder: recorder)

        // Right sender, right session id, ciphertext that is not ours.
        var packet = try makeInvite(peers: peers)
        packet = RemoteSessionPacket(type: packet.type,
                                     sessionId: packet.sessionId,
                                     sender: packet.sender,
                                     senderPublicKeyB64: packet.senderPublicKeyB64,
                                     port: packet.port,
                                     nonce: packet.nonce,
                                     ciphertext: Data(repeating: 7, count: 48)
                                        .base64EncodedString())

        coordinator.handleInvite(packet, from: "10.0.0.9")

        XCTAssertTrue(recorder.prompts.isEmpty)
        XCTAssertTrue(recorder.sent.isEmpty,
                      "a body that does not open is not a peer we should answer")
    }

    // MARK: - Consent

    func testDecliningSendsTheReasonTheUserChose() throws {
        let peers = Peers()
        let recorder = Recorder()
        recorder.consentAnswer = .declined(.timeout)
        let coordinator = makeCoordinator(peers: peers, recorder: recorder)

        coordinator.handleInvite(try makeInvite(peers: peers), from: "10.0.0.9")

        XCTAssertEqual(recorder.prompts.count, 1)
        XCTAssertEqual(recorder.prompts.first?.kind, .viewing,
                       "the first prompt is always viewing; control is a later, separate grant")
        XCTAssertEqual(recorder.decodedTypes(), ["remote_decline"])
        XCTAssertEqual(recorder.decodedReasons(), ["timeout"])
    }

    func testAcceptingOpensTheWindowBeforeTheAcceptIsSent() throws {
        let peers = Peers()
        let recorder = Recorder()
        recorder.consentAnswer = .accepted
        let registry = RemoteSessionRegistry()
        let coordinator = makeCoordinator(peers: peers, registry: registry, recorder: recorder)

        let invite = try makeInvite(peers: peers)
        coordinator.handleInvite(invite, from: "10.0.0.9")

        XCTAssertEqual(recorder.decodedTypes(), ["remote_accept"])
        // The race: a peer can attach the instant it reads the accept, and a
        // media_attach with no matching window is dropped and the connection
        // closed. Opening the window afterwards would fail only on a fast
        // network, which is the worst kind of intermittent.
        XCTAssertEqual(recorder.windowOpenWhenAccepted[invite.sessionId], true,
                       "the accept window must exist before the accept goes out")
    }

    func testAcceptingArmsHostingButDoesNotStartCapturing() throws {
        let peers = Peers()
        let recorder = Recorder()
        recorder.consentAnswer = .accepted
        let coordinator = makeCoordinator(peers: peers, recorder: recorder)

        let invite = try makeInvite(peers: peers)
        coordinator.handleInvite(invite, from: "10.0.0.9")

        // Agreeing is not sharing. Capture begins when the viewer attaches, so
        // a peer that changes its mind never causes this screen to be read.
        XCTAssertEqual(recorder.hostingArmed.count, 1)
        XCTAssertEqual(recorder.hostingArmed.first?.0, invite.sessionId)
        XCTAssertTrue(recorder.viewingStarts.isEmpty)
    }

    // MARK: - Initiator

    func testAnAcceptForAnUnknownSessionIsIgnored() {
        let peers = Peers()
        let recorder = Recorder()
        let coordinator = makeCoordinator(peers: peers, recorder: recorder)

        let stray = RemoteSessionPacket(type: "remote_accept",
                                        sessionId: RemoteSessionCrypto.newSessionID(),
                                        sender: "Dell",
                                        senderPublicKeyB64: peers.remoteKeyB64,
                                        port: 54232,
                                        nonce: "", ciphertext: "")
        coordinator.handleAccept(stray, from: "10.0.0.9")

        XCTAssertEqual(recorder.attachCalls, 0, "we must not dial a session we never asked for")
        XCTAssertTrue(recorder.sent.isEmpty)
    }

    func testInvitingTwiceDoesNotSendASecondInvite() {
        let peers = Peers()
        let recorder = Recorder()
        let coordinator = makeCoordinator(peers: peers, recorder: recorder)

        coordinator.invite(peerKey: peers.remoteKeyB64, peerIP: "10.0.0.9", peerName: "Dell")
        coordinator.invite(peerKey: peers.remoteKeyB64, peerIP: "10.0.0.9", peerName: "Dell")

        XCTAssertEqual(recorder.decodedTypes(), ["remote_invite"])
        XCTAssertTrue(coordinator.hasInviteInFlight)
    }

    // MARK: - Addressed by identity, not by a remembered IP

    /// The sent frames aimed at `ip`, by packet type.
    private func types(sentTo ip: String, in recorder: Recorder) -> [String] {
        recorder.sent.compactMap { entry in
            guard entry.ip == ip, entry.frame.count > 4,
                  let obj = try? JSONSerialization.jsonObject(with: entry.frame.dropFirst(4))
                    as? [String: Any] else { return nil }
            return obj["type"] as? String
        }
    }

    func testInvitingSomebodyElseSupersedesThePendingInvite() {
        // The bug: one invite outstanding refused every other for up to a
        // minute, silently. Invite Ari by mistake — or have an invite aimed at
        // an address that now belongs to Ari — and the Dell's button did
        // nothing, nine clicks in a row, with only `invite_blocked` in a log.
        let peers = Peers()
        let ari = Curve25519.KeyAgreement.PrivateKey().publicKey
            .rawRepresentation.base64EncodedString()
        let recorder = Recorder()
        let coordinator = makeCoordinator(
            peers: peers,
            contacts: [KnownContact(publicKeyB64: peers.remoteKeyB64, username: "Dell", lastIP: "192.168.68.27"),
                       KnownContact(publicKeyB64: ari, username: "Ari", lastIP: "192.168.68.31")],
            recorder: recorder)

        coordinator.invite(peerKey: ari, peerIP: "192.168.68.31", peerName: "Ari")
        let first = coordinator.pendingSessionID
        coordinator.invite(peerKey: peers.remoteKeyB64, peerIP: "192.168.68.27", peerName: "Dell")

        XCTAssertEqual(types(sentTo: "192.168.68.27", in: recorder), ["remote_invite"],
                       "the Dell's invite was refused behind Ari's")
        XCTAssertNotEqual(coordinator.pendingSessionID, first)
        // Withdrawn, not abandoned: a prompt that did reach Ari must close.
        XCTAssertEqual(types(sentTo: "192.168.68.31", in: recorder),
                       ["remote_invite", "remote_end"])
        XCTAssertEqual(recorder.statuses.last, "Waiting for Dell to accept…")
    }

    func testTheSamePeerAtANewAddressSupersedesTooBecauseTheOldOneCannotArrive() {
        // DHCP moved the peer between two clicks. The pending invite went to an
        // address that reaches nobody — or somebody else — so keeping it and
        // refusing the new one would wait a full minute for an answer that is
        // not coming.
        let peers = Peers()
        let recorder = Recorder()
        let coordinator = makeCoordinator(peers: peers, recorder: recorder)

        coordinator.invite(peerKey: peers.remoteKeyB64, peerIP: "192.168.68.31", peerName: "Dell")
        coordinator.invite(peerKey: peers.remoteKeyB64, peerIP: "192.168.68.27", peerName: "Dell")

        XCTAssertEqual(types(sentTo: "192.168.68.27", in: recorder), ["remote_invite"])
        XCTAssertEqual(types(sentTo: "192.168.68.31", in: recorder),
                       ["remote_invite", "remote_end"])
    }

    func testARepeatedClickSaysItIsStillWaitingRatherThanNothing() {
        // Same device, same address: genuinely still asking. No second invite —
        // but the status is said again, because a second click is a user who
        // cannot tell whether the first one did anything.
        let peers = Peers()
        let recorder = Recorder()
        let coordinator = makeCoordinator(peers: peers, recorder: recorder)

        coordinator.invite(peerKey: peers.remoteKeyB64, peerIP: "10.0.0.9", peerName: "Dell")
        recorder.statuses.removeAll()
        coordinator.invite(peerKey: peers.remoteKeyB64, peerIP: "10.0.0.9", peerName: "Dell")

        XCTAssertEqual(recorder.decodedTypes(), ["remote_invite"])
        XCTAssertEqual(recorder.statuses, ["Waiting for Dell to accept…"])
    }

    func testTheMediaChannelDialsWhereTheAuthenticatedAcceptCameFrom() throws {
        // The accept only opens under the peer's identity key, so its source
        // address is proof of where that device is now. The invite's own
        // address is older by a round trip and a human decision, and DHCP may
        // have handed it on in the meantime.
        let peers = Peers()
        let recorder = Recorder()
        let coordinator = makeCoordinator(peers: peers, recorder: recorder)

        coordinator.invite(peerKey: peers.remoteKeyB64, peerIP: "192.168.68.31", peerName: "Dell")
        let sessionID = try XCTUnwrap(coordinator.pendingSessionID)

        let sealed = try makeInvite(peers: peers, sessionID: sessionID)
        let accept = RemoteSessionPacket(type: "remote_accept",
                                         sessionId: sessionID,
                                         sender: "Dell",
                                         senderPublicKeyB64: peers.remoteKeyB64,
                                         port: 54232,
                                         nonce: sealed.nonce,
                                         ciphertext: sealed.ciphertext)
        coordinator.handleAccept(accept, from: "192.168.68.27")

        XCTAssertEqual(recorder.attachAddresses, ["192.168.68.27"])
    }

    func testAnAcceptThatDoesNotOpenDialsNothingWhateverItsAddress() throws {
        // The other half of trusting the source address: it is only trusted
        // after the body opens. A forged accept from a third machine must not
        // make us dial it.
        let peers = Peers()
        let recorder = Recorder()
        let coordinator = makeCoordinator(peers: peers, recorder: recorder)

        coordinator.invite(peerKey: peers.remoteKeyB64, peerIP: "192.168.68.27", peerName: "Dell")
        let sessionID = try XCTUnwrap(coordinator.pendingSessionID)

        let forged = RemoteSessionPacket(type: "remote_accept",
                                         sessionId: sessionID,
                                         sender: "Dell",
                                         senderPublicKeyB64: peers.remoteKeyB64,
                                         port: 54232,
                                         nonce: "AAAAAAAAAAAAAAAA",
                                         ciphertext: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")
        coordinator.handleAccept(forged, from: "192.168.68.66")

        XCTAssertTrue(recorder.attachAddresses.isEmpty)
    }

    func testADeclineForAnotherSessionDoesNotCancelOurs() {
        let peers = Peers()
        let recorder = Recorder()
        let coordinator = makeCoordinator(peers: peers, recorder: recorder)

        coordinator.invite(peerKey: peers.remoteKeyB64, peerIP: "10.0.0.9", peerName: "Dell")
        let ours = coordinator.pendingSessionID

        coordinator.handleDecline(RemoteControlPacket(type: "remote_decline",
                                                      sessionId: RemoteSessionCrypto.newSessionID(),
                                                      sender: "Someone",
                                                      senderPublicKeyB64: peers.remoteKeyB64,
                                                      port: 54232,
                                                      reason: "declined"),
                                  from: "10.0.0.9")

        XCTAssertEqual(coordinator.pendingSessionID, ours,
                       "a decline naming a different session must not tear ours down")
    }

    func testAnUnknownDeclineReasonStillProducesASentence() {
        // The spec requires tolerating tokens we have not learned yet, and a
        // newer peer saying something sensible must not surface as raw text.
        let message = RemoteInviteCoordinator.declineMessage(
            RemoteDeclineReason(rawValue: "some_future_token") ?? .declined,
            peerName: "Dell")
        XCTAssertEqual(message, "Dell declined.")
    }

    func testEveryDeclineReasonHasItsOwnWording() {
        var seen = Set<String>()
        for reason in RemoteDeclineReason.allCases {
            let message = RemoteInviteCoordinator.declineMessage(reason, peerName: "Dell")
            XCTAssertTrue(message.contains("Dell"), "\(reason) should name the peer")
            XCTAssertTrue(seen.insert(message).inserted,
                          "\(reason) reuses another reason's wording")
        }
    }

    // MARK: - Sealed body

    func testTheSealedBodyRoundTripsWithItsParameters() throws {
        let peers = Peers()
        let recorder = Recorder()
        let coordinator = makeCoordinator(peers: peers, recorder: recorder)

        let packet = try makeInvite(peers: peers,
                                    params: ["protocol": 1, "video": "h264", "displays": 2])
        let body = try coordinator.openSealedBody(packet, peerKey: peers.remoteKeyB64)

        XCTAssertEqual(Data(base64Encoded: body.ephemeralPublicKeyB64)?.count, 32)
        XCTAssertEqual(body.params["protocol"], .int(1))
        XCTAssertEqual(body.params["video"], .string("h264"))
        XCTAssertEqual(body.params["displays"], .int(2))
    }

    func testAParameterWeCannotRepresentFailsTheHandshake() throws {
        let peers = Peers()
        let recorder = Recorder()
        let coordinator = makeCoordinator(peers: peers, recorder: recorder)

        // The peer hashed this into their transcript. Silently dropping it here
        // would derive different keys on each side, and the failure would land
        // later as an unexplained tag mismatch on every media frame.
        let packet = try makeInvite(peers: peers,
                                    params: ["protocol": 1, "nested": ["a": 1]])

        XCTAssertThrowsError(try coordinator.openSealedBody(packet,
                                                            peerKey: peers.remoteKeyB64))
    }

    func testABodySealedForSomebodyElseDoesNotOpen() throws {
        let peers = Peers()
        let other = Curve25519.KeyAgreement.PrivateKey()
        let recorder = Recorder()
        let coordinator = makeCoordinator(peers: peers, recorder: recorder)

        let sessionID = RemoteSessionCrypto.newSessionID()
        let ephemeral = Curve25519.KeyAgreement.PrivateKey()
        let plaintext = try JSONSerialization.data(withJSONObject: [
            "eph_pub_b64": ephemeral.publicKey.rawRepresentation.base64EncodedString(),
            "params": ["protocol": 1],
        ])
        let sealed = try SessionCrypto.encryptForPeer(
            myPrivate: peers.remotePrivate,
            peerPublicKeyB64: other.publicKey.rawRepresentation.base64EncodedString(),
            plaintext: plaintext,
            aad: Data(sessionID.utf8))

        let packet = RemoteSessionPacket(type: "remote_invite", sessionId: sessionID,
                                         sender: "Dell",
                                         senderPublicKeyB64: peers.remoteKeyB64,
                                         port: 54232,
                                         nonce: sealed.nonceB64,
                                         ciphertext: sealed.ciphertextB64)

        XCTAssertThrowsError(try coordinator.openSealedBody(packet,
                                                            peerKey: peers.remoteKeyB64))
    }

    func testTheSessionIdIsTheAssociatedData() throws {
        let peers = Peers()
        let recorder = Recorder()
        let coordinator = makeCoordinator(peers: peers, recorder: recorder)

        // Same ciphertext, different session id. Without the AAD binding, an
        // invite could be replayed under a fresh session id and the handshake
        // would proceed against a transcript neither side agreed to.
        let original = try makeInvite(peers: peers)
        let replayed = RemoteSessionPacket(type: original.type,
                                           sessionId: RemoteSessionCrypto.newSessionID(),
                                           sender: original.sender,
                                           senderPublicKeyB64: original.senderPublicKeyB64,
                                           port: original.port,
                                           nonce: original.nonce,
                                           ciphertext: original.ciphertext)

        XCTAssertNoThrow(try coordinator.openSealedBody(original,
                                                        peerKey: peers.remoteKeyB64))
        XCTAssertThrowsError(try coordinator.openSealedBody(replayed,
                                                            peerKey: peers.remoteKeyB64))
    }
}
