using LanMessenger.Core.Persistence;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace LanMessenger.Tests;

// ConfigStore uses real AppData paths, so we test the config model + serialization directly
// (not the singleton, which would mutate user data).
[TestClass]
public class ConfigStoreTests
{
    [TestMethod]
    public void AppConfigRoundTrip()
    {
        var config = new AppConfig
        {
            Username          = "TestUser",
            UpdateServerURL   = "https://example.com",
            InboxDir          = @"C:\Users\test\Received",
            HiddenConversations = ["10.0.0.5"],
            Contacts =
            [
                new ContactConfig
                {
                    PublicKeyB64 = "AAAA==",
                    Username     = "Alice",
                    LastIP       = "10.0.0.2",
                }
            ],
        };

        var json      = JsonSerializer.Serialize(config);
        var recovered = JsonSerializer.Deserialize<AppConfig>(json)!;

        Assert.AreEqual("TestUser",             recovered.Username);
        Assert.AreEqual("https://example.com",  recovered.UpdateServerURL);
        Assert.AreEqual(1,                      recovered.Contacts.Count);
        Assert.AreEqual("Alice",                recovered.Contacts[0].Username);
        Assert.AreEqual("AAAA==",               recovered.Contacts[0].PublicKeyB64);
        Assert.AreEqual("10.0.0.5",             recovered.HiddenConversations[0]);
    }

    [TestMethod]
    public void PendingMessageRoundTrip()
    {
        var msg = new PendingMessageConfig
        {
            MessageId        = "aabbccddeeff00112233445566778899",
            PeerPublicKeyB64 = "BBBB==",
            PeerUsername     = "Bob",
            Text             = "Hello",
            Timestamp        = 1700000000.0,
        };

        var json      = JsonSerializer.Serialize(msg);
        var recovered = JsonSerializer.Deserialize<PendingMessageConfig>(json)!;

        Assert.AreEqual(msg.MessageId,        recovered.MessageId);
        Assert.AreEqual(msg.PeerPublicKeyB64, recovered.PeerPublicKeyB64);
        Assert.AreEqual(msg.Text,             recovered.Text);
        Assert.AreEqual(msg.Timestamp,        recovered.Timestamp);
    }

    [TestMethod]
    public void AppConfigJsonUsesSnakeCaseKeys()
    {
        var config = new AppConfig { Username = "Alice" };
        var json   = JsonSerializer.Serialize(config);
        // Spot-check that snake_case is used, not PascalCase
        Assert.IsTrue(json.Contains("\"username\""),               "Expected snake_case key 'username'");
        Assert.IsTrue(json.Contains("\"update_server_url\""),      "Expected snake_case key 'update_server_url'");
        Assert.IsTrue(json.Contains("\"hidden_conversations\""),   "Expected snake_case key 'hidden_conversations'");
        Assert.IsTrue(json.Contains("\"pending_messages\""),       "Expected snake_case key 'pending_messages'");
        Assert.IsTrue(json.Contains("\"inbox_dir\""),              "Expected snake_case key 'inbox_dir'");
    }

    [TestMethod]
    public void MessageEntryJsonUsesSnakeCaseKeys()
    {
        var entry = new MessageEntry
        {
            Sender = "Bob", Text = "hi", Incoming = true,
            Timestamp = 1.0, MessageId = "abc", Status = "Sent",
            ReadReceiptSent = false,
        };
        var json = JsonSerializer.Serialize(entry);
        Assert.IsTrue(json.Contains("\"message_id\""),        "Expected snake_case key 'message_id'");
        Assert.IsTrue(json.Contains("\"read_receipt_sent\""), "Expected snake_case key 'read_receipt_sent'");
    }

    [TestMethod]
    public void DefaultConfigHasExpectedValues()
    {
        var config = new AppConfig();
        Assert.AreEqual("User", config.Username);
        Assert.AreEqual(0, config.Contacts.Count);
        Assert.AreEqual(0, config.PendingMessages.Count);
    }
}
