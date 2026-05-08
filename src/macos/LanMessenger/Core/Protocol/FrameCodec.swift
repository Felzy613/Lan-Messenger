import Foundation

enum FrameCodecError: Error {
    case frameTooLarge(Int)
    case invalidFrameSize
    case connectionClosed
    case encodingFailed
}

enum FrameCodec {

    static let maxFrameSize = 50 * 1024 * 1024  // 50 MiB

    // Encode a Codable value as a length-prefixed frame.
    // Layout: [4 bytes big-endian uint32 length][UTF-8 JSON body]
    static func encode<T: Encodable>(_ value: T) throws -> Data {
        let body = try JSONEncoder().encode(value)
        guard body.count > 0 && body.count <= maxFrameSize else {
            throw FrameCodecError.frameTooLarge(body.count)
        }
        var frame = Data(capacity: 4 + body.count)
        var length = UInt32(body.count).bigEndian
        frame.append(Data(bytes: &length, count: 4))
        frame.append(body)
        return frame
    }

    // Encode a raw [String: Any] dict as a length-prefixed frame.
    static func encodeDict(_ dict: [String: Any]) throws -> Data {
        let body = try JSONSerialization.data(withJSONObject: dict)
        guard body.count > 0 && body.count <= maxFrameSize else {
            throw FrameCodecError.frameTooLarge(body.count)
        }
        var frame = Data(capacity: 4 + body.count)
        var length = UInt32(body.count).bigEndian
        frame.append(Data(bytes: &length, count: 4))
        frame.append(body)
        return frame
    }

    // Read one frame from a stream. Returns nil on clean close.
    // Throws FrameCodecError on protocol violations.
    static func readFrame(from stream: InputStream) throws -> Data? {
        // Read 4-byte header
        var header = [UInt8](repeating: 0, count: 4)
        let headerRead = try readExact(stream: stream, buffer: &header, count: 4)
        guard headerRead else { return nil }   // clean EOF

        let length = Int(UInt32(bigEndian: header.withUnsafeBytes { $0.load(as: UInt32.self) }))
        guard length > 0 && length <= maxFrameSize else {
            throw FrameCodecError.frameTooLarge(length)
        }

        var body = [UInt8](repeating: 0, count: length)
        guard try readExact(stream: stream, buffer: &body, count: length) else {
            return nil
        }
        return Data(body)
    }

    // Parse a frame body into a [String: Any] dict.
    static func parseJSON(from data: Data) throws -> [String: Any] {
        guard let dict = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw FrameCodecError.encodingFailed
        }
        return dict
    }

    // MARK: - Private helpers

    private static func readExact(stream: InputStream, buffer: inout [UInt8], count: Int) throws -> Bool {
        var totalRead = 0
        while totalRead < count {
            let n = buffer.withUnsafeMutableBytes { ptr in
                stream.read(ptr.baseAddress!.advanced(by: totalRead).assumingMemoryBound(to: UInt8.self),
                            maxLength: count - totalRead)
            }
            if n < 0 { throw FrameCodecError.connectionClosed }
            if n == 0 {
                if totalRead == 0 { return false }
                throw FrameCodecError.connectionClosed
            }
            totalRead += n
        }
        return true
    }
}
