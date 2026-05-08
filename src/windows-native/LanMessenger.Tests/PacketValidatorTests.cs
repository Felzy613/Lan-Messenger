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
}
