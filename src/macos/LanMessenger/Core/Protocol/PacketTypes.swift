import Foundation

// MARK: - Known packet type strings

enum PacketType: String, Codable {
    case discovery
    case discoveryReply = "discovery_reply"
    case text
    case typing
    case sentReceipt = "sent_receipt"
    case readReceipt = "read_receipt"
    case deleteMessage = "delete_message"
    case editMessage = "edit_message"
    case fileStart = "file_start"
    case fileChunk = "file_chunk"
    case fileEnd = "file_end"
    case remoteInvite = "remote_invite"
    case remoteAccept = "remote_accept"
    case remoteDecline = "remote_decline"
    case remoteEnd = "remote_end"
    case mediaAttach = "media_attach"
}

// MARK: - Discovery (UDP, no framing)

/// Capability tokens advertised in the optional discovery `caps` field.
///
/// The field exists because `PacketValidator` drops unknown packet types
/// *silently*. A client that sends `remote_invite` to a peer too old to know the
/// type waits forever for a reply that is never coming — so the capability is
/// advertised, and the interface disables the feature for that peer instead of
/// offering something that can only time out.
///
/// Add a token only for an extension that must be negotiated before first use.
/// Extensions that degrade safely — `reply_to_*`, which an old client simply
/// ignores — must not have one, or every future field becomes a negotiation.
enum ProtocolCapability {
    /// Remote desktop, as specified in PROTOCOL.md → Remote Desktop.
    static let remoteDesktopV1 = "remote-desktop-v1"

    /// What this build implements. Advertised as a statement of capability, not
    /// of willingness: whether a host will *accept* an invite is a policy
    /// question answered by `remote_decline`, which is a fast, clear answer
    /// rather than the hang this field exists to prevent.
    static let advertised: [String] = [remoteDesktopV1]
}

struct DiscoveryPacket: Codable {
    let type: String        // "discovery" or "discovery_reply"
    let username: String
    let port: Int
    let publicKeyB64: String
    let ips: [String]
    // SHA256(relay_id) hex — the sender's cloud relay mailbox address.
    // Optional: older clients that don't include this field are silently ignored.
    let relayIdHash: String?
    /// Optional capability tokens. Absent means "assume nothing beyond the base
    /// protocol"; unknown tokens must be tolerated, because a newer peer will
    /// advertise tokens this build has never heard of.
    let caps: [String]?

    enum CodingKeys: String, CodingKey {
        case type, username, port
        case publicKeyB64 = "public_key_b64"
        case ips
        case relayIdHash = "relay_id_hash"
        case caps
    }

    /// True when the peer advertised remote-desktop support. A peer that
    /// advertises nothing is not assumed capable — that assumption is exactly
    /// the hang this field prevents.
    var supportsRemoteDesktop: Bool {
        caps?.contains(ProtocolCapability.remoteDesktopV1) ?? false
    }

    // Custom decoding: relay_id_hash and caps are optional (old clients omit
    // both, and must keep working).
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        type          = try c.decode(String.self, forKey: .type)
        username      = try c.decode(String.self, forKey: .username)
        port          = try c.decode(Int.self,    forKey: .port)
        publicKeyB64  = try c.decode(String.self, forKey: .publicKeyB64)
        ips           = try c.decode([String].self, forKey: .ips)
        relayIdHash   = try c.decodeIfPresent(String.self, forKey: .relayIdHash)
        // Tolerant on purpose, in both directions. A malformed `caps` — a bare
        // string, a number, anything — is treated as absent rather than
        // failing the packet: a peer with a broken capability field is still a
        // peer, and dropping its beacon would make it vanish from the network
        // entirely over a field that is optional by definition.
        //
        // Bounded, too. Discovery is unauthenticated UDP from anyone on the
        // LAN, and the tokens are retained per peer; a datagram full of them
        // should cost nothing.
        let declared = (try? c.decodeIfPresent([String].self, forKey: .caps)) ?? nil
        caps = declared.map { tokens in
            Array(tokens.filter { !$0.isEmpty && $0.count <= DiscoveryPacket.maxCapTokenLength }
                        .prefix(DiscoveryPacket.maxCapTokens))
        }
    }

    static let maxCapTokens = 16
    static let maxCapTokenLength = 64

    init(type: String, username: String, port: Int, publicKeyB64: String,
         ips: [String], relayIdHash: String? = nil,
         caps: [String]? = ProtocolCapability.advertised) {
        self.type         = type
        self.username     = username
        self.port         = port
        self.publicKeyB64 = publicKeyB64
        self.ips          = ips
        self.relayIdHash  = relayIdHash
        self.caps         = caps
    }
}

// MARK: - Text message (TCP, framed)

struct TextPacket: Codable {
    let type: String        // "text"
    let messageId: String
    let timestamp: Double
    let sender: String
    let senderPublicKeyB64: String
    let port: Int
    let nonce: String
    let ciphertext: String
    // Optional reply metadata — present when this message is a reply to another.
    // Plain (unencrypted) for backward compatibility with Python/older clients.
    let replyToMessageId: String?
    let replyToPreview: String?
    let replyToSender: String?

