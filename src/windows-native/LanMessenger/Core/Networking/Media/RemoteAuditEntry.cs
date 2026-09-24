using LanMessenger.Core.Persistence;
using System.Text.Encodings.Web;
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
    // The explicit order is alphabetical because the Swift encoder writes
    // sorted keys; declaration order would put "event" first and every record
    // would differ from the Mac's in its first byte.
    [JsonPropertyName("event"), JsonPropertyOrder(1)]    public string EventToken { get; set; } = "";
    [JsonPropertyName("peerName"), JsonPropertyOrder(2)] public string PeerName { get; set; } = "";
    /// Present on SessionEnded. The wire token, so the stored record survives a
    /// change of wording in the sentence shown for it.
    [JsonPropertyName("reason"), JsonPropertyOrder(3)]   public string? Reason { get; set; }
    /// Seconds. Present on SessionEnded.
    [JsonPropertyName("duration"), JsonPropertyOrder(0)] public double? Duration { get; set; }
    /// <summary>Whether WE were the one watching.</summary>
    /// <remarks>
    /// Optional, and absent means host — both backwards compatible with records
    /// written before this field existed and the right default, since the trail
    /// was designed around the machine whose screen was shared. Without it every
    /// sentence is written from the host's chair, so a PC that spent ten minutes
    /// watching somebody else's screen recorded "You stopped sharing your
    /// screen." in its own history. Written only when true, so a host's records
    /// keep exactly the bytes they had before.
    /// </remarks>
    [JsonPropertyName("viewing"), JsonPropertyOrder(4)]  public bool? Viewing { get; set; }

    /// <summary>True when we were watching. Null is host, for older records.</summary>
    [JsonIgnore] public bool WasViewing => Viewing == true;

    public RemoteAuditRecord() { }

    public RemoteAuditRecord(RemoteAuditEvent e, string peerName,
                             string? reason = null, double? duration = null,
                             bool viewing = false)
    {
        EventToken = Token(e);
        PeerName = peerName;
        Reason = reason;
        Duration = duration;
        Viewing = viewing ? true : null;
    }

    /// The prefix that marks a history entry as an audit record rather than a
    /// message. Every place that inspects message text must know this one: the
    /// sidebar preview (AppModel.LastMessagePreview), the editability guard
    /// (AppModel.IsEditable), the chat row builder (MessageRowViewModel.From)
    /// and the reply/edit banner (MessagingService.ReplyPreviewText). One that
    /// forgets renders raw JSON at the user.
    public const string Marker = "__REMOTE__:";

    public static string Token(RemoteAuditEvent e) => e switch
    {
        RemoteAuditEvent.SessionStarted => "session_started",
        RemoteAuditEvent.ControlGranted => "control_granted",
        RemoteAuditEvent.ControlRevoked => "control_revoked",
        _                               => "session_ended",
    };

    /// <summary>
    /// The decoded event. Ignored by the serializer, like every other derived
    /// member here: System.Text.Json writes get-only properties, so without
    /// these attributes a stored record carried <c>Event</c>, <c>Summary</c> and
    /// <c>DurationSummary</c> beside the four real fields — keys the Swift
    /// record never writes, and rendered sentences frozen into storage that a
    /// change of wording would leave stale. The two platforms are supposed to
    /// produce the same bytes for the same record.
    /// </summary>
    [JsonIgnore] public RemoteAuditEvent? Event => EventToken switch
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
        // The default encoder writes an apostrophe as \u0027 and any accented
        // letter as \u00XX, so "Zoë's PC" was stored in a form the Swift encoder
        // never produces. This is not embedded in HTML anywhere; it is the body
        // of an encrypted history entry. Swift still writes '/' as "\/", which
        // nothing here can match, and both decoders read either spelling.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Encoded() => Marker + JsonSerializer.Serialize(this, Options);

    /// <summary>The history entry that carries this record.</summary>
    /// <remarks>
    /// Not incoming, because an incoming entry counts toward the unread badge
    /// and enters the read-receipt path, and this is a note about what happened
    /// rather than something the peer said. No message id either: there is no
    /// packet it corresponds to, which also puts it out of reach of every
    /// edit_message and delete_message, since both find their target by id.
    /// Mirror of RemoteAuditEntry.historyEntry(at:).
    /// </remarks>
    public MessageEntry HistoryEntry(double timestamp) => new()
    {
        Sender          = "",
        Text            = Encoded(),
        Incoming        = false,
        Timestamp       = timestamp,
        MessageId       = null,
        Status          = "",
        ReadReceiptSent = true,
    };

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

    /// <summary>The sentence to show for a stored text that carries the marker.</summary>
    /// <remarks>
    /// Call only when IsAudit is true. A marked text that will not decode (a
    /// record from a newer build, or a damaged one) is still an audit record,
    /// and its JSON body is the one thing it must never be shown as.
    /// </remarks>
    public static string SummaryOf(string text) => Decode(text)?.Summary ?? UndecodableSummary;

    public const string UndecodableSummary = "Screen sharing event.";

    /// <summary>The sentence shown in the thread and in the sidebar preview.</summary>
    /// <remarks>
    /// Said from whichever chair we were sitting in. A record claiming our
    /// screen was shared when it was not is worse than no record, because this
    /// is the trail somebody reads weeks later to answer exactly that question.
    /// </remarks>
    [JsonIgnore] public string Summary => Event switch
    {
        RemoteAuditEvent.SessionStarted => WasViewing
            ? $"You started viewing {PeerName}'s screen."
            : $"{PeerName} started viewing your screen.",
        RemoteAuditEvent.ControlGranted => WasViewing
            ? $"{PeerName} gave you control of their screen."
            : $"You gave {PeerName} control of your screen.",
        RemoteAuditEvent.ControlRevoked => WasViewing
            ? $"{PeerName} took back control."
            : $"You took back control from {PeerName}.",
        RemoteAuditEvent.SessionEnded   => EndSummary(),
        _                               => "",
    };

    private string EndSummary()
    {
        foreach (RemoteStopReason r in Enum.GetValues<RemoteStopReason>())
        {
            if (r.ToToken() == Reason) return r.AuditDescription(WasViewing);
        }
        return WasViewing
            ? $"Your session with {PeerName} ended."
            : $"Screen sharing with {PeerName} ended.";
    }

    /// Appended when a session ended, so the trail answers "for how long"
    /// without arithmetic on two timestamps.
    [JsonIgnore] public string? DurationSummary
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
