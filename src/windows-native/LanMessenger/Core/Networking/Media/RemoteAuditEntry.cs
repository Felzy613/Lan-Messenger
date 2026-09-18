using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanMessenger.Core.Networking.Media;

// The audit trail. Mirror of RemoteAuditEntry.swift.
//
// The least glamorous requirement in the feature and probably the most useful:
// the question it answers is "was my screen shared last Tuesday, and who was
// watching", asked weeks later by somebody who is not going to read a log file.
// So it lives in the conversation rather than in the log channel.
//
// Storage follows the __FILE__: precedent exactly — an ordinary history entry
// whose text carries a marker prefix and a JSON body — which keeps the history
// format unchanged at the cost of every call site that inspects text needing to
// know one more prefix.

public enum RemoteAuditEvent { SessionStarted, ControlGranted, ControlRevoked, SessionEnded }

public sealed class RemoteAuditRecord
{
    [JsonPropertyName("event")]    public string EventToken { get; set; } = "";
    [JsonPropertyName("peerName")] public string PeerName { get; set; } = "";
    /// Present on SessionEnded. The wire token, so the stored record survives a
    /// change of wording in the sentence shown for it.
    [JsonPropertyName("reason")]   public string? Reason { get; set; }
    /// Seconds. Present on SessionEnded.
    [JsonPropertyName("duration")] public double? Duration { get; set; }

    public RemoteAuditRecord() { }

    public RemoteAuditRecord(RemoteAuditEvent e, string peerName,
                             string? reason = null, double? duration = null)
    {
        EventToken = Token(e);
        PeerName = peerName;
        Reason = reason;
        Duration = duration;
    }

    /// The prefix that marks a history entry as an audit record rather than a
    /// message. Three places inspect message text for a prefix and all three
    /// must know this one; a fourth that forgets renders raw JSON at the user.
    public const string Marker = "__REMOTE__:";

    public static string Token(RemoteAuditEvent e) => e switch
    {
        RemoteAuditEvent.SessionStarted => "session_started",
        RemoteAuditEvent.ControlGranted => "control_granted",
        RemoteAuditEvent.ControlRevoked => "control_revoked",
        _                               => "session_ended",
    };

    public RemoteAuditEvent? Event => EventToken switch
    {
        "session_started" => RemoteAuditEvent.SessionStarted,
        "control_granted" => RemoteAuditEvent.ControlGranted,
        "control_revoked" => RemoteAuditEvent.ControlRevoked,
        "session_ended"   => RemoteAuditEvent.SessionEnded,
        _                 => null,
    };

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Encoded() => Marker + JsonSerializer.Serialize(this, Options);

    /// Decodes a stored text, or null if it is not an audit record. Tolerant of
    /// a record written by a newer build: a history entry that cannot be
    /// understood must never take the conversation down with it.
    public static RemoteAuditRecord? Decode(string text)
    {
        if (!IsAudit(text)) return null;
        try
        {
            var record = JsonSerializer.Deserialize<RemoteAuditRecord>(text[Marker.Length..]);
            return record?.Event is null ? null : record;
        }
        catch (JsonException) { return null; }
    }

    public static bool IsAudit(string text) => text.StartsWith(Marker, StringComparison.Ordinal);

    /// The sentence shown in the thread and in the sidebar preview.
    public string Summary => Event switch
    {
        RemoteAuditEvent.SessionStarted => $"{PeerName} started viewing your screen.",
        RemoteAuditEvent.ControlGranted => $"You gave {PeerName} control of your screen.",
        RemoteAuditEvent.ControlRevoked => $"You took back control from {PeerName}.",
        RemoteAuditEvent.SessionEnded   => EndSummary(),
        _                               => "",
    };

    private string EndSummary()
    {
        foreach (RemoteStopReason r in Enum.GetValues<RemoteStopReason>())
        {
            if (r.ToToken() == Reason) return r.AuditDescription();
        }
        return $"Screen sharing with {PeerName} ended.";
    }

    /// Appended when a session ended, so the trail answers "for how long"
    /// without arithmetic on two timestamps.
    public string? DurationSummary
    {
        get
        {
            if (Duration is not { } d || d < 1) return null;
            int seconds = (int)d, hours = seconds / 3600,
                minutes = (seconds % 3600) / 60, rest = seconds % 60;
            if (hours > 0) return $"Lasted {hours}h {minutes}m.";
            if (minutes > 0) return $"Lasted {minutes}m {rest}s.";
            return $"Lasted {rest}s.";
        }
    }
}
