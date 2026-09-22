using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanMessenger.Core.Protocol;

// UDP discovery (no frame prefix)
/// <summary>
/// Capability tokens advertised in the optional discovery <c>caps</c> field.
///
/// The field exists because <see cref="PacketValidator"/> drops unknown packet
/// types <i>silently</i>. A client that sends <c>remote_invite</c> to a peer too
/// old to know the type waits forever for a reply that is never coming — so the
/// capability is advertised, and the interface disables the feature for that
/// peer instead of offering something that can only time out.
///
/// Add a token only for an extension that must be negotiated before first use.
/// Extensions that degrade safely — <c>reply_to_*</c>, which an old client simply
/// ignores — must not have one, or every future field becomes a negotiation.
/// </summary>
public static class ProtocolCapability
{
    /// Remote desktop, as specified in PROTOCOL.md -> Remote Desktop.
    public const string RemoteDesktopV1 = "remote-desktop-v1";

    /// What this build implements. Advertised as a statement of capability, not
    /// of willingness: whether a host will <i>accept</i> an invite is a policy
    /// question answered by <c>remote_decline</c>, which is a fast, clear answer
    /// rather than the hang this field exists to prevent.
    public static List<string> Advertised => [RemoteDesktopV1];

    public const int MaxTokens = 16;
    public const int MaxTokenLength = 64;

    /// Normalises a received <c>caps</c> array.
    ///
    /// Tolerant and bounded. Discovery is unauthenticated UDP from anyone on the
    /// LAN and the tokens are retained per peer, so a datagram full of them
    /// should cost nothing; and a peer with a broken capability field is still a
    /// peer, so nothing here is grounds for dropping its beacon.
    public static List<string> Sanitize(List<string>? declared) =>
        declared is null
            ? []
            : [.. declared.Where(t => !string.IsNullOrEmpty(t) && t.Length <= MaxTokenLength)
                          .Take(MaxTokens)];
}

/// <summary>
/// Reads a JSON string array, and treats anything else as absent.
///
/// System.Text.Json's default behaviour here is to throw, and
/// <c>ValidateDiscovery</c> catches that and drops the whole datagram — so a
/// peer with a malformed <c>caps</c> field would vanish from the network
/// entirely, over a field that is optional by definition. Swift's decoder is
/// deliberately tolerant in the same way; the two must agree, because the peer
/// that disappears is only ever the one on the *other* platform.
/// </summary>
public sealed class TolerantStringListConverter : JsonConverter<List<string>?>
{
    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert,
                                       JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return null;
        }

        var values = new List<string>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) return values;
            if (reader.TokenType == JsonTokenType.String)
            {
                values.Add(reader.GetString() ?? "");
            }
            else
            {
                // A non-string entry is skipped rather than fatal, for the same
                // reason the whole field is.
                reader.Skip();
            }
        }
        return values;
    }

    public override void Write(Utf8JsonWriter writer, List<string>? value,
                               JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        writer.WriteStartArray();
        foreach (var token in value) writer.WriteStringValue(token);
        writer.WriteEndArray();
    }
}

public sealed class DiscoveryPacket
{
    [JsonPropertyName("type")]           public string       Type         { get; set; } = "";
    [JsonPropertyName("username")]       public string       Username     { get; set; } = "";
    [JsonPropertyName("port")]           public int          Port         { get; set; }
    [JsonPropertyName("public_key_b64")] public string       PublicKeyB64 { get; set; } = "";
    [JsonPropertyName("ips")]            public List<string> Ips          { get; set; } = [];
    // SHA256(relay_id) hex — the sender's cloud relay mailbox address.
    // Optional: older clients that omit this field are silently handled.
    [JsonPropertyName("relay_id_hash")]  public string?      RelayIdHash  { get; set; }
    // Optional capability tokens. Absent means "assume nothing beyond the base
    // protocol"; unknown tokens must be tolerated, because a newer peer will
    // advertise tokens this build has never heard of.
    [JsonPropertyName("caps")]
    [JsonConverter(typeof(TolerantStringListConverter))]
    public List<string>? Caps { get; set; }

