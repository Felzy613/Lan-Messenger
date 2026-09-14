using LanMessenger.Core.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;
using System.Text.Json;

namespace LanMessenger.Tests;

[TestClass]
public class PacketValidatorTests
{
    private const string OwnKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    // MARK: - Self-suppression

    [TestMethod]
    public void SelfSuppressionByPublicKey()
    {
        var dict = new Dictionary<string, object?>
        {
            ["type"]                  = "text",
            ["message_id"]            = "aabbcc",
            ["timestamp"]             = 1.0,
            ["sender"]                = "Alice",
            ["sender_public_key_b64"] = OwnKey,
            ["port"]                  = 54232,
            ["nonce"]                 = Convert.ToBase64String(new byte[12]),
            ["ciphertext"]            = Convert.ToBase64String(new byte[17]),
        };
        var data = JsonSerializer.SerializeToUtf8Bytes(dict);
        var result = PacketValidator.Validate(data, "10.0.0.1", OwnKey);
        Assert.IsNull(result, "Own packets must be dropped");
    }

    [TestMethod]
    public void SelfSuppressionByIPForDiscovery()
    {
        var pkt = new DiscoveryPacket
        {
            Type         = "discovery",
            Username     = "Alice",
            Port         = 54232,
            PublicKeyB64 = "other_key==",
            Ips          = ["10.0.0.1"],
        };
        var data = JsonSerializer.SerializeToUtf8Bytes(pkt);
        var ownIPs = new HashSet<string> { "10.0.0.1" };
        var result = PacketValidator.ValidateDiscovery(data, "10.0.0.1", OwnKey, ownIPs);
        Assert.IsNull(result, "Own IP must be dropped");
    }

    // MARK: - Valid packets

    [TestMethod]
    public void ValidTextPacketAccepted()
    {
        var dict = new Dictionary<string, object?>
        {
            ["type"]                  = "text",
            ["message_id"]            = "aabbcc112233",
            ["timestamp"]             = 1700000000.0,
            ["sender"]                = "Bob",
            ["sender_public_key_b64"] = "other_key==",
            ["port"]                  = 54232,
            ["nonce"]                 = Convert.ToBase64String(new byte[12]),
            ["ciphertext"]            = Convert.ToBase64String(new byte[20]),
        };
        var data = JsonSerializer.SerializeToUtf8Bytes(dict);
        var result = PacketValidator.Validate(data, "10.0.0.2", OwnKey);
        Assert.IsInstanceOfType<ValidatedText>(result);
    }

    // MARK: - Field validation

