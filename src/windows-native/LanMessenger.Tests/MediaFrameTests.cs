using LanMessenger.Core.Networking.Media;
using LanMessenger.Core.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LanMessenger.Tests;

// Guards the remote-desktop media framing, mux and demux. Mirror of the macOS
// MediaFrameTests.swift, deliberately with the same test names so a Windows CI
// failure names a macOS test that already passes.
//
// Every test here maps to a failure that is silent, cross-version, or only
// reproducible under load:
//
//   * A length computed from the plaintext instead of the sealed payload is off
//     by exactly the 16-byte GCM tag, and because the length sits inside the
//     AAD, EVERY frame then fails the peer's tag check with no other symptom.
//   * Re-encoding a parsed header to build the AAD differs by one byte from any
//     peer that sets a reserved flag bit — a failure that appears only after a
//     future version ships.
//   * An input event stuck behind a 500 KiB keyframe is the exact complaint the
//     protocol calls out, and it is invisible to inspection.
//   * A sequence gate initialised to 0 rejects a legitimate first frame and the
//     session dies at hello looking like a crypto fault.
//
// See PROTOCOL.md -> Remote Desktop -> Media Framing.
[TestClass]
public class MediaFrameTests
{
    private static readonly byte[] Key  = Convert.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
    private static readonly byte[] Salt = [0xDE, 0xAD, 0xBE, 0xEF];

    // ---- Header layout -----------------------------------------------------

    [TestMethod]
    public void HeaderIsTwentyTwoBytesAndMatchesCryptoAAD()
    {
        Assert.AreEqual(22, MediaFrameCodec.HeaderLength);
        Assert.AreEqual(Core.Crypto.RemoteSessionCrypto.HeaderSize, MediaFrameCodec.HeaderLength,
            "the AAD is the header; if these diverge every frame fails authentication");
        Assert.AreEqual(18, MediaFrameCodec.PostLengthHeader);
    }

