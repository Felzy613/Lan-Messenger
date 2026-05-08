using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace LanMessenger.Core.Protocol;

public static class FrameCodec
{
    public const int MaxFrameSize = 50 * 1024 * 1024; // 50 MiB

    private static readonly JsonSerializerOptions _compact = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // Encodes a typed object as a length-prefixed frame.
    // Layout: [4 bytes big-endian uint32 length][UTF-8 JSON body]
    public static byte[] Encode<T>(T value)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value, _compact);
        return BuildFrame(body);
    }

    // Encodes a raw dictionary as a length-prefixed frame.
    public static byte[] EncodeDict(Dictionary<string, object?> dict)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(dict, _compact);
        return BuildFrame(body);
    }

    // Reads exactly one frame from stream. Returns null on clean EOF.
    // Throws IOException on protocol violations.
    public static byte[]? ReadFrame(Stream stream)
    {
        byte[]? header = ReadExact(stream, 4);
        if (header is null) return null;

        int length = (int)BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length <= 0 || length > MaxFrameSize)
            throw new InvalidDataException($"Invalid frame size: {length}");

        byte[]? body = ReadExact(stream, length);
        return body; // null means connection closed mid-frame (treated as error by caller)
    }

    // Async version for use with NetworkStream.
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct = default)
    {
        byte[]? header = await ReadExactAsync(stream, 4, ct).ConfigureAwait(false);
        if (header is null) return null;

        int length = (int)BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length <= 0 || length > MaxFrameSize)
            throw new InvalidDataException($"Invalid frame size: {length}");

        return await ReadExactAsync(stream, length, ct).ConfigureAwait(false);
    }

    // Parses a frame body to a JsonDocument.
    public static JsonDocument ParseJson(byte[] data) => JsonDocument.Parse(data);

    // MARK: - Helpers

    private static byte[] BuildFrame(byte[] body)
    {
        if (body.Length == 0 || body.Length > MaxFrameSize)
            throw new InvalidOperationException($"Frame body out of range: {body.Length}");
        byte[] frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, 4), (uint)body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    // Returns null on clean EOF at the start; throws on mid-read disconnect.
    private static byte[]? ReadExact(Stream stream, int count)
    {
        byte[] buf = new byte[count];
        int total = 0;
        while (total < count)
        {
            int n = stream.Read(buf, total, count - total);
            if (n == 0)
            {
                if (total == 0) return null; // clean EOF
                throw new IOException("Connection closed mid-frame");
            }
            total += n;
        }
        return buf;
    }

    private static async Task<byte[]?> ReadExactAsync(Stream stream, int count, CancellationToken ct)
    {
        byte[] buf = new byte[count];
        int total = 0;
        while (total < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(total, count - total), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (total == 0) return null;
                throw new IOException("Connection closed mid-frame");
            }
            total += n;
        }
        return buf;
    }
}
