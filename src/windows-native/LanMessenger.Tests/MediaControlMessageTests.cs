using LanMessenger.Core.Networking.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;
using System.Text.Json;

namespace LanMessenger.Tests;

// The control sub-channel codec, against the same shared vector the Swift suite
// asserts. Mirror of MediaControlMessageTests.swift.
//
// The vector is the important part. Every other test here could pass on both
// platforms while the two encoders disagreed about key order, number formatting
// or string escaping — and the symptom of that is not a test failure, it is a
// session that works Mac-to-Mac and Windows-to-Windows and fails across.
[TestClass]
public class MediaControlMessageTests
{
    private static Dictionary<string, string> Vector()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "media_control_vector.json");
        if (!File.Exists(path)) path = "media_control_vector.json";
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var map = new Dictionary<string, string>();
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            map[item.GetProperty("name").GetString()!] = item.GetProperty("json").GetString()!;
        }
        return map;
    }

    /// The same cases as the Swift generator, in the same order.
    private static List<(string Name, MediaControlMessage Message)> Cases() =>
    [
        ("hello", MediaControlMessage.Hello("dHJhbnNjcmlwdA==")),
        ("hello_ack", MediaControlMessage.HelloAck()),
        ("video_config", MediaControlMessage.Video(new VideoConfig
            { Width = 1920, Height = 1080, Scale = 2, DisplayId = 69732928, Codec = "h264" })),
        ("keyframe_request", MediaControlMessage.KeyframeRequest("presenter_flush")),
        ("control_request", MediaControlMessage.ControlRequest()),
        ("control_grant", MediaControlMessage.ControlGrant()),
        ("control_revoke", MediaControlMessage.ControlRevoke()),
        ("display_list", MediaControlMessage.DisplayList(
        [
            new RemoteDisplayInfo { DisplayId = 1, Width = 1920, Height = 1080,
                                    IsPrimary = true, Name = "Built-in Display" },
            new RemoteDisplayInfo { DisplayId = 2, Width = 2560, Height = 1440,
                                    IsPrimary = false, Name = "Dell U2718Q" },
        ])),
        ("display_select", MediaControlMessage.DisplaySelect(2)),
        ("host_state", MediaControlMessage.Host(new RemoteHostState { SecureDesktop = true })),
        ("ping", MediaControlMessage.Ping(42, 1700000000123456)),
        ("pong", MediaControlMessage.Pong(42, 1700000000123456)),
        ("stats", MediaControlMessage.Statistics(new RemoteSessionStats
            { RoundTripMs = 12, DecodedFps = 30, DroppedFrames = 3,
              DecodeQueueDepth = 1, EndToEndLatencyMs = 48 })),
    ];

    // ---- Conformance -------------------------------------------------------

    [TestMethod]
    public void EveryCaseEncodesToExactlyTheSharedVector()
    {
        var expected = Vector();
        foreach (var (name, message) in Cases())
        {
            var encoded = Encoding.UTF8.GetString(MediaControlCodec.Encode(message));
            Assert.AreEqual(expected[name], encoded, $"{name} does not match the shared vector");
        }
    }

    [TestMethod]
    public void EveryCaseDecodesBackFromTheSharedVector()
    {
        var expected = Vector();
        foreach (var (name, message) in Cases())
        {
            var decoded = MediaControlCodec.Decode(Encoding.UTF8.GetBytes(expected[name]));
            Assert.AreEqual(message.Kind, decoded.Kind, $"{name} decoded to the wrong kind");
            Assert.AreEqual(message.Type, decoded.Type);

            switch (message.Kind)
            {
                case MediaControlType.Hello:
                    Assert.AreEqual(message.TranscriptB64, decoded.TranscriptB64); break;
                case MediaControlType.VideoConfig:
                    Assert.AreEqual(message.Config, decoded.Config); break;
                case MediaControlType.KeyframeRequest:
                    Assert.AreEqual(message.Reason, decoded.Reason); break;
                case MediaControlType.DisplayList:
                    CollectionAssert.AreEqual(message.Displays, decoded.Displays); break;
                case MediaControlType.DisplaySelect:
                    Assert.AreEqual(message.DisplayId, decoded.DisplayId); break;
                case MediaControlType.HostState:
                    Assert.AreEqual(message.State, decoded.State); break;
                case MediaControlType.Ping:
                case MediaControlType.Pong:
                    Assert.AreEqual(message.Id, decoded.Id);
                    Assert.AreEqual(message.SentUs, decoded.SentUs); break;
                case MediaControlType.Stats:
                    Assert.AreEqual(message.Stats, decoded.Stats); break;
            }
        }
    }

    [TestMethod]
    public void TheVectorCoversEveryMessageTypeWeCanSend()
    {
        // A type added without a vector case is one whose cross-platform
        // encoding nothing checks.
        Assert.AreEqual(13, Vector().Count);
        Assert.AreEqual(13, Cases().Count);
    }

    // ---- Forward compatibility ---------------------------------------------

    [TestMethod]
    public void AnUnknownTypeDecodesRatherThanThrows()
    {
        var decoded = MediaControlCodec.Decode(
            Encoding.UTF8.GetBytes("""{"t":"clipboard_v2","payload":"x"}"""));
        Assert.AreEqual(MediaControlType.Unknown, decoded.Kind);
        Assert.AreEqual("clipboard_v2", decoded.Type, "the name survives, so a log can say it");
    }

    [TestMethod]
    public void AKnownTypeWithExtraFieldsStillDecodes()
    {
        var decoded = MediaControlCodec.Decode(
            Encoding.UTF8.GetBytes("""{"t":"keyframe_request","reason":"flush","urgency":"high"}"""));
        Assert.AreEqual(MediaControlType.KeyframeRequest, decoded.Kind);
        Assert.AreEqual("flush", decoded.Reason);
    }

    // ---- Hostile input -----------------------------------------------------

    [TestMethod]
    public void AnOversizedPayloadIsRefusedOnBothSides()
    {
        var huge = new byte[MediaControlCodec.MaxPayloadBytes + 1];
        Array.Fill(huge, (byte)' ');
        Assert.ThrowsException<MediaControlCodecException>(() => MediaControlCodec.Decode(huge));

        var longReason = new string('x', MediaControlCodec.MaxPayloadBytes);
        Assert.ThrowsException<MediaControlCodecException>(
            () => MediaControlCodec.Encode(MediaControlMessage.KeyframeRequest(longReason)));
    }

    [TestMethod]
    public void MalformedPayloadsFaultRatherThanGuess()
    {
        foreach (var bad in new[] { "not json", "[1,2,3]", "{}", """{"t":""}""",
                                    """{"t":"video_config"}""" })
        {
            Assert.ThrowsException<MediaControlCodecException>(
                () => MediaControlCodec.Decode(Encoding.UTF8.GetBytes(bad)), $"accepted {bad}");
        }
    }

    [TestMethod]
    public void AnImplausibleVideoConfigIsRecognisedAsSuch()
    {
        // Decoding it is fine; acting on it is not. A config claiming two
        // million pixels wide is not a resolution, it is an allocation request.
        Assert.IsTrue(new VideoConfig { Width = 1920, Height = 1080 }.IsPlausible);
        Assert.IsFalse(new VideoConfig { Width = 0, Height = 1080 }.IsPlausible);
        Assert.IsFalse(new VideoConfig { Width = 2_000_000, Height = 1080 }.IsPlausible);
        Assert.IsFalse(new VideoConfig { Width = 1920, Height = -1 }.IsPlausible);
        Assert.IsFalse(new VideoConfig { Width = 1920, Height = 1080, Scale = 0 }.IsPlausible);
        Assert.IsFalse(new VideoConfig { Width = 1920, Height = 1080, Scale = 99 }.IsPlausible);
    }

    [TestMethod]
    public void OneBadDisplayDoesNotCostThePicker()
    {
        // Losing one monitor from a picker is recoverable; losing the picker is
        // not. On the Swift side the naive version of this was worse than a
        // dropped entry — JSONSerialization raises an uncatchable ObjC exception
        // for a non-container value, which would have taken the read loop down.
        var json = """{"t":"display_list","displays":[{"display_id":1,"width":1920,"height":1080,"is_primary":true,"name":"Good"},"nonsense"]}""";
        var decoded = MediaControlCodec.Decode(Encoding.UTF8.GetBytes(json));
        Assert.AreEqual(MediaControlType.DisplayList, decoded.Kind);
        Assert.AreEqual(1, decoded.Displays.Count);
        Assert.AreEqual("Good", decoded.Displays[0].Name);
    }

    // ---- Host state --------------------------------------------------------

    [TestMethod]
    public void AnOrdinaryHostStateSaysNothing()
    {
        Assert.IsFalse(new RemoteHostState().IsNotable);
        Assert.IsTrue(new RemoteHostState { SecureDesktop = true }.IsNotable);
        Assert.IsTrue(new RemoteHostState { ElevatedFocus = true }.IsNotable);
        Assert.IsTrue(new RemoteHostState { Locked = true }.IsNotable);
    }

    // ---- Round trip --------------------------------------------------------

    [TestMethod]
    public void EverySendableMessageRoundTrips()
    {
        foreach (var (name, message) in Cases())
        {
            var again = MediaControlCodec.Decode(MediaControlCodec.Encode(message));
            Assert.AreEqual(message.Kind, again.Kind, $"{name} did not survive its own codec");
        }
    }
}