    [TestMethod]
    public void EncodedHeaderByteLayout()
    {
        byte[] h = MediaFrameCodec.EncodeHeader(
            MediaChannel.Video, MediaFlags.Keyframe | MediaFlags.Fragmented,
            0x0102_0304_0506_0708UL, 0x1112_1314_1516_1718UL, 100);
        Assert.AreEqual(22, h.Length);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 118 }, h[..4]);   // 18 + 100
        Assert.AreEqual((byte)MediaChannel.Video, h[4]);
        Assert.AreEqual(0b0000_0011, h[5]);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, h[6..14]);
        CollectionAssert.AreEqual(new byte[] { 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18 }, h[14..22]);
    }

    [TestMethod]
    public void LengthCountsSealedPayloadNotPlaintext()
    {
        byte[] plaintext = new byte[64];
        Array.Fill(plaintext, (byte)7);
        byte[] frame = MediaFrameCodec.EncodeFrame(
            MediaChannel.Control, MediaFlags.None, 1, 9, plaintext, Key, Salt);
        var header = MediaFrameCodec.DecodeHeader(frame);
        Assert.AreEqual(18 + 64 + Core.Crypto.RemoteSessionCrypto.TagSize, header.Length);
        Assert.AreEqual(4 + header.Length, frame.Length);
        Assert.AreEqual(64 + Core.Crypto.RemoteSessionCrypto.TagSize, header.SealedPayloadLength);
    }

    [TestMethod]
    public void MediaCapIsIndependentOfTheJsonCap()
    {
        // A future "unify the two codecs" refactor must break here rather than
        // silently restore a 50 MiB allocation on a path that runs at 30 fps.
        Assert.AreNotEqual(FrameCodec.MaxFrameSize, MediaFrameCodec.MaxFrameLength);
        Assert.AreEqual(4 * 1024 * 1024, MediaFrameCodec.MaxFrameLength);
        Assert.IsTrue(MediaFrameCodec.MaxFrameLength < FrameCodec.MaxFrameSize);
    }

    // ---- Round trip --------------------------------------------------------

    [TestMethod]
    public void FrameRoundTrip()
    {
        byte[] plaintext = Encoding.UTF8.GetBytes("a media payload");
        byte[] frame = MediaFrameCodec.EncodeFrame(
            MediaChannel.Input, MediaFlags.None, 42, 1234, plaintext, Key, Salt);

        var header = MediaFrameCodec.DecodeHeader(frame);
        Assert.AreEqual(MediaChannel.Input, header.Channel);
        Assert.AreEqual(42UL, header.Sequence);
        Assert.AreEqual(1234UL, header.CaptureUs);

        byte[] opened = MediaFrameCodec.DecodePayload(
            header, frame.AsSpan(0, 22), frame.AsSpan(22), Key, Salt);
        CollectionAssert.AreEqual(plaintext, opened);
    }

    [TestMethod]
    public void EmptyPayloadRoundTrips()
    {
        byte[] frame = MediaFrameCodec.EncodeFrame(
            MediaChannel.Stats, MediaFlags.None, 0, 0, [], Key, Salt);
        Assert.AreEqual(22 + Core.Crypto.RemoteSessionCrypto.TagSize, frame.Length);
        Assert.AreEqual(MediaFrameCodec.MinFrameLength, MediaFrameCodec.DecodeHeader(frame).Length);
    }

    // ---- Reserved flag bits ------------------------------------------------

    [TestMethod]
    public void ReservedFlagBitsSurviveRoundTripAndDoNotBreakAuthentication()
    {
        const byte rawFlags = 0b0010_0001;   // keyframe + a reserved bit
        byte[] header = MediaFrameCodec.EncodeHeader(
            MediaChannel.Video, MediaFlags.Keyframe, 3, 5, MediaFrameCodec.SealedLength(8));
        header[5] = rawFlags;

        byte[] payload = new byte[8];
        Array.Fill(payload, (byte)1);
        byte[] sealedBody = Core.Crypto.RemoteSessionCrypto.Seal(payload, header, Key, Salt, 3);

        var parsed = MediaFrameCodec.DecodeHeader(header);
        Assert.AreEqual(rawFlags, parsed.RawFlags, "the raw byte must be retained verbatim");
        Assert.AreEqual(MediaFlags.Keyframe, parsed.Flags, "interpretation masks the reserved bit off");
        Assert.IsTrue(parsed.IsKeyframe);

        // Authenticating against the received bytes succeeds...
        byte[] opened = MediaFrameCodec.DecodePayload(parsed, header, sealedBody, Key, Salt);
        CollectionAssert.AreEqual(payload, opened);

        // ...and against a normalised re-encode it does not. This is the bug.
        byte[] reEncoded = MediaFrameCodec.EncodeHeader(
            MediaChannel.Video, parsed.Flags, 3, 5, MediaFrameCodec.SealedLength(8));
        CollectionAssert.AreNotEqual(header, reEncoded);
        Assert.ThrowsException<MediaProtocolException>(
            () => MediaFrameCodec.DecodePayload(parsed, reEncoded, sealedBody, Key, Salt),
            "re-encoding the header to build the AAD must be shown to break authentication");
    }

    [TestMethod]
    public void UnknownChannelIsParsedNotRejected()
    {
        byte[] header = MediaFrameCodec.EncodeHeader(MediaChannel.Control, MediaFlags.None, 1, 0, 16);
        header[4] = 200;
        var parsed = MediaFrameCodec.DecodeHeader(header);
        Assert.IsNull(parsed.Channel, "forward compatibility: unknown ids parse, the reader discards");
        Assert.AreEqual(200, parsed.RawChannel);
    }

    // ---- Length bounds -----------------------------------------------------

    [TestMethod]
    public void LengthBoundsRejectedBeforeAllocation()
    {
        static byte[] LengthBytes(uint n)
        {
            byte[] b = new byte[22];
            BinaryPrimitives.WriteUInt32BigEndian(b, n);
            return b;
        }
        Assert.ThrowsException<MediaProtocolException>(
            () => MediaFrameCodec.DecodeLength(LengthBytes((uint)MediaFrameCodec.MaxFrameLength + 1)));
        Assert.ThrowsException<MediaProtocolException>(() => MediaFrameCodec.DecodeLength(LengthBytes(0)));
        Assert.ThrowsException<MediaProtocolException>(() => MediaFrameCodec.DecodeLength(LengthBytes(5)),
            "a length below the 18+16 minimum cannot be a well-formed frame");
        Assert.ThrowsException<MediaProtocolException>(() => MediaFrameCodec.DecodeLength(LengthBytes(0xFFFF_FFFF)),
            "a hostile length must not reach an allocation");
        Assert.AreEqual(MediaFrameCodec.MinFrameLength,
            MediaFrameCodec.DecodeLength(LengthBytes((uint)MediaFrameCodec.MinFrameLength)));
    }

    [TestMethod]
    public void OversizePlaintextIsRefused()
    {
        Assert.ThrowsException<MediaProtocolException>(() => MediaFrameCodec.EncodeFrame(
            MediaChannel.Video, MediaFlags.None, 0, 0,
            new byte[MediaFrameCodec.MaxPlaintext + 1], Key, Salt));
    }

    // ---- Sequence gate -----------------------------------------------------

    [TestMethod]
    public void SequenceGateAcceptsZeroFirst()
    {
        // A reconnect restarts at zero against fresh keys. Initialising the gate
        // to 0 instead of null would reject this and kill the session at hello.
        var gate = new MediaSequenceGate();
        Assert.IsNull(gate.LastAccepted);
        Assert.IsTrue(gate.Admit(0));
        Assert.AreEqual(0UL, gate.LastAccepted);
    }

    [TestMethod]
    public void SequenceGateRequiresStrictIncrease()
    {
        var gate = new MediaSequenceGate();
        Assert.IsTrue(gate.Admit(0));
        Assert.IsTrue(gate.Admit(1));
        Assert.IsFalse(gate.Admit(1), "a repeat is a replay");
        Assert.IsFalse(gate.Admit(0), "a rewind is a replay");
        Assert.IsTrue(gate.Admit(99), "gaps are fine; only order matters");
        Assert.IsFalse(gate.Admit(98));
    }

    // ---- Scheduler: fragmentation ------------------------------------------

    [TestMethod]
    public void SmallVideoFrameIsNotMarkedFragmented()
    {
        var s = new MediaWriteScheduler();
        s.Submit(new MediaOutboundFrame(MediaChannel.Video, new byte[100], 7));
        var seg = s.NextSegment();
        Assert.AreEqual(MediaFlags.None, seg!.Value.Flags,
            "a one-fragment frame marked fragmented faults the peer's reassembler");
        Assert.IsNull(s.NextSegment());
    }

    [TestMethod]
    public void LargeVideoFrameFragmentsWithCorrectFlags()
    {
        var s = new MediaWriteScheduler();
        int size = MediaFrameCodec.MaxFragmentPayload * 2 + 500;
        s.Submit(new MediaOutboundFrame(MediaChannel.Video, new byte[size], 11, Keyframe: true));

        var segs = new List<MediaSegment>();
        while (s.NextSegment() is { } seg) segs.Add(seg);

        Assert.AreEqual(3, segs.Count);
        CollectionAssert.AreEqual(
            new[] { MediaFrameCodec.MaxFragmentPayload, MediaFrameCodec.MaxFragmentPayload, 500 },
            segs.Select(x => x.Payload.Length).ToArray());
        for (int i = 0; i < segs.Count; i++)
        {
            Assert.IsTrue(segs[i].Flags.HasFlag(MediaFlags.Fragmented));
            Assert.IsTrue(segs[i].Flags.HasFlag(MediaFlags.Keyframe),
                "keyframe rides every segment so a mid-frame joiner can classify it");
            Assert.AreEqual(i == segs.Count - 1, segs[i].Flags.HasFlag(MediaFlags.FinalFragment));
            Assert.AreEqual(11UL, segs[i].CaptureUs);
        }
    }

    // ---- Scheduler: interleaving (the headline requirement) ----------------

    [TestMethod]
    public void InputNeverWaitsBehindAKeyframe()
    {
        // The product requirement, stated as an assertion.
        var s = new MediaWriteScheduler();
        s.Submit(new MediaOutboundFrame(MediaChannel.Video, new byte[512 * 1024], 1, Keyframe: true));
        Assert.AreEqual(MediaChannel.Video, s.NextSegment()!.Value.Channel);

        s.Submit(new MediaOutboundFrame(MediaChannel.Input, [0xAB], 2));
        Assert.AreEqual(MediaChannel.Input, s.NextSegment()!.Value.Channel,
            "an input event must overtake the remainder of a keyframe");
        Assert.AreEqual(MediaChannel.Video, s.NextSegment()!.Value.Channel,
            "and video then resumes where it left off");
    }

    [TestMethod]
    public void ChannelPriorityOrder()
    {
        var s = new MediaWriteScheduler();
        s.Submit(new MediaOutboundFrame(MediaChannel.Stats,   [4], 0));
        s.Submit(new MediaOutboundFrame(MediaChannel.Cursor,  [3], 0));
        s.Submit(new MediaOutboundFrame(MediaChannel.Input,   [2], 0));
        s.Submit(new MediaOutboundFrame(MediaChannel.Control, [1], 0));
        s.Submit(new MediaOutboundFrame(MediaChannel.Video,   [5], 0));

        CollectionAssert.AreEqual(
            new[] { MediaChannel.Control, MediaChannel.Input, MediaChannel.Cursor,
                    MediaChannel.Stats, MediaChannel.Video },
            new[] { s.NextSegment()!.Value.Channel, s.NextSegment()!.Value.Channel,
                    s.NextSegment()!.Value.Channel, s.NextSegment()!.Value.Channel,
                    s.NextSegment()!.Value.Channel });
    }

    [TestMethod]
    public void InterleavedFrameDoesNotCorruptVideoReassembly()
    {
        // The writer-side interleaving is only safe because the reader keys its
        // buffers per channel. Prove the pair works end to end.
        byte[] videoPayload = new byte[MediaFrameCodec.MaxFragmentPayload * 2];
        for (int i = 0; i < videoPayload.Length; i++) videoPayload[i] = (byte)(i & 0xFF);

        var s = new MediaWriteScheduler();
        var r = new MediaReassembler();
        s.Submit(new MediaOutboundFrame(MediaChannel.Video, videoPayload, 5));

        ulong seq = 0;
        MediaInboundFrame? video = null;
        var first = s.NextSegment()!.Value;
        Feed(first);
        s.Submit(new MediaOutboundFrame(MediaChannel.Control, Encoding.UTF8.GetBytes("hello"), 6));
        while (s.NextSegment() is { } seg) Feed(seg);

        void Feed(MediaSegment seg)
        {
            byte[] h = MediaFrameCodec.EncodeHeader(seg.Channel, seg.Flags, seq++, seg.CaptureUs,
                                                    MediaFrameCodec.SealedLength(seg.Payload.Length));
            var outcome = r.Accept(MediaFrameCodec.DecodeHeader(h), seg.Payload);
            if (outcome.Kind == MediaReassemblyKind.Complete && outcome.Frame.Channel == MediaChannel.Video)
                video = outcome.Frame;
        }

        Assert.IsNotNull(video);
        CollectionAssert.AreEqual(videoPayload, video!.Value.Payload,
            "a control frame between two video fragments must not corrupt the video");
        Assert.AreEqual(2, video.Value.FragmentCount);
        Assert.AreEqual(5UL, video.Value.CaptureUs, "timestamp comes from the first fragment");
    }

    // ---- Scheduler: drop policy -------------------------------------------

    [TestMethod]
    public void ThirdVideoFrameDropsTheQueuedOneNotTheInProgressOne()
    {
        var s = new MediaWriteScheduler();
        s.Submit(new MediaOutboundFrame(MediaChannel.Video,
            new byte[MediaFrameCodec.MaxFragmentPayload * 3], 1));
        s.NextSegment();   // frame A is now mid-fragmentation

        Assert.AreEqual(MediaSubmissionKind.Queued,
            s.Submit(new MediaOutboundFrame(MediaChannel.Video, [2], 2)).Kind);
        var outcome = s.Submit(new MediaOutboundFrame(MediaChannel.Video, [3], 3));
        Assert.AreEqual(MediaSubmissionKind.DroppedStaleVideo, outcome.Kind);
        Assert.AreEqual(1, s.DroppedVideoFrames);

        // A must still finish: abandoning it would leave a dangling
        // fragmented-without-final on the wire and desync the peer for good.
        int videoSegments = 0;
        while (s.NextSegment() is { } seg) if (seg.Channel == MediaChannel.Video) videoSegments++;
        Assert.IsTrue(videoSegments >= 3, "the in-progress frame must run to completion");
    }

    [TestMethod]
    public void DroppedKeyframeIsLatchedForIdrRecovery()
    {
        var s = new MediaWriteScheduler();
        s.Submit(new MediaOutboundFrame(MediaChannel.Video, [1], 1));
        s.Submit(new MediaOutboundFrame(MediaChannel.Video, [2], 2, Keyframe: true));
        var outcome = s.Submit(new MediaOutboundFrame(MediaChannel.Video, [3], 3));

        Assert.AreEqual(MediaSubmissionKind.DroppedStaleVideo, outcome.Kind);
        Assert.IsTrue(outcome.WasKeyframe);
        Assert.AreEqual(1, s.DroppedKeyframes);
        Assert.IsTrue(s.TakeKeyframeDropPending(), "a dropped keyframe must be recoverable via a forced IDR");
        Assert.IsFalse(s.TakeKeyframeDropPending(), "the latch is consumed once");
    }

    [TestMethod]
    public void CursorReplacesRatherThanQueues()
    {
        var s = new MediaWriteScheduler(channelDepth: 4);
        for (int i = 0; i < 50; i++)
            Assert.AreEqual(MediaSubmissionKind.Queued,
                s.Submit(new MediaOutboundFrame(MediaChannel.Cursor, [(byte)i], 0)).Kind);
        CollectionAssert.AreEqual(new byte[] { 49 }, s.NextSegment()!.Value.Payload);
        Assert.IsNull(s.NextSegment());
    }

    [TestMethod]
    public void NonVideoOverflowIsReportedNotSilentlyDropped()
    {
        // A vanished input record is a stuck modifier key on the host.
        var s = new MediaWriteScheduler(channelDepth: 3);
        for (int i = 0; i < 3; i++)
            Assert.AreEqual(MediaSubmissionKind.Queued,
                s.Submit(new MediaOutboundFrame(MediaChannel.Input, [1], 0)).Kind);
        var overflow = s.Submit(new MediaOutboundFrame(MediaChannel.Input, [1], 0));
        Assert.AreEqual(MediaSubmissionKind.Overflow, overflow.Kind);
        Assert.AreEqual(MediaChannel.Input, overflow.Channel);
    }

    // ---- Reassembler faults -----------------------------------------------

    [TestMethod]
    public void WholeFrameMidReassemblyFaults()
    {
        var r = new MediaReassembler();
        var partial = MediaFrameCodec.DecodeHeader(MediaFrameCodec.EncodeHeader(
            MediaChannel.Video, MediaFlags.Fragmented, 0, 0, 20));
        Assert.AreEqual(MediaReassemblyKind.Partial, r.Accept(partial, [1]).Kind);

        var whole = MediaFrameCodec.DecodeHeader(MediaFrameCodec.EncodeHeader(
            MediaChannel.Video, MediaFlags.None, 1, 0, 20));
        var outcome = r.Accept(whole, [2]);
        Assert.AreEqual(MediaReassemblyKind.Fault, outcome.Kind);
        Assert.AreEqual(MediaFaultKind.WholeFrameMidReassembly, outcome.Fault);
    }

    [TestMethod]
    public void ZeroLengthFragmentStormIsBoundedByFragmentCount()
    {
        // A bytes-only cap never fires here: every frame passes the wire check
        // while the buffer grows without limit.
        var r = new MediaReassembler(maxFragments: 8);
        var h = MediaFrameCodec.DecodeHeader(MediaFrameCodec.EncodeHeader(
            MediaChannel.Input, MediaFlags.Fragmented, 0, 0, 16));
        for (int i = 0; i < 8; i++) r.Accept(h, []);
        var outcome = r.Accept(h, []);
        Assert.AreEqual(MediaReassemblyKind.Fault, outcome.Kind);
        Assert.AreEqual(MediaFaultKind.FragmentCountExceeded, outcome.Fault);
    }

    [TestMethod]
    public void ReassemblyByteOverflowFaults()
    {
        var r = new MediaReassembler(maxAssembledBytes: 100, maxFragments: 999);
        var h = MediaFrameCodec.DecodeHeader(MediaFrameCodec.EncodeHeader(
            MediaChannel.Video, MediaFlags.Fragmented, 0, 0, 80));
        Assert.AreEqual(MediaReassemblyKind.Partial, r.Accept(h, new byte[60]).Kind);
        var outcome = r.Accept(h, new byte[60]);
        Assert.AreEqual(MediaReassemblyKind.Fault, outcome.Kind);
        Assert.AreEqual(MediaFaultKind.ReassemblyOverflow, outcome.Fault);
    }

    [TestMethod]
    public void PerChannelBuffersAreIndependent()
    {
        var r = new MediaReassembler();
        static MediaFrameHeader H(MediaChannel ch, MediaFlags f, ulong seq) =>
            MediaFrameCodec.DecodeHeader(MediaFrameCodec.EncodeHeader(ch, f, seq, 0, 20));

        Assert.AreEqual(MediaReassemblyKind.Partial, r.Accept(H(MediaChannel.Video, MediaFlags.Fragmented, 0), [1]).Kind);
        Assert.AreEqual(MediaReassemblyKind.Partial, r.Accept(H(MediaChannel.Input, MediaFlags.Fragmented, 1), [9]).Kind);
        Assert.AreEqual(1, r.PendingBytes(MediaChannel.Video));
        Assert.AreEqual(1, r.PendingBytes(MediaChannel.Input));

        var done = r.Accept(H(MediaChannel.Video, MediaFlags.Fragmented | MediaFlags.FinalFragment, 2), [2]);
        Assert.AreEqual(MediaReassemblyKind.Complete, done.Kind);
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, done.Frame.Payload);
        Assert.AreEqual(1, r.PendingBytes(MediaChannel.Input), "the input buffer is untouched");
    }

    // ---- Cross-platform golden vector --------------------------------------

    // The only test here that can catch the two platforms drifting apart on the
    // wire. Everything else verifies internal consistency, which both
    // implementations can satisfy independently while still disagreeing byte for
    // byte. Generated by the macOS implementation; MediaFrameTests.swift asserts
    // the same file.
    [TestMethod]
    public void MatchesCrossPlatformFrameVector()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(VectorPath()));
        var root = doc.RootElement;
        byte[] key = Convert.FromHexString(root.GetProperty("key_hex").GetString()!);
        byte[] salt = Convert.FromHexString(root.GetProperty("salt_hex").GetString()!);

        foreach (var f in root.GetProperty("frames").EnumerateArray())
        {
            string name = f.GetProperty("name").GetString()!;
            var channel = (MediaChannel)f.GetProperty("channel").GetByte();
            var flags = (MediaFlags)f.GetProperty("raw_flags").GetByte();
            ulong sequence = f.GetProperty("sequence").GetUInt64();
            ulong captureUs = f.GetProperty("capture_us").GetUInt64();
            byte[] plaintext = Convert.FromHexString(f.GetProperty("plaintext_hex").GetString()!);
            string expected = f.GetProperty("frame_hex").GetString()!;

            byte[] frame = MediaFrameCodec.EncodeFrame(channel, flags, sequence, captureUs, plaintext, key, salt);
            Assert.AreEqual(expected, Convert.ToHexString(frame).ToLowerInvariant(),
                $"frame '{name}' differs from the macOS encoding");

            // Decode the vector's own bytes too, so a reader regression is caught.
            byte[] wire = Convert.FromHexString(expected);
            var header = MediaFrameCodec.DecodeHeader(wire);
            Assert.AreEqual(sequence, header.Sequence, $"frame '{name}' sequence");
            Assert.AreEqual(captureUs, header.CaptureUs, $"frame '{name}' capture_us");
            Assert.AreEqual(f.GetProperty("raw_flags").GetByte(), header.RawFlags, $"frame '{name}' raw flags");
            CollectionAssert.AreEqual(plaintext,
                MediaFrameCodec.DecodePayload(header, wire.AsSpan(0, 22), wire.AsSpan(22), key, salt),
                $"frame '{name}' payload");
        }
    }

    private static string VectorPath()
    {
        const string name = "media_frame_vector.json";
        if (File.Exists(name)) return name;
        string beside = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(beside)) return beside;
        return Path.Combine(Path.GetDirectoryName(typeof(MediaFrameTests).Assembly.Location) ?? ".", name);
    }
}