    /// True when the peer advertised remote-desktop support. A peer that
    /// advertises nothing is not assumed capable — that assumption is exactly
    /// the hang this field prevents.
    [JsonIgnore]
    public bool SupportsRemoteDesktop =>
        Caps is not null && Caps.Contains(ProtocolCapability.RemoteDesktopV1);
}

// TCP framed packets
public sealed class TextPacket
{
    [JsonPropertyName("type")]                public string Type              { get; set; } = "text";
    [JsonPropertyName("message_id")]          public string MessageId         { get; set; } = "";
    [JsonPropertyName("timestamp")]           public double Timestamp         { get; set; }
    [JsonPropertyName("sender")]              public string Sender            { get; set; } = "";
    [JsonPropertyName("sender_public_key_b64")] public string SenderPublicKeyB64 { get; set; } = "";
    [JsonPropertyName("port")]                public int    Port              { get; set; }
    [JsonPropertyName("nonce")]               public string Nonce             { get; set; } = "";
    [JsonPropertyName("ciphertext")]          public string Ciphertext        { get; set; } = "";
    // Optional reply metadata — unencrypted top-level fields, ignored by older clients.
    [JsonPropertyName("reply_to_message_id")] public string? ReplyToMessageId { get; set; }
    [JsonPropertyName("reply_to_preview")]    public string? ReplyToPreview   { get; set; }
    [JsonPropertyName("reply_to_sender")]     public string? ReplyToSender    { get; set; }
}

public sealed class TypingPacket
{
    [JsonPropertyName("type")]                public string Type              { get; set; } = "typing";
    [JsonPropertyName("active")]              public bool   Active            { get; set; }
    [JsonPropertyName("sender")]              public string Sender            { get; set; } = "";
    [JsonPropertyName("sender_public_key_b64")] public string SenderPublicKeyB64 { get; set; } = "";
    [JsonPropertyName("port")]                public int    Port              { get; set; }
}

public sealed class ReceiptPacket
{
    [JsonPropertyName("type")]                public string Type              { get; set; } = "";
    [JsonPropertyName("message_id")]          public string MessageId         { get; set; } = "";
    [JsonPropertyName("sender")]              public string Sender            { get; set; } = "";
    [JsonPropertyName("sender_public_key_b64")] public string SenderPublicKeyB64 { get; set; } = "";
    [JsonPropertyName("port")]                public int    Port              { get; set; }
}

public sealed class FileStartPacket
{
    [JsonPropertyName("type")]                public string Type              { get; set; } = "file_start";
    [JsonPropertyName("transfer_id")]         public string TransferId        { get; set; } = "";
    [JsonPropertyName("filename")]            public string Filename          { get; set; } = "";
    [JsonPropertyName("size")]                public long   Size              { get; set; }
    [JsonPropertyName("sender")]              public string Sender            { get; set; } = "";
    [JsonPropertyName("sender_public_key_b64")] public string SenderPublicKeyB64 { get; set; } = "";
    [JsonPropertyName("port")]                public int    Port              { get; set; }
}

public sealed class FileChunkPacket
{
    [JsonPropertyName("type")]                public string Type              { get; set; } = "file_chunk";
    [JsonPropertyName("transfer_id")]         public string TransferId        { get; set; } = "";
    [JsonPropertyName("sender")]              public string Sender            { get; set; } = "";
    [JsonPropertyName("sender_public_key_b64")] public string SenderPublicKeyB64 { get; set; } = "";
    [JsonPropertyName("port")]                public int    Port              { get; set; }
    [JsonPropertyName("nonce")]               public string Nonce             { get; set; } = "";
    [JsonPropertyName("ciphertext")]          public string Ciphertext        { get; set; } = "";
}