    enum CodingKeys: String, CodingKey {
        case type, timestamp, sender, port, nonce, ciphertext
        case messageId = "message_id"
        case senderPublicKeyB64 = "sender_public_key_b64"
        case replyToMessageId = "reply_to_message_id"
        case replyToPreview = "reply_to_preview"
        case replyToSender = "reply_to_sender"
    }

    // Custom decoding so replyTo* are truly optional (older messages won't have them).
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        type = try c.decode(String.self, forKey: .type)
        messageId = try c.decode(String.self, forKey: .messageId)
        timestamp = try c.decode(Double.self, forKey: .timestamp)
        sender = try c.decode(String.self, forKey: .sender)
        senderPublicKeyB64 = try c.decode(String.self, forKey: .senderPublicKeyB64)
        port = try c.decode(Int.self, forKey: .port)
        nonce = try c.decode(String.self, forKey: .nonce)
        ciphertext = try c.decode(String.self, forKey: .ciphertext)
        replyToMessageId = try c.decodeIfPresent(String.self, forKey: .replyToMessageId)
        replyToPreview = try c.decodeIfPresent(String.self, forKey: .replyToPreview)
        replyToSender = try c.decodeIfPresent(String.self, forKey: .replyToSender)
    }

    init(type: String, messageId: String, timestamp: Double, sender: String,
         senderPublicKeyB64: String, port: Int, nonce: String, ciphertext: String,
         replyToMessageId: String? = nil, replyToPreview: String? = nil,
         replyToSender: String? = nil) {
        self.type = type
        self.messageId = messageId
        self.timestamp = timestamp
        self.sender = sender
        self.senderPublicKeyB64 = senderPublicKeyB64
        self.port = port
        self.nonce = nonce
        self.ciphertext = ciphertext
        self.replyToMessageId = replyToMessageId
        self.replyToPreview = replyToPreview
        self.replyToSender = replyToSender
    }
}

// MARK: - Typing indicator (TCP, framed)

struct TypingPacket: Codable {
    let type: String        // "typing"
    let active: Bool
    let sender: String
    let senderPublicKeyB64: String
    let port: Int

    enum CodingKeys: String, CodingKey {
        case type, active, sender, port
        case senderPublicKeyB64 = "sender_public_key_b64"
    }
}

// MARK: - Receipt (TCP, framed)

struct ReceiptPacket: Codable {
    let type: String        // "sent_receipt" or "read_receipt"
    let messageId: String
    let sender: String
    let senderPublicKeyB64: String
    let port: Int

    enum CodingKeys: String, CodingKey {
        case type, sender, port
        case messageId = "message_id"
        case senderPublicKeyB64 = "sender_public_key_b64"
    }
}

// MARK: - File transfer (TCP, framed)

struct FileStartPacket: Codable {
    let type: String        // "file_start"
    let transferId: String
    let filename: String
    let size: Int64
    let sender: String
    let senderPublicKeyB64: String
    let port: Int

    enum CodingKeys: String, CodingKey {
        case type, filename, size, sender, port
        case transferId = "transfer_id"
        case senderPublicKeyB64 = "sender_public_key_b64"
    }
}

struct FileChunkPacket: Codable {
    let type: String        // "file_chunk"
    let transferId: String
    let sender: String
    let senderPublicKeyB64: String
    let port: Int
    let nonce: String
    let ciphertext: String

    enum CodingKeys: String, CodingKey {
        case type, sender, port, nonce, ciphertext
        case transferId = "transfer_id"
        case senderPublicKeyB64 = "sender_public_key_b64"
    }
}

struct FileEndPacket: Codable {
    let type: String        // "file_end"
    let transferId: String
    let sender: String
    let senderPublicKeyB64: String
    let port: Int

    enum CodingKeys: String, CodingKey {
        case type, sender, port
        case transferId = "transfer_id"
        case senderPublicKeyB64 = "sender_public_key_b64"
    }
}

// MARK: - Remote desktop (TCP, framed)

// Two shapes cover all five remote-desktop packets.
//
// `remote_invite` / `remote_accept` carry a sealed body — the sender's ephemeral
// X25519 key plus the negotiated parameters — encrypted with the ordinary
// session key and AAD'd to `session_id`. Sealing the ephemeral rather than
// sending it in the clear does not stop an attacker who cannot complete the
// triple DH anyway; it hardens against unauthenticated peers making a host do
// X25519 work, and it authenticates the parameters for free.
//
// `session_id` itself is plaintext because the receiver must look up the session
// before it can decrypt anything.
struct RemoteSessionPacket: Codable {
    let type: String        // "remote_invite" or "remote_accept"
    let sessionId: String
    let sender: String
    let senderPublicKeyB64: String
    let port: Int
    let nonce: String
    let ciphertext: String

