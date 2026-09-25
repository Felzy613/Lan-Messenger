using LanMessenger.Core.Crypto;
using LanMessenger.Core.Networking.Media;
using LanMessenger.Core.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace LanMessenger.Tests;

/// <summary>
/// The invite exchange, exercised through its injected environment. Mirror of
/// RemoteInviteCoordinatorTests.swift — the two suites assert the same
/// behaviours, because the two implementations have to agree on the wire.
///
/// Everything interesting here is a refusal, a race or a timeout, and none of
/// those are reachable in a test that needs two machines. What is asserted is
/// mostly what does NOT happen: no prompt for a stranger, no reply to a
/// stranger, no accept window left behind after a failure.
/// </summary>
[TestClass]
public class RemoteInviteCoordinatorTests
{
    // ---- Fixtures ----------------------------------------------------------

    /// <summary>
    /// Both ends of a conversation, so a packet sealed by one really opens on
    /// the other. Nothing here fakes the crypto — a test that did would prove
    /// only that the test agrees with itself.
    /// </summary>
    private sealed class Peers : IDisposable
    {
        public Key LocalPrivate { get; } = NewKey();
        public Key RemotePrivate { get; } = NewKey();

        public string LocalKeyB64 =>
            Convert.ToBase64String(LocalPrivate.PublicKey.Export(KeyBlobFormat.RawPublicKey));
        public string RemoteKeyB64 =>
            Convert.ToBase64String(RemotePrivate.PublicKey.Export(KeyBlobFormat.RawPublicKey));

        private static Key NewKey() => Key.Create(KeyAgreementAlgorithm.X25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        public void Dispose() { LocalPrivate.Dispose(); RemotePrivate.Dispose(); }
    }

    /// <summary>Everything the coordinator did, in order.</summary>
    private sealed class Recorder
    {
        public List<(byte[] Frame, string IP)> Sent { get; } = [];
        public List<RemoteConsentRequest> Prompts { get; } = [];
        public List<(string Name, string IP)> ViewingStarts { get; } = [];
        public List<(string SessionId, string Name, string IP)> HostingArmed { get; } = [];
        public int AttachCalls { get; set; }
        /// <summary>Where each media attach was dialled.</summary>
        public List<string> AttachAddresses { get; } = [];
        /// <summary>Everything the coordinator told the interface, in order.</summary>
        public List<string> Statuses { get; } = [];

        /// Set by the test to answer whatever prompt is raised.
        public RemoteConsentOutcome ConsentAnswer { get; set; } = RemoteConsentOutcome.Declined();

        /// Session ids that had an accept window open at the moment the accept
        /// frame was written. The race this exists to catch is subtle: a peer
        /// can attach the instant it reads the accept, so the window must
        /// already be open when that frame goes out.
        public Dictionary<string, bool> WindowOpenWhenAccepted { get; } = [];

        public List<string> DecodedTypes() => Decoded("type");
        public List<string> DecodedReasons() => Decoded("reason");

        private List<string> Decoded(string field)
        {
            var result = new List<string>();
            foreach (var (frame, _) in Sent)
            {
                if (frame.Length <= 4) continue;
                using var document = JsonDocument.Parse(frame.AsMemory(4));
                if (document.RootElement.TryGetProperty(field, out var value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    result.Add(value.GetString()!);
                }
            }
            return result;
        }
    }

