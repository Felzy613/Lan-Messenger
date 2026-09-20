using System.Buffers.Binary;
using System.Text;

namespace LanMessenger.Core.Networking.Media;

// The input sub-channel's wire records. Mirror of RemoteInputRecord.swift.
// See PROTOCOL.md → Input Sub-Channel.
//
// Every record is [1 byte type][payload], big-endian like the rest of the
// protocol, and every type has exactly one length. That is the point: a host can
// reject a malformed record on length alone, before interpreting a single field
// a peer chose.
//
// Two decisions are the difference between a remote desktop that works and one
// that half-works:
//
//  * **Coordinates are normalized to the video surface, not the window.** The
//    host owns the authoritative geometry — display scale, DPI, multi-monitor
//    offsets — and resolving on the viewer would mean shipping all of it across
//    and keeping it in step. A letterboxed viewer normalizes against the
//    picture, not the black bars around it.
//  * **Keys are USB HID usages, never characters and never virtual key codes.**
//    A usage names a physical key position, so the *host's* layout decides what
//    it produces. That is what makes dead keys, AltGr and IME work.
//
// Everything here arrives from a peer, so decoding is deliberately boring: no
// allocation before a length is checked, no trust in any float, and a malformed
// record ends the whole frame rather than attempting resynchronisation.

public enum RemoteInputRecordKind : byte
{
    PointerMove   = 0x01,
    PointerButton = 0x02,
    PointerScroll = 0x03,
    Key           = 0x04,
    Text          = 0x05,
}

/// <summary>Which physical button. Higher values are reserved and ignored.</summary>
public enum RemotePointerButton : byte { Left = 0, Right = 1, Middle = 2 }

[Flags]
public enum RemoteInputModifiers : ushort
{
    None     = 0,
    Shift    = 0x01,
    Control  = 0x02,
    Alt      = 0x04,
    /// Command on macOS, the Windows key on Windows.
    Meta     = 0x08,
    CapsLock = 0x10,

    /// Bits this build understands. Unknown bits are ignored rather than
    /// rejected: a newer peer may set one, and refusing the record would turn a
    /// forward-compatible addition into a dead keyboard.
    Known = Shift | Control | Alt | Meta | CapsLock,
}

/// <summary>One input record. Exactly one of the shapes below is meaningful,
/// decided by <see cref="Kind"/>.</summary>
public readonly record struct RemoteInputRecord(
    RemoteInputRecordKind Kind,
    float X, float Y,
    float Dx, float Dy,
    RemotePointerButton Button,
    bool Down,
    ushort Usage,
    bool Repeating,
    RemoteInputModifiers Modifiers,
    string? Text)
{
    /// <summary>Longest string one text record may carry. A viewer splits beyond this.</summary>
    public const int MaxTextBytes = 1024;

    public static RemoteInputRecord PointerMove(float x, float y) =>
        new(RemoteInputRecordKind.PointerMove, x, y, 0, 0, RemotePointerButton.Left, false, 0, false,
            RemoteInputModifiers.None, null);

    public static RemoteInputRecord PointerButton(RemotePointerButton button, bool down,
                                                  float x, float y) =>
        new(RemoteInputRecordKind.PointerButton, x, y, 0, 0, button, down, 0, false,
            RemoteInputModifiers.None, null);

    public static RemoteInputRecord PointerScroll(float dx, float dy, float x, float y) =>
        new(RemoteInputRecordKind.PointerScroll, x, y, dx, dy, RemotePointerButton.Left, false, 0,
            false, RemoteInputModifiers.None, null);

    public static RemoteInputRecord Key(ushort usage, bool down, bool repeating,
                                        RemoteInputModifiers modifiers) =>
        new(RemoteInputRecordKind.Key, 0, 0, 0, 0, RemotePointerButton.Left, down, usage, repeating,
            modifiers, null);

    public static RemoteInputRecord TextRecord(string text) =>
        new(RemoteInputRecordKind.Text, 0, 0, 0, 0, RemotePointerButton.Left, false, 0, false,
            RemoteInputModifiers.None, text);
}

public static class RemoteInputCodec
{
    /// <summary>Exact byte count for a type, or -1 for one this build does not
    /// know. Text is variable and answers -1; its length is in the record.</summary>
    public static int FixedLength(byte type) => type switch
    {
        0x01 => 9,
        0x02 => 11,
        0x03 => 17,
        0x04 => 7,
        _    => -1,
    };

    // ---- Encode ------------------------------------------------------------

