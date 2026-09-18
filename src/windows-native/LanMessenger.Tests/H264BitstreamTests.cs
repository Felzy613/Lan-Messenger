using LanMessenger.Core.Networking.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanMessenger.Tests;

// Guards the AVCC <-> Annex-B conversion. Mirror of the macOS
// H264BitstreamTests.swift, with the same test names so a failure here names a
// macOS test that already passes.
//
// The tests that matter run against windows_h264_sample.h264, produced by the
// real Microsoft H264 Encoder MFT on this very machine
// (spikes/windows-mf-probe, 2026-09-14). Testing a converter against its own
// output proves only self-consistency; testing it against a genuine encoder
// artefact is what de-risks the interop.
[TestClass]
public class H264BitstreamTests
{
    private static byte[] Nal(byte type, byte fill = 0xAA, int count = 8)
    {
        var d = new byte[count + 1];
        d[0] = type;
        for (int i = 1; i < d.Length; i++) d[i] = fill;
        return d;
    }

    // ---- Synthetic structure ----------------------------------------------

    [TestMethod]
    public void ScannerConsumesFourByteStartCodesWhole()
    {
        // The bug this pins: a 4-byte start code contains a 3-byte one at
        // offset+1, so a scanner that advances a single byte finds a phantom NAL
        // inside every real one and reports exactly double. Not hypothetical —
        // the first analysis of this very fixture reported "126 four-byte and
        // 126 three-byte codes", and the equal counts were the only clue.
        byte[] stream = H264Bitstream.AnnexB(
            Nal(H264NALType.SPS), Nal(H264NALType.PPS), Nal(H264NALType.IDRSlice));

        var units = H264Bitstream.ScanAnnexB(stream);
        Assert.AreEqual(3, units.Count, "a 4-byte start code must be consumed whole");
        CollectionAssert.AreEqual(
            new byte[] { H264NALType.SPS, H264NALType.PPS, H264NALType.IDRSlice },
            units.Select(u => u.Type).ToArray());
    }

    [TestMethod]
    public void ScannerHandlesThreeByteStartCodes()
    {
        var stream = new List<byte>();
        stream.AddRange([0, 0, 1]); stream.AddRange(Nal(H264NALType.SPS));
        stream.AddRange([0, 0, 1]); stream.AddRange(Nal(H264NALType.IDRSlice));
        var units = H264Bitstream.ScanAnnexB(stream.ToArray());
        Assert.AreEqual(2, units.Count);
        CollectionAssert.AreEqual(new byte[] { H264NALType.SPS, H264NALType.IDRSlice },
                                  units.Select(u => u.Type).ToArray());
    }

    [TestMethod]
    public void ScannerHandlesMixedStartCodeSizes()
    {
        // Legal, and some encoders do it. Our own output is uniformly 4-byte,
        // but the reader must not assume the peer's is.
        var stream = new List<byte>();
        stream.AddRange([0, 0, 0, 1]); stream.AddRange(Nal(H264NALType.SPS));
        stream.AddRange([0, 0, 1]);    stream.AddRange(Nal(H264NALType.PPS));
        stream.AddRange([0, 0, 0, 1]); stream.AddRange(Nal(H264NALType.IDRSlice));
        CollectionAssert.AreEqual(
            new byte[] { H264NALType.SPS, H264NALType.PPS, H264NALType.IDRSlice },
            H264Bitstream.ScanAnnexB(stream.ToArray()).Select(u => u.Type).ToArray());
    }

    [TestMethod]
    public void EmptyAndStartCodeOnlyBuffers()
    {
        Assert.AreEqual(0, H264Bitstream.ScanAnnexB([]).Count);
        Assert.AreEqual(0, H264Bitstream.ScanAnnexB([0, 0, 0, 1]).Count,
            "a start code with no payload is not a NAL unit");
        Assert.ThrowsException<H264BitstreamException>(() => H264Bitstream.AnnexBToAVCC([1, 2, 3]));
    }

    // ---- Round trips -------------------------------------------------------

