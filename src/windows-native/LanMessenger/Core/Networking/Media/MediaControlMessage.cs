using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanMessenger.Core.Networking.Media;

// The control sub-channel: channel 0, UTF-8 JSON objects, each with a `t`
// discriminator. Mirror of the macOS MediaControlMessage.swift.
//
// The two encoders must produce byte-identical output for the same message,
// which is what makes a shared conformance vector possible. Two things get in
// the way of that on this side and both are handled here:
//
//   * System.Text.Json writes properties in declaration order and has no
//     sort option, so every object is written by hand with Utf8JsonWriter in
//     sorted key order to match Foundation's .sortedKeys.
//   * System.Text.Json escapes '/' and every non-ASCII character by default,
//     where Foundation does not. UnsafeRelaxedJsonEscaping turns that off. The
//     same trap bit the handshake transcript, which is why that is hand-rolled
//     too — one byte of difference and the two sides disagree.

public sealed class VideoConfig
{
    [JsonPropertyName("width")]      public int    Width     { get; set; }
    [JsonPropertyName("height")]     public int    Height    { get; set; }
    [JsonPropertyName("scale")]      public double Scale     { get; set; } = 1;
    [JsonPropertyName("display_id")] public uint   DisplayId { get; set; }
    [JsonPropertyName("codec")]      public string Codec     { get; set; } = "h264";

    /// The largest picture either platform will agree to describe. Well past any
    /// real display, and small enough that a peer cannot use the field as an
    /// allocation primitive.
    public const int MaxDimension = 16384;

    [JsonIgnore]
    public bool IsPlausible =>
        Width > 0 && Height > 0 && Width <= MaxDimension && Height <= MaxDimension
        && Scale > 0 && Scale <= 8;

    public override bool Equals(object? obj) =>
        obj is VideoConfig o && o.Width == Width && o.Height == Height
        && o.Scale.Equals(Scale) && o.DisplayId == DisplayId && o.Codec == Codec;

    public override int GetHashCode() => HashCode.Combine(Width, Height, Scale, DisplayId, Codec);
}

public sealed class RemoteDisplayInfo
{
    [JsonPropertyName("display_id")] public uint   DisplayId { get; set; }
    [JsonPropertyName("width")]      public int    Width     { get; set; }
    [JsonPropertyName("height")]     public int    Height    { get; set; }
    [JsonPropertyName("is_primary")] public bool   IsPrimary { get; set; }
    [JsonPropertyName("name")]       public string Name      { get; set; } = "";

    public override bool Equals(object? obj) =>
        obj is RemoteDisplayInfo o && o.DisplayId == DisplayId && o.Width == Width
        && o.Height == Height && o.IsPrimary == IsPrimary && o.Name == Name;

    public override int GetHashCode() => HashCode.Combine(DisplayId, Width, Height, IsPrimary, Name);
}

/// What turns an inexplicable frozen image into an explanation. A Windows host
/// cannot capture or drive the secure desktop, and cannot inject into a focused
/// elevated window; neither is a bug and neither is fixable from user mode, so
/// the viewer is told and shows a banner rather than a mystery.
public sealed class RemoteHostState
{
    [JsonPropertyName("secure_desktop")] public bool SecureDesktop { get; set; }
    [JsonPropertyName("elevated_focus")] public bool ElevatedFocus { get; set; }
    [JsonPropertyName("locked")]         public bool Locked        { get; set; }

    [JsonIgnore]
    public bool IsNotable => SecureDesktop || ElevatedFocus || Locked;

    public override bool Equals(object? obj) =>
        obj is RemoteHostState o && o.SecureDesktop == SecureDesktop
        && o.ElevatedFocus == ElevatedFocus && o.Locked == Locked;

    public override int GetHashCode() => HashCode.Combine(SecureDesktop, ElevatedFocus, Locked);
}

public sealed class RemoteSessionStats
{
    [JsonPropertyName("rtt_ms")]        public int RoundTripMs       { get; set; }
    [JsonPropertyName("decoded_fps")]   public int DecodedFps        { get; set; }
    [JsonPropertyName("dropped_frames")] public int DroppedFrames    { get; set; }
    [JsonPropertyName("decode_queue")]  public int DecodeQueueDepth  { get; set; }
    /// Glass-to-glass, derived from capture_us. Negative is possible when the two
    /// machines' clocks disagree, and is reported rather than hidden — a nonsense
    /// latency figure is a clock problem worth seeing.
    [JsonPropertyName("latency_ms")]    public int EndToEndLatencyMs { get; set; }

