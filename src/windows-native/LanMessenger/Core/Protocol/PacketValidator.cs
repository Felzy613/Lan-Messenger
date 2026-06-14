using System.Text.Json;

namespace LanMessenger.Core.Protocol;

public static class PacketValidator
{
    private static readonly JsonSerializerOptions _opts = new() { PropertyNameCaseInsensitive = false };

    // Validate a TCP frame body. Returns a ValidatedPacket or null (silently dropped).
    public static ValidatedPacket? Validate(byte[] data, string senderIP, string ownPublicKeyB64)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp)) return null;
            var typeStr = typeProp.GetString() ?? "";

            // Self-suppression by public key
            if (root.TryGetProperty("sender_public_key_b64", out var keyProp) &&
                keyProp.GetString() == ownPublicKeyB64)
                return null;

            switch (typeStr)
            {
                case "text":
                {
                    var pkt = JsonSerializer.Deserialize<TextPacket>(data);
                    if (pkt is null || string.IsNullOrEmpty(pkt.MessageId)) return null;
                    if (!ValidateNonce(pkt.Nonce)) return null;
                    return new ValidatedText(pkt, senderIP);
                }
                case "typing":
                {
                    var pkt = JsonSerializer.Deserialize<TypingPacket>(data);
                    if (pkt is null) return null;
                    return new ValidatedTyping(pkt, senderIP);
                }
                case "sent_receipt":
                case "read_receipt":
                {
                    var pkt = JsonSerializer.Deserialize<ReceiptPacket>(data);
                    if (pkt is null || string.IsNullOrEmpty(pkt.MessageId)) return null;
                    return new ValidatedReceipt(pkt, senderIP);
                }
                case "delete_message":
                {
                    var pkt = JsonSerializer.Deserialize<ReceiptPacket>(data);
                    if (pkt is null || string.IsNullOrEmpty(pkt.MessageId)) return null;
                    return new ValidatedDelete(pkt, senderIP);
                }
                case "file_start":
                {
                    var pkt = JsonSerializer.Deserialize<FileStartPacket>(data);
                    if (pkt is null) return null;
                    if (pkt.Size < 0 || pkt.Size > 2L * 1024 * 1024 * 1024) return null;
                    return new ValidatedFileStart(pkt, senderIP);
                }
                case "file_chunk":
                {
                    var pkt = JsonSerializer.Deserialize<FileChunkPacket>(data);
                    if (pkt is null) return null;
                    if (!ValidateNonce(pkt.Nonce)) return null;
                    return new ValidatedFileChunk(pkt, senderIP);
                }
                case "file_end":
                {
                    var pkt = JsonSerializer.Deserialize<FileEndPacket>(data);
                    if (pkt is null) return null;
                    return new ValidatedFileEnd(pkt, senderIP);
                }
                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }
    }

    // Validate a UDP datagram as a discovery packet.
    public static DiscoveryPacket? ValidateDiscovery(
        byte[] data, string senderIP, string ownPublicKeyB64, HashSet<string> ownIPs)
    {
        // Drop own-sourced packets by IP
        if (ownIPs.Contains(senderIP)) return null;

        try
        {
            var pkt = JsonSerializer.Deserialize<DiscoveryPacket>(data);
            if (pkt is null) return null;
            if (pkt.Type != "discovery" && pkt.Type != "discovery_reply" && pkt.Type != "goodbye") return null;
            if (string.IsNullOrEmpty(pkt.PublicKeyB64)) return null;
            if (pkt.PublicKeyB64 == ownPublicKeyB64) return null;
            return pkt;
        }
        catch
        {
            return null;
        }
    }

    // Nonce must be exactly 12 bytes when base64-decoded.
    public static bool ValidateNonce(string b64)
    {
        try
        {
            var bytes = Convert.FromBase64String(b64);
            return bytes.Length == 12;
        }
        catch { return false; }
    }

    // Windows-aware filename sanitization.
    // Path.GetFileName handles both '/' and '\' separators on Windows.
    public static string SanitizeFilename(string name)
    {
        var component = Path.GetFileName(name.Replace('/', Path.DirectorySeparatorChar));
        var stripped = component.Trim().Replace("\0", "");
        return stripped.Length == 0 ? "file" : stripped;
    }
}
