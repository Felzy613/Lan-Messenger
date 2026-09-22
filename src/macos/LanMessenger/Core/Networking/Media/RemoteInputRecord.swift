import Foundation

// The input sub-channel's wire records. See PROTOCOL.md → Input Sub-Channel.
//
// Every record is `[1 byte type][payload]`, big-endian like the rest of the
// protocol, and every type has exactly one length. That is the point: a host can
// reject a malformed record on length alone, before interpreting a single field
// a peer chose.
//
// Two decisions here are the difference between a remote desktop that works and
// one that half-works, and both are in the spec for reasons worth restating:
//
//  * **Coordinates are normalized to the video surface, not the window.** The
//    host owns the authoritative geometry — display scale, Retina backing,
//    multi-monitor offsets — and resolving on the viewer would mean shipping all
//    of that across and keeping it in step. A letterboxed viewer normalizes
//    against the picture, not the black bars around it.
//  * **Keys are USB HID usages, never characters and never platform key codes.**
//    A usage names a physical key position, so the *host's* layout decides what
//    it produces. That is what makes dead keys, AltGr and IME work: the event
//    goes through the host's real text input path. It also means an AZERTY
//    viewer typing `a` produces `q` on a QWERTY host, which is correct and
//    matches every other remote desktop.
//
// Everything in here arrives from a peer, so decoding is written to be boring:
// no allocation before a length is checked, no trust in any float, and a
// malformed record ends the whole frame rather than attempting resynchronisation
// — a partial record cannot be safely resynchronized, and the next frame is
// milliseconds away.

enum RemoteInputRecord: Equatable {
    case pointerMove(x: Float, y: Float)
    case pointerButton(button: RemotePointerButton, down: Bool, x: Float, y: Float)
    case pointerScroll(dx: Float, dy: Float, x: Float, y: Float)
    case key(usage: UInt16, down: Bool, repeating: Bool, modifiers: RemoteInputModifiers)
    case text(String)

    /// The wire type byte.
    var typeByte: UInt8 {
        switch self {
        case .pointerMove:   return 0x01
        case .pointerButton: return 0x02
        case .pointerScroll: return 0x03
        case .key:           return 0x04
        case .text:          return 0x05
        }
    }

    /// Longest string one `text` record may carry. A viewer splits beyond this.
    static let maxTextBytes = 1024
}

/// Which physical button. Higher values are reserved, and a host ignores records
/// naming them rather than guessing.
enum RemotePointerButton: UInt8, Equatable, CaseIterable {
    case left = 0
    case right = 1
    case middle = 2
}

struct RemoteInputModifiers: OptionSet, Equatable {
    let rawValue: UInt16

    init(rawValue: UInt16) { self.rawValue = rawValue }

    static let shift    = RemoteInputModifiers(rawValue: 0x01)
    static let control  = RemoteInputModifiers(rawValue: 0x02)
    static let option   = RemoteInputModifiers(rawValue: 0x04)
    /// Command on macOS, the Windows key on Windows.
    static let meta     = RemoteInputModifiers(rawValue: 0x08)
    static let capsLock = RemoteInputModifiers(rawValue: 0x10)

    /// Bits this build understands. Unknown bits are ignored rather than
    /// rejected: a newer peer may set one, and refusing the record would turn a
    /// forward-compatible addition into a dead keyboard.
    static let known: RemoteInputModifiers = [.shift, .control, .option, .meta, .capsLock]

    var recognised: RemoteInputModifiers { intersection(.known) }
}

enum RemoteInputCodec {

    /// Exact byte count for a type, or nil for one this build does not know.
    /// `text` is variable and answers nil here; its length is read from the
    /// record itself.
    static func fixedLength(forType type: UInt8) -> Int? {
        switch type {
        case 0x01: return 9
        case 0x02: return 11
        case 0x03: return 17
        case 0x04: return 7
        default:   return nil
        }
    }

    // MARK: - Encode

    static func encode(_ record: RemoteInputRecord) -> Data {
        var out = Data([record.typeByte])
        switch record {
        case .pointerMove(let x, let y):
            appendFloat(&out, x)
            appendFloat(&out, y)

        case .pointerButton(let button, let down, let x, let y):
            out.append(button.rawValue)
            out.append(down ? 1 : 0)
            appendFloat(&out, x)
            appendFloat(&out, y)

        case .pointerScroll(let dx, let dy, let x, let y):
            appendFloat(&out, dx)
            appendFloat(&out, dy)
            appendFloat(&out, x)
            appendFloat(&out, y)

        case .key(let usage, let down, let repeating, let modifiers):
            appendUInt16(&out, usage)
            out.append(down ? 1 : 0)
            out.append(repeating ? 1 : 0)
            appendUInt16(&out, modifiers.rawValue)

        case .text(let string):
            // Truncated on a character boundary, never mid-scalar: half a UTF-8
            // sequence is not a shorter string, it is an invalid one.
            var bytes = Array(string.utf8)
            if bytes.count > RemoteInputRecord.maxTextBytes {
                bytes = Array(truncate(string, toUTF8Bytes: RemoteInputRecord.maxTextBytes).utf8)
            }
            appendUInt16(&out, UInt16(bytes.count))
            out.append(contentsOf: bytes)
        }
        return out
    }

