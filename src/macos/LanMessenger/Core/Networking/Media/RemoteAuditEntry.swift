import Foundation

// The audit trail.
//
// PROTOCOL.md's consent rules end with one line: "session start, stop, and every
// control grant are recorded in the conversation history as an audit trail".
// It is the least glamorous requirement in the feature and probably the most
// useful — the question it answers is "was my screen shared last Tuesday, and
// who was watching", asked weeks later by somebody who is not going to read a
// log file.
//
// Which is why it lives in the conversation rather than in the `remote` log
// channel. Logs rotate, and nobody opens them. The thread with that peer is
// where a user already looks to find out what happened between the two of them.
//
// Storage follows the `__FILE__:` precedent exactly: an ordinary `MessageEntry`
// whose `text` carries a marker prefix and a JSON body. That keeps the history
// format unchanged — no new optional fields to migrate, no decode path that
// older builds trip over — at the cost of every call site that inspects `text`
// needing to know about one more prefix. There are three, and they are named in
// `RemoteAuditEntry.marker`'s documentation.

struct RemoteAuditEntry: Codable, Equatable {

    enum Event: String, Codable, Equatable, CaseIterable {
        case sessionStarted = "session_started"
        case controlGranted = "control_granted"
        case controlRevoked = "control_revoked"
        case sessionEnded = "session_ended"
    }

    let event: Event
    let peerName: String
    /// Present on `sessionEnded`. The wire token, so the stored record survives
    /// a change of wording in the sentence shown for it.
    let reason: String?
    /// Seconds. Present on `sessionEnded`.
    let duration: Double?

    init(event: Event, peerName: String, reason: String? = nil, duration: Double? = nil) {
        self.event = event
        self.peerName = peerName
        self.reason = reason
        self.duration = duration
    }

    /// The prefix that marks a history entry as an audit record rather than a
    /// message.
    ///
    /// Three places inspect message text for a prefix and all three must know
    /// this one: the sidebar's last-message preview, the editability guard, and
    /// the chat row builder. A fourth that forgets will render raw JSON at
    /// somebody, which is how a privacy feature ends up looking broken.
    static let marker = "__REMOTE__:"

    /// Encodes to the stored `text`. Sorted keys so a record round-trips to the
    /// same bytes and a diff of history is readable.
    func encoded() -> String {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        guard let data = try? encoder.encode(self),
              let json = String(data: data, encoding: .utf8) else {
            return "\(Self.marker){}"
        }
        return Self.marker + json
    }

    /// Decodes a stored `text`, or nil if it is not an audit record.
    ///
    /// Tolerant of a record written by a newer build: an unknown `event` makes
    /// the whole thing nil rather than throwing, and the caller falls back to
    /// rendering nothing at all for that row. A history entry that cannot be
    /// understood must never take the conversation down with it.
    static func decode(_ text: String) -> RemoteAuditEntry? {
        guard text.hasPrefix(marker) else { return nil }
        let json = String(text.dropFirst(marker.count))
        return try? JSONDecoder().decode(RemoteAuditEntry.self, from: Data(json.utf8))
    }

    static func isAudit(_ text: String) -> Bool { text.hasPrefix(marker) }

    /// The sentence shown in the thread and in the sidebar preview.
    var summary: String {
        switch event {
        case .sessionStarted:
            return "\(peerName) started viewing your screen."
        case .controlGranted:
            return "You gave \(peerName) control of your screen."
        case .controlRevoked:
            return "You took back control from \(peerName)."
        case .sessionEnded:
            let cause = reason.flatMap { RemoteStopReason(rawValue: $0)?.auditDescription }
            return cause ?? "Screen sharing with \(peerName) ended."
        }
    }

    /// Appended to the summary when a session ended, so the trail answers "for
    /// how long" without arithmetic on two timestamps.
    var durationSummary: String? {
        guard let duration, duration >= 1 else { return nil }
        let seconds = Int(duration)
        let (hours, minutes, remainder) = (seconds / 3600, (seconds % 3600) / 60, seconds % 60)
        if hours > 0 { return "Lasted \(hours)h \(minutes)m." }
        if minutes > 0 { return "Lasted \(minutes)m \(remainder)s." }
        return "Lasted \(remainder)s."
    }

    /// Builds the history entry. Not incoming, because the host is the one whose
    /// screen was shared and the trail is a record of what happened to them —
    /// and because an incoming entry would count toward the unread badge.
    func historyEntry(at timestamp: Date = Date()) -> MessageEntry {
        MessageEntry(
            sender: "",
            text: encoded(),
            incoming: false,
            timestamp: timestamp.timeIntervalSince1970,
            messageId: nil,
            status: "",
            readReceiptSent: true)
    }
}