    private static RemoteInviteCoordinator MakeCoordinator(
        Peers peers,
        Recorder recorder,
        RemoteDesktopMode mode = RemoteDesktopMode.On,
        IReadOnlyList<KnownContact>? contacts = null,
        bool hasLiveSession = false,
        RemoteSessionRegistry? registry = null)
    {
        registry ??= new RemoteSessionRegistry();
        var saved = contacts ?? [new KnownContact(peers.RemoteKeyB64, "Dell", "10.0.0.9")];

        var env = new RemoteInviteEnvironment
        {
            Send = (frame, ip) =>
            {
                recorder.Sent.Add((frame, ip));
                if (frame.Length > 4)
                {
                    using var document = JsonDocument.Parse(frame.AsMemory(4));
                    if (document.RootElement.TryGetProperty("type", out var type)
                        && type.GetString() == "remote_accept"
                        && document.RootElement.TryGetProperty("session_id", out var id))
                    {
                        recorder.WindowOpenWhenAccepted[id.GetString()!] =
                            registry.HasWindow(id.GetString()!);
                    }
                }
            },
            AttachOutbound = (ip, _) =>
            {
                recorder.AttachCalls++;
                recorder.AttachAddresses.Add(ip);
                return null;
            },
            OwnPublicKeyB64 = () => peers.LocalKeyB64,
            OwnUsername = () => "Dell",
            PrivateKey = () => peers.LocalPrivate,
            Mode = () => mode,
            Contacts = () => saved,
            HasLiveSession = () => hasLiveSession,
            Registry = () => registry,
            PresentConsent = (request, onOutcome) =>
            {
                recorder.Prompts.Add(request);
                onOutcome(recorder.ConsentAnswer);
            },
            StartViewing = (name, ip, _) => recorder.ViewingStarts.Add((name, ip)),
            ArmHosting = (id, name, ip) => recorder.HostingArmed.Add((id, name, ip)),
        };
        var coordinator = new RemoteInviteCoordinator(env);
        coordinator.OnStateChange = status => recorder.Statuses.Add(status);
        return coordinator;
    }

