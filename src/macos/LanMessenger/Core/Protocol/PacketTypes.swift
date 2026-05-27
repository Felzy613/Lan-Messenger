import Foundation

// MARK: - Known packet type strings

enum PacketType: String, Codable {
    case discovery
    case discoveryReply = "discovery_reply"
    case text
    case typing
    case sentReceipt = "sent_receipt"
    case readReceipt = "read_receipt"
    case fileStart = "file_start"
    case fileChunk = "file_chunk"
    case fileEnd = "file_end"
}

// MARK: - Discovery (UDP, no framing)

struct DiscoveryPacket: Codable {
    let type: String        // "discovery" or "discovery_reply"
    let username: String
    let port: Int
    let publicKeyB64: String
    let ips: [String]
    // SHA256(relay_id) hex — the sender's cloud relay mailbox address.
    // Optional: older clients that don't include this field are silently ignored.
    let relayIdHash: String?

    enum CodingKeys: String, CodingKey {
        case type, username, port
        case publicKeyB64 = "public_key_b64"
        case ips
        case relayIdHash = "relay_id_hash"
    }

    // Custom decoding: relay_id_hash is optional (old clients omit it).
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        type          = try c.decode(String.self, forKey: .type)
        username      = try c.decode(String.self, forKey: .username)
        port          = try c.decode(Int.self,    forKey: .port)
        publicKeyB64  = try c.decode(String.self, forKey: .publicKeyB64)
        ips           = try c.decode([String].self, forKey: .ips)
        relayIdHash   = try c.decodeIfPresent(String.self, forKey: .relayIdHash)
    }

    init(type: String, username: String, port: Int, publicKeyB64: String,
         ips: [String], relayIdHash: String? = nil) {
        self.type         = type
        self.username     = username
        self.port         = port
        self.publicKeyB64 = publicKeyB64
        self.ips          = ips
        self.relayIdHash  = relayIdHash
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

// MARK: - Unified parsed packet (output of PacketValidator)

enum ValidatedPacket {
    case text(TextPacket, senderIP: String)
    case typing(TypingPacket, senderIP: String)
    case receipt(ReceiptPacket, senderIP: String)
    case fileStart(FileStartPacket, senderIP: String)
    case fileChunk(FileChunkPacket, senderIP: String)
    case fileEnd(FileEndPacket, senderIP: String)
    case discovery(DiscoveryPacket, senderIP: String)

    var senderPublicKeyB64: String? {
        switch self {
        case .text(let p, _):      return p.senderPublicKeyB64
        case .typing(let p, _):    return p.senderPublicKeyB64
        case .receipt(let p, _):   return p.senderPublicKeyB64
        case .fileStart(let p, _): return p.senderPublicKeyB64
        case .fileChunk(let p, _): return p.senderPublicKeyB64
        case .fileEnd(let p, _):   return p.senderPublicKeyB64
        case .discovery(let p, _): return p.publicKeyB64
        }
    }

    var senderIP: String {
        switch self {
        case .text(_, let ip), .typing(_, let ip), .receipt(_, let ip),
             .fileStart(_, let ip), .fileChunk(_, let ip), .fileEnd(_, let ip),
             .discovery(_, let ip):
            return ip
        }
    }
}
