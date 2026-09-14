namespace LanMessenger.Core.Networking.Media;

// Conversion between the two H.264 bitstream packagings, which is the single
// biggest cross-platform risk in the remote-desktop work. Mirror of the macOS
// H264Bitstream.swift.
//
// VideoToolbox speaks AVCC: each NAL unit prefixed by its big-endian length,
// with SPS/PPS held out-of-band in the CMVideoFormatDescription.
// Media Foundation speaks Annex-B: each NAL unit prefixed by a 3- or 4-byte
// start code, with SPS/PPS emitted in-band ahead of every IDR.
//
// Neither decoder accepts the other's packaging, and the failure mode is not a
// clean error — it is a picture that never appears, or green garbage. So both
// directions are pure byte manipulation with no Media Foundation types, which
// is what makes them testable against a real encoder artefact.
//
// Observed on the real Windows encoder (spikes/windows-mf-probe, 2026-09-14):
// 60 frames produced 126 NAL units, SPS and PPS in-band before every IDR and at
// stream start, and 4-byte start codes throughout. Three-byte codes are still
// handled — that run exercised only the software MFT, not Quick Sync.

public static class H264NALType
{
    public const byte NonIDRSlice = 1;
    public const byte IDRSlice    = 5;
    public const byte SEI         = 6;
    public const byte SPS         = 7;
    public const byte PPS         = 8;
    public const byte AccessUnitDelimiter = 9;
}

/// <summary>
/// One NAL unit located inside a buffer. <see cref="Payload"/> excludes the
/// start code or length prefix; <see cref="Type"/> is the low 5 bits of the
/// first payload byte.
/// </summary>
public readonly record struct H264NALUnit(byte Type, byte[] Payload)
{
    public bool IsParameterSet => Type is H264NALType.SPS or H264NALType.PPS;
    public bool IsKeyframe => Type == H264NALType.IDRSlice;
}

public enum H264BitstreamFault
{
    NoStartCodeFound,
    TruncatedLengthPrefix,
    NalLengthExceedsBuffer,
    UnsupportedLengthSize,
}

public sealed class H264BitstreamException(H264BitstreamFault fault, string message) : Exception(message)
{
    public H264BitstreamFault Fault { get; } = fault;
}

public static class H264Bitstream
{
    /// <summary>
    /// NAL types that carry no picture data and are dropped when building sample
    /// data. Parameter sets live in the format description, and an access unit
    /// delimiter is decoration a decoder does not need.
    /// </summary>
    public static readonly HashSet<byte> NonPictureTypes =
        [H264NALType.SPS, H264NALType.PPS, H264NALType.AccessUnitDelimiter];

    // ---- Annex-B -----------------------------------------------------------

    /// <summary>Splits an Annex-B buffer into NAL units.</summary>
    /// <remarks>
    /// Scanning for 00 00 01 naively is safe, and deliberately so: H.264
    /// emulation-prevention bytes guarantee that sequence never occurs inside a
    /// NAL payload, so there is nothing to escape and no state to track. A
    /// 4-byte start code is a 3-byte code preceded by an extra zero, so the
    /// scanner must consume the whole code before continuing — advancing a
    /// single byte instead finds a phantom code inside every 4-byte one and
    /// reports exactly twice as many units as exist.
    /// </remarks>
    public static List<H264NALUnit> ScanAnnexB(ReadOnlySpan<byte> data)
    {
        var starts = new List<(int Offset, int CodeLength)>();
        int i = 0;
        while (i + 3 <= data.Length)
        {
            if (data[i] == 0 && data[i + 1] == 0)
            {
                if (i + 4 <= data.Length && data[i + 2] == 0 && data[i + 3] == 1)
                {
                    starts.Add((i, 4)); i += 4; continue;
                }
                if (data[i + 2] == 1)
                {
                    starts.Add((i, 3)); i += 3; continue;
                }
            }
            i++;
        }

        var units = new List<H264NALUnit>(starts.Count);
        for (int k = 0; k < starts.Count; k++)
        {
            int payloadStart = starts[k].Offset + starts[k].CodeLength;
            int payloadEnd = k + 1 < starts.Count ? starts[k + 1].Offset : data.Length;
            if (payloadEnd <= payloadStart) continue;
            byte[] payload = data[payloadStart..payloadEnd].ToArray();
            units.Add(new H264NALUnit((byte)(payload[0] & 0x1F), payload));
        }
        return units;
    }

    /// <summary>Extracts the parameter sets an Annex-B stream carries in-band.</summary>
    public static (List<byte[]> Sps, List<byte[]> Pps) ParameterSets(ReadOnlySpan<byte> annexB)
    {
        List<byte[]> sps = [], pps = [];
        foreach (var unit in ScanAnnexB(annexB))
        {
            if (unit.Type == H264NALType.SPS) sps.Add(unit.Payload);
            if (unit.Type == H264NALType.PPS) pps.Add(unit.Payload);
        }
        return (sps, pps);
    }

    /// <summary>True if the buffer contains an IDR slice.</summary>
    public static bool AnnexBContainsKeyframe(ReadOnlySpan<byte> data) =>
        ScanAnnexB(data).Any(u => u.IsKeyframe);