    public override bool Equals(object? obj) =>
        obj is RemoteSessionStats o && o.RoundTripMs == RoundTripMs
        && o.DecodedFps == DecodedFps && o.DroppedFrames == DroppedFrames
        && o.DecodeQueueDepth == DecodeQueueDepth && o.EndToEndLatencyMs == EndToEndLatencyMs;

    public override int GetHashCode() =>
        HashCode.Combine(RoundTripMs, DecodedFps, DroppedFrames, DecodeQueueDepth, EndToEndLatencyMs);
}

public enum MediaControlType
{
    Hello, HelloAck, VideoConfig, KeyframeRequest, ControlRequest, ControlGrant,
    ControlRevoke, DisplayList, DisplaySelect, HostState, Ping, Pong, Stats, Unknown,
}

public sealed class MediaControlMessage
{
    public MediaControlType Kind { get; init; }
    /// The wire value of `t`. Carried verbatim so an unknown message can be
    /// logged by name.
    public string Type { get; init; } = "";

    public string TranscriptB64 { get; init; } = "";
    public VideoConfig? Config { get; init; }
    public string Reason { get; init; } = "";
    public List<RemoteDisplayInfo> Displays { get; init; } = [];
    public uint DisplayId { get; init; }
    public RemoteHostState? State { get; init; }
    public ulong Id { get; init; }
    public ulong SentUs { get; init; }
    public RemoteSessionStats? Stats { get; init; }

    public static MediaControlMessage Hello(string transcriptB64) =>
        new() { Kind = MediaControlType.Hello, Type = "hello", TranscriptB64 = transcriptB64 };
    public static MediaControlMessage HelloAck() =>
        new() { Kind = MediaControlType.HelloAck, Type = "hello_ack" };
    public static MediaControlMessage Video(VideoConfig config) =>
        new() { Kind = MediaControlType.VideoConfig, Type = "video_config", Config = config };
    public static MediaControlMessage KeyframeRequest(string reason) =>
        new() { Kind = MediaControlType.KeyframeRequest, Type = "keyframe_request", Reason = reason };
    public static MediaControlMessage ControlRequest() =>
        new() { Kind = MediaControlType.ControlRequest, Type = "control_request" };
    public static MediaControlMessage ControlGrant() =>
        new() { Kind = MediaControlType.ControlGrant, Type = "control_grant" };
    public static MediaControlMessage ControlRevoke() =>
        new() { Kind = MediaControlType.ControlRevoke, Type = "control_revoke" };
    public static MediaControlMessage DisplayList(List<RemoteDisplayInfo> displays) =>
        new() { Kind = MediaControlType.DisplayList, Type = "display_list", Displays = displays };
    public static MediaControlMessage DisplaySelect(uint displayId) =>
        new() { Kind = MediaControlType.DisplaySelect, Type = "display_select", DisplayId = displayId };
    public static MediaControlMessage Host(RemoteHostState state) =>
        new() { Kind = MediaControlType.HostState, Type = "host_state", State = state };
    public static MediaControlMessage Ping(ulong id, ulong sentUs) =>
        new() { Kind = MediaControlType.Ping, Type = "ping", Id = id, SentUs = sentUs };
    public static MediaControlMessage Pong(ulong id, ulong sentUs) =>
        new() { Kind = MediaControlType.Pong, Type = "pong", Id = id, SentUs = sentUs };
    public static MediaControlMessage Statistics(RemoteSessionStats stats) =>
        new() { Kind = MediaControlType.Stats, Type = "stats", Stats = stats };
    public static MediaControlMessage Unknown(string type) =>
        new() { Kind = MediaControlType.Unknown, Type = type };
}

public sealed class MediaControlCodecException(string message) : Exception(message);