public sealed class FileEndPacket
{
    [JsonPropertyName("type")]                public string Type              { get; set; } = "file_end";
    [JsonPropertyName("transfer_id")]         public string TransferId        { get; set; } = "";
    [JsonPropertyName("sender")]              public string Sender            { get; set; } = "";
    [JsonPropertyName("sender_public_key_b64")] public string SenderPublicKeyB64 { get; set; } = "";
    [JsonPropertyName("port")]                public int    Port              { get; set; }
}

// Discriminated union — output of PacketValidator
public abstract class ValidatedPacket
{
    public abstract string SenderIP { get; }
    public abstract string? SenderPublicKeyB64 { get; }

    /// <summary>Whether receiving this packet should refresh the sender's presence.</summary>
    /// <remarks>
    /// Virtual with a "yes" default, overridden only by ValidatedMediaAttach:
    /// media_attach is the last JSON frame on a socket that is about to become a
    /// binary media channel, and treating it as ordinary peer traffic would have
    /// the presence path touching a connection that is no longer a JSON peer.
    /// </remarks>
    public virtual bool RefreshesPresence => true;
}

public sealed class ValidatedText(TextPacket packet, string senderIP) : ValidatedPacket
{
    public TextPacket Packet { get; } = packet;
    public override string  SenderIP           { get; } = senderIP;
    public override string? SenderPublicKeyB64 { get; } = packet.SenderPublicKeyB64;
}

public sealed class ValidatedTyping(TypingPacket packet, string senderIP) : ValidatedPacket
{
    public TypingPacket Packet { get; } = packet;
    public override string  SenderIP           { get; } = senderIP;
    public override string? SenderPublicKeyB64 { get; } = packet.SenderPublicKeyB64;
}

public sealed class ValidatedReceipt(ReceiptPacket packet, string senderIP) : ValidatedPacket
{
    public ReceiptPacket Packet { get; } = packet;
    public override string  SenderIP           { get; } = senderIP;
    public override string? SenderPublicKeyB64 { get; } = packet.SenderPublicKeyB64;
}

public sealed class ValidatedDelete(ReceiptPacket packet, string senderIP) : ValidatedPacket
{
    public ReceiptPacket Packet { get; } = packet;
    public override string  SenderIP           { get; } = senderIP;
    public override string? SenderPublicKeyB64 { get; } = packet.SenderPublicKeyB64;
}

// edit_message reuses TextPacket: identical shape, but MessageId names the
// ORIGINAL message rather than a new one. See PROTOCOL.md -> edit_message.
public sealed class ValidatedEdit(TextPacket packet, string senderIP) : ValidatedPacket
{
    public TextPacket Packet { get; } = packet;
    public override string  SenderIP           { get; } = senderIP;
    public override string? SenderPublicKeyB64 { get; } = packet.SenderPublicKeyB64;
}

public sealed class ValidatedFileStart(FileStartPacket packet, string senderIP) : ValidatedPacket
{
    public FileStartPacket Packet { get; } = packet;
    public override string  SenderIP           { get; } = senderIP;
    public override string? SenderPublicKeyB64 { get; } = packet.SenderPublicKeyB64;
}

public sealed class ValidatedFileChunk(FileChunkPacket packet, string senderIP) : ValidatedPacket
{
    public FileChunkPacket Packet { get; } = packet;
    public override string  SenderIP           { get; } = senderIP;
    public override string? SenderPublicKeyB64 { get; } = packet.SenderPublicKeyB64;
}

public sealed class ValidatedFileEnd(FileEndPacket packet, string senderIP) : ValidatedPacket
{
    public FileEndPacket Packet { get; } = packet;
    public override string  SenderIP           { get; } = senderIP;
    public override string? SenderPublicKeyB64 { get; } = packet.SenderPublicKeyB64;
}

public sealed class ValidatedDiscovery(DiscoveryPacket packet, string senderIP) : ValidatedPacket
{
    public DiscoveryPacket Packet { get; } = packet;
    public override string  SenderIP           { get; } = senderIP;
    public override string? SenderPublicKeyB64 { get; } = packet.PublicKeyB64;
}

