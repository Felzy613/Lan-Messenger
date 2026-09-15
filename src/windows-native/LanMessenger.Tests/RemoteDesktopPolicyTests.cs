using LanMessenger.Core.Crypto;
using LanMessenger.Core.Networking.Media;
using LanMessenger.Core.Persistence;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;
using System.Text.Json;

namespace LanMessenger.Tests;

// The consent gate. Mirror of the macOS RemoteDesktopPolicyTests.
//
// PROTOCOL.md calls these protocol requirements rather than interface
// preferences — "a client that does not enforce them is not compatible" — so
// they are tested like protocol rules: exhaustively, and from the direction of
// the mistake that would be worst to ship.
//
// That direction is always "more permissive than intended". A prompt that never
// appears is a bug report; a prompt that appears for a stranger, or a session
// that starts without one, is the thing that turns a chat app into a RAT.
[TestClass]
public class RemoteDesktopPolicyTests
{
    private const string DaveKey = "ZGF2ZS1rZXktMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDA=";
    private const string StrangerKey = "c3RyYW5nZXItMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDA=";
    private const string OwnKey = "b3duLWtleS0wMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDA=";

    private static List<KnownContact> Contacts =>
        [new KnownContact(DaveKey, "Dave", "10.0.0.5")];

    private static RemoteInviteContext Context(
        string key,
        string ip = "10.0.0.5",
        RemoteDesktopMode mode = RemoteDesktopMode.On,
        bool inFlight = false,
        List<KnownContact>? contacts = null) => new()
        {
            PeerPublicKeyB64 = key,
            PeerIP = ip,
            OwnPublicKeyB64 = OwnKey,
            Mode = mode,
            Contacts = contacts ?? Contacts,
            HasSessionInFlight = inFlight,
        };

    // ---- The mode ----------------------------------------------------------

    [TestMethod]
    public void TheFeatureIsOffByDefault()
    {
        Assert.AreEqual(RemoteDesktopMode.Off, new AppConfig().RemoteDesktopMode);
        Assert.IsFalse(new AppConfig().RemoteDesktopMode.IsEnabled());
    }

    [TestMethod]
    public void AnUnrecognisedModeFailsClosed()
    {
        // A config written by a newer build, or a corrupted one, must not leave
        // the screen reachable. This is the reason the setting is a string rather
        // than a bool: a bool has no way to express "I don't know".
        Assert.AreEqual(RemoteDesktopMode.Off, RemoteDesktopModeParser.Parse("viewOnly"));
        Assert.AreEqual(RemoteDesktopMode.Off, RemoteDesktopModeParser.Parse(""));
        Assert.AreEqual(RemoteDesktopMode.Off, RemoteDesktopModeParser.Parse(null));
        Assert.AreEqual(RemoteDesktopMode.On, RemoteDesktopModeParser.Parse("ON"),
                        "case should not decide security");
    }

    [TestMethod]
    public void AnUnknownModeInStoredConfigDoesNotThrowAwayTheRestOfIt()
    {
        // Failing closed must not mean failing loudly: a single unrecognised
        // value cannot be allowed to take the user's contacts with it. This is
        // why the raw string is what gets serialized — deserializing straight
        // into the enum would throw here.
        const string json =
            """{"username":"Dave","remote_desktop_mode":"unattended","relay_enabled":true}""";
        var config = JsonSerializer.Deserialize<AppConfig>(Encoding.UTF8.GetBytes(json))!;
        Assert.AreEqual(RemoteDesktopMode.Off, config.RemoteDesktopMode);
        Assert.AreEqual("Dave", config.Username);
        Assert.IsTrue(config.RelayEnabled);
    }

    [TestMethod]
    public void TheModeSurvivesARoundTripUnderTheWireKey()
    {
        var config = new AppConfig { RemoteDesktopMode = RemoteDesktopMode.On };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(config);

        using var document = JsonDocument.Parse(bytes);
        Assert.AreEqual("on", document.RootElement.GetProperty("remote_desktop_mode").GetString());

        var decoded = JsonSerializer.Deserialize<AppConfig>(bytes)!;
        Assert.AreEqual(RemoteDesktopMode.On, decoded.RemoteDesktopMode);
    }

    [TestMethod]
    public void AConfigPredatingTheSettingReadsAsOff()
    {
        var config = JsonSerializer.Deserialize<AppConfig>(
            Encoding.UTF8.GetBytes("""{"username":"Dave"}"""))!;
        Assert.AreEqual(RemoteDesktopMode.Off, config.RemoteDesktopMode,
                        "an upgrade must never switch the feature on for somebody");
    }

    // ---- Inbound: strangers ------------------------------------------------

    [TestMethod]
    public void AStrangerGetsSilenceNotADecline()
    {
        // A decline confirms that this address runs the app and has the feature.
        // A stranger who can provoke any response at all can use that to probe.
        var decision = RemoteDesktopPolicy.Decide(Context(StrangerKey, "172.16.9.9"));
        Assert.AreEqual(RemoteInviteAction.Ignore, decision.Action);
        Assert.AreEqual(RemoteIgnoreReason.NotAContact, decision.IgnoreReason);
    }

