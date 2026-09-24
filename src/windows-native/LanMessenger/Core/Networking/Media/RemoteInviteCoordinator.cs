using LanMessenger.Core.Crypto;
using LanMessenger.Core.Protocol;
using LanMessenger.Core.Services;
using NSec.Cryptography;
using System.Text;
using System.Text.Json;

namespace LanMessenger.Core.Networking.Media;

// The exchange that gets two machines to agree to share a screen.
// Mirror of RemoteInviteCoordinator.swift, and identical on the wire.
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
// the answer arrives, and be discarded if it never does. Nothing else in the app
// has anywhere to keep them: RemoteSessionRegistry holds accept windows for
// sessions already agreed, and RemoteDesktopSession only exists once there is
// something to show.
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
// Every outside dependency arrives as a delegate rather than a singleton, for
// the same reason RemoteDesktopPolicy takes a context object: everything
// interesting here is a refusal, a race or a timeout, and none of those are
// reachable in a test that needs a live network stack and a real screen.

/// <summary>The encrypted half of an invite or an accept.</summary>
public sealed record RemoteSealedBody(string EphemeralPublicKeyB64, RemoteHandshakeParams Params);

/// <summary>What the coordinator needs from the rest of the app.</summary>
public sealed class RemoteInviteEnvironment
{
    /// Writes one JSON frame to a peer, one-shot.
    public required Action<byte[], string> Send { get; init; }

    /// Connects, writes media_attach, and hands back the still-open connection —
    /// or null. It speaks binary media framing from that point on.
    public required Func<string, byte[], object?> AttachOutbound { get; init; }

    public required Func<string> OwnPublicKeyB64 { get; init; }
    public required Func<string> OwnUsername { get; init; }
    public required Func<Key> PrivateKey { get; init; }
    public required Func<RemoteDesktopMode> Mode { get; init; }
    public required Func<IReadOnlyList<KnownContact>> Contacts { get; init; }

    /// Whether a session is already live locally, whichever side started it.
    public required Func<bool> HasLiveSession { get; init; }
    public required Func<RemoteSessionRegistry> Registry { get; init; }

    /// Raise the consent prompt. Must call back exactly once.
    public required Action<RemoteConsentRequest, Action<RemoteConsentOutcome>> PresentConsent { get; init; }

    /// The peer accepted and the media channel is up: show their screen.
    /// The object is the connection returned by AttachOutbound.
    public required Action<string, string, object> StartViewing { get; init; }

    /// We accepted: be ready to share this screen when they attach.
    public required Action<string, string, string> ArmHosting { get; init; }

    public Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;
}

public sealed class RemoteInviteCoordinator
{
    /// <summary>How long an initiator waits for any answer at all.</summary>
    /// <remarks>
    /// Longer than the responder's 45s consent countdown on purpose: the peer
    /// declines on its own at 45s and tells us, and this only has to cover the
    /// case where that message never arrives. Expiring first would turn a
    /// perfectly normal slow decision into a reported network failure.
    /// </remarks>
    public const int InviteTimeoutSeconds = 60;

    private sealed class PendingInvite
    {
        public required string SessionId { get; init; }
        public required string PeerKey { get; init; }
        public required string PeerIP { get; init; }
        public required string PeerName { get; init; }
        public required Key Ephemeral { get; init; }
        public CancellationTokenSource? Timer { get; set; }
    }

    private readonly RemoteInviteEnvironment _env;
    private readonly object _gate = new();
    private PendingInvite? _pending;

    /// Progress for the interface. Empty string means "nothing in flight".
    public Action<string>? OnStateChange { get; set; }

    public RemoteInviteCoordinator(RemoteInviteEnvironment environment) => _env = environment;

    public bool HasInviteInFlight { get { lock (_gate) { return _pending is not null; } } }
    public string? PendingSessionId { get { lock (_gate) { return _pending?.SessionId; } } }

    // ---- Initiator ---------------------------------------------------------