// ---- Remote desktop (TCP, framed) -----------------------------------------

// Two shapes cover all five remote-desktop packets.
//
// remote_invite / remote_accept carry a sealed body — the sender's ephemeral
// X25519 key plus the negotiated parameters — encrypted with the ordinary
// session key and AAD'd to session_id. Sealing the ephemeral rather than
// sending it in the clear does not stop an attacker who cannot complete the
// triple DH anyway; it hardens against unauthenticated peers making a host do
// X25519 work, and it authenticates the parameters for free.
//
// session_id itself is plaintext because the receiver must look up the session
// before it can decrypt anything.
public sealed class RemoteSessionPacket
{
    [JsonPropertyName("type")]                  public string Type       { get; set; } = "";
    [JsonPropertyName("session_id")]            public string SessionId  { get; set; } = "";
    [JsonPropertyName("sender")]                public string Sender     { get; set; } = "";
    [JsonPropertyName("sender_public_key_b64")] public string SenderPublicKeyB64 { get; set; } = "";
    [JsonPropertyName("port")]                  public int    Port       { get; set; }
    [JsonPropertyName("nonce")]                 public string Nonce      { get; set; } = "";
    [JsonPropertyName("ciphertext")]            public string Ciphertext { get; set; } = "";
}

// remote_decline, remote_end and media_attach: the spine plus an optional
// machine-readable reason. Nothing here is sensitive — the initiator already
// knows it asked — so `reason` is deliberately unencrypted, which is what lets a
// client show "they have it switched off" rather than a generic failure.
public sealed class RemoteControlPacket
{
    [JsonPropertyName("type")]                  public string  Type      { get; set; } = "";
    [JsonPropertyName("session_id")]            public string  SessionId { get; set; } = "";
    [JsonPropertyName("sender")]                public string  Sender    { get; set; } = "";
    [JsonPropertyName("sender_public_key_b64")] public string  SenderPublicKeyB64 { get; set; } = "";
    [JsonPropertyName("port")]                  public int     Port      { get; set; }
    [JsonPropertyName("reason")]                public string? Reason    { get; set; }
}

public sealed class ValidatedRemoteInvite(RemoteSessionPacket packet, string senderIP) : ValidatedPacket
{
    public RemoteSessionPacket Packet { get; } = packet;
    public override string  SenderIP           { get; } = senderIP;
    public override string? SenderPublicKeyB64 { get; } = packet.SenderPublicKeyB64;
}

public sealed class ValidatedRemoteAccept(RemoteSessionPacket packet, string senderIP) : ValidatedPacket
{
    public RemoteSessionPacket Packet { get; } = packet;
    public override string  SenderIP           { get; } = senderIP;
    public override string? SenderPublicKeyB64 { get; } = packet.SenderPublicKeyB64;
}

public sealed class ValidatedRemoteDecline(RemoteControlPacket packet, string senderIP) : ValidatedPacket
{
    public RemoteControlPacket Packet { get; } = packet;
    public override string  SenderIP           { get; } = senderIP;
    public override string? SenderPublicKeyB64 { get; } = packet.SenderPublicKeyB64;
}

public sealed class ValidatedRemoteEnd(RemoteControlPacket packet, string senderIP) : ValidatedPacket
{
    public RemoteControlPacket Packet { get; } = packet;
    public override string  SenderIP           { get; } = senderIP;
    public override string? SenderPublicKeyB64 { get; } = packet.SenderPublicKeyB64;
}

public sealed class ValidatedMediaAttach(RemoteControlPacket packet, string senderIP) : ValidatedPacket
{
    public RemoteControlPacket Packet { get; } = packet;
    public override string  SenderIP           { get; } = senderIP;
    public override string? SenderPublicKeyB64 { get; } = packet.SenderPublicKeyB64;
    public override bool RefreshesPresence => false;
}
