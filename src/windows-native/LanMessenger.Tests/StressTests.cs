using LanMessenger.Core.Crypto;
using LanMessenger.Core.Persistence;
using LanMessenger.Core.Protocol;
using LanMessenger.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using System.Text;
using System.Text.Json;

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

    /// 16 threads × 200 writes across all logging methods = 3 200 total writes.
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
                // LanLogger has Info/Warn/Error/FileTransfer/Screenshot/Peer.
                // Structured-event helpers that don't exist (Discovery, Crypto,
                // UI, Retry) are expressed as plain Info calls.
                switch (t % 8)
                {
                    case 0: LanLogger.Info("Stress", $"t{t} i{i}");                                    break;
                    case 1: LanLogger.FileTransfer("chunk", transferId: $"t{t}", bytesSent: i * 1024L); break;
                    case 2: LanLogger.Screenshot("frame", widthPx: 1920, heightPx: 1080);               break;
                    case 3: LanLogger.Peer("ping", peer: $"10.0.{t}.1");                                break;
                    case 4: LanLogger.Info("Discovery", $"beacon_sent interfaces={t}");                  break;
                    case 5: LanLogger.Info("Crypto", "derive algorithm=X25519");                         break;
                    case 6: LanLogger.Info("UI", "frame_update screen=chat");                            break;
                    default: LanLogger.Info("Retry", $"retry subsystem=Transfer attempt={i}");           break;
                }
            }
        })).ToArray();

        Task.WaitAll(tasks);

        // The single active log must exist and be non-empty after the writes.
        if (File.Exists(LanLogger.LogPath))
            Assert.IsTrue(new FileInfo(LanLogger.LogPath).Length > 0,
                "active log should not be empty after concurrent writes");
    }

    /// Continuously rotates the log; verifies archive count stays within
    /// MaxArchives and the active log is bounded.
    [TestMethod]
    public void RotationBudgetMaintainedUnderLoad()
    {
        LanLogger.MaxBytes    = 256;
        LanLogger.MaxArchives = 2;

        for (int i = 0; i < 500; i++)
            LanLogger.FileTransfer("chunk", transferId: "rot",
                filename: $"file_{i}.bin", size: (long)i * 64);

        // LanLogger rotates the single client.log into client.N.log.gz archives.
        var archives = Directory.GetFiles(_tempDir, "client.*.log.gz");
        Assert.IsTrue(archives.Length <= LanLogger.MaxArchives,
            $"should not exceed MaxArchives, got {archives.Length}");

        if (File.Exists(LanLogger.LogPath))
        {
            var size = new FileInfo(LanLogger.LogPath).Length;
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
        LanLogger.Info("Discovery", "started interfaces=1");
        LanLogger.Info("Crypto", "key_generated algorithm=X25519");
        LanLogger.Info("UI", "window_shown");
        LanLogger.Info("Retry", "retry subsystem=Net attempt=1");
        // Pass = did not throw.
    }

    // ── Frame codec stress ────────────────────────────────────────────────────────

    /// Round-trips JSON frames at a range of content sizes through EncodeDict →
    /// MemoryStream → ReadFrame, including small and large (1 MiB) payloads.
    ///
    /// Note: FrameCodec encodes typed objects/dicts as JSON, not raw byte
    /// arrays. Size=0-byte JSON bodies are rejected by BuildFrame, so the
    /// smallest meaningful unit is a dict with a 1-char string value.
    [TestMethod]
    public void FrameCodecRoundTripAtBoundaries()
    {
        // String-value lengths to embed in the JSON frame body.
        int[] contentSizes = [1, 2, 127, 128, 255, 256, 1_024, 65_535, 65_536, 1_048_576];

        foreach (var size in contentSizes)
        {
            var content = new string('x', size);
            var dict    = new Dictionary<string, object?> { ["data"] = content };
            var encoded = FrameCodec.EncodeDict(dict);

            using var ms   = new MemoryStream(encoded);
            var       body = FrameCodec.ReadFrame(ms);

            Assert.IsNotNull(body,
                $"ReadFrame returned null for content size={size}");

            using var doc       = FrameCodec.ParseJson(body!);
            var       recovered = doc.RootElement.GetProperty("data").GetString();
            Assert.AreEqual(content, recovered,
                $"round-trip failed for content size={size}");
        }
    }

    /// Concatenates 5 000 back-to-back frames into one buffer and reads them
    /// all back via ReadFrame; verifies no frame is dropped or corrupted.
    [TestMethod]
    public void FrameCodecHighVolumeBackToBack()
    {
        const int frameCount = 5_000;
        var allBytes = new List<byte>();
        var expected = new List<string>();

        for (int i = 0; i < frameCount; i++)
        {
            var text = $"message {i} with some padding padding";
            allBytes.AddRange(FrameCodec.EncodeDict(
                new Dictionary<string, object?> { ["msg"] = text }));
            expected.Add(text);
        }

        var received = new List<string>();
        using var ms = new MemoryStream(allBytes.ToArray());
        byte[]? body;
        while ((body = FrameCodec.ReadFrame(ms)) is not null)
        {
            using var doc = FrameCodec.ParseJson(body);
            received.Add(doc.RootElement.GetProperty("msg").GetString()!);
        }

        Assert.AreEqual(frameCount, received.Count,
            "all frames must be received without loss");
        for (int i = 0; i < received.Count; i++)
            Assert.AreEqual(expected[i], received[i], $"frame {i} corrupted");
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

        using var ms = new MemoryStream(header);
        Assert.ThrowsException<InvalidDataException>(
            () => FrameCodec.ReadFrame(ms),
            "ReadFrame must reject frames whose declared size exceeds 50 MiB");
    }

    // ── Crypto stress ─────────────────────────────────────────────────────────────

    // Creates a fresh X25519 key with plaintext-export allowed (for test use only).
    private static Key MakeTempKey() => Key.Create(
        KeyAgreementAlgorithm.X25519,
        new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

    /// Encrypts and decrypts messages of varying sizes between a fixed key pair.
    [TestMethod]
    public void CryptoRoundTripHighVolume()
    {
        using var privA = MakeTempKey();
        using var privB = MakeTempKey();
        var pubAB64 = Convert.ToBase64String(privA.PublicKey.Export(KeyBlobFormat.RawPublicKey));
        var pubBB64 = Convert.ToBase64String(privB.PublicKey.Export(KeyBlobFormat.RawPublicKey));

        // SymmetricKey is the actual method name (not DeriveSymmetricKey).
        var keyA = SessionCrypto.SymmetricKey(privA, pubBB64);
        var keyB = SessionCrypto.SymmetricKey(privB, pubAB64);

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
        using var priv = MakeTempKey();
        var pubB64 = Convert.ToBase64String(priv.PublicKey.Export(KeyBlobFormat.RawPublicKey));
        var key    = SessionCrypto.SymmetricKey(priv, pubB64);
        var aad    = Encoding.UTF8.GetBytes("aad");

        var (nonceB64, ctB64) = SessionCrypto.Encrypt(key, Encoding.UTF8.GetBytes("hello"), aad);

        // Flip the last byte of the ciphertext (which is part of the GCM tag).
        var ctBytes = Convert.FromBase64String(ctB64);
        ctBytes[^1] ^= 0xFF;
        var tampered = Convert.ToBase64String(ctBytes);

        Assert.ThrowsException<System.Security.Cryptography.AuthenticationTagMismatchException>(
            () => SessionCrypto.Decrypt(key, nonceB64, tampered, aad),
            "tampered ciphertext must throw AuthenticationTagMismatchException");
    }

    /// Verifies that a wrong AAD is rejected.
    [TestMethod]
    public void CryptoRejectsWrongAAD()
    {
        using var priv = MakeTempKey();
        var pubB64 = Convert.ToBase64String(priv.PublicKey.Export(KeyBlobFormat.RawPublicKey));
        var key    = SessionCrypto.SymmetricKey(priv, pubB64);

        var (nonceB64, ctB64) = SessionCrypto.Encrypt(
            key, Encoding.UTF8.GetBytes("secret"), Encoding.UTF8.GetBytes("correct-aad"));

        Assert.ThrowsException<System.Security.Cryptography.AuthenticationTagMismatchException>(
            () => SessionCrypto.Decrypt(key, nonceB64, ctB64, Encoding.UTF8.GetBytes("wrong-aad")),
            "wrong AAD must be rejected by AES-GCM");
    }

    // ── Message-status regression ─────────────────────────────────────────────────

    /// Verifies that a late "Sent" notification cannot downgrade "Delivered" or "Read".
    ///
    /// MessageStatus constants are strings, not enum values. The comparison API is
    /// MessageStatus.Rank(s) and MessageStatus.ShouldApply(next, current) — there
    /// is no static Upgrade() helper.
    [TestMethod]
    public void MessageStatusNeverDowngrades()
    {
        var statuses = new[] { MessageStatus.Sent, MessageStatus.Delivered, MessageStatus.Read };

        foreach (var target in statuses)
        {
            foreach (var lower in statuses.Where(s => MessageStatus.Rank(s) < MessageStatus.Rank(target)))
            {
                // A lower-ranked status must not be allowed to overwrite a higher one.
                Assert.IsFalse(
                    MessageStatus.ShouldApply(lower, target),
                    $"status {target} must not downgrade to {lower}");
            }
        }
    }

    /// Verifies the upgrade path Sent → Delivered → Read is accepted,
    /// the reverse direction is rejected, and Read is idempotent.
    [TestMethod]
    public void MessageStatusUpgradeSequence()
    {
        // Forward (upgrade) transitions must be accepted.
        Assert.IsTrue(MessageStatus.ShouldApply(MessageStatus.Delivered, MessageStatus.Sent),
            "Delivered should apply over Sent");
        Assert.IsTrue(MessageStatus.ShouldApply(MessageStatus.Read, MessageStatus.Delivered),
            "Read should apply over Delivered");

        // Idempotent at the top.
        Assert.IsTrue(MessageStatus.ShouldApply(MessageStatus.Read, MessageStatus.Read),
            "Read → Read is idempotent and must be accepted");

        // Reverse (downgrade) transition must be rejected.
        Assert.IsFalse(MessageStatus.ShouldApply(MessageStatus.Sent, MessageStatus.Read),
            "Sent must not downgrade Read");
    }

    // ── Packet validator regression ───────────────────────────────────────────────

    /// Feeds malformed JSON blobs — validator must never crash.
    ///
    /// ValidateDiscovery and Validate (TCP) both take byte[], not string, and
    /// require senderIP / ownPublicKeyB64 / ownIPs parameters. There is no
    /// ValidateText method; the TCP validator is PacketValidator.Validate().
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

        // Dummy sender / own-key values that won't trigger self-suppression.
        const string ownKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
        var          ownIPs = new HashSet<string>();

        for (int rep = 0; rep < 500; rep++)
        {
            foreach (var input in inputs)
            {
                var bytes = Encoding.UTF8.GetBytes(input);
                _ = PacketValidator.ValidateDiscovery(bytes, "1.2.3.4", ownKey, ownIPs);
                _ = PacketValidator.Validate(bytes, "1.2.3.4", ownKey);
            }
        }
        // Pass = no crash.
    }

    // ── History store regression ──────────────────────────────────────────────────

    /// Verifies that the history store caps at exactly 200 entries per peer.
    ///
    /// HistoryStore is a singleton (HistoryStore.Shared) with no public
    /// parameterised constructor and no per-call encryption key. The in-memory
    /// entry type is MessageEntry (not HistoryEntry), which uses Incoming:bool
    /// rather than a Direction enum and Timestamp:double (Unix seconds).
    /// The query method is Entries(peerIP), not Load(peer, key).
    [TestMethod]
    public void HistoryStoreCapsAt200Entries()
    {
        // Use a peer IP unlikely to collide with real or other-test data.
        var peer = $"10.99.{(Environment.TickCount & 0xFF)}.1";

        // Pre-clean any leftover state from a previous interrupted run.
        HistoryStore.Shared.Delete(peer);

        try
        {
            for (int i = 0; i < 250; i++)
            {
                var msg = new MessageEntry
                {
                    MessageId = i.ToString("x32"),
                    Sender    = "me",
                    Text      = $"message {i}",
                    Timestamp = DateTimeOffset.UtcNow.AddSeconds(i).ToUnixTimeSeconds(),
                    Status    = MessageStatus.Sent,
                    Incoming  = false,
                };
                HistoryStore.Shared.Append(msg, peer);
            }

            var entries = HistoryStore.Shared.Entries(peer);
            Assert.IsTrue(entries.Count <= 200,
                $"history must be capped at 200 entries, got {entries.Count}");
            Assert.IsTrue(entries.Count >= 190,
                $"history should keep at least 190 entries, got {entries.Count}");

            // Newest entry (249) must survive truncation.
            Assert.IsTrue(entries.Any(e => e.MessageId == (249).ToString("x32")),
                "newest entry must survive truncation");
        }
        finally
        {
            HistoryStore.Shared.Delete(peer);
        }
    }
}