    public static byte[] Encode(RemoteInputRecord record)
    {
        switch (record.Kind)
        {
            case RemoteInputRecordKind.PointerMove:
            {
                var buffer = new byte[9];
                buffer[0] = (byte)record.Kind;
                WriteFloat(buffer.AsSpan(1), record.X);
                WriteFloat(buffer.AsSpan(5), record.Y);
                return buffer;
            }
            case RemoteInputRecordKind.PointerButton:
            {
                var buffer = new byte[11];
                buffer[0] = (byte)record.Kind;
                buffer[1] = (byte)record.Button;
                buffer[2] = record.Down ? (byte)1 : (byte)0;
                WriteFloat(buffer.AsSpan(3), record.X);
                WriteFloat(buffer.AsSpan(7), record.Y);
                return buffer;
            }
            case RemoteInputRecordKind.PointerScroll:
            {
                var buffer = new byte[17];
                buffer[0] = (byte)record.Kind;
                WriteFloat(buffer.AsSpan(1), record.Dx);
                WriteFloat(buffer.AsSpan(5), record.Dy);
                WriteFloat(buffer.AsSpan(9), record.X);
                WriteFloat(buffer.AsSpan(13), record.Y);
                return buffer;
            }
            case RemoteInputRecordKind.Key:
            {
                var buffer = new byte[7];
                buffer[0] = (byte)record.Kind;
                BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(1), record.Usage);
                buffer[3] = record.Down ? (byte)1 : (byte)0;
                buffer[4] = record.Repeating ? (byte)1 : (byte)0;
                BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(5), (ushort)record.Modifiers);
                return buffer;
            }
            case RemoteInputRecordKind.Text:
            {
                // Truncated on a character boundary, never mid-scalar: half a
                // UTF-8 sequence is not a shorter string, it is an invalid one.
                string text = record.Text ?? "";
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                if (bytes.Length > RemoteInputRecord.MaxTextBytes)
                {
                    bytes = Encoding.UTF8.GetBytes(Truncate(text, RemoteInputRecord.MaxTextBytes));
                }
                var buffer = new byte[3 + bytes.Length];
                buffer[0] = (byte)record.Kind;
                BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(1), (ushort)bytes.Length);
                bytes.CopyTo(buffer.AsSpan(3));
                return buffer;
            }
            default:
                return [];
        }
    }

    /// <summary>Several records in one payload, which is how a burst of pointer
    /// moves travels without a frame each.</summary>
    public static byte[] Encode(IEnumerable<RemoteInputRecord> records)
    {
        var output = new List<byte>();
        foreach (var record in records) output.AddRange(Encode(record));
        return [.. output];
    }

    // ---- Decode ------------------------------------------------------------

    /// <summary>
    /// Decodes every record in a payload. Null for a payload malformed anywhere.
    /// </summary>
    /// <remarks>
    /// Partial success is deliberately not offered: a record of the wrong length
    /// means the stream position is no longer trustworthy, and injecting the
    /// prefix of a corrupted burst is worse than injecting nothing.
    /// </remarks>
    public static List<RemoteInputRecord>? Decode(ReadOnlySpan<byte> payload)
    {
        var records = new List<RemoteInputRecord>();
        int index = 0;

        while (index < payload.Length)
        {
            byte type = payload[index];

            if (type == (byte)RemoteInputRecordKind.Text)
            {
                if (payload.Length - index < 3) return null;
                int count = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(index + 1, 2));
                if (count > RemoteInputRecord.MaxTextBytes) return null;
                if (payload.Length - index - 3 < count) return null;

                string text;
                try
                {
                    // Invalid UTF-8 is a malformed record, not an empty string:
                    // silently injecting nothing would look like a dropped
                    // keystroke rather than a broken peer.
                    text = new UTF8Encoding(false, throwOnInvalidBytes: true)
                        .GetString(payload.Slice(index + 3, count));
                }
                catch (ArgumentException) { return null; }

                records.Add(RemoteInputRecord.TextRecord(text));
                index += 3 + count;
                continue;
            }

            int length = FixedLength(type);
            if (length < 0) return null;
            if (payload.Length - index < length) return null;

            var body = payload.Slice(index + 1, length - 1);
            switch ((RemoteInputRecordKind)type)
            {
                case RemoteInputRecordKind.PointerMove:
                    records.Add(RemoteInputRecord.PointerMove(ReadFloat(body), ReadFloat(body[4..])));
                    break;

                case RemoteInputRecordKind.PointerButton:
                {
                    byte raw = body[0];
                    if (raw > (byte)RemotePointerButton.Middle)
                    {
                        // A reserved button. The record is well-formed, so the
                        // stream stays trustworthy — skip just this one.
                        index += length;
                        continue;
                    }
                    records.Add(RemoteInputRecord.PointerButton(
                        (RemotePointerButton)raw, body[1] != 0,
                        ReadFloat(body[2..]), ReadFloat(body[6..])));
                    break;
                }

                case RemoteInputRecordKind.PointerScroll:
                    records.Add(RemoteInputRecord.PointerScroll(
                        ReadFloat(body), ReadFloat(body[4..]),
                        ReadFloat(body[8..]), ReadFloat(body[12..])));
                    break;

                case RemoteInputRecordKind.Key:
                {
                    ushort usage = BinaryPrimitives.ReadUInt16BigEndian(body);
                    ushort rawModifiers = BinaryPrimitives.ReadUInt16BigEndian(body[4..]);
                    records.Add(RemoteInputRecord.Key(
                        usage, body[2] != 0, body[3] != 0,
                        (RemoteInputModifiers)rawModifiers & RemoteInputModifiers.Known));
                    break;
                }

                default:
                    return null;
            }
            index += length;
        }
        return records;
    }

    // ---- Primitives --------------------------------------------------------

    private static void WriteFloat(Span<byte> destination, float value) =>
        BinaryPrimitives.WriteUInt32BigEndian(destination, BitConverter.SingleToUInt32Bits(value));

    private static float ReadFloat(ReadOnlySpan<byte> source) =>
        BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32BigEndian(source));

    /// <summary>Longest prefix whose UTF-8 fits in `limit` bytes.</summary>
    private static string Truncate(string text, int limit)
    {
        var builder = new StringBuilder();
        int used = 0;
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            string element = (string)enumerator.Current;
            int size = Encoding.UTF8.GetByteCount(element);
            if (used + size > limit) break;
            builder.Append(element);
            used += size;
        }
        return builder.ToString();
    }
}