    [TestMethod]
    public void InvalidNonceDropped()
    {
        var dict = new Dictionary<string, object?>
        {
            ["type"]                  = "text",
            ["message_id"]            = "aabbcc",
            ["timestamp"]             = 1.0,
            ["sender"]                = "Bob",
            ["sender_public_key_b64"] = "other==",
            ["port"]                  = 54232,
            ["nonce"]                 = Convert.ToBase64String(new byte[11]), // wrong: 11 bytes, not 12
            ["ciphertext"]            = Convert.ToBase64String(new byte[20]),
        };
        var data = JsonSerializer.SerializeToUtf8Bytes(dict);
        var result = PacketValidator.Validate(data, "10.0.0.2", OwnKey);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void FileSizeTooLargeDropped()
    {
        var dict = new Dictionary<string, object?>
        {
            ["type"]                  = "file_start",
            ["transfer_id"]           = "aabb",
            ["filename"]              = "test.zip",
            ["size"]                  = (long)(2L * 1024 * 1024 * 1024 + 1), // > 2 GiB
            ["sender"]                = "Bob",
            ["sender_public_key_b64"] = "other==",
            ["port"]                  = 54232,
        };
        var data = JsonSerializer.SerializeToUtf8Bytes(dict);
        var result = PacketValidator.Validate(data, "10.0.0.2", OwnKey);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void FileSizeNegativeDropped()
    {
        var dict = new Dictionary<string, object?>
        {
            ["type"]                  = "file_start",
            ["transfer_id"]           = "aabb",
            ["filename"]              = "test.zip",
            ["size"]                  = (long)(-1),
            ["sender"]                = "Bob",
            ["sender_public_key_b64"] = "other==",
            ["port"]                  = 54232,
        };
        var data = JsonSerializer.SerializeToUtf8Bytes(dict);
        var result = PacketValidator.Validate(data, "10.0.0.2", OwnKey);
        Assert.IsNull(result);
    }

    // MARK: - Filename sanitization

    [TestMethod]
    public void SanitizeFilenameStripsPath()
    {
        Assert.AreEqual("evil.txt",   PacketValidator.SanitizeFilename("/etc/evil.txt"));
        Assert.AreEqual("evil.txt",   PacketValidator.SanitizeFilename("C:\\Windows\\evil.txt"));
        Assert.AreEqual("evil.txt",   PacketValidator.SanitizeFilename("../../../evil.txt"));
        Assert.AreEqual("file",       PacketValidator.SanitizeFilename("   "));
        Assert.AreEqual("hello.txt",  PacketValidator.SanitizeFilename("  hello.txt  "));
    }

    [TestMethod]
    public void SanitizeFilenameRemovesNullBytes()
    {
        var name = "evil\0file.txt";
        var result = PacketValidator.SanitizeFilename(name);
        Assert.IsFalse(result.Contains('\0'));
    }

    // MARK: - Discovery

    [TestMethod]
    public void ValidDiscoveryPacketAccepted()
    {
        var pkt = new DiscoveryPacket
        {
            Type         = "discovery",
            Username     = "Bob",
            Port         = 54232,
            PublicKeyB64 = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBK=",
            Ips          = ["10.0.0.2"],
        };
        var data = JsonSerializer.SerializeToUtf8Bytes(pkt);
        var ownIPs = new HashSet<string> { "10.0.0.1" };
        var result = PacketValidator.ValidateDiscovery(data, "10.0.0.2", OwnKey, ownIPs);
        Assert.IsNotNull(result);
        Assert.AreEqual("Bob", result!.Username);
    }

    [TestMethod]
    public void GoodbyePacketAccepted()
    {
        // The departure datagram must pass the validator, otherwise peers can
        // never flip offline promptly. This was the missing piece in the rebuild.
        var pkt = new DiscoveryPacket
        {
            Type         = "goodbye",
            Username     = "Bob",
            Port         = 54232,
            PublicKeyB64 = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBK=",
            Ips          = ["10.0.0.2"],
        };
        var data = JsonSerializer.SerializeToUtf8Bytes(pkt);
        var result = PacketValidator.ValidateDiscovery(data, "10.0.0.2", OwnKey, new HashSet<string>());
        Assert.IsNotNull(result);
        Assert.AreEqual("goodbye", result!.Type);
    }

    [TestMethod]
    public void UnknownDiscoveryTypeDropped()
    {
        var pkt = new DiscoveryPacket
        {
            Type         = "banana",
            Username     = "Bob",
            Port         = 54232,
            PublicKeyB64 = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBK=",
            Ips          = ["10.0.0.2"],
        };
        var data = JsonSerializer.SerializeToUtf8Bytes(pkt);
        var result = PacketValidator.ValidateDiscovery(data, "10.0.0.2", OwnKey, new HashSet<string>());
        Assert.IsNull(result);
    }

    // ---- Remote desktop ----------------------------------------------------

    private const string GoodSessionId = "9f2c4a6e8b0d1f3a5c7e9b1d3f5a7c9e";
    private static readonly string ValidNonce = Convert.ToBase64String(new byte[12]);

    private static byte[] RemoteJson(string type, string sessionId = GoodSessionId,
                                     string? nonce = null, string? reason = null,
                                     string senderKey = "cGVlci1rZXk=")
    {
        var fields = new List<string>
        {
            $"\"type\":\"{type}\"",
            $"\"session_id\":\"{sessionId}\"",
            "\"sender\":\"Alice\"",
            $"\"sender_public_key_b64\":\"{senderKey}\"",
            "\"port\":54232",
        };
        if (nonce is not null) { fields.Add($"\"nonce\":\"{nonce}\""); fields.Add("\"ciphertext\":\"Y2lwaGVy\""); }
        if (reason is not null) fields.Add($"\"reason\":\"{reason}\"");
        return Encoding.UTF8.GetBytes("{" + string.Join(",", fields) + "}");
    }

    [TestMethod]
    public void RemoteInviteAndAcceptValidate()
    {
        foreach (string type in new[] { "remote_invite", "remote_accept" })
        {
            var pkt = PacketValidator.Validate(RemoteJson(type, nonce: ValidNonce), "10.0.0.5", "mine");
            Assert.IsNotNull(pkt, $"{type} should validate");
            Assert.AreEqual("10.0.0.5", pkt!.SenderIP);
            Assert.IsTrue(pkt.RefreshesPresence);
        }
    }

    [TestMethod]
    public void RemoteControlPacketsValidate()
    {
        foreach (string type in new[] { "remote_decline", "remote_end", "media_attach" })
        {
            var reason = type == "media_attach" ? null : "declined";
            Assert.IsNotNull(PacketValidator.Validate(RemoteJson(type, reason: reason), "10.0.0.5", "mine"),
                $"{type} should validate");
        }
    }

    [TestMethod]
    public void MediaAttachDoesNotRefreshPresence()
    {
        // It is the last JSON frame on a socket that is about to become a binary
        // media channel; treating it as ordinary peer traffic would have the
        // presence path touching a connection that is no longer a JSON peer.
        var pkt = PacketValidator.Validate(RemoteJson("media_attach"), "10.0.0.5", "mine");
        Assert.IsNotNull(pkt);
        Assert.IsFalse(pkt!.RefreshesPresence);
    }

    [TestMethod]
    public void RemotePacketsRejectMalformedSessionId()
    {
        // A session id is the lookup key for an accept window, so a malformed one
        // must never reach the registry.
        foreach (string bad in new[] { "", "short", "9F2C4A6E8B0D1F3A5C7E9B1D3F5A7C9E",
                                       "9f2c4a6e-8b0d-1f3a-5c7e-9b1d3f5a7c9e" })
        {
            foreach (string type in new[] { "remote_invite", "remote_decline", "media_attach" })
            {
                string? nonce = type == "remote_invite" ? ValidNonce : null;
                Assert.IsNull(PacketValidator.Validate(RemoteJson(type, bad, nonce), "10.0.0.5", "mine"),
                    $"{type} must reject session_id '{bad}'");
            }
        }
    }

    [TestMethod]
    public void RemoteInviteRejectsBadNonce()
    {
        string shortNonce = Convert.ToBase64String(new byte[8]);
        Assert.IsNull(PacketValidator.Validate(
            RemoteJson("remote_invite", nonce: shortNonce), "10.0.0.5", "mine"),
            "a nonce that is not 12 bytes must be rejected");
    }

    [TestMethod]
    public void RemotePacketFromSelfIsDropped()
    {
        Assert.IsNull(PacketValidator.Validate(
            RemoteJson("remote_invite", nonce: ValidNonce, senderKey: "mine"), "10.0.0.5", "mine"),
            "self-suppression must apply to remote-desktop packets too");
    }
}
