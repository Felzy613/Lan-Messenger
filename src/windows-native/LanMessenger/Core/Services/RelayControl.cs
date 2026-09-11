using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanMessenger.Core.Services;

public enum RelayControlOp { Edit, Delete }

/// <summary>
/// An edit or delete that has to reach a peer who isn't on the LAN.
///
/// edit_message and delete_message are LAN-only, one-shot TCP writes: if the
/// peer is offline when you edit or delete, the change never reaches them. This
/// carries the same two operations through the cloud relay mailbox instead, so
/// the peer applies it the next time they poll.
///
/// Shape on the wire: the envelope is the *plaintext* of an ordinary relay
/// record. The Worker is untouched and still sees nothing but ciphertext — it
/// can't tell a control record from a chat message, and it never learns which
/// message was edited or deleted. Two consequences that matter:
///
///  * The record is stored under its own fresh message_id, never the target's.
///    The Worker dedups /store on message_id and returns {ok:true,
///    duplicate:true} for a repeat, so re-uploading under the original's id
///    would be silently discarded while reporting success. A fresh id also
///    means this works whether or not the original is still in the mailbox.
///  * A client older than 1.7 has no idea what this is and renders the marker
///    line as a literal chat message. Both ends need 1.7+ for relayed edits.
/// </summary>
public sealed record RelayControlEnvelope(RelayControlOp Op, string Target, string? Text, double At)
{
    /// Prefix that marks a relay plaintext as a control envelope rather than a
    /// chat body. Same convention as the "__FILE__:" prefix used for
    /// attachments in history.
    public const string Marker = "__CTRL__:";

    private sealed class Payload
    {
        [JsonPropertyName("op")]     public string  Op     { get; set; } = "";
        [JsonPropertyName("target")] public string  Target { get; set; } = "";
        [JsonPropertyName("text")]   public string? Text   { get; set; }
        [JsonPropertyName("at")]     public double  At     { get; set; }
    }

    public string Encoded()
    {
        var payload = new Payload
        {
            Op     = Op == RelayControlOp.Edit ? "edit" : "delete",
            Target = Target,
            Text   = Text,
            At     = At,
        };
        return Marker + JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// Parses a decrypted relay plaintext. Returns null for ordinary chat text,
    /// which is the overwhelmingly common case, so the check stays a cheap
    /// prefix test before any JSON work.
    /// </summary>
    public static RelayControlEnvelope? Decode(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || !plaintext.StartsWith(Marker, StringComparison.Ordinal))
            return null;

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(plaintext[Marker.Length..]);
        }
        catch (JsonException)
        {
            return null;
        }
        if (payload is null) return null;

        RelayControlOp op;
        switch (payload.Op)
        {
            case "edit":   op = RelayControlOp.Edit;   break;
            case "delete": op = RelayControlOp.Delete; break;
            default: return null;
        }

        // A target that isn't a message id can't match anything; reject it here
        // rather than letting it reach the history store.
        if (!IsMessageId(payload.Target)) return null;
        // An edit with no replacement body would blank the message.
        if (op == RelayControlOp.Edit && string.IsNullOrEmpty(payload.Text)) return null;

        return new RelayControlEnvelope(op, payload.Target, payload.Text, payload.At);
    }

    /// 32 lowercase hex characters — the uuid4().hex form PROTOCOL.md requires
    /// of every message_id.
    public static bool IsMessageId(string? s)
    {
        if (s is null || s.Length != 32) return false;
        foreach (var c in s)
        {
            var ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!ok) return false;
        }
        return true;
    }

    /// Fresh id for the relay record that carries this envelope. Deliberately
    /// not the target's id — see the type's note on Worker dedup.
    public static string NewRecordId() => Guid.NewGuid().ToString("N").ToLowerInvariant();
}
