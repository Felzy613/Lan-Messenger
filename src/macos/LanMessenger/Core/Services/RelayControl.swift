import Foundation

/// An edit or delete that has to reach a peer who isn't on the LAN.
///
/// `edit_message` and `delete_message` are LAN-only, one-shot TCP writes: if the
/// peer is offline when you edit or delete, the change never reaches them. This
/// carries the same two operations through the cloud relay mailbox instead, so
/// the peer applies it the next time they poll.
///
/// Shape on the wire: the envelope is the *plaintext* of an ordinary relay
/// record. The Worker is untouched and still sees nothing but ciphertext — it
/// can't tell a control record from a chat message, and it never learns which
/// message was edited or deleted. Two consequences that matter:
///
///  * The record is stored under its own fresh `message_id`, never the target's.
///    The Worker dedups `/store` on `message_id` and returns `{ok:true,
///    duplicate:true}` for a repeat, so re-uploading under the original's id
///    would be silently discarded while reporting success. A fresh id also means
///    this works whether or not the original is still sitting in the mailbox.
///  * A client older than 1.7 has no idea what this is and renders the marker
///    line as a literal chat message. Both ends need 1.7+ for relayed edits.
enum RelayControlOp: String, Codable {
    case edit
    case delete
}

struct RelayControlEnvelope: Equatable {
    let op: RelayControlOp
    /// The `message_id` of the message being edited or deleted.
    let target: String
    /// Replacement body. Present for `.edit`, nil for `.delete`.
    let text: String?
    /// When the edit/delete was made (Unix seconds).
    let at: Double

    /// Prefix that marks a relay plaintext as a control envelope rather than a
    /// chat body. Same convention as the `__FILE__:` prefix used for
    /// attachments in history.
    static let marker = "__CTRL__:"

    private struct Payload: Codable {
        let op: String
        let target: String
        let text: String?
        let at: Double
    }

    func encoded() -> String {
        let payload = Payload(op: op.rawValue, target: target, text: text, at: at)
        guard let data = try? JSONEncoder().encode(payload),
              let json = String(data: data, encoding: .utf8) else {
            return ""
        }
        return Self.marker + json
    }

    /// Parses a decrypted relay plaintext. Returns nil for ordinary chat text,
    /// which is the overwhelmingly common case, so the check stays a cheap
    /// prefix test before any JSON work.
    static func decode(_ plaintext: String) -> RelayControlEnvelope? {
        guard plaintext.hasPrefix(marker) else { return nil }
        let json = String(plaintext.dropFirst(marker.count))
        guard let data = json.data(using: .utf8),
              let payload = try? JSONDecoder().decode(Payload.self, from: data),
              let op = RelayControlOp(rawValue: payload.op) else { return nil }
        // A target that isn't a message id can't match anything; reject it here
        // rather than letting it reach the history store.
        guard isMessageId(payload.target) else { return nil }
        // An edit with no replacement body would blank the message.
        if op == .edit, (payload.text ?? "").isEmpty { return nil }
        return RelayControlEnvelope(op: op, target: payload.target,
                                    text: payload.text, at: payload.at)
    }

    /// 32 lowercase hex characters — the `uuid4().hex` form PROTOCOL.md requires
    /// of every `message_id`.
    static func isMessageId(_ s: String) -> Bool {
        s.count == 32 && s.allSatisfy { $0.isHexDigit && !$0.isUppercase }
    }

    /// Fresh id for the relay record that carries this envelope. Deliberately
    /// not the target's id — see the type's note on Worker dedup.
    static func newRecordId() -> String {
        UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
    }
}