    // ---- Annex-B -> AVCC ---------------------------------------------------

    /// <summary>Rewrites Annex-B sample data as AVCC length-prefixed NAL units.</summary>
    /// <remarks>
    /// Parameter sets and access unit delimiters are dropped by default: SPS and
    /// PPS belong in the CMVideoFormatDescription on the far side, and feeding
    /// them in the sample data as well is a decode error on VideoToolbox rather
    /// than a harmless duplicate.
    /// </remarks>
    public static byte[] AnnexBToAVCC(
        ReadOnlySpan<byte> data, int nalLengthSize = 4, HashSet<byte>? dropping = null)
    {
        RequireLengthSize(nalLengthSize);
        dropping ??= NonPictureTypes;

        var units = ScanAnnexB(data);
        if (units.Count == 0)
            throw new H264BitstreamException(H264BitstreamFault.NoStartCodeFound,
                "no Annex-B start code in the buffer");

        using var output = new MemoryStream();
        foreach (var unit in units)
        {
            if (dropping.Contains(unit.Type)) continue;
            WriteLength(output, unit.Payload.Length, nalLengthSize);
            output.Write(unit.Payload);
        }
        return output.ToArray();
    }

    // ---- AVCC -> Annex-B ---------------------------------------------------

    /// <summary>Splits an AVCC buffer into NAL units.</summary>
    /// <remarks>
    /// <paramref name="nalLengthSize"/> must come from the format description,
    /// never be assumed to be 4. VideoToolbox emits 4 in practice, which is
    /// exactly why hard-coding it survives testing and fails later.
    /// </remarks>
    public static List<H264NALUnit> ScanAVCC(ReadOnlySpan<byte> data, int nalLengthSize)
    {
        RequireLengthSize(nalLengthSize);
        var units = new List<H264NALUnit>();
        int i = 0;
        while (i < data.Length)
        {
            if (i + nalLengthSize > data.Length)
                throw new H264BitstreamException(H264BitstreamFault.TruncatedLengthPrefix,
                    $"AVCC length prefix truncated at offset {i}");

            int length = 0;
            for (int k = 0; k < nalLengthSize; k++) length = (length << 8) | data[i + k];

            int payloadStart = i + nalLengthSize;
            if (length <= 0 || payloadStart + length > data.Length)
                throw new H264BitstreamException(H264BitstreamFault.NalLengthExceedsBuffer,
                    $"AVCC NAL at offset {i} claims {length} bytes, past the end of the buffer");

            byte[] payload = data[payloadStart..(payloadStart + length)].ToArray();
            units.Add(new H264NALUnit((byte)(payload[0] & 0x1F), payload));
            i = payloadStart + length;
        }
        return units;
    }

    /// <summary>Rewrites AVCC sample data as an Annex-B byte stream.</summary>
    /// <remarks>
    /// When parameter sets are supplied and the buffer contains an IDR, the SPS
    /// and PPS are emitted ahead of it. That is not optional politeness: Media
    /// Foundation's decoder has no out-of-band channel for them, so a stream
    /// whose parameter sets live only in a format description decodes to nothing.
    /// </remarks>
    public static byte[] AVCCToAnnexB(
        ReadOnlySpan<byte> data, int nalLengthSize,
        IReadOnlyList<byte[]>? sps = null, IReadOnlyList<byte[]>? pps = null)
    {
        var units = ScanAVCC(data, nalLengthSize);
        using var output = new MemoryStream();

        bool hasKeyframe = units.Any(u => u.IsKeyframe);
        if (hasKeyframe && (sps is { Count: > 0 } || pps is { Count: > 0 }))
        {
            foreach (var set in sps ?? []) { WriteStartCode(output); output.Write(set); }
            foreach (var set in pps ?? []) { WriteStartCode(output); output.Write(set); }
        }
        foreach (var unit in units)
        {
            WriteStartCode(output);
            output.Write(unit.Payload);
        }
        return output.ToArray();
    }

    /// <summary>Builds an Annex-B buffer from explicit NAL payloads.</summary>
    public static byte[] AnnexB(params byte[][] payloads)
    {
        using var output = new MemoryStream();
        foreach (var payload in payloads) { WriteStartCode(output); output.Write(payload); }
        return output.ToArray();
    }

    // ---- Private -----------------------------------------------------------

    private static void RequireLengthSize(int size)
    {
        if (size is not (1 or 2 or 4))
            throw new H264BitstreamException(H264BitstreamFault.UnsupportedLengthSize,
                $"NAL length prefix size {size} is not 1, 2 or 4");
    }

    /// Four-byte start codes everywhere. Three-byte codes are legal and are
    /// parsed, but emitting one size uniformly keeps our output trivially
    /// predictable, and the extra byte per NAL is noise next to a video frame.
    private static void WriteStartCode(Stream output) => output.Write([0x00, 0x00, 0x00, 0x01]);

    private static void WriteLength(Stream output, int length, int size)
    {
        for (int shift = (size - 1) * 8; shift >= 0; shift -= 8)
            output.WriteByte((byte)((length >> shift) & 0xFF));
    }
}