public static class MediaControlCodec
{
    /// A control frame is a few hundred bytes. The media frame cap is 4 MiB, so
    /// without a second limit a peer can make a host parse four megabytes of JSON
    /// per frame on the session's read queue, for free.
    public const int MaxPayloadBytes = 64 * 1024;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // Foundation does not escape '/' or non-ASCII; System.Text.Json does
        // both by default. One byte of difference and the two sides disagree.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    // ---- Encode ------------------------------------------------------------

    public static byte[] Encode(MediaControlMessage message)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            w.WriteStartObject();
            // Sorted key order throughout, by hand, to match Foundation's
            // .sortedKeys. System.Text.Json writes declaration order and offers
            // no sort option.
            switch (message.Kind)
            {
                case MediaControlType.VideoConfig:
                    WriteConfig(w, "config", message.Config ?? new VideoConfig());
                    w.WriteString("t", message.Type);
                    break;
                case MediaControlType.DisplaySelect:
                    w.WriteNumber("display_id", message.DisplayId);
                    w.WriteString("t", message.Type);
                    break;
                case MediaControlType.DisplayList:
                    w.WriteStartArray("displays");
                    foreach (var display in message.Displays) WriteDisplay(w, display);
                    w.WriteEndArray();
                    w.WriteString("t", message.Type);
                    break;
                case MediaControlType.Ping:
                case MediaControlType.Pong:
                    w.WriteNumber("id", message.Id);
                    w.WriteNumber("sent_us", message.SentUs);
                    w.WriteString("t", message.Type);
                    break;
                case MediaControlType.KeyframeRequest:
                    w.WriteString("reason", message.Reason);
                    w.WriteString("t", message.Type);
                    break;
                case MediaControlType.HostState:
                    WriteState(w, "state", message.State ?? new RemoteHostState());
                    w.WriteString("t", message.Type);
                    break;
                case MediaControlType.Stats:
                    WriteStats(w, "stats", message.Stats ?? new RemoteSessionStats());
                    w.WriteString("t", message.Type);
                    break;
                case MediaControlType.Hello:
                    w.WriteString("t", message.Type);
                    w.WriteString("transcript_b64", message.TranscriptB64);
                    break;
                default:
                    w.WriteString("t", message.Type);
                    break;
            }
            w.WriteEndObject();
        }

        var bytes = stream.ToArray();
        if (bytes.Length > MaxPayloadBytes)
        {
            throw new MediaControlCodecException(
                $"control payload of {bytes.Length} bytes exceeds the cap");
        }
        return bytes;
    }

    private static void WriteConfig(Utf8JsonWriter w, string name, VideoConfig c)
    {
        w.WriteStartObject(name);
        w.WriteString("codec", c.Codec);
        w.WriteNumber("display_id", c.DisplayId);
        w.WriteNumber("height", c.Height);
        WriteScale(w, "scale", c.Scale);
        w.WriteNumber("width", c.Width);
        w.WriteEndObject();
    }

    private static void WriteDisplay(Utf8JsonWriter w, RemoteDisplayInfo d)
    {
        w.WriteStartObject();
        w.WriteNumber("display_id", d.DisplayId);
        w.WriteNumber("height", d.Height);
        w.WriteBoolean("is_primary", d.IsPrimary);
        w.WriteString("name", d.Name);
        w.WriteNumber("width", d.Width);
        w.WriteEndObject();
    }

    private static void WriteState(Utf8JsonWriter w, string name, RemoteHostState s)
    {
        w.WriteStartObject(name);
        w.WriteBoolean("elevated_focus", s.ElevatedFocus);
        w.WriteBoolean("locked", s.Locked);
        w.WriteBoolean("secure_desktop", s.SecureDesktop);
        w.WriteEndObject();
    }

    private static void WriteStats(Utf8JsonWriter w, string name, RemoteSessionStats s)
    {
        w.WriteStartObject(name);
        w.WriteNumber("decode_queue", s.DecodeQueueDepth);
        w.WriteNumber("decoded_fps", s.DecodedFps);
        w.WriteNumber("dropped_frames", s.DroppedFrames);
        w.WriteNumber("latency_ms", s.EndToEndLatencyMs);
        w.WriteNumber("rtt_ms", s.RoundTripMs);
        w.WriteEndObject();
    }

    /// Foundation writes a whole-valued Double without a decimal point, so 1.0
    /// becomes `1`. Matching that is what keeps the encoded bytes identical.
    private static void WriteScale(Utf8JsonWriter w, string name, double value)
    {
        if (value == Math.Floor(value) && Math.Abs(value) < 1e15)
        {
            w.WriteNumber(name, (long)value);
        }
        else
        {
            w.WriteNumber(name, value);
        }
    }

    // ---- Decode ------------------------------------------------------------

    public static MediaControlMessage Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxPayloadBytes)
        {
            throw new MediaControlCodecException(
                $"control payload of {data.Length} bytes exceeds the cap");
        }

        JsonDocument document;
        try { document = JsonDocument.Parse(data.ToArray()); }
        catch (JsonException) { throw new MediaControlCodecException("control payload is not JSON"); }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new MediaControlCodecException("control payload is not a JSON object");
            }
            if (!root.TryGetProperty("t", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(typeElement.GetString()))
            {
                throw new MediaControlCodecException("control payload has no `t`");
            }

            var type = typeElement.GetString()!;
            switch (type)
            {
                case "hello":
                    return MediaControlMessage.Hello(GetString(root, "transcript_b64"));
                case "hello_ack":
                    return MediaControlMessage.HelloAck();
                case "video_config":
                    return MediaControlMessage.Video(
                        Deserialize<VideoConfig>(root, "config", type));
                case "keyframe_request":
                    return MediaControlMessage.KeyframeRequest(GetString(root, "reason"));
                case "control_request":
                    return MediaControlMessage.ControlRequest();
                case "control_grant":
                    return MediaControlMessage.ControlGrant();
                case "control_revoke":
                    return MediaControlMessage.ControlRevoke();
                case "display_list":
                {
                    if (!root.TryGetProperty("displays", out var list) ||
                        list.ValueKind != JsonValueKind.Array)
                    {
                        throw new MediaControlCodecException("`display_list` is malformed");
                    }
                    // A single unparseable entry drops that display rather than
                    // the whole list: losing one monitor from a picker is
                    // recoverable, losing the picker is not.
                    var displays = new List<RemoteDisplayInfo>();
                    foreach (var item in list.EnumerateArray())
                    {
                        try
                        {
                            var parsed = item.Deserialize<RemoteDisplayInfo>();
                            if (parsed is not null) displays.Add(parsed);
                        }
                        catch (JsonException) { }
                    }
                    return MediaControlMessage.DisplayList(displays);
                }
                case "display_select":
                {
                    if (!root.TryGetProperty("display_id", out var id) ||
                        !id.TryGetUInt32(out var value))
                    {
                        throw new MediaControlCodecException("`display_select` is malformed");
                    }
                    return MediaControlMessage.DisplaySelect(value);
                }
                case "host_state":
                    return MediaControlMessage.Host(
                        Deserialize<RemoteHostState>(root, "state", type));
                case "ping":
                case "pong":
                {
                    var id = root.TryGetProperty("id", out var i) && i.TryGetUInt64(out var iv) ? iv : 0;
                    var sent = root.TryGetProperty("sent_us", out var s) && s.TryGetUInt64(out var sv) ? sv : 0;
                    return type == "ping"
                        ? MediaControlMessage.Ping(id, sent)
                        : MediaControlMessage.Pong(id, sent);
                }
                case "stats":
                    return MediaControlMessage.Statistics(
                        Deserialize<RemoteSessionStats>(root, "stats", type));
                default:
                    // The whole point of a discriminated channel is that it can
                    // grow. Faulting here would turn every future extension into
                    // a flag-day upgrade.
                    return MediaControlMessage.Unknown(type);
            }
        }
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() ?? "" : "";

    private static T Deserialize<T>(JsonElement root, string name, string type) where T : class
    {
        if (!root.TryGetProperty(name, out var element))
        {
            throw new MediaControlCodecException($"`{type}` is malformed");
        }
        try
        {
            return element.Deserialize<T>()
                   ?? throw new MediaControlCodecException($"`{type}` is malformed");
        }
        catch (JsonException)
        {
            throw new MediaControlCodecException($"`{type}` is malformed");
        }
    }
}