    [TestMethod]
    public void AnnexBToAVCCAndBack()
    {
        byte[] slice = Nal(H264NALType.NonIDRSlice, 0x11, 40);
        byte[] stream = H264Bitstream.AnnexB(slice);

        byte[] avcc = H264Bitstream.AnnexBToAVCC(stream);
        Assert.AreEqual(4 + slice.Length, avcc.Length, "4-byte length prefix plus payload");
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, (byte)slice.Length }, avcc[..4]);

        CollectionAssert.AreEqual(stream, H264Bitstream.AVCCToAnnexB(avcc, 4));
    }

    [TestMethod]
    public void AVCCLengthSizesOtherThanFour()
    {
        byte[] slice = Nal(H264NALType.NonIDRSlice, 0x22, 20);
        byte[] stream = H264Bitstream.AnnexB(slice);
        foreach (int size in new[] { 1, 2, 4 })
        {
            byte[] avcc = H264Bitstream.AnnexBToAVCC(stream, size);
            Assert.AreEqual(size + slice.Length, avcc.Length);
            var units = H264Bitstream.ScanAVCC(avcc, size);
            Assert.AreEqual(1, units.Count);
            CollectionAssert.AreEqual(slice, units[0].Payload);
        }
        Assert.ThrowsException<H264BitstreamException>(() => H264Bitstream.AnnexBToAVCC(stream, 3),
            "3 is not a legal NAL length size");
    }

    [TestMethod]
    public void ParameterSetsAreDroppedFromSampleData()
    {
        byte[] stream = H264Bitstream.AnnexB(
            Nal(H264NALType.AccessUnitDelimiter, 0x01, 1),
            Nal(H264NALType.SPS, 0x67, 12),
            Nal(H264NALType.PPS, 0x68, 3),
            Nal(H264NALType.IDRSlice, 0x65, 60));
        var units = H264Bitstream.ScanAVCC(H264Bitstream.AnnexBToAVCC(stream), 4);
        CollectionAssert.AreEqual(new byte[] { H264NALType.IDRSlice },
            units.Select(u => u.Type).ToArray(), "only picture data belongs in the sample");
    }

    [TestMethod]
    public void KeyframeGetsParameterSetsPrependedOnTheWayOut()
    {
        byte[] sps = Nal(H264NALType.SPS, 0x67, 12);
        byte[] pps = Nal(H264NALType.PPS, 0x68, 3);
        byte[] avcc = H264Bitstream.AnnexBToAVCC(H264Bitstream.AnnexB(Nal(H264NALType.IDRSlice, 0x65, 50)));

        byte[] outBytes = H264Bitstream.AVCCToAnnexB(avcc, 4, [sps], [pps]);
        CollectionAssert.AreEqual(
            new byte[] { H264NALType.SPS, H264NALType.PPS, H264NALType.IDRSlice },
            H264Bitstream.ScanAnnexB(outBytes).Select(u => u.Type).ToArray());
    }

    [TestMethod]
    public void NonKeyframeDoesNotGetParameterSets()
    {
        byte[] sps = Nal(H264NALType.SPS, 0x67, 12);
        byte[] pps = Nal(H264NALType.PPS, 0x68, 3);
        byte[] avcc = H264Bitstream.AnnexBToAVCC(H264Bitstream.AnnexB(Nal(H264NALType.NonIDRSlice, 0x41, 50)));

        byte[] outBytes = H264Bitstream.AVCCToAnnexB(avcc, 4, [sps], [pps]);
        CollectionAssert.AreEqual(new byte[] { H264NALType.NonIDRSlice },
            H264Bitstream.ScanAnnexB(outBytes).Select(u => u.Type).ToArray(),
            "repeating parameter sets on every frame is pure bitrate waste");
    }

    // ---- Malformed AVCC ----------------------------------------------------

    [TestMethod]
    public void MalformedAVCCIsRejectedNotMisparsed()
    {
        var claimsTooMuch = new List<byte> { 0x00, 0x00, 0xFF, 0xFF };
        claimsTooMuch.AddRange(Enumerable.Repeat((byte)0x41, 10));
        Assert.ThrowsException<H264BitstreamException>(
            () => H264Bitstream.ScanAVCC(claimsTooMuch.ToArray(), 4));

        Assert.ThrowsException<H264BitstreamException>(
            () => H264Bitstream.ScanAVCC([0x00, 0x00], 4), "a truncated length prefix must fault");
        Assert.ThrowsException<H264BitstreamException>(
            () => H264Bitstream.ScanAVCC([0, 0, 0, 0], 4), "a zero-length NAL would loop forever");
    }

    // ---- The real Windows encoder output -----------------------------------

    private static byte[] WindowsSample()
    {
        const string name = "windows_h264_sample.h264";
        if (File.Exists(name)) return File.ReadAllBytes(name);
        string beside = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(beside)) return File.ReadAllBytes(beside);
        return File.ReadAllBytes(Path.Combine(
            Path.GetDirectoryName(typeof(H264BitstreamTests).Assembly.Location) ?? ".", name));
    }

    [TestMethod]
    public void WindowsSampleStructureMatchesWhatTheEncoderActuallyProduced()
    {
        var units = H264Bitstream.ScanAnnexB(WindowsSample());

        // Pinned against the recorded run: 60 NV12 frames in, 126 NAL units out.
        Assert.AreEqual(126, units.Count, "scanner disagrees with the recorded structure");

        var counts = units.GroupBy(u => u.Type).ToDictionary(g => g.Key, g => g.Count());
        Assert.AreEqual(60, counts[H264NALType.AccessUnitDelimiter], "one AUD per frame");
        Assert.AreEqual(58, counts[H264NALType.NonIDRSlice]);
        Assert.AreEqual(2,  counts[H264NALType.IDRSlice]);
        Assert.AreEqual(2,  counts[H264NALType.SPS]);
        Assert.AreEqual(2,  counts[H264NALType.PPS]);
        Assert.AreEqual(60, counts[H264NALType.NonIDRSlice] + counts[H264NALType.IDRSlice],
            "one slice per frame");
    }

    [TestMethod]
    public void WindowsSampleCarriesParameterSetsInBandBeforeEveryIDR()
    {
        // The property the macOS receive path depends on. If Media Foundation
        // ever stopped doing it we would have to negotiate parameter sets out of
        // band, so it is worth a test rather than an assumption.
        var units = H264Bitstream.ScanAnnexB(WindowsSample());
        var idrIndices = Enumerable.Range(0, units.Count).Where(i => units[i].IsKeyframe).ToList();
        Assert.AreEqual(2, idrIndices.Count);

        foreach (int idr in idrIndices)
        {
            // Look back to the start of the access unit, not a fixed number of
            // NAL units: the encoder puts two SEI messages between the parameter
            // sets and the first IDR, so a narrow window misses them.
            int start = idr;
            while (start > 0 && units[start - 1].Type != H264NALType.AccessUnitDelimiter) start--;
            var accessUnit = units.GetRange(start, idr - start).Select(u => u.Type).ToList();
            Assert.IsTrue(accessUnit.Contains(H264NALType.SPS),
                $"no SPS in the access unit containing the IDR at {idr}");
            Assert.IsTrue(accessUnit.Contains(H264NALType.PPS),
                $"no PPS in the access unit containing the IDR at {idr}");
        }
    }

    [TestMethod]
    public void WindowsSampleParameterSetsAreExtractable()
    {
        var (sps, pps) = H264Bitstream.ParameterSets(WindowsSample());
        Assert.AreEqual(2, sps.Count);
        Assert.AreEqual(2, pps.Count);
        // Recorded sizes from the real stream; a converter that silently
        // truncated a parameter set would still "work" without this.
        Assert.AreEqual(25, sps[0].Length);
        Assert.AreEqual(4,  pps[0].Length);
        Assert.AreEqual(H264NALType.SPS, (byte)(sps[0][0] & 0x1F));
        Assert.AreEqual(H264NALType.PPS, (byte)(pps[0][0] & 0x1F));
        CollectionAssert.AreEqual(sps[0], sps[1], "both IDRs should carry identical parameter sets");
    }

    [TestMethod]
    public void WindowsSampleConvertsToAVCCAndBackWithoutLoss()
    {
        // The end-to-end property: Windows Annex-B in, AVCC for VideoToolbox,
        // Annex-B back out for the return path, with every picture NAL intact.
        byte[] original = WindowsSample();
        var pictureUnits = H264Bitstream.ScanAnnexB(original)
            .Where(u => !H264Bitstream.NonPictureTypes.Contains(u.Type)).ToList();

        byte[] avcc = H264Bitstream.AnnexBToAVCC(original);
        var roundTripped = H264Bitstream.ScanAVCC(avcc, 4);

        Assert.AreEqual(pictureUnits.Count, roundTripped.Count);
        for (int i = 0; i < pictureUnits.Count; i++)
            CollectionAssert.AreEqual(pictureUnits[i].Payload, roundTripped[i].Payload,
                $"payload bytes of NAL {i} must survive the conversion untouched");

        var back = H264Bitstream.ScanAnnexB(H264Bitstream.AVCCToAnnexB(avcc, 4));
        Assert.AreEqual(pictureUnits.Count, back.Count);
    }

    [TestMethod]
    public void WindowsSampleKeyframeDetection()
    {
        byte[] data = WindowsSample();
        Assert.IsTrue(H264Bitstream.AnnexBContainsKeyframe(data));

        var nonIDR = H264Bitstream.ScanAnnexB(data).First(u => u.Type == H264NALType.NonIDRSlice);
        Assert.IsFalse(H264Bitstream.AnnexBContainsKeyframe(H264Bitstream.AnnexB(nonIDR.Payload)));
    }

    // ---- The macOS encoder output, for the other direction ------------------

    private static byte[] MacOSSample()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "macos_h264_sample.h264");
        if (!File.Exists(path)) path = "macos_h264_sample.h264";
        return File.ReadAllBytes(path);
    }

    [TestMethod]
    public void MacOSSampleIsTheExactShapeTheWindowsOneIsNot()
    {
        // Real VideoToolbox output, committed so this suite can assert the
        // macOS -> Windows direction without a Mac, exactly as the Swift suite
        // asserts the reverse without a PC.
        //
        // Its value is in how unlike the Windows sample it is: VideoToolbox
        // emits no access unit delimiters at all and parameter sets only at
        // IDRs. It is direct evidence for the rule that an AUD-based split
        // collapses a stream — there are no AUDs here to split on.
        var units = H264Bitstream.ScanAnnexB(MacOSSample());
        Assert.AreEqual(64, units.Count, "pinned against the recorded run");

        var counts = new Dictionary<byte, int>();
        foreach (var unit in units)
        {
            counts.TryGetValue(unit.Type, out var n);
            counts[unit.Type] = n + 1;
        }

        Assert.IsFalse(counts.ContainsKey(H264NALType.AccessUnitDelimiter),
                       "VideoToolbox emits no AUDs — that is the point of this fixture");
        Assert.AreEqual(2, counts[H264NALType.IDRSlice]);
        Assert.AreEqual(58, counts[H264NALType.NonIDRSlice]);
        Assert.AreEqual(2, counts[H264NALType.SPS], "parameter sets ride the IDRs");
        Assert.AreEqual(2, counts[H264NALType.PPS]);
    }

    [TestMethod]
    public void MacOSSampleCarriesParameterSetsInBandBeforeEveryIDR()
    {
        // What Media Foundation depends on: it has no out-of-band channel for
        // parameter sets, so a stream whose SPS/PPS live only in a
        // CMVideoFormatDescription decodes to nothing here.
        var units = H264Bitstream.ScanAnnexB(MacOSSample());
        for (int i = 0; i < units.Count; i++)
        {
            if (units[i].Type != H264NALType.IDRSlice) continue;
            bool sps = false, pps = false;
            for (int k = Math.Max(0, i - 4); k < i; k++)
            {
                if (units[k].Type == H264NALType.SPS) sps = true;
                if (units[k].Type == H264NALType.PPS) pps = true;
            }
            Assert.IsTrue(sps, $"IDR at {i} has no SPS before it");
            Assert.IsTrue(pps, $"IDR at {i} has no PPS before it");
        }
    }
}
