using System.Text.Json.Serialization;

namespace LanMessenger.Core.Protocol;

// UDP discovery (no frame prefix)
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
