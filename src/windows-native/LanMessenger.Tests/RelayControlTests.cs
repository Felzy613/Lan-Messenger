using LanMessenger.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanMessenger.Tests;

// Guards the relay control envelope — the path that carries an edit or delete
// to a peer who wasn't on the LAN when it happened.
//
// See PROTOCOL.md -> Relay control records. The envelope lives in the *plaintext*
// of an ordinary relay record, so the Worker needs no change and still learns
// nothing: not the operation, not which message it targets.
[TestClass]
public class RelayControlTests
{
    private static string Id(char c) => new(c, 32);

    // MARK: - Envelope round-trip

    [TestMethod]
    public void EditEnvelopeRoundTrips()
    {
        var env = new RelayControlEnvelope(RelayControlOp.Edit, Id('a'), "corrected text", 1715000000.5);
        var decoded = RelayControlEnvelope.Decode(env.Encoded());
        Assert.AreEqual(env, decoded);
    }

    [TestMethod]
    public void DeleteEnvelopeRoundTrips()
    {
        var env = new RelayControlEnvelope(RelayControlOp.Delete, Id('b'), null, 1715000001);
        var decoded = RelayControlEnvelope.Decode(env.Encoded());
        Assert.AreEqual(env, decoded);
    }

    [TestMethod]
    public void EncodedFormStartsWithTheMarker()
    {
        var env = new RelayControlEnvelope(RelayControlOp.Delete, Id('c'), null, 1);
        StringAssert.StartsWith(env.Encoded(), "__CTRL__:");
    }

    // Cross-platform: macOS writes the same marker and field names, so a
    // macOS-produced envelope must decode here verbatim.
    [TestMethod]
    public void DecodesAnEnvelopeProducedByTheOtherPlatform()
    {
        var json = "__CTRL__:{\"op\":\"edit\",\"target\":\"" + Id('a') + "\",\"text\":\"fixed\",\"at\":1715000000.5}";
        var env = RelayControlEnvelope.Decode(json);
        Assert.IsNotNull(env);
        Assert.AreEqual(RelayControlOp.Edit, env!.Op);
        Assert.AreEqual("fixed", env.Text);
        Assert.AreEqual(1715000000.5, env.At);
    }

    // MARK: - Ordinary chat text must not be mistaken for a control record

    [TestMethod]
    public void PlainTextIsNotAControlEnvelope()
    {
        Assert.IsNull(RelayControlEnvelope.Decode("hey, are you around?"));
        Assert.IsNull(RelayControlEnvelope.Decode(""));
        Assert.IsNull(RelayControlEnvelope.Decode("{\"op\":\"delete\"}"));
        // A file message is a different prefixed convention entirely.
        Assert.IsNull(RelayControlEnvelope.Decode(@"__FILE__:C:\tmp\report.pdf"));
    }

    [TestMethod]
    public void MarkerWithGarbagePayloadIsRejected()
    {
        Assert.IsNull(RelayControlEnvelope.Decode("__CTRL__:not json"));
        Assert.IsNull(RelayControlEnvelope.Decode("__CTRL__:"));
    }

    [TestMethod]
    public void UnknownOpIsRejected()
    {
        Assert.IsNull(RelayControlEnvelope.Decode(
            "__CTRL__:{\"op\":\"wipe\",\"target\":\"" + Id('a') + "\",\"at\":1}"));
    }

    // A target that isn't a message id can't match anything, so it is rejected
    // before it reaches the history store rather than after.
    [TestMethod]
    public void MalformedTargetIsRejected()
    {
        foreach (var bad in new[] { "", "short", new string('A', 32), new string('a', 31) })
        {
            Assert.IsNull(
                RelayControlEnvelope.Decode("__CTRL__:{\"op\":\"delete\",\"target\":\"" + bad + "\",\"at\":1}"),
                $"should reject target '{bad}'");
        }
    }

    // An edit with no replacement body would blank the message rather than
    // change it — that is what delete is for.
    [TestMethod]
    public void EditWithoutTextIsRejected()
    {
        Assert.IsNull(RelayControlEnvelope.Decode(
            "__CTRL__:{\"op\":\"edit\",\"target\":\"" + Id('a') + "\",\"at\":1}"));
        Assert.IsNull(RelayControlEnvelope.Decode(
            "__CTRL__:{\"op\":\"edit\",\"target\":\"" + Id('a') + "\",\"text\":\"\",\"at\":1}"));
    }

    // MARK: - Record id

    // The record must NOT reuse the target's id: the Worker dedups /store on
    // message_id and answers a repeat with {ok:true,duplicate:true}, so a
    // re-post under the original id is dropped while reporting success.
    [TestMethod]
    public void NewRecordIdIsAFreshMessageId()
    {
        var a = RelayControlEnvelope.NewRecordId();
        var b = RelayControlEnvelope.NewRecordId();
        Assert.AreNotEqual(a, b);
        Assert.IsTrue(RelayControlEnvelope.IsMessageId(a), $"record id must be a valid message_id: {a}");
        Assert.AreEqual(32, a.Length);
    }

    [TestMethod]
    public void IsMessageIdMatchesProtocolForm()
    {
        Assert.IsTrue(RelayControlEnvelope.IsMessageId("a3f1b2c4d5e6f7a8b9c0d1e2f3a4b5c6"));
        Assert.IsFalse(RelayControlEnvelope.IsMessageId("A3F1B2C4D5E6F7A8B9C0D1E2F3A4B5C6"), "must be lowercase");
        Assert.IsFalse(RelayControlEnvelope.IsMessageId("a3f1b2c4-d5e6-f7a8-b9c0-d1e2f3a4b5c6"), "no dashes");
        Assert.IsFalse(RelayControlEnvelope.IsMessageId("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz"), "must be hex");
        Assert.IsFalse(RelayControlEnvelope.IsMessageId(null));
    }
}
