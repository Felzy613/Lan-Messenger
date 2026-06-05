import Foundation

enum ValidationError: Error, CustomStringConvertible {
    case missingType
    case unknownType(String)
    case selfPacket
    case invalidNonce
    case invalidFileSize
    case missingRequiredField(String)

    var description: String {
        switch self {
        case .missingType:               return "Packet missing 'type' field"
        case .unknownType(let t):        return "Unknown packet type: \(t)"
        case .selfPacket:                return "Packet originated from self"
        case .invalidNonce:              return "Nonce is not exactly 12 bytes"
        case .invalidFileSize:           return "File size out of range"
        case .missingRequiredField(let f): return "Missing required field: \(f)"
        }
    }
}

enum PacketValidator {

    static let maxFrameSize: Int = 50 * 1024 * 1024   // 50 MiB

    // Validate and parse a raw JSON dict received over TCP.
    // ownPublicKeyB64: base64 of our X25519 public key (used for self-suppression).
    static func validate(
        json: [String: Any],
        senderIP: String,
        ownPublicKeyB64: String
    ) -> Result<ValidatedPacket, ValidationError> {

        guard let typeStr = json["type"] as? String else {
            return .failure(.missingType)
        }
        guard let packetType = PacketType(rawValue: typeStr) else {
            return .failure(.unknownType(typeStr))
        }

        // Self-suppression: drop if the sender_public_key_b64 equals our own
        if let senderKey = json["sender_public_key_b64"] as? String,
           senderKey == ownPublicKeyB64 {
            return .failure(.selfPacket)
        }

        let data = try? JSONSerialization.data(withJSONObject: json)

        switch packetType {
        case .discovery, .discoveryReply:
            guard let d = data, let pkt = try? JSONDecoder().decode(DiscoveryPacket.self, from: d) else {
                return .failure(.missingRequiredField("discovery fields"))
            }
            if pkt.publicKeyB64 == ownPublicKeyB64 { return .failure(.selfPacket) }
            return .success(.discovery(pkt, senderIP: senderIP))

        case .text:
            guard let d = data, let pkt = try? JSONDecoder().decode(TextPacket.self, from: d) else {
                return .failure(.missingRequiredField("text fields"))
            }
            guard validateNonce(pkt.nonce) else { return .failure(.invalidNonce) }
            return .success(.text(pkt, senderIP: senderIP))

        case .typing:
            guard let d = data, let pkt = try? JSONDecoder().decode(TypingPacket.self, from: d) else {
                return .failure(.missingRequiredField("typing fields"))
            }
            return .success(.typing(pkt, senderIP: senderIP))

        case .sentReceipt, .readReceipt:
            guard let d = data, let pkt = try? JSONDecoder().decode(ReceiptPacket.self, from: d) else {
                return .failure(.missingRequiredField("receipt fields"))
            }
            return .success(.receipt(pkt, senderIP: senderIP))

        case .fileStart:
            guard let d = data, let pkt = try? JSONDecoder().decode(FileStartPacket.self, from: d) else {
                return .failure(.missingRequiredField("file_start fields"))
            }
            guard pkt.size >= 0 && pkt.size <= 2 * 1024 * 1024 * 1024 else {
                return .failure(.invalidFileSize)
            }
            return .success(.fileStart(pkt, senderIP: senderIP))

        case .fileChunk:
            guard let d = data, let pkt = try? JSONDecoder().decode(FileChunkPacket.self, from: d) else {
                return .failure(.missingRequiredField("file_chunk fields"))
            }
            guard validateNonce(pkt.nonce) else { return .failure(.invalidNonce) }
            return .success(.fileChunk(pkt, senderIP: senderIP))

        case .fileEnd:
            guard let d = data, let pkt = try? JSONDecoder().decode(FileEndPacket.self, from: d) else {
                return .failure(.missingRequiredField("file_end fields"))
            }
            return .success(.fileEnd(pkt, senderIP: senderIP))
        }
    }

    // Validate and parse a raw JSON dict received over UDP (discovery only).
    static func validateDiscovery(
        data: Data,
        senderIP: String,
        ownPublicKeyB64: String,
        ownIPs: Set<String>
    ) -> DiscoveryPacket? {
        guard !ownIPs.contains(senderIP) else { return nil }
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let typeStr = json["type"] as? String,
              typeStr == "discovery" || typeStr == "discovery_reply" || typeStr == "goodbye",
              let pkt = try? JSONDecoder().decode(DiscoveryPacket.self, from: data) else {
            return nil
        }
        guard pkt.publicKeyB64 != ownPublicKeyB64,
              !pkt.publicKeyB64.isEmpty else { return nil }
        return pkt
    }

    // MARK: - Helpers

    static func validateNonce(_ b64: String) -> Bool {
        guard let data = Data(base64Encoded: b64) else { return false }
        return data.count == 12
    }

    // Sanitize a filename from a file_start packet.
    // Mirrors Python: Path(name).name.strip() or "file", then remove null bytes.
    // On POSIX, backslashes are NOT path separators, so they are preserved (same as Python on macOS).
    static func sanitizeFilename(_ name: String) -> String {
        // Split on "/" and take the last component — equivalent to Path(name).name on POSIX
        let component = name.components(separatedBy: "/").last ?? ""
        let stripped = component
            .trimmingCharacters(in: .whitespaces)
            .replacingOccurrences(of: "\0", with: "")
        return stripped.isEmpty ? "file" : stripped
    }
}