    enum CodingKeys: String, CodingKey {
        case type, sender, port, nonce, ciphertext
        case sessionId = "session_id"
        case senderPublicKeyB64 = "sender_public_key_b64"
    }
}

// `remote_decline`, `remote_end` and `media_attach`: the spine plus an optional
// machine-readable reason. Nothing here is sensitive — the initiator already
// knows it asked — so `reason` is deliberately unencrypted, which is what lets a
// client show "they have it switched off" rather than a generic failure.
struct RemoteControlPacket: Codable {
    let type: String        // "remote_decline", "remote_end" or "media_attach"
    let sessionId: String
    let sender: String
    let senderPublicKeyB64: String
    let port: Int
    let reason: String?

    enum CodingKeys: String, CodingKey {
        case type, sender, port, reason
        case sessionId = "session_id"
        case senderPublicKeyB64 = "sender_public_key_b64"
    }

    // `reason` is absent on media_attach and optional elsewhere.
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        type = try c.decode(String.self, forKey: .type)
        sessionId = try c.decode(String.self, forKey: .sessionId)
        sender = try c.decode(String.self, forKey: .sender)
        senderPublicKeyB64 = try c.decode(String.self, forKey: .senderPublicKeyB64)
        port = try c.decode(Int.self, forKey: .port)
        reason = try c.decodeIfPresent(String.self, forKey: .reason)
    }

    init(type: String, sessionId: String, sender: String, senderPublicKeyB64: String,
         port: Int, reason: String? = nil) {
        self.type = type
        self.sessionId = sessionId
        self.sender = sender
        self.senderPublicKeyB64 = senderPublicKeyB64
        self.port = port
        self.reason = reason
    }
}

// MARK: - Unified parsed packet (output of PacketValidator)

enum ValidatedPacket {
    case text(TextPacket, senderIP: String)
    case typing(TypingPacket, senderIP: String)
    case receipt(ReceiptPacket, senderIP: String)
    case delete(ReceiptPacket, senderIP: String)
    // edit_message reuses TextPacket: identical shape, but `messageId` names the
    // ORIGINAL message rather than a new one. See PROTOCOL.md → edit_message.
    case edit(TextPacket, senderIP: String)
    case fileStart(FileStartPacket, senderIP: String)
    case fileChunk(FileChunkPacket, senderIP: String)
    case fileEnd(FileEndPacket, senderIP: String)
    case discovery(DiscoveryPacket, senderIP: String)
    case remoteInvite(RemoteSessionPacket, senderIP: String)
    case remoteAccept(RemoteSessionPacket, senderIP: String)
    case remoteDecline(RemoteControlPacket, senderIP: String)
    case remoteEnd(RemoteControlPacket, senderIP: String)
    case mediaAttach(RemoteControlPacket, senderIP: String)

    var senderPublicKeyB64: String? {
        switch self {
        case .text(let p, _):      return p.senderPublicKeyB64
        case .typing(let p, _):    return p.senderPublicKeyB64
        case .receipt(let p, _):   return p.senderPublicKeyB64
        case .delete(let p, _):    return p.senderPublicKeyB64
        case .edit(let p, _):      return p.senderPublicKeyB64
        case .fileStart(let p, _): return p.senderPublicKeyB64
        case .fileChunk(let p, _): return p.senderPublicKeyB64
        case .fileEnd(let p, _):   return p.senderPublicKeyB64
        case .discovery(let p, _): return p.publicKeyB64
        case .remoteInvite(let p, _), .remoteAccept(let p, _):
            return p.senderPublicKeyB64
        case .remoteDecline(let p, _), .remoteEnd(let p, _), .mediaAttach(let p, _):
            return p.senderPublicKeyB64
        }
    }

    /// Whether receiving this packet should refresh the sender's presence.
    ///
    /// Exhaustively switched on purpose: a future case has to make this decision
    /// explicitly rather than inherit "yes" from a default. `media_attach` is the
    /// one that must NOT — it is the last JSON frame on a socket that is about to
    /// become a binary media channel, and treating it as ordinary peer traffic
    /// would have the presence path touching a connection that is no longer a
    /// JSON peer at all.
    var refreshesPresence: Bool {
        switch self {
        case .text, .typing, .receipt, .delete, .edit,
             .fileStart, .fileChunk, .fileEnd, .discovery,
             .remoteInvite, .remoteAccept, .remoteDecline, .remoteEnd:
            return true
        case .mediaAttach:
            return false
        }
    }

    var senderIP: String {
        switch self {
        case .text(_, let ip), .typing(_, let ip), .receipt(_, let ip), .delete(_, let ip),
             .edit(_, let ip),
             .fileStart(_, let ip), .fileChunk(_, let ip), .fileEnd(_, let ip),
             .discovery(_, let ip),
             .remoteInvite(_, let ip), .remoteAccept(_, let ip),
             .remoteDecline(_, let ip), .remoteEnd(_, let ip), .mediaAttach(_, let ip):
            return ip
        }
    }
}