    /// Several records in one payload, which is how a burst of pointer moves
    /// travels without a frame each.
    static func encode(_ records: [RemoteInputRecord]) -> Data {
        var out = Data()
        for record in records { out.append(encode(record)) }
        return out
    }

    // MARK: - Decode

    /// Decodes every record in a payload.
    ///
    /// Returns nil for a payload that is malformed anywhere. Partial success is
    /// deliberately not offered: a record of the wrong length means the stream
    /// position is no longer trustworthy, and injecting the prefix of a
    /// corrupted burst is worse than injecting nothing.
    static func decode(_ payload: Data) -> [RemoteInputRecord]? {
        var records: [RemoteInputRecord] = []
        var index = payload.startIndex

        while index < payload.endIndex {
            let type = payload[index]
            let bodyStart = payload.index(after: index)

            if type == 0x05 {
                guard payload.distance(from: bodyStart, to: payload.endIndex) >= 2 else { return nil }
                let count = Int(readUInt16(payload, at: bodyStart))
                let textStart = payload.index(bodyStart, offsetBy: 2)
                guard count <= RemoteInputRecord.maxTextBytes,
                      payload.distance(from: textStart, to: payload.endIndex) >= count else {
                    return nil
                }
                let textEnd = payload.index(textStart, offsetBy: count)
                // Invalid UTF-8 is a malformed record, not an empty string:
                // silently injecting nothing would look like a dropped keystroke.
                guard let string = String(data: payload[textStart..<textEnd], encoding: .utf8) else {
                    return nil
                }
                records.append(.text(string))
                index = textEnd
                continue
            }

            guard let length = fixedLength(forType: type) else { return nil }
            guard payload.distance(from: index, to: payload.endIndex) >= length else { return nil }

            switch type {
            case 0x01:
                records.append(.pointerMove(x: readFloat(payload, at: bodyStart),
                                            y: readFloat(payload, at: payload.index(bodyStart, offsetBy: 4))))

            case 0x02:
                guard let button = RemotePointerButton(rawValue: payload[bodyStart]) else {
                    // A reserved button. The record is well-formed, so the
                    // stream stays trustworthy — skip just this one.
                    index = payload.index(index, offsetBy: length)
                    continue
                }
                let down = payload[payload.index(bodyStart, offsetBy: 1)] != 0
                let x = readFloat(payload, at: payload.index(bodyStart, offsetBy: 2))
                let y = readFloat(payload, at: payload.index(bodyStart, offsetBy: 6))
                records.append(.pointerButton(button: button, down: down, x: x, y: y))

            case 0x03:
                records.append(.pointerScroll(
                    dx: readFloat(payload, at: bodyStart),
                    dy: readFloat(payload, at: payload.index(bodyStart, offsetBy: 4)),
                    x:  readFloat(payload, at: payload.index(bodyStart, offsetBy: 8)),
                    y:  readFloat(payload, at: payload.index(bodyStart, offsetBy: 12))))

            case 0x04:
                let usage = readUInt16(payload, at: bodyStart)
                let down = payload[payload.index(bodyStart, offsetBy: 2)] != 0
                let repeating = payload[payload.index(bodyStart, offsetBy: 3)] != 0
                let raw = readUInt16(payload, at: payload.index(bodyStart, offsetBy: 4))
                records.append(.key(usage: usage, down: down, repeating: repeating,
                                    modifiers: RemoteInputModifiers(rawValue: raw).recognised))

            default:
                return nil
            }
            index = payload.index(index, offsetBy: length)
        }
        return records
    }

    // MARK: - Primitives

    private static func appendFloat(_ data: inout Data, _ value: Float) {
        appendUInt32(&data, value.bitPattern)
    }

    private static func appendUInt32(_ data: inout Data, _ value: UInt32) {
        data.append(UInt8(truncatingIfNeeded: value >> 24))
        data.append(UInt8(truncatingIfNeeded: value >> 16))
        data.append(UInt8(truncatingIfNeeded: value >> 8))
        data.append(UInt8(truncatingIfNeeded: value))
    }

    private static func appendUInt16(_ data: inout Data, _ value: UInt16) {
        data.append(UInt8(truncatingIfNeeded: value >> 8))
        data.append(UInt8(truncatingIfNeeded: value))
    }

    private static func readFloat(_ data: Data, at index: Data.Index) -> Float {
        Float(bitPattern: readUInt32(data, at: index))
    }

    private static func readUInt32(_ data: Data, at index: Data.Index) -> UInt32 {
        var value: UInt32 = 0
        var cursor = index
        for _ in 0..<4 {
            value = (value << 8) | UInt32(data[cursor])
            cursor = data.index(after: cursor)
        }
        return value
    }

    private static func readUInt16(_ data: Data, at index: Data.Index) -> UInt16 {
        let high = UInt16(data[index])
        let low = UInt16(data[data.index(after: index)])
        return (high << 8) | low
    }

    /// Longest prefix of `string` whose UTF-8 fits in `limit` bytes.
    private static func truncate(_ string: String, toUTF8Bytes limit: Int) -> String {
        var result = ""
        var used = 0
        for character in string {
            let size = String(character).utf8.count
            if used + size > limit { break }
            result.append(character)
            used += size
        }
        return result
    }
}
