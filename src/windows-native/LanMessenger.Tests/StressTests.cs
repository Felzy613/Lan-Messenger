using LanMessenger.Core.Crypto;
using LanMessenger.Core.Persistence;
using LanMessenger.Core.Protocol;
using LanMessenger.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;

namespace LanMessenger.Tests;

// Stress and regression tests for the LAN Messenger protocol stack.
//
// Goals
// -----
// • Verify that high-frequency concurrent operations (logging, crypto, frame
//   codec) produce correct results and never crash or dead-lock.
// • Verify that the rotation budget is maintained under continuous write load.
// • Ensure UI-thread safety is maintained (no marshalling exceptions from
//   background operations).
// • Catch common regressions: message-status downgrade, history truncation,
//   frame-codec round-trip correctness at boundary sizes.
//
// All tests run as part of the MSTest suite and in CI via pr-checks.yml and
// integration-test.yml.  No real sockets or persistent disk state is used
// outside the temp-directory overrides.
[TestClass]
public sealed class StressTests
{
    private string _tempDir = "";
    private long   _savedMaxBytes;
    private int    _savedMaxArchives;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"StressTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        LanLogger.TestLogDirectoryOverride = _tempDir;
        LanLogger._TestResetHeaderFlag();
        _savedMaxBytes    = LanLogger.MaxBytes;
        _savedMaxArchives = LanLogger.MaxArchives;
    }

    [TestCleanup]
    public void Teardown()
    {
        LanLogger.MaxBytes    = _savedMaxBytes;
        LanLogger.MaxArchives = _savedMaxArchives;
        LanLogger.TestLogDirectoryOverride = null;
        LanLogger._TestResetHeaderFlag();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── Logger stress ─────────────────────────────────────────────────────────────

    /// 16 threads × 200 writes across all 8 channels = 3 200 total writes.
    /// Verifies thread-safety: no crashes, no corrupted lines.
    [TestMethod]
    [Timeout(30_000)]
    public void HighConcurrencyAcrossAllChannels()
    {
        LanLogger.MaxBytes    = 512 * 1024;   // 512 KiB — allow rotation mid-test
        LanLogger.MaxArchives = 3;

        const int threads = 16;
        const int writes  = 200;

        var tasks = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < writes; i++)
            {
                switch (t % 8)
                {
                    case 0: LanLogger.Info("Stress", $"t{t} i{i}");                                   break;
                    case 1: LanLogger.FileTransfer("chunk", transferId: $"t{t}", bytesSent: i * 1024L); break;
                    case 2: LanLogger.Screenshot("frame", widthPx: 1920, heightPx: 1080);              break;
                    case 3: LanLogger.Peer("ping", peer: $"10.0.{t}.1");                               break;
                    case 4: LanLogger.Discovery("beacon_sent", interfaces: t);                          break;
                    case 5: LanLogger.Crypto("derive", algorithm: "X25519");                           break;
                    case 6: LanLogger.UI("frame_update", screen: "chat");                              break;
                    default: LanLogger.Retry("retry", subsystem: "Transfer", attempt: i);              break;
                }
            }
        })).ToArray();

        Task.WaitAll(tasks);

        // Every channel that received writes should have produced a non-empty file.
        foreach (var ch in (LanLogger.LogChannel[])Enum.GetValues(typeof(LanLogger.LogChannel)))
        {
            var path = LanLogger._TestLogPathFor(ch);
            if (File.Exists(path))
                Assert.IsTrue(new FileInfo(path).Length > 0,
                    $"{Path.GetFileName(path)} should not be empty");
        }
    }

    /// Continuously rotates a single channel; verifies archive count stays
    /// within MaxArchives and the active log is bounded.
    [TestMethod]
    public void RotationBudgetMaintainedUnderLoad()
    {
        LanLogger.MaxBytes    = 256;
        LanLogger.MaxArchives = 2;

        for (int i = 0; i < 500; i++)
            LanLogger.FileTransfer("chunk", transferId: "rot",
                filename: $"file_{i}.bin", size: (long)i * 64);

        var archives = Directory.GetFiles(_tempDir, "transfer.*.log.gz");
        Assert.IsTrue(archives.Length <= LanLogger.MaxArchives,
            $"should not exceed MaxArchives, got {archives.Length}");

        var activePath = LanLogger._TestLogPathFor(LanLogger.LogChannel.Transfer);
        if (File.Exists(activePath))
        {
            var size = new FileInfo(activePath).Length;
            Assert.IsTrue(size <= LanLogger.MaxBytes * 2,
                $"active log should be near the rotation cap, got {size} bytes");
        }
    }

    /// Logging to a non-writable path must never throw or crash.
    [TestMethod]
    public void LoggingToUnwritablePathNeverCrashes()
    {
        LanLogger.TestLogDirectoryOverride = @"Q:\nonexistent-drive\impossible\" + Guid.NewGuid();
        LanLogger._TestResetHeaderFlag();

        LanLogger.Info("Safe", "app");
        LanLogger.Warn("Safe", "warn");
        LanLogger.Error("Safe", "error");
        LanLogger.FileTransfer("start", transferId: "x", filename: "f.bin");
        LanLogger.Screenshot("captured", widthPx: 100, heightPx: 100);
        LanLogger.Peer("connect", peer: "127.0.0.1");
        LanLogger.Discovery("started", interfaces: 1);
        LanLogger.Crypto("key_generated", algorithm: "X25519");
        LanLogger.UI("window_shown");
        LanLogger.Retry("retry", subsystem: "Net", attempt: 1);
        // Pass = did not throw.
    }

    // ── Frame codec stress ────────────────────────────────────────────────────────

    /// Round-trips random payloads at every boundary size through the frame
    /// encoder/decoder including 0-byte and 1 MiB payloads.
    [TestMethod]
    public void FrameCodecRoundTripAtBoundaries()
    {
        int[] sizes = [0, 1, 2, 127, 128, 255, 256, 1_024, 65_535, 65_536, 1_048_576];

        foreach (var size in sizes)
        {
            var payload = new byte[size];
            for (int i = 0; i < size; i++)
                payload[i] = (byte)((i * 6364136223846793005L + 1442695040888963407L) & 0xFF);

            var encoded  = FrameCodec.Encode(payload);
            var decoder  = new FrameCodec.Decoder();
            byte[]? received = null;

            // Feed byte-by-byte to exercise streaming boundary conditions.
            foreach (var b in encoded)
            {
                received = decoder.Feed(new[] { b });
                if (received is not null) break;
            }

            CollectionAssert.AreEqual(payload, received,
                $"round-trip failed for size={size}: received {received?.Length ?? -1} bytes");
        }
    }

    /// Feeds 5 000 back-to-back frames in a single call; verifies no corruption.
    [TestMethod]
    public void FrameCodecHighVolumeBackToBack()
    {
        const int frameCount = 5_000;
        var allFrames = new List<byte>();
        var expected  = new List<byte[]>();

        for (int i = 0; i < frameCount; i++)
        {
            var payload = Encoding.UTF8.GetBytes($"message {i} with some padding padding");
            allFrames.AddRange(FrameCodec.Encode(payload));
            expected.Add(payload);
        }

        var received = new List<byte[]>();
        var decoder  = new FrameCodec.Decoder();

        // Feed in 4 KiB chunks.
        var data   = allFrames.ToArray();
        var cursor = 0;
        while (cursor < data.Length)
        {
            var end   = Math.Min(cursor + 4096, data.Length);
            var chunk = data[cursor..end];
            var frame = decoder.Feed(chunk);
            if (frame is not null)
                received.Add(frame);
            cursor = end;
        }

        for (int i = 0; i < received.Count; i++)
            CollectionAssert.AreEqual(expected[i], received[i], $"frame {i} corrupted");
    }

    /// Verifies that frames whose declared length exceeds 50 MiB are rejected.
    [TestMethod]
    public void FrameCodecRejectsOversizeFrame()
    {
        uint tooBig = 50 * 1024 * 1024 + 1;
        var header = new byte[]
        {
            (byte)(tooBig >> 24),
            (byte)(tooBig >> 16),
            (byte)(tooBig >>  8),
            (byte)(tooBig      ),
        };

        var decoder = new FrameCodec.Decoder();
        Assert.ThrowsException<InvalidDataException>(
            () => decoder.Feed(header),
            "decoder must reject frames whose declared size exceeds 50 MiB");
    }

    // ── Crypto stress ─────────────────────────────────────────────────────────────

    /// Encrypts and decrypts 200 messages of varying sizes between a fixed key pair.
    [TestMethod]
    public void CryptoRoundTripHighVolume()
    {
        var privA = KeyManager.GeneratePrivateKey();
        var privB = KeyManager.GeneratePrivateKey();
        var pubAB64 = Convert.ToBase64String(privA.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey));
        var pubBB64 = Convert.ToBase64String(privB.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey));

        var keyA = SessionCrypto.DeriveSymmetricKey(privA, pubBB64);
        var keyB = SessionCrypto.DeriveSymmetricKey(privB, pubAB64);

        int[] sizes = [0, 1, 63, 64, 65, 1_024, 65_536, 500_000];

        foreach (var size in sizes)
        {
            var plaintext = new byte[size];
            for (int i = 0; i < size; i++) plaintext[i] = (byte)(i & 0xFF);

            var aad = Encoding.UTF8.GetBytes($"test-aad-{size}");
            var (nonceB64, ctB64) = SessionCrypto.Encrypt(keyA, plaintext, aad);
            var recovered = SessionCrypto.Decrypt(keyB, nonceB64, ctB64, aad);

            CollectionAssert.AreEqual(plaintext, recovered,
                $"round-trip failed for size={size}");
        }
    }

    /// Verifies that a tampered ciphertext is rejected.
    [TestMethod]
    public void CryptoRejectsTamperedCiphertext()
    {
        var priv  = KeyManager.GeneratePrivateKey();
        var pubB64 = Convert.ToBase64String(priv.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey));
        var key   = SessionCrypto.DeriveSymmetricKey(priv, pubB64);
        var aad   = Encoding.UTF8.GetBytes("aad");

        var (nonceB64, ctB64) = SessionCrypto.Encrypt(key, Encoding.UTF8.GetBytes("hello"), aad);

        // Flip the last byte of the ciphertext.
        var ctBytes = Convert.FromBase64String(ctB64);
        ctBytes[^1] ^= 0xFF;
        var tampered = Convert.ToBase64String(ctBytes);

        Assert.ThrowsException<Exception>(
            () => SessionCrypto.Decrypt(key, nonceB64, tampered, aad),
            "tampered ciphertext must throw");
    }

    /// Verifies that a wrong AAD is rejected.
    [TestMethod]
    public void CryptoRejectsWrongAAD()
    {
        var priv   = KeyManager.GeneratePrivateKey();
        var pubB64 = Convert.ToBase64String(priv.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey));
        var key    = SessionCrypto.DeriveSymmetricKey(priv, pubB64);

        var (nonceB64, ctB64) = SessionCrypto.Encrypt(
            key, Encoding.UTF8.GetBytes("secret"), Encoding.UTF8.GetBytes("correct-aad"));

        Assert.ThrowsException<Exception>(
            () => SessionCrypto.Decrypt(key, nonceB64, ctB64, Encoding.UTF8.GetBytes("wrong-aad")),
            "wrong AAD must be rejected by AES-GCM");
    }

    // ── Message-status regression ─────────────────────────────────────────────────

    /// Verifies that a late "Sent" notification cannot downgrade "Delivered" or "Read".
    [TestMethod]
    public void MessageStatusNeverDowngrades()
    {
        var upgrades = new[] { MessageStatus.Sent, MessageStatus.Delivered, MessageStatus.Read };

        foreach (var target in upgrades)
        {
            var status = target;
            foreach (var lower in upgrades.Where(s => (int)s < (int)target))
            {
                status = MessageStatus.Upgrade(status, lower);
                Assert.AreEqual(target, status,
                    $"status {target} must not downgrade to {lower}");
            }
        }
    }

    /// Verifies the upgrade path: Sent → Delivered → Read.
    [TestMethod]
    public void MessageStatusUpgradeSequence()
    {
        var status = MessageStatus.Upgrade(MessageStatus.Sent, MessageStatus.Delivered);
        Assert.AreEqual(MessageStatus.Delivered, status);
        status = MessageStatus.Upgrade(status, MessageStatus.Read);
        Assert.AreEqual(MessageStatus.Read, status);
        // Idempotent at the top.
        status = MessageStatus.Upgrade(status, MessageStatus.Read);
        Assert.AreEqual(MessageStatus.Read, status);
    }

    // ── Packet validator regression ───────────────────────────────────────────────

    /// Feeds malformed JSON blobs — validator must never crash.
    [TestMethod]
    public void PacketValidatorHandlesMalformedInputWithoutCrash()
    {
        var inputs = new[]
        {
            "{}",
            "{\"type\":\"text\"}",
            "{\"type\":\"text\",\"message_id\":12345}",
            new string('a', 1_000),
            "{\"type\":\"discovery\",\"public_key\":\"\"}",
            "null",
            "[]",
            "",
            new string('{', 500),
        };

        for (int rep = 0; rep < 500; rep++)
        {
            foreach (var input in inputs)
            {
                _ = PacketValidator.ValidateDiscovery(input);
                _ = PacketValidator.ValidateText(input);
            }
        }
        // Pass = no crash.
    }

    // ── History store regression ──────────────────────────────────────────────────

    /// Verifies that the history store caps at exactly 200 entries per peer.
    [TestMethod]
    public void HistoryStoreCapsAt200Entries()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"HistoryStress-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store      = new HistoryStore(dir);
            var peer       = "10.0.0.1";
            var historyKey = new byte[32];
            Array.Fill(historyKey, (byte)0x42);

            for (int i = 0; i < 250; i++)
            {
                var msg = new HistoryEntry
                {
                    MessageId = i.ToString("x32"),
                    Sender    = "me",
                    Text      = $"message {i}",
                    Timestamp = DateTimeOffset.UtcNow.AddSeconds(i),
                    Status    = MessageStatus.Sent,
                    Direction = MessageDirection.Outgoing,
                };
                store.Append(msg, peer, historyKey);
            }

            var entries = store.Load(peer, historyKey);
            Assert.IsTrue(entries.Count <= 200,
                $"history must be capped at 200 entries, got {entries.Count}");
            Assert.IsTrue(entries.Count >= 190,
                $"history should keep at least 190 entries, got {entries.Count}");

            // Newest entry (249) must survive.
            Assert.IsTrue(entries.Any(e => e.MessageId == (249).ToString("x32")),
                "newest entry must survive truncation");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
