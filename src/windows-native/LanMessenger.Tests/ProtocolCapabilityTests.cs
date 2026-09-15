using LanMessenger.Core.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;
using System.Text.Json;

namespace LanMessenger.Tests;

// The optional discovery `caps` field. Mirror of the macOS
// ProtocolCapabilityTests — the two suites assert the same wire key, the same
// token string, and the same tolerance rules, because the peer that disappears
// when they disagree is only ever the one on the other platform.
//
// The field exists because PacketValidator drops unknown packet types silently.
// A client that sends remote_invite to a peer too old to know the type gets no
// reply, no error and no timeout of its own — it waits forever. Advertising the
// capability turns that hang into a disabled menu item.
[TestClass]
public class ProtocolCapabilityTests
{
    private const string LegacyBeacon =
        """{"type":"discovery","username":"Dave","port":54232,"public_key_b64":"AAAA","ips":["10.0.0.5"]}""";

    private static DiscoveryPacket Decode(string json) =>
        JsonSerializer.Deserialize<DiscoveryPacket>(Encoding.UTF8.GetBytes(json))!;

    // MARK: - Compatibility

    [TestMethod]
    public void AClientThatPredatesTheFieldStillDecodes()
    {
        var packet = Decode(LegacyBeacon);
        Assert.AreEqual("Dave", packet.Username);
        Assert.IsNull(packet.Caps);
        Assert.IsFalse(packet.SupportsRemoteDesktop,
                       "absence must never be read as 'probably supports it'");
    }

    [TestMethod]
    public void ALegacyBeaconStillValidates()
    {
        var packet = PacketValidator.ValidateDiscovery(
            Encoding.UTF8.GetBytes(LegacyBeacon), "10.0.0.5", "BBBB", []);
        Assert.IsNotNull(packet, "adding an optional field must not drop older peers");
        Assert.IsFalse(packet!.SupportsRemoteDesktop);
    }

    [TestMethod]
    public void UnknownTokensAreToleratedAndDoNotImplySupport()
    {
        var packet = Decode(
            """{"type":"discovery","username":"Dave","port":54232,"public_key_b64":"AAAA","ips":[],"caps":["audio-v3","clipboard-v9"]}""");
        CollectionAssert.AreEqual(new List<string> { "audio-v3", "clipboard-v9" }, packet.Caps);
        Assert.IsFalse(packet.SupportsRemoteDesktop);
    }

    [TestMethod]
    public void AMalformedCapsFieldIsTreatedAsAbsentRatherThanFatal()
    {
        // System.Text.Json's default here is to throw, and ValidateDiscovery
        // catches that and drops the datagram — so without the tolerant
        // converter a peer with a broken capability field would vanish from the
        // network entirely.
        foreach (var malformed in new[] { "\"remote-desktop-v1\"", "7", "{\"a\":1}", "null" })
        {
            var json =
                $$"""{"type":"discovery","username":"Dave","port":54232,"public_key_b64":"AAAA","ips":[],"caps":{{malformed}}}""";
            var packet = Decode(json);
            Assert.AreEqual("Dave", packet.Username, $"caps={malformed} dropped the packet");
            Assert.IsFalse(packet.SupportsRemoteDesktop);

            var validated = PacketValidator.ValidateDiscovery(
                Encoding.UTF8.GetBytes(json), "10.0.0.5", "BBBB", []);
            Assert.IsNotNull(validated, $"caps={malformed} made the peer disappear");
        }
    }

    [TestMethod]
    public void ANonStringEntryIsSkippedNotFatal()
    {
        var packet = Decode(
            """{"type":"discovery","username":"Dave","port":54232,"public_key_b64":"AAAA","ips":[],"caps":[1,"remote-desktop-v1",{"x":2}]}""");
        CollectionAssert.AreEqual(new List<string> { "remote-desktop-v1" }, packet.Caps);
        Assert.IsTrue(packet.SupportsRemoteDesktop);
    }

    // MARK: - Bounds

    [TestMethod]
    public void CapsAreBoundedBecauseDiscoveryIsUnauthenticatedUDP()
    {
        // Anyone on the LAN can send this, and the tokens are retained per peer.
        var many = string.Join(",", Enumerable.Range(0, 200).Select(i => $"\"token-{i}\""));
        var packet = Decode(
            $$"""{"type":"discovery","username":"Dave","port":54232,"public_key_b64":"AAAA","ips":[],"caps":[{{many}}]}""");
        Assert.AreEqual(ProtocolCapability.MaxTokens,
                        ProtocolCapability.Sanitize(packet.Caps).Count);
    }

    [TestMethod]
    public void OverlongAndEmptyTokensAreDropped()
    {
        var overlong = new string('x', ProtocolCapability.MaxTokenLength + 1);
        var packet = Decode(
            $$"""{"type":"discovery","username":"Dave","port":54232,"public_key_b64":"AAAA","ips":[],"caps":["{{overlong}}","","remote-desktop-v1"]}""");
        CollectionAssert.AreEqual(new List<string> { "remote-desktop-v1" },
                                  ProtocolCapability.Sanitize(packet.Caps));
    }

    // MARK: - What we send

    [TestMethod]
    public void OurOwnBeaconAdvertisesRemoteDesktopUnderTheWireKey()
    {
        var beacon = new DiscoveryPacket
        {
            Type = "discovery", Username = "Dave", Port = 54232,
            PublicKeyB64 = "AAAA", Ips = ["10.0.0.5"],
            Caps = ProtocolCapability.Advertised,
        };
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(beacon));

        // The key is asserted literally: macOS writes the same string, and a
        // rename on one platform is invisible to the other until a user reports
        // that remote desktop is greyed out for half their contacts.
        var caps = document.RootElement.GetProperty("caps");
        Assert.AreEqual(1, caps.GetArrayLength());
        Assert.AreEqual("remote-desktop-v1", caps[0].GetString());
        Assert.AreEqual("remote-desktop-v1", ProtocolCapability.RemoteDesktopV1);
    }

    [TestMethod]
    public void TheAdvertisementSurvivesARoundTrip()
    {
        var sent = new DiscoveryPacket
        {
            Type = "discovery", Username = "Dave", Port = 54232,
            PublicKeyB64 = "AAAA", Ips = [], Caps = ProtocolCapability.Advertised,
        };
        var received = JsonSerializer.Deserialize<DiscoveryPacket>(
            JsonSerializer.SerializeToUtf8Bytes(sent))!;
        Assert.IsTrue(received.SupportsRemoteDesktop);
    }

    // MARK: - The peer record

    [TestMethod]
    public void SanitizeDoesNotInventCapabilities()
    {
        Assert.AreEqual(0, ProtocolCapability.Sanitize(null).Count);
        Assert.AreEqual(0, ProtocolCapability.Sanitize([]).Count);
    }
}