    /// <summary>A remote_invite as the Mac would actually send one.</summary>
    private static RemoteSessionPacket MakeInvite(
        Peers peers,
        string? sessionId = null,
        Dictionary<string, object>? parameters = null)
    {
        sessionId ??= RemoteSessionCrypto.NewSessionId();
        parameters ??= new Dictionary<string, object> { ["protocol"] = 1, ["video"] = "h264" };

        using var ephemeral = Key.Create(KeyAgreementAlgorithm.X25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        var body = new Dictionary<string, object>
        {
            ["eph_pub_b64"] = Convert.ToBase64String(
                ephemeral.PublicKey.Export(KeyBlobFormat.RawPublicKey)),
            ["params"] = parameters,
        };
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(body);
        var sealed_ = SessionCrypto.EncryptForPeer(
            peers.RemotePrivate, peers.LocalKeyB64, plaintext, Encoding.UTF8.GetBytes(sessionId));

        return new RemoteSessionPacket
        {
            Type = "remote_invite",
            SessionId = sessionId,
            Sender = "Mac",
            SenderPublicKeyB64 = peers.RemoteKeyB64,
            Port = 54232,
            Nonce = sealed_.NonceB64,
            Ciphertext = sealed_.CiphertextB64,
        };
    }

    // ---- The gate ----------------------------------------------------------

    [TestMethod]
    public void AnInviteFromAStrangerProducesNoPromptAndNoReply()
    {
        using var peers = new Peers();
        var recorder = new Recorder();
        var coordinator = MakeCoordinator(peers, recorder, contacts: []);

        coordinator.HandleInvite(MakeInvite(peers), "10.0.0.9");

        Assert.AreEqual(0, recorder.Prompts.Count,
            "a stranger must not be able to put a dialog on the screen");
        // Silence is the point. A decline would confirm this address is running
        // the app, which is exactly what an unsolicited invite is probing for.
        Assert.AreEqual(0, recorder.Sent.Count, "a stranger must not get an answer either");
    }

    [TestMethod]
    public void AnInviteIsDeclinedAsDisabledWhenTheFeatureIsOff()
    {
        using var peers = new Peers();
        var recorder = new Recorder();
        var coordinator = MakeCoordinator(peers, recorder, mode: RemoteDesktopMode.Off);

        coordinator.HandleInvite(MakeInvite(peers), "10.0.0.9");

        Assert.AreEqual(0, recorder.Prompts.Count);
        CollectionAssert.AreEqual(new[] { "remote_decline" }, recorder.DecodedTypes());
        // `disabled` rather than `declined`, deliberately: somebody who switched
        // the feature off wants their peer told that, not left guessing whether
        // they were personally refused.
        CollectionAssert.AreEqual(new[] { "disabled" }, recorder.DecodedReasons());
    }

    [TestMethod]
    public void AnInviteDuringALiveSessionIsDeclinedAsBusy()
    {
        using var peers = new Peers();
        var recorder = new Recorder();
        var coordinator = MakeCoordinator(peers, recorder, hasLiveSession: true);

        coordinator.HandleInvite(MakeInvite(peers), "10.0.0.9");

        Assert.AreEqual(0, recorder.Prompts.Count);
        CollectionAssert.AreEqual(new[] { "busy" }, recorder.DecodedReasons());
    }

    [TestMethod]
    public void AnInviteWhoseSealedBodyDoesNotOpenIsDroppedSilently()
    {
        using var peers = new Peers();
        var recorder = new Recorder();
        var coordinator = MakeCoordinator(peers, recorder);

        var packet = MakeInvite(peers);
        packet.Ciphertext = Convert.ToBase64String(new byte[48]);

        coordinator.HandleInvite(packet, "10.0.0.9");

        Assert.AreEqual(0, recorder.Prompts.Count);
        Assert.AreEqual(0, recorder.Sent.Count,
            "a body that does not open is not a peer we should answer");
    }

    // ---- Consent -----------------------------------------------------------

    [TestMethod]
    public void DecliningSendsTheReasonTheUserChose()
    {
        using var peers = new Peers();
        var recorder = new Recorder { ConsentAnswer = RemoteConsentOutcome.TimedOut };
        var coordinator = MakeCoordinator(peers, recorder);

        coordinator.HandleInvite(MakeInvite(peers), "10.0.0.9");

        Assert.AreEqual(1, recorder.Prompts.Count);
        Assert.AreEqual(RemoteConsentKind.Viewing, recorder.Prompts[0].Kind,
            "the first prompt is always viewing; control is a later, separate grant");
        CollectionAssert.AreEqual(new[] { "remote_decline" }, recorder.DecodedTypes());
        CollectionAssert.AreEqual(new[] { "timeout" }, recorder.DecodedReasons());
    }

    [TestMethod]
    public void AcceptingOpensTheWindowBeforeTheAcceptIsSent()
    {
        using var peers = new Peers();
        var recorder = new Recorder { ConsentAnswer = RemoteConsentOutcome.Accepted };
        var registry = new RemoteSessionRegistry();
        var coordinator = MakeCoordinator(peers, recorder, registry: registry);

        var invite = MakeInvite(peers);
        coordinator.HandleInvite(invite, "10.0.0.9");

        CollectionAssert.AreEqual(new[] { "remote_accept" }, recorder.DecodedTypes());
        // The race: a peer can attach the instant it reads the accept, and a
        // media_attach with no matching window is dropped and the connection
        // closed. Opening the window afterwards would fail only on a fast
        // network, which is the worst kind of intermittent.
        Assert.IsTrue(recorder.WindowOpenWhenAccepted.TryGetValue(invite.SessionId, out bool open)
                      && open,
            "the accept window must exist before the accept goes out");
    }

    [TestMethod]
    public void AcceptingArmsHostingButDoesNotStartCapturing()
    {
        using var peers = new Peers();
        var recorder = new Recorder { ConsentAnswer = RemoteConsentOutcome.Accepted };
        var coordinator = MakeCoordinator(peers, recorder);

        var invite = MakeInvite(peers);
        coordinator.HandleInvite(invite, "10.0.0.9");

        // Agreeing is not sharing. Capture begins when the viewer attaches, so
        // a peer that changes its mind never causes this screen to be read.
        Assert.AreEqual(1, recorder.HostingArmed.Count);
        Assert.AreEqual(invite.SessionId, recorder.HostingArmed[0].SessionId);
        Assert.AreEqual(0, recorder.ViewingStarts.Count);
    }

    // ---- Initiator ---------------------------------------------------------

    [TestMethod]
    public void AnAcceptForAnUnknownSessionIsIgnored()
    {
        using var peers = new Peers();
        var recorder = new Recorder();
        var coordinator = MakeCoordinator(peers, recorder);

        coordinator.HandleAccept(new RemoteSessionPacket
        {
            Type = "remote_accept",
            SessionId = RemoteSessionCrypto.NewSessionId(),
            Sender = "Mac",
            SenderPublicKeyB64 = peers.RemoteKeyB64,
            Port = 54232,
        }, "10.0.0.9");

        Assert.AreEqual(0, recorder.AttachCalls, "we must not dial a session we never asked for");
        Assert.AreEqual(0, recorder.Sent.Count);
    }

    [TestMethod]
    public void InvitingTwiceDoesNotSendASecondInvite()
    {
        using var peers = new Peers();
        var recorder = new Recorder();
        var coordinator = MakeCoordinator(peers, recorder);

        coordinator.Invite(peers.RemoteKeyB64, "10.0.0.9", "Mac");
        coordinator.Invite(peers.RemoteKeyB64, "10.0.0.9", "Mac");

        CollectionAssert.AreEqual(new[] { "remote_invite" }, recorder.DecodedTypes());
        Assert.IsTrue(coordinator.HasInviteInFlight);
        coordinator.CancelInvite();
    }

    // ---- Addressed by identity, not by a remembered IP ----------------------

    /// <summary>The packet types sent to <paramref name="ip"/>, in order.</summary>
    private static List<string> TypesSentTo(string ip, Recorder recorder) =>
        recorder.Sent
            .Where(e => e.IP == ip && e.Frame.Length > 4)
            .Select(e =>
            {
                using var document = JsonDocument.Parse(e.Frame.AsMemory(4));
                return document.RootElement.GetProperty("type").GetString()!;
            })
            .ToList();

    [TestMethod]
    public void InvitingSomebodyElseSupersedesThePendingInvite()
    {
        // The bug: one invite outstanding refused every other for up to a
        // minute, silently. An invite to Ari — clicked by mistake, or aimed at
        // an address that now belongs to Ari — made the button for everyone
        // else do nothing, with only invite_blocked in a log.
        using var peers = new Peers();
        using var ari = Key.Create(KeyAgreementAlgorithm.X25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        string ariKey = Convert.ToBase64String(ari.PublicKey.Export(KeyBlobFormat.RawPublicKey));
        var recorder = new Recorder();
        var coordinator = MakeCoordinator(peers, recorder, contacts:
        [
            new KnownContact(peers.RemoteKeyB64, "Mac", "192.168.68.15"),
            new KnownContact(ariKey, "Ari", "192.168.68.31"),
        ]);

        coordinator.Invite(ariKey, "192.168.68.31", "Ari");
        string? first = coordinator.PendingSessionId;
        coordinator.Invite(peers.RemoteKeyB64, "192.168.68.15", "Mac");

        CollectionAssert.AreEqual(new[] { "remote_invite" }, TypesSentTo("192.168.68.15", recorder),
                                  "the Mac's invite was refused behind Ari's");
        Assert.AreNotEqual(first, coordinator.PendingSessionId);
        // Withdrawn, not abandoned: a prompt that did reach Ari must close.
        CollectionAssert.AreEqual(new[] { "remote_invite", "remote_end" },
                                  TypesSentTo("192.168.68.31", recorder));
        Assert.AreEqual("Waiting for Mac to accept…", recorder.Statuses[^1]);
        coordinator.CancelInvite();
    }

    [TestMethod]
    public void TheSamePeerAtANewAddressSupersedesTooBecauseTheOldOneCannotArrive()
    {
        // DHCP moved the peer between two clicks. The pending invite went to an
        // address that reaches nobody — or somebody else — so keeping it would
        // wait a full minute for an answer that is not coming.
        using var peers = new Peers();
        var recorder = new Recorder();
        var coordinator = MakeCoordinator(peers, recorder);

        coordinator.Invite(peers.RemoteKeyB64, "192.168.68.31", "Mac");
        coordinator.Invite(peers.RemoteKeyB64, "192.168.68.15", "Mac");

        CollectionAssert.AreEqual(new[] { "remote_invite" }, TypesSentTo("192.168.68.15", recorder));
        CollectionAssert.AreEqual(new[] { "remote_invite", "remote_end" },
                                  TypesSentTo("192.168.68.31", recorder));
        coordinator.CancelInvite();
    }

    [TestMethod]
    public void ARepeatedClickSaysItIsStillWaitingRatherThanNothing()
    {
        // Same device, same address: genuinely still asking. No second invite —
        // but the status is said again, because a second click is a user who
        // cannot tell whether the first one did anything.
        using var peers = new Peers();
        var recorder = new Recorder();
        var coordinator = MakeCoordinator(peers, recorder);

        coordinator.Invite(peers.RemoteKeyB64, "10.0.0.9", "Mac");
        recorder.Statuses.Clear();
        coordinator.Invite(peers.RemoteKeyB64, "10.0.0.9", "Mac");

        CollectionAssert.AreEqual(new[] { "remote_invite" }, recorder.DecodedTypes());
        CollectionAssert.AreEqual(new[] { "Waiting for Mac to accept…" }, recorder.Statuses);
        coordinator.CancelInvite();
    }

    [TestMethod]
    public void TheMediaChannelDialsWhereTheAuthenticatedAcceptCameFrom()
    {
        // The accept only opens under the peer's identity key, so its source
        // address is proof of where that device is now. The invite's own
        // address is older by a round trip and a human decision.
        using var peers = new Peers();
        var recorder = new Recorder();
        var coordinator = MakeCoordinator(peers, recorder);

        coordinator.Invite(peers.RemoteKeyB64, "192.168.68.31", "Mac");
        string sessionId = coordinator.PendingSessionId!;

        var sealed_ = MakeInvite(peers, sessionId);
        var accept = new RemoteSessionPacket
        {
            Type = "remote_accept",
            SessionId = sessionId,
            Sender = "Mac",
            SenderPublicKeyB64 = peers.RemoteKeyB64,
            Port = 54232,
            Nonce = sealed_.Nonce,
            Ciphertext = sealed_.Ciphertext,
        };
        coordinator.HandleAccept(accept, "192.168.68.15");

        CollectionAssert.AreEqual(new[] { "192.168.68.15" }, recorder.AttachAddresses);
    }

    [TestMethod]
    public void AnAcceptThatDoesNotOpenDialsNothingWhateverItsAddress()
    {
        // The source address is trusted only after the body opens. A forged
        // accept from a third machine must not make us dial it.
        using var peers = new Peers();
        var recorder = new Recorder();
        var coordinator = MakeCoordinator(peers, recorder);

        coordinator.Invite(peers.RemoteKeyB64, "192.168.68.15", "Mac");
        string sessionId = coordinator.PendingSessionId!;

        var forged = new RemoteSessionPacket
        {
            Type = "remote_accept",
            SessionId = sessionId,
            Sender = "Mac",
            SenderPublicKeyB64 = peers.RemoteKeyB64,
            Port = 54232,
            Nonce = "AAAAAAAAAAAAAAAA",
            Ciphertext = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
        };
        coordinator.HandleAccept(forged, "192.168.68.66");

        Assert.AreEqual(0, recorder.AttachAddresses.Count);
    }

    [TestMethod]
    public void ADeclineForAnotherSessionDoesNotCancelOurs()
    {
        using var peers = new Peers();
        var recorder = new Recorder();
        var coordinator = MakeCoordinator(peers, recorder);

        coordinator.Invite(peers.RemoteKeyB64, "10.0.0.9", "Mac");
        string? ours = coordinator.PendingSessionId;

        coordinator.HandleDecline(new RemoteControlPacket
        {
            Type = "remote_decline",
            SessionId = RemoteSessionCrypto.NewSessionId(),
            Sender = "Someone",
            SenderPublicKeyB64 = peers.RemoteKeyB64,
            Port = 54232,
            Reason = "declined",
        }, "10.0.0.9");

        Assert.AreEqual(ours, coordinator.PendingSessionId,
            "a decline naming a different session must not tear ours down");
        coordinator.CancelInvite();
    }

    [TestMethod]
    public void AnUnknownDeclineReasonStillProducesASentence()
    {
        // The spec requires tolerating tokens we have not learned yet, and a
        // newer peer saying something sensible must not surface as raw text.
        var parsed = RemoteDeclineReasonExtensions.Parse("some_future_token")
                     ?? RemoteDeclineReason.Declined;
        Assert.AreEqual("Mac declined.", RemoteInviteCoordinator.DeclineMessage(parsed, "Mac"));
    }

    [TestMethod]
    public void EveryDeclineReasonHasItsOwnWording()
    {
        var seen = new HashSet<string>();
        foreach (RemoteDeclineReason reason in Enum.GetValues<RemoteDeclineReason>())
        {
            string message = RemoteInviteCoordinator.DeclineMessage(reason, "Mac");
            StringAssert.Contains(message, "Mac", $"{reason} should name the peer");
            Assert.IsTrue(seen.Add(message), $"{reason} reuses another reason's wording");
        }
    }

    [TestMethod]
    public void EveryDeclineReasonRoundTripsThroughItsWireToken()
    {
        // The two platforms have to agree byte for byte, and `no_encoder` is not
        // `NoEncoder` lowercased — which is why the tokens are spelled out
        // rather than derived from the enum names.
        foreach (RemoteDeclineReason reason in Enum.GetValues<RemoteDeclineReason>())
        {
            Assert.AreEqual(reason, RemoteDeclineReasonExtensions.Parse(reason.ToToken()));
        }
        Assert.AreEqual("no_encoder", RemoteDeclineReason.NoEncoder.ToToken());
        Assert.IsNull(RemoteDeclineReasonExtensions.Parse("NoEncoder"));
    }

    // ---- Teardown ----------------------------------------------------------

    [TestMethod]
    public void AClosedSessionFreesThePeerForANewInvite()
    {
        // The regression: the controller replaced the MediaSession's OnClosed
        // handler, and the handler it replaced was the one freeing the registry
        // entry. The peer stayed marked in-flight forever, so the next invite
        // was refused as busy with no session actually running — which presents
        // as "it worked once, and now it will not start again".
        using var peers = new Peers();
        var registry = new RemoteSessionRegistry();
        var keys = FakeKeys();

        var first = registry.Open("a".PadLeft(32, '0'), peers.RemoteKeyB64, "10.0.0.9",
                                  RemoteSessionRole.Responder, keys);
        Assert.AreEqual(RemoteOpenKind.Opened, first.Kind);

        // A second session with the same peer while the first is in flight.
        var blocked = registry.Open("b".PadLeft(32, '0'), peers.RemoteKeyB64, "10.0.0.9",
                                    RemoteSessionRole.Responder, keys);
        Assert.AreEqual(RemoteOpenKind.Busy, blocked.Kind, "one at a time, per peer");

        registry.Remove("a".PadLeft(32, '0'));

        var afterClose = registry.Open("c".PadLeft(32, '0'), peers.RemoteKeyB64, "10.0.0.9",
                                       RemoteSessionRole.Responder, keys);
        Assert.AreEqual(RemoteOpenKind.Opened, afterClose.Kind,
            "once a session has closed the peer must be invitable again");
    }

    [TestMethod]
    public void CancellingAnInviteFreesThePeerToo()
    {
        // The other way a session id is released: an accept that was never
        // attached. Without this an initiator that walked away would lock the
        // peer out until the accept window expired.
        using var peers = new Peers();
        var registry = new RemoteSessionRegistry();
        var keys = FakeKeys();

        registry.Open("a".PadLeft(32, '0'), peers.RemoteKeyB64, "10.0.0.9",
                      RemoteSessionRole.Responder, keys);
        registry.Cancel("a".PadLeft(32, '0'));

        var again = registry.Open("b".PadLeft(32, '0'), peers.RemoteKeyB64, "10.0.0.9",
                                  RemoteSessionRole.Responder, keys);
        Assert.AreEqual(RemoteOpenKind.Opened, again.Kind);
    }

    private static RemoteSessionKeys FakeKeys() => new(
        new byte[32], new byte[32], new byte[4], new byte[4], new byte[32]);

    // ---- Sealed body -------------------------------------------------------

    [TestMethod]
    public void TheSealedBodyRoundTripsWithItsParameters()
    {
        using var peers = new Peers();
        var recorder = new Recorder();
        var coordinator = MakeCoordinator(peers, recorder);

        var packet = MakeInvite(peers, parameters: new Dictionary<string, object>
        {
            ["protocol"] = 1, ["video"] = "h264", ["displays"] = 2,
        });
        var body = coordinator.OpenSealedBody(packet, peers.RemoteKeyB64);

        Assert.AreEqual(32, Convert.FromBase64String(body.EphemeralPublicKeyB64).Length);
        Assert.AreEqual(RemoteParamValue.Int(1), body.Params["protocol"]);
        Assert.AreEqual(RemoteParamValue.String("h264"), body.Params["video"]);
        Assert.AreEqual(RemoteParamValue.Int(2), body.Params["displays"]);
    }

    [TestMethod]
    public void AParameterWeCannotRepresentFailsTheHandshake()
    {
        using var peers = new Peers();
        var recorder = new Recorder();
        var coordinator = MakeCoordinator(peers, recorder);

        // The peer hashed this into their transcript. Silently dropping it here
        // would derive different keys on each side, and the failure would land
        // later as an unexplained tag mismatch on every media frame.
        var packet = MakeInvite(peers, parameters: new Dictionary<string, object>
        {
            ["protocol"] = 1,
            ["nested"] = new Dictionary<string, object> { ["a"] = 1 },
        });

        Assert.ThrowsException<InvalidOperationException>(
            () => coordinator.OpenSealedBody(packet, peers.RemoteKeyB64));
    }

    [TestMethod]
    public void TheSessionIdIsTheAssociatedData()
    {
        using var peers = new Peers();
        var recorder = new Recorder();
        var coordinator = MakeCoordinator(peers, recorder);

        // Same ciphertext, different session id. Without the AAD binding, an
        // invite could be replayed under a fresh session id and the handshake
        // would proceed against a transcript neither side agreed to.
        var original = MakeInvite(peers);
        var replayed = new RemoteSessionPacket
        {
            Type = original.Type,
            SessionId = RemoteSessionCrypto.NewSessionId(),
            Sender = original.Sender,
            SenderPublicKeyB64 = original.SenderPublicKeyB64,
            Port = original.Port,
            Nonce = original.Nonce,
            Ciphertext = original.Ciphertext,
        };

        _ = coordinator.OpenSealedBody(original, peers.RemoteKeyB64);

        // Caught by base type rather than by Assert.ThrowsException, which
        // matches exactly: the tag failure surfaces as the more specific
        // AuthenticationTagMismatchException, and pinning that exact type would
        // tie the test to a platform detail rather than to the guarantee.
        try
        {
            coordinator.OpenSealedBody(replayed, peers.RemoteKeyB64);
            Assert.Fail("a body replayed under a different session id must not open");
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Expected: the session id is the associated data.
        }
    }
}
