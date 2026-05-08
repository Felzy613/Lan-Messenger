using LanMessenger.Core.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Buffers.Binary;

namespace LanMessenger.Tests;

[TestClass]
public class FrameCodecTests
{
    [TestMethod]
    public void EncodeDecodeDictRoundTrip()
    {
        var dict = new Dictionary<string, object?>
        {
            ["type"]    = "text",
            ["message_id"] = "aabb1234",
            ["sender"]  = "Alice",
        };
        var frame = FrameCodec.EncodeDict(dict);
        Assert.IsTrue(frame.Length > 4);

        // Verify big-endian length prefix
        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(0, 4));
        Assert.AreEqual(frame.Length - 4, length);

        // Read back via stream
        using var ms = new MemoryStream(frame);
        var body = FrameCodec.ReadFrame(ms);
        Assert.IsNotNull(body);
        using var doc = FrameCodec.ParseJson(body!);
        Assert.AreEqual("text", doc.RootElement.GetProperty("type").GetString());
    }

    [TestMethod]
    public void BigEndianLengthPrefixIsCorrect()
    {
        var dict  = new Dictionary<string, object?> { ["x"] = "hello" };
        var frame = FrameCodec.EncodeDict(dict);

        // Manually check: first 4 bytes big-endian == body length
        uint prefixedLen = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(0, 4));
        Assert.AreEqual((uint)(frame.Length - 4), prefixedLen);
    }

    [TestMethod]
    public void CleanEofReturnsNull()
    {
        using var ms = new MemoryStream(Array.Empty<byte>());
        var result = FrameCodec.ReadFrame(ms);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void FrameWithSizeZeroThrows()
    {
        // Build a frame with length=0
        var bad = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bad, 0);
        using var ms = new MemoryStream(bad);
        Assert.ThrowsException<InvalidDataException>(() => FrameCodec.ReadFrame(ms));
    }

    [TestMethod]
    public void FrameWithSizeOver50MibThrows()
    {
        var bad = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bad, (uint)(50 * 1024 * 1024 + 1));
        using var ms = new MemoryStream(bad);
        Assert.ThrowsException<InvalidDataException>(() => FrameCodec.ReadFrame(ms));
    }

    [TestMethod]
    public void KnownWireFrame()
    {
        // Encode a minimal packet and verify the wire bytes match the format
        var dict = new Dictionary<string, object?> { ["type"] = "discovery" };
        var frame = FrameCodec.EncodeDict(dict);

        // First 4 bytes should equal body length
        uint len = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(0, 4));
        Assert.AreEqual((uint)(frame.Length - 4), len);

        // Body should be valid UTF-8 JSON
        var body = frame[4..];
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        Assert.AreEqual("discovery", doc.RootElement.GetProperty("type").GetString());
    }
}