    [TestMethod]
    public void TurningTheFeatureOnDoesNotWidenWhoMayReachYou()
    {
        // Trust is settled before the mode is consulted, so the switch changes
        // what happens for contacts who could already reach you — never who.
        foreach (var mode in new[] { RemoteDesktopMode.Off, RemoteDesktopMode.On })
        {
            var decision = RemoteDesktopPolicy.Decide(Context(StrangerKey, "172.16.9.9", mode));
            Assert.AreEqual(RemoteInviteAction.Ignore, decision.Action, $"mode {mode} let a stranger through");
            Assert.AreEqual(RemoteIgnoreReason.NotAContact, decision.IgnoreReason);
        }
    }

    [TestMethod]
    public void AChangedKeyAtAKnownAddressIsIgnoredAndNamedDistinctly()
    {
        // Not a saved contact, so the consent rules say drop. But an unfamiliar
        // key arriving at a familiar address is the exact shape of the attack
        // pinning defends against, and "nothing happened" is a poor account of it
        // in a bug report.
        var decision = RemoteDesktopPolicy.Decide(Context(StrangerKey, "10.0.0.5"));
        Assert.AreEqual(RemoteInviteAction.Ignore, decision.Action);
        Assert.AreEqual(RemoteIgnoreReason.KeyChangedAtKnownAddress, decision.IgnoreReason);
        Assert.IsFalse(decision.IsPrompt);
    }

    [TestMethod]
    public void AnInviteFromOurselvesIsIgnored()
    {
        var decision = RemoteDesktopPolicy.Decide(Context(OwnKey));
        Assert.AreEqual(RemoteIgnoreReason.SelfInvite, decision.IgnoreReason);
    }

    [TestMethod]
    public void AnInviteWithNoKeyIsIgnored()
    {
        var decision = RemoteDesktopPolicy.Decide(Context(""));
        Assert.AreEqual(RemoteIgnoreReason.Malformed, decision.IgnoreReason);
    }

    [TestMethod]
    public void NoContactsMeansNoPromptEver()
    {
        var decision = RemoteDesktopPolicy.Decide(Context(DaveKey, contacts: []));
        Assert.AreEqual(RemoteIgnoreReason.NotAContact, decision.IgnoreReason);
    }

    // ---- Inbound: contacts -------------------------------------------------

    [TestMethod]
    public void ASavedContactPromptsWhenTheFeatureIsOn()
    {
        var decision = RemoteDesktopPolicy.Decide(Context(DaveKey));
        Assert.IsTrue(decision.IsPrompt);
        Assert.IsTrue(decision.Trust.IsPinned);
        Assert.AreEqual("Dave", decision.Trust.Username);
    }

    [TestMethod]
    public void ASavedContactIsDeclinedNotIgnoredWhenTheFeatureIsOff()
    {
        // They already know this address runs the app, so silence tells them
        // nothing they did not know and leaves them hanging — which is exactly
        // the failure `caps` exists to prevent.
        var decision = RemoteDesktopPolicy.Decide(Context(DaveKey, mode: RemoteDesktopMode.Off));
        Assert.AreEqual(RemoteInviteAction.Decline, decision.Action);
        Assert.AreEqual(RemoteDeclineReason.Disabled, decision.DeclineReason);
    }

    [TestMethod]
    public void ASecondInviteWhileOneIsInFlightIsDeclined()
    {
        var decision = RemoteDesktopPolicy.Decide(Context(DaveKey, inFlight: true));
        Assert.AreEqual(RemoteInviteAction.Decline, decision.Action);
        Assert.AreEqual(RemoteDeclineReason.Busy, decision.DeclineReason);
    }

    [TestMethod]
    public void ASavedContactOnANewAddressStillPrompts()
    {
        // Peers roam. Warning or refusing here would train the user to click
        // through the warning that matters.
        var decision = RemoteDesktopPolicy.Decide(Context(DaveKey, "192.168.1.77"));
        Assert.IsTrue(decision.IsPrompt);
        Assert.IsTrue(decision.Trust.IsPinned);
    }

    [TestMethod]
    public void EveryPromptCarriesAPinnedKey()
    {
        // The invariant behind the dialog: if a prompt is ever raised for a key
        // that is not pinned, the fingerprint it shows is decoration.
        (string Key, string IP, RemoteDesktopMode Mode, bool InFlight)[] cases =
        [
            (DaveKey, "10.0.0.5", RemoteDesktopMode.On, false),
            (DaveKey, "192.168.1.77", RemoteDesktopMode.On, false),
            (StrangerKey, "10.0.0.5", RemoteDesktopMode.On, false),
            (StrangerKey, "172.16.9.9", RemoteDesktopMode.On, false),
            (DaveKey, "10.0.0.5", RemoteDesktopMode.Off, false),
            (DaveKey, "10.0.0.5", RemoteDesktopMode.On, true),
            (OwnKey, "10.0.0.5", RemoteDesktopMode.On, false),
            ("", "10.0.0.5", RemoteDesktopMode.On, false),
        ];

        foreach (var (key, ip, mode, inFlight) in cases)
        {
            var decision = RemoteDesktopPolicy.Decide(Context(key, ip, mode, inFlight));
            if (decision.IsPrompt)
            {
                Assert.IsTrue(decision.Trust.IsPinned,
                    $"prompted for an unpinned key: {key} at {ip}");
            }
        }
    }