    /// <summary>Asks a peer to share their screen.</summary>
    /// <remarks>
    /// <para>
    /// <paramref name="peerIP"/> must be where the peer IS, resolved from its
    /// identity key at the moment of the click — never an address a
    /// conversation remembered. LAN addresses are recycled between machines by
    /// DHCP: the Dell's address on the day this was written had previously
    /// belonged to Ari, and a remembered one sent an invite to Ari's screen.
    /// </para>
    /// <para>
    /// <b>A new request supersedes a pending one</b> unless it is the same peer
    /// at the same address. This used to refuse everything while any invite was
    /// outstanding — for up to a minute, with only a log line to say so — so one
    /// click in the wrong conversation, or one invite aimed at a stale address,
    /// made the button do nothing at all for everyone else.
    /// </para>
    /// </remarks>
    public void Invite(string peerKey, string peerIP, string peerName)
    {
        PendingInvite? current;
        lock (_gate) { current = _pending; }
        if (current is not null)
        {
            if (current.PeerKey == peerKey && current.PeerIP == peerIP)
            {
                // Genuinely already asking this device at this address. Say so
                // again rather than nothing: a repeated click is a user who
                // cannot tell whether the first one worked.
                LanLogger.Remote("invite_already_pending", peer: peerIP,
                                 sessionId: current.SessionId);
                OnStateChange?.Invoke($"Waiting for {current.PeerName} to accept…");
                return;
            }
            // A different device, or the same device at a new address — either
            // way the outstanding invite is not wanted or cannot arrive.
            // Withdrawn properly, so a prompt that did reach somebody closes.
            LanLogger.Remote("invite_superseded", peer: current.PeerIP,
                             sessionId: current.SessionId,
                             reason: current.PeerKey == peerKey ? "peer moved" : "different peer");
            CancelInvite();
        }

        string sessionId = RemoteSessionCrypto.NewSessionId();
        var ephemeral = Key.Create(KeyAgreementAlgorithm.X25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        // Ours to state, theirs to answer. Canonicalised into the handshake
        // transcript, so a peer that rewrites a parameter in flight produces
        // different keys on each side — the tamper surfaces as a failure to
        // connect rather than as a silent downgrade.
        var parameters = new RemoteHandshakeParams();
        parameters["protocol"] = RemoteParamValue.Int(1);
        parameters["video"] = RemoteParamValue.String("h264");

        byte[]? frame = SealedFrame("remote_invite", sessionId, ephemeral, parameters, peerKey);
        if (frame is null)
        {
            LanLogger.Remote("error", peer: peerIP, sessionId: sessionId,
                             reason: "could not seal the invite");
            ephemeral.Dispose();
            return;
        }

        var timer = new CancellationTokenSource();
        lock (_gate)
        {
            _pending = new PendingInvite
            {
                SessionId = sessionId, PeerKey = peerKey, PeerIP = peerIP,
                PeerName = peerName, Ephemeral = ephemeral, Timer = timer,
            };
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(InviteTimeoutSeconds), timer.Token);
                InviteTimedOut(sessionId);
            }
            catch (TaskCanceledException) { /* answered in time */ }
        });

        _env.Send(frame, peerIP);
        LanLogger.Remote("invite_sent", peer: peerIP, sessionId: sessionId);
        OnStateChange?.Invoke($"Waiting for {peerName} to accept…");
    }

    /// <summary>The peer agreed. Derive the keys, then connect and upgrade.</summary>
    public void HandleAccept(RemoteSessionPacket packet, string ip)
    {
        PendingInvite invite;
        lock (_gate)
        {
            if (_pending is null || _pending.SessionId != packet.SessionId)
            {
                // An accept for a session we are not waiting on. Nothing to tear
                // down, and answering would confirm to an unknown sender that we
                // are here.
                LanLogger.Remote("accept_ignored", peer: ip, sessionId: packet.SessionId,
                                 reason: "no invite in flight for this session");
                return;
            }
            invite = _pending;
        }
        ClearPending();

        try
        {
            var body = OpenSealedBody(packet, invite.PeerKey);
            var keys = RemoteSessionCrypto.DeriveKeys(
                RemoteSessionRole.Initiator,
                invite.Ephemeral,
                _env.PrivateKey(),
                body.EphemeralPublicKeyB64,
                invite.PeerKey,
                invite.SessionId,
                // Their parameters, not ours. They answer with what they
                // actually agreed to, and the transcript binds that answer.
                body.Params);

            // Attach to where the answer came FROM. It opened under this peer's
            // identity key, so its source address is proof of where that
            // device is right now — fresher than the address the invite went
            // to, which DHCP may have handed to someone else since.
            AttachAndView(invite, keys, ip);
        }
        catch (Exception ex)
        {
            LanLogger.Remote("error", peer: ip, sessionId: packet.SessionId,
                             reason: $"accept handling failed: {ex.Message}");
            OnStateChange?.Invoke("Could not start the session.");
            SendEnd(packet.SessionId, ip, RemoteStopReason.Error);
        }
        finally
        {
            invite.Ephemeral.Dispose();
        }
    }

    /// <summary>
    /// Connects, upgrades the socket and opens the viewer, at
    /// <paramref name="address"/> — the authenticated source of the accept, not
    /// the address the invite used.
    /// </summary>
    private void AttachAndView(PendingInvite invite, RemoteSessionKeys keys, string address)
    {
        var attach = new RemoteControlPacket
        {
            Type = "media_attach",
            SessionId = invite.SessionId,
            Sender = _env.OwnUsername(),
            SenderPublicKeyB64 = _env.OwnPublicKeyB64(),
            Port = 54232,
        };
        byte[] frame = FrameCodec.Encode(attach);

        object? connection = _env.AttachOutbound(address, frame);
        if (connection is null)
        {
            OnStateChange?.Invoke($"Could not reach {invite.PeerName}.");
            return;
        }

        LanLogger.Remote("attach_sent", peer: address, sessionId: invite.SessionId);
        OnStateChange?.Invoke($"Connected to {invite.PeerName}.");

        // The caller owns the connection from here: it builds the MediaSession
        // with these keys and opens the viewer window. Kept out of this object
        // on purpose — it is the one step that needs a UI thread, and this
        // class stays testable by not having one.
        _env.StartViewing(invite.PeerName, address, new RemoteAttachedChannel(
            invite.SessionId, invite.PeerKey, address, keys, connection));
    }

    /// <summary>The peer refused, or could not.</summary>
    public void HandleDecline(RemoteControlPacket packet, string ip)
    {
        PendingInvite invite;
        lock (_gate)
        {
            if (_pending is null || _pending.SessionId != packet.SessionId) return;
            invite = _pending;
        }
        ClearPending();
        invite.Ephemeral.Dispose();

        // An unknown token is a newer peer saying something sensible we have not
        // learned yet. The spec requires tolerating it, so it gets the generic
        // sentence rather than its raw text.
        var reason = RemoteDeclineReasonExtensions.Parse(packet.Reason ?? "")
                     ?? RemoteDeclineReason.Declined;
        OnStateChange?.Invoke(DeclineMessage(reason, invite.PeerName));
        LanLogger.Remote("declined", peer: ip, sessionId: packet.SessionId,
                         reason: packet.Reason ?? "declined");
    }

    public static string DeclineMessage(RemoteDeclineReason reason, string peerName) => reason switch
    {
        RemoteDeclineReason.Declined    => $"{peerName} declined.",
        RemoteDeclineReason.Busy        => $"{peerName} is already in a session.",
        RemoteDeclineReason.Unsupported => $"{peerName}'s version does not support screen sharing.",
        RemoteDeclineReason.Disabled    => $"{peerName} has screen sharing switched off.",
        RemoteDeclineReason.NoEncoder   => $"{peerName}'s machine has no video encoder available.",
        RemoteDeclineReason.Timeout     => $"{peerName} did not answer.",
        _                               => $"{peerName} declined.",
    };

    private void InviteTimedOut(string sessionId)
    {
        PendingInvite invite;
        lock (_gate)
        {
            if (_pending is null || _pending.SessionId != sessionId) return;
            invite = _pending;
        }
        ClearPending();
        invite.Ephemeral.Dispose();

        LanLogger.Remote("invite_timeout", peer: invite.PeerIP, sessionId: sessionId);
        OnStateChange?.Invoke($"{invite.PeerName} did not answer.");
        SendEnd(sessionId, invite.PeerIP, RemoteStopReason.Watchdog);
    }

    /// <summary>Withdraws an invite the user gave up on.</summary>
    public void CancelInvite()
    {
        PendingInvite invite;
        lock (_gate)
        {
            if (_pending is null) return;
            invite = _pending;
        }
        ClearPending();
        invite.Ephemeral.Dispose();
        SendEnd(invite.SessionId, invite.PeerIP, RemoteStopReason.UserStopped);
        OnStateChange?.Invoke("");
    }

    private void ClearPending()
    {
        lock (_gate)
        {
            _pending?.Timer?.Cancel();
            _pending?.Timer?.Dispose();
            _pending = null;
        }
    }

    // ---- Responder ---------------------------------------------------------

    /// <summary>An invite arrived. Judge it, then ask the user.</summary>
    public void HandleInvite(RemoteSessionPacket packet, string ip)
    {
        string sessionId = packet.SessionId;
        string peerKey = packet.SenderPublicKeyB64;

        // The gate first, and before the prompt. A peer who is not a saved
        // contact must produce no interface at all: an unsolicited dialog from a
        // stranger is itself the attack, whatever the user then clicks.
        var decision = RemoteDesktopPolicy.Decide(new RemoteInviteContext
        {
            PeerPublicKeyB64 = peerKey,
            PeerIP = ip,
            OwnPublicKeyB64 = _env.OwnPublicKeyB64(),
            Mode = _env.Mode(),
            Contacts = _env.Contacts(),
            HasSessionInFlight = HasInviteInFlight
                                 || _env.Registry().HasWindow(sessionId)
                                 || _env.HasLiveSession(),
        });

        switch (decision.Action)
        {
            case RemoteInviteAction.Ignore:
                LanLogger.Remote("invite_dropped", peer: ip, sessionId: sessionId,
                                 reason: decision.IgnoreReason.ToString());
                return;
            case RemoteInviteAction.Decline:
                LanLogger.Remote("invite_refused", peer: ip, sessionId: sessionId,
                                 reason: decision.DeclineReason.ToToken());
                SendDecline(sessionId, ip, decision.DeclineReason);
                return;
        }

        // Only now does the peer get to cost us an X25519 agreement or a dialog.
        RemoteSealedBody body;
        try
        {
            body = OpenSealedBody(packet, peerKey);
        }
        catch (Exception ex)
        {
            LanLogger.Remote("invite_dropped", peer: ip, sessionId: sessionId,
                             reason: $"sealed body did not open: {ex.Message}");
            return;
        }

        var request = new RemoteConsentRequest(
            sessionId, RemoteConsentKind.Viewing, packet.Sender, ip, peerKey,
            decision.Trust,
            _env.Now().AddSeconds(RemoteConsentRequest.DefaultTimeoutSeconds));

        _env.PresentConsent(request, outcome => FinishConsent(outcome, packet, body, ip));
    }

    private void FinishConsent(RemoteConsentOutcome outcome, RemoteSessionPacket packet,
                               RemoteSealedBody body, string ip)
    {
        string sessionId = packet.SessionId;
        string peerKey = packet.SenderPublicKeyB64;

        if (outcome.Kind != RemoteConsentOutcomeKind.Accepted)
        {
            SendDecline(sessionId, ip, outcome.Reason);
            return;
        }

        Key? ephemeral = null;
        try
        {
            ephemeral = Key.Create(KeyAgreementAlgorithm.X25519,
                new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

            // What we actually agreed to, which is what gets sealed back and
            // what both sides hash into the transcript. Echoing their params
            // unchanged would make the field decorative; naming the display we
            // will share is the point of answering at all.
            var parameters = body.Params.Clone();
            parameters["display"] = RemoteParamValue.Int(0);

            var keys = RemoteSessionCrypto.DeriveKeys(
                RemoteSessionRole.Responder,
                ephemeral,
                _env.PrivateKey(),
                body.EphemeralPublicKeyB64,
                peerKey,
                sessionId,
                parameters);

            // The window opens BEFORE the accept goes out. The peer may attach
            // the instant it reads our answer, and a media_attach arriving
            // against a window that does not exist yet is dropped and the
            // connection closed — a race that would present as an intermittent
            // failure to connect and would reproduce only on a fast network.
            var opened = _env.Registry().Open(sessionId, peerKey, ip,
                                              RemoteSessionRole.Responder, keys);
            if (opened.Kind != RemoteOpenKind.Opened)
            {
                SendDecline(sessionId, ip, RemoteDeclineReason.Busy);
                return;
            }

            byte[]? frame = SealedFrame("remote_accept", sessionId, ephemeral, parameters, peerKey);
            if (frame is null)
            {
                _env.Registry().Cancel(sessionId);
                SendDecline(sessionId, ip, RemoteDeclineReason.Declined);
                return;
            }

            _env.Send(frame, ip);
            LanLogger.Remote("accept_sent", peer: ip, sessionId: sessionId);

            // Capture does not start here. It starts when they attach, so a
            // viewer that never arrives never causes this screen to be read:
            // the window simply expires.
            _env.ArmHosting(sessionId, packet.Sender, ip);
        }
        catch (Exception ex)
        {
            LanLogger.Remote("error", peer: ip, sessionId: sessionId,
                             reason: $"accept failed: {ex.Message}");
            _env.Registry().Cancel(sessionId);
            SendDecline(sessionId, ip, RemoteDeclineReason.Declined);
        }
        finally
        {
            ephemeral?.Dispose();
        }
    }

    // ---- Outbound helpers --------------------------------------------------

    public void SendDecline(string sessionId, string ip, RemoteDeclineReason reason)
    {
        byte[]? frame = ControlFrame("remote_decline", sessionId, reason.ToToken());
        if (frame is null) return;
        _env.Send(frame, ip);
        LanLogger.Remote("decline_sent", peer: ip, sessionId: sessionId, reason: reason.ToToken());
    }

    public void SendEnd(string sessionId, string ip, RemoteStopReason reason)
    {
        byte[]? frame = ControlFrame("remote_end", sessionId, reason.ToToken());
        if (frame is null) return;
        _env.Send(frame, ip);
        LanLogger.Remote("end_sent", peer: ip, sessionId: sessionId, reason: reason.ToToken());
    }

    private byte[]? ControlFrame(string type, string sessionId, string reason)
    {
        try
        {
            return FrameCodec.Encode(new RemoteControlPacket
            {
                Type = type,
                SessionId = sessionId,
                Sender = _env.OwnUsername(),
                SenderPublicKeyB64 = _env.OwnPublicKeyB64(),
                Port = 54232,
                Reason = reason,
            });
        }
        catch { return null; }
    }

    // ---- Sealed body -------------------------------------------------------

    /// <summary>Builds a sealed remote_invite or remote_accept frame.</summary>
    /// <remarks>
    /// The ephemeral key is sealed rather than sent in the clear. That does not
    /// stop an attacker who cannot complete the triple DH anyway — it stops an
    /// unauthenticated peer making us do X25519 work, and it authenticates
    /// `params` for free.
    /// </remarks>
    private byte[]? SealedFrame(string type, string sessionId, Key ephemeral,
                                RemoteHandshakeParams parameters, string peerKey)
    {
        try
        {
            // Hand-built rather than serialized from a dictionary: key order and
            // escaping have to match the Swift side byte for byte, and neither
            // platform's serializer guarantees the other's.
            var body = new Dictionary<string, object>
            {
                ["eph_pub_b64"] = Convert.ToBase64String(
                    ephemeral.PublicKey.Export(KeyBlobFormat.RawPublicKey)),
                ["params"] = parameters.ToDictionary(),
            };

            byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(body,
                new JsonSerializerOptions { DictionaryKeyPolicy = null });

            var sealed_ = SessionCrypto.EncryptForPeer(
                _env.PrivateKey(), peerKey, plaintext, Encoding.UTF8.GetBytes(sessionId));

            return FrameCodec.EncodeDict(new Dictionary<string, object?>
            {
                ["type"] = type,
                ["session_id"] = sessionId,
                ["sender"] = _env.OwnUsername(),
                ["sender_public_key_b64"] = _env.OwnPublicKeyB64(),
                ["port"] = 54232,
                ["nonce"] = sealed_.NonceB64,
                ["ciphertext"] = sealed_.CiphertextB64,
            });
        }
        catch (Exception ex)
        {
            LanLogger.Remote("error", sessionId: sessionId,
                             reason: $"sealing {type} failed: {ex.Message}");
            return null;
        }
    }

    public RemoteSealedBody OpenSealedBody(RemoteSessionPacket packet, string peerKey)
    {
        byte[] plaintext = SessionCrypto.DecryptFromPeer(
            _env.PrivateKey(), peerKey, packet.Nonce, packet.Ciphertext,
            Encoding.UTF8.GetBytes(packet.SessionId));

        using var document = JsonDocument.Parse(plaintext);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("sealed body is not an object");

        if (!root.TryGetProperty("eph_pub_b64", out var ephElement)
            || ephElement.ValueKind != JsonValueKind.String
            || ephElement.GetString() is not { } eph
            || Convert.FromBase64String(eph).Length != 32)
        {
            throw new InvalidOperationException("sealed body has no usable ephemeral key");
        }

        var parameters = new RemoteHandshakeParams();
        if (root.TryGetProperty("params", out var paramsElement)
            && paramsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in paramsElement.EnumerateObject())
            {
                // A value we cannot represent must fail the handshake rather
                // than be dropped: the peer hashed it into their transcript
                // either way, so ignoring it here derives different keys on each
                // side and the failure lands later as an unexplained tag
                // mismatch on every media frame.
                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.Number when property.Value.TryGetInt64(out long i):
                        parameters[property.Name] = RemoteParamValue.Int(i);
                        break;
                    case JsonValueKind.String:
                        parameters[property.Name] = RemoteParamValue.String(property.Value.GetString()!);
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"parameter '{property.Name}' has a type this build cannot hash");
                }
            }
        }

        return new RemoteSealedBody(eph, parameters);
    }
}

/// <summary>
/// A media channel that has been opened and upgraded but not yet turned into a
/// session.
///
/// Carries the keys across the seam between the coordinator, which must stay
/// free of the UI thread, and the controller, which owns the window. Without it
/// the coordinator would have to build the MediaSession itself and would drag
/// WinUI in behind it.
/// </summary>
public sealed record RemoteAttachedChannel(
    string SessionId, string PeerPublicKeyB64, string PeerIP,
    RemoteSessionKeys Keys, object Connection);