    // ---- Outbound ----------------------------------------------------------

    private static RemoteInviteTarget Target(
        bool contact = true, bool online = true, bool capable = true, bool inFlight = false) => new()
        {
            IsSavedContact = contact,
            IsOnline = online,
            AdvertisesRemoteDesktop = capable,
            HasSessionInFlight = inFlight,
        };

    [TestMethod]
    public void ACapableOnlineContactMayBeInvited()
    {
        Assert.IsTrue(RemoteDesktopPolicy.Availability(RemoteDesktopMode.On, Target()).IsAvailable);
    }

    [TestMethod]
    public void TheSwitchGovernsBothDirections()
    {
        // One setting for the whole feature: a host that will not be viewed does
        // not offer to view. A switch that only half-applies is one users misread.
        var availability = RemoteDesktopPolicy.Availability(RemoteDesktopMode.Off, Target());
        Assert.IsFalse(availability.IsAvailable);
        Assert.AreEqual(RemoteUnavailableReason.LocalFeatureOff, availability.Reason);
    }

    [TestMethod]
    public void APeerThatDoesNotAdvertiseTheCapabilityIsNotOffered()
    {
        // Sending anyway means an invite silently dropped by PacketValidator and
        // an initiator waiting forever — the whole reason `caps` exists.
        var availability = RemoteDesktopPolicy.Availability(
            RemoteDesktopMode.On, Target(capable: false));
        Assert.AreEqual(RemoteUnavailableReason.PeerLacksCapability, availability.Reason);
    }

    [TestMethod]
    public void OfflineIsReportedAheadOfAMissingCapability()
    {
        // Capability is learned from discovery, so a peer we have not heard from
        // may have an empty record rather than a genuinely incapable one.
        // "Offline" is both true and more actionable.
        var availability = RemoteDesktopPolicy.Availability(
            RemoteDesktopMode.On, Target(online: false, capable: false));
        Assert.AreEqual(RemoteUnavailableReason.PeerOffline, availability.Reason);
    }

    [TestMethod]
    public void ANonContactIsNeverOffered()
    {
        var availability = RemoteDesktopPolicy.Availability(
            RemoteDesktopMode.On, Target(contact: false));
        Assert.AreEqual(RemoteUnavailableReason.PeerNotAContact, availability.Reason);
    }

    [TestMethod]
    public void OneSessionPerPeer()
    {
        var availability = RemoteDesktopPolicy.Availability(
            RemoteDesktopMode.On, Target(inFlight: true));
        Assert.AreEqual(RemoteUnavailableReason.SessionInFlight, availability.Reason);
    }

    [TestMethod]
    public void TheLocalSwitchOutranksEveryPeerProblem()
    {
        // Whatever else is wrong, "you have this switched off" is the one the
        // user can act on, and the one that must not be hidden behind another.
        var availability = RemoteDesktopPolicy.Availability(
            RemoteDesktopMode.Off,
            Target(contact: false, online: false, capable: false, inFlight: true));
        Assert.AreEqual(RemoteUnavailableReason.LocalFeatureOff, availability.Reason);
    }

    // ---- Key trust, mirrored from PeerKeyTrustTests.swift -------------------

    [TestMethod]
    public void APinnedKeyFromANewAddressIsStillPinned()
    {
        var trust = PeerKeyTrustEvaluator.Evaluate(DaveKey, "192.168.1.77", Contacts);
        Assert.AreEqual(PeerKeyTrustKind.Pinned, trust.Kind);
        Assert.IsFalse(trust.RequiresWarning);
    }

    [TestMethod]
    public void AnEmptyKeyIsNeverTrusted()
    {
        // "No key supplied" must not be able to match a contact whose key is also
        // blank.
        var trust = PeerKeyTrustEvaluator.Evaluate(
            "", "10.0.0.5", [new KnownContact("", "Ghost", "10.0.0.5")]);
        Assert.AreEqual(PeerKeyTrustKind.Unknown, trust.Kind);
    }

    [TestMethod]
    public void AContactWithNoRecordedAddressDoesNotMatchAnEmptyIP()
    {
        // Contacts imported from the legacy Python config can carry an empty
        // LastIP. Matching "" to "" would make every such contact vouch for every
        // unknown key that arrives without a source address.
        var trust = PeerKeyTrustEvaluator.Evaluate(
            StrangerKey, "", [new KnownContact(DaveKey, "Dave", "")]);
        Assert.AreEqual(PeerKeyTrustKind.Unknown, trust.Kind);
    }
}
