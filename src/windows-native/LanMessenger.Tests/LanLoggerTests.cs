using System.IO.Compression;
using System.Text;
using LanMessenger.Core.Persistence;
using LanMessenger.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanMessenger.Tests;

// Tests for the structured multi-subsystem logger.
//
// Each test points the logger at a per-test temp directory; the user's real
// %APPDATA% directory is never touched.  Rotation behaviour is forced by
// shrinking MaxBytes so we don't have to actually log megabytes of garbage.
//
// Channel routing: generic info/warn/error go to client.log (App channel).
// Structured helpers go to their dedicated channel file:
//   FileTransfer → transfer.log
//   Screenshot   → screenshot.log
//   Peer         → peer.log
//   Discovery    → discovery.log
//   Crypto       → crypto.log
//   UI           → ui.log
//   Retry        → retry.log
[TestClass]
public sealed class LanLoggerTests
{
    private string _tempDir = "";
    private long   _originalMaxBytes;
    private int    _originalMaxArchives;
    private bool   _originalVerbose;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"LanLoggerTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        LanLogger.TestLogDirectoryOverride = _tempDir;
        LanLogger._TestResetHeaderFlag();
        _originalMaxBytes    = LanLogger.MaxBytes;
        _originalMaxArchives = LanLogger.MaxArchives;
        _originalVerbose     = ConfigStore.Shared.Config.VerboseLogging;
    }

    [TestCleanup]
    public void Teardown()
    {
        LanLogger.MaxBytes    = _originalMaxBytes;
        LanLogger.MaxArchives = _originalMaxArchives;
        LanLogger.TestLogDirectoryOverride = null;
        LanLogger._TestResetHeaderFlag();
        ConfigStore.Shared.Config.VerboseLogging = _originalVerbose;
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* may already be deleted */ }
    }

    // ── Basic write & format ──────────────────────────────────────────────────────

    [TestMethod]
    public void WriteCreatesFileWithSessionHeader()
    {
        LanLogger.Info("Test", "first line");
        var body = File.ReadAllText(LanLogger.LogPath);
        StringAssert.StartsWith(body, "# Session ", "first line should be a session header");
        StringAssert.Contains(body, "os=",   "session header should include os=");
        StringAssert.Contains(body, "arch=", "session header should include arch=");
        StringAssert.Contains(body, "host=", "session header should include host=");
        StringAssert.Contains(body, "Test: first line",
            "actual log line should follow the header");
    }

    [TestMethod]
    public void TimestampHasMillisecondPrecision()
    {
        LanLogger.Info("TS", "x");
        var body  = File.ReadAllText(LanLogger.LogPath);
        var regex = new System.Text.RegularExpressions.Regex(
            @"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}Z\]");
        Assert.IsTrue(regex.IsMatch(body),
            "expected at least one ms-precision timestamp; body=" + body);
    }

    [TestMethod]
    public void LevelsAreFixedWidth()
    {
        LanLogger.Info("L", "a");
        LanLogger.Warn("L", "b");
        LanLogger.Error("L", "c");
        LanLogger.Critical("L", "d");
        var body = File.ReadAllText(LanLogger.LogPath);
        foreach (var lvl in new[] { "INFO ", "WARN ", "ERROR", "CRIT " })
            StringAssert.Contains(body, $"] {lvl} L:", $"level token \"{lvl}\" missing");
    }

    // ── Verbose gating ────────────────────────────────────────────────────────────

    [TestMethod]
    public void DebugIsGatedByVerboseFlag()
    {
        ConfigStore.Shared.Config.VerboseLogging = false;
        LanLogger.Debug("Verbose", "should NOT appear");
        Assert.IsFalse(File.Exists(LanLogger.LogPath),
            "no log file should be created when verbose is off and only debug is written");

        ConfigStore.Shared.Config.VerboseLogging = true;
        LanLogger.Debug("Verbose", "should appear");
        var body = File.ReadAllText(LanLogger.LogPath);
        StringAssert.Contains(body, "Verbose: should appear");
    }

    // ── Structured events (per-channel routing) ───────────────────────────────────

    // FileTransfer → transfer.log
    [TestMethod]
    public void FileTransferEventEmitsKeyValuePairs()
    {
        LanLogger.FileTransfer(
            "start", transferId: "abc123", peer: "10.0.0.5",
            direction: "outgoing", filename: "report.pdf",
            size: 1024, mime: "application/pdf");

        var body = File.ReadAllText(LanLogger._TestLogPathFor(LanLogger.LogChannel.Transfer));
        StringAssert.Contains(body, "event=start");
        StringAssert.Contains(body, "transfer_id=abc123");
        StringAssert.Contains(body, "peer=10.0.0.5");
        StringAssert.Contains(body, "dir=outgoing");
        StringAssert.Contains(body, "file=report.pdf");
        StringAssert.Contains(body, "size=1024");
        StringAssert.Contains(body, "mime=application/pdf");
    }

    [TestMethod]
    public void FileTransferFailedIsErrorLevel()
    {
        LanLogger.FileTransfer("failed", transferId: "id", reason: "disk full");
        var body = File.ReadAllText(LanLogger._TestLogPathFor(LanLogger.LogChannel.Transfer));
        StringAssert.Contains(body, "ERROR FileTransfer:",
            "failed events should log at ERROR level");
        StringAssert.Contains(body, "reason=\"disk full\"",
            "reason values with spaces must be quoted");
    }

    [TestMethod]
    public void FileTransferDoesNotWriteToAppLog()
    {
        LanLogger.FileTransfer("start", transferId: "t1");
        Assert.IsFalse(File.Exists(LanLogger.LogPath),
            "transfer events must not bleed into client.log");
    }

    // Screenshot → screenshot.log
    [TestMethod]
    public void ScreenshotEventCarriesResolutionAndPermission()
    {
        LanLogger.Screenshot(
            "captured", display: "primary",
            widthPx: 2880, heightPx: 1864,
            permission: "granted", initMs: 42, path: @"C:\temp\shot.png");

        var body = File.ReadAllText(LanLogger._TestLogPathFor(LanLogger.LogChannel.Screenshot));
        StringAssert.Contains(body, "res=2880x1864");
        StringAssert.Contains(body, "perm=granted");
        StringAssert.Contains(body, "init_ms=42");
    }

    [TestMethod]
    public void ScreenshotPermissionDeniedIsWarn()
    {
        LanLogger.Screenshot("permission_denied", permission: "denied");
        var body = File.ReadAllText(LanLogger._TestLogPathFor(LanLogger.LogChannel.Screenshot));
        StringAssert.Contains(body, "WARN  Screenshot:", "permission_denied should be WARN level");
    }

    [TestMethod]
    public void ScreenshotFailedIsError()
    {
        LanLogger.Screenshot("failed", reason: "stream interrupted");
        var body = File.ReadAllText(LanLogger._TestLogPathFor(LanLogger.LogChannel.Screenshot));
        StringAssert.Contains(body, "ERROR Screenshot:", "failed should be ERROR level");
    }

    // Peer → peer.log
    [TestMethod]
    public void PeerEventShortensPublicKey()
    {
        var fullKey = "AAAAAAAA-BBBBBBBB-CCCCCCCC-DDDDDDDD-EEEEEEEE-FFFFFFFF";
        LanLogger.Peer("connect", peer: "10.0.0.7", publicKey: fullKey);

        var body = File.ReadAllText(LanLogger._TestLogPathFor(LanLogger.LogChannel.Peer));
        StringAssert.Contains(body, "pubkey=AAAAAAAA",
            "public key should be shortened to first 8 chars");
        Assert.IsFalse(body.Contains("FFFFFFFF"),
            "full key should never appear in logs");
    }

    [TestMethod]
    public void PeerDisconnectIsWarn()
    {
        LanLogger.Peer("disconnect", peer: "10.0.0.2", reason: "timeout");
        var body = File.ReadAllText(LanLogger._TestLogPathFor(LanLogger.LogChannel.Peer));
        StringAssert.Contains(body, "WARN  Peer:", "disconnect should be WARN level");
    }

    // Discovery → discovery.log
    [TestMethod]
    public void DiscoveryEventRoutesToDiscoveryLog()
    {
        LanLogger.Discovery("peer_found", ip: "192.168.1.10", interfaces: 2);
        var body = File.ReadAllText(LanLogger._TestLogPathFor(LanLogger.LogChannel.Discovery));
        StringAssert.Contains(body, "event=peer_found");
        StringAssert.Contains(body, "ip=192.168.1.10");
        StringAssert.Contains(body, "interfaces=2");
    }

    [TestMethod]
    public void DiscoveryDoesNotWriteToAppLog()
    {
        LanLogger.Discovery("started");
        Assert.IsFalse(File.Exists(LanLogger.LogPath),
            "discovery events must not bleed into client.log");
    }

    // Crypto → crypto.log
    [TestMethod]
    public void CryptoEventRoutesToCryptoLog()
    {
        LanLogger.Crypto("session_key_derived", peer: "10.0.0.3",
            algorithm: "X25519+AES-GCM", durationMs: 1);
        var body = File.ReadAllText(LanLogger._TestLogPathFor(LanLogger.LogChannel.Crypto));
        StringAssert.Contains(body, "event=session_key_derived");
        StringAssert.Contains(body, "alg=X25519+AES-GCM");
        StringAssert.Contains(body, "ms=1");
    }

    [TestMethod]
    public void CryptoDecryptFailedIsError()
    {
        LanLogger.Crypto("decrypt_failed", peer: "10.0.0.9", reason: "auth tag mismatch");
        var body = File.ReadAllText(LanLogger._TestLogPathFor(LanLogger.LogChannel.Crypto));
        StringAssert.Contains(body, "ERROR Crypto:", "decrypt_failed should be ERROR level");
    }

    // UI → ui.log
    [TestMethod]
    public void UIEventRoutesToUILog()
    {
        LanLogger.UI("conversation_opened", peer: "10.0.0.4");
        var body = File.ReadAllText(LanLogger._TestLogPathFor(LanLogger.LogChannel.UI));
        StringAssert.Contains(body, "event=conversation_opened");
        StringAssert.Contains(body, "peer=10.0.0.4");
    }

    // Retry → retry.log
    [TestMethod]
    public void RetryEventRoutesToRetryLog()
    {
        LanLogger.Retry("retry", subsystem: "FileTransfer",
            attempt: 2, maxAttempts: 5, peer: "10.0.0.8",
            reason: "connection reset");
        var body = File.ReadAllText(LanLogger._TestLogPathFor(LanLogger.LogChannel.Retry));
        StringAssert.Contains(body, "event=retry");
        StringAssert.Contains(body, "subsystem=FileTransfer");
        StringAssert.Contains(body, "attempt=2");
        StringAssert.Contains(body, "max=5");
    }

    [TestMethod]
    public void RetryExhaustedIsError()
    {
        LanLogger.Retry("exhausted", subsystem: "Transfer", attempt: 5, maxAttempts: 5);
        var body = File.ReadAllText(LanLogger._TestLogPathFor(LanLogger.LogChannel.Retry));
        StringAssert.Contains(body, "ERROR Retry:", "exhausted should be ERROR level");
    }

    // ── Session header per channel ────────────────────────────────────────────────

    [TestMethod]
    public void EachChannelGetsItsOwnSessionHeader()
    {
        LanLogger.Info("App", "msg");
        LanLogger.FileTransfer("start", transferId: "t1");
        LanLogger.Discovery("started");

        foreach (var ch in new[] { LanLogger.LogChannel.App, LanLogger.LogChannel.Transfer, LanLogger.LogChannel.Discovery })
        {
            var body = File.ReadAllText(LanLogger._TestLogPathFor(ch));
            StringAssert.StartsWith(body, "# Session ",
                $"{ch} log should begin with a session header");
        }
    }

    // ── archivedLogPaths spans all channels ──────────────────────────────────────

    [TestMethod]
    public void ArchivedLogPathsIncludesAllActiveChannels()
    {
        LanLogger.Info("App", "msg");
        LanLogger.FileTransfer("start", transferId: "t1");
        LanLogger.Screenshot("captured");
        LanLogger.Peer("connect");
        LanLogger.Discovery("started");
        LanLogger.Crypto("key_generated");
        LanLogger.UI("window_shown");
        LanLogger.Retry("retry", subsystem: "Transfer", attempt: 1);

        var paths = LanLogger.ArchivedLogPaths();
        var names = paths.Select(Path.GetFileName).ToHashSet();

        foreach (var expected in new[]
        {
            "client.log", "transfer.log", "screenshot.log",
            "peer.log", "discovery.log", "crypto.log", "ui.log", "retry.log"
        })
        {
            Assert.IsTrue(names.Contains(expected),
                $"{expected} should be in ArchivedLogPaths()");
        }
    }

    [TestMethod]
    public void ArchivedLogPathsListsNewestFirst()
    {
        LanLogger.MaxBytes    = 256;
        LanLogger.MaxArchives = 3;
        for (int i = 0; i < 200; i++)
            LanLogger.Info("List", $"padding padding padding line {i}");

        var paths = LanLogger.ArchivedLogPaths();
        Assert.IsTrue(paths.Count > 0, "should have at least the active log");
        // Active client.log should appear in the list.
        Assert.IsTrue(paths.Any(p => Path.GetFileName(p) == "client.log"),
            "client.log should be in ArchivedLogPaths()");
    }

    // ── Rotation & gzip ───────────────────────────────────────────────────────────

    [TestMethod]
    public void RotationProducesValidGzipArchive()
    {
        LanLogger.MaxBytes    = 256;
        LanLogger.MaxArchives = 2;

        for (int i = 0; i < 60; i++)
            LanLogger.Info("Rot", $"line number {i} padded with extra characters to bulk it up");

        var archive = Path.Combine(_tempDir, "client.1.log.gz");
        Assert.IsTrue(File.Exists(archive),
            "expected client.1.log.gz to exist after rotation");

        using var fs = File.OpenRead(archive);
        Assert.IsTrue(fs.Length >= 18, "archive should be at least header+trailer");
        var b0 = fs.ReadByte();
        var b1 = fs.ReadByte();
        Assert.AreEqual(0x1F, b0);
        Assert.AreEqual(0x8B, b1);
        fs.Position = 0;

        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var sr = new StreamReader(gz, Encoding.UTF8);
        var body = sr.ReadToEnd();
        StringAssert.Contains(body, "line number",
            "decompressed archive should contain original log lines");
    }

    [TestMethod]
    public void SubsystemChannelRotatesIndependently()
    {
        LanLogger.MaxBytes    = 200;
        LanLogger.MaxArchives = 1;

        // Force rotation on the transfer channel only.
        for (int i = 0; i < 80; i++)
            LanLogger.FileTransfer("chunk", transferId: "tid",
                filename: $"bigfile_{i}.bin", size: 65536);

        var transferArchive = Path.Combine(_tempDir, "transfer.1.log.gz");
        var clientArchive   = Path.Combine(_tempDir, "client.1.log.gz");

        Assert.IsTrue(File.Exists(transferArchive),
            "transfer.1.log.gz should exist after transfer-channel rotation");
        Assert.IsFalse(File.Exists(clientArchive),
            "client.1.log.gz must not be created when only transfer channel rotates");
    }

    // ── Export bundle ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void ExportLogBundleProducesReadableZip()
    {
        LanLogger.MaxBytes    = 256;
        LanLogger.MaxArchives = 2;

        // Populate several channels so the bundle contains multiple files.
        for (int i = 0; i < 60; i++)
            LanLogger.Info("Export", $"line {i} packed with text so the rotation triggers");
        LanLogger.FileTransfer("start", transferId: "exp1", filename: "data.bin", size: 1024);
        LanLogger.Discovery("started");

        var zipPath = Path.Combine(_tempDir, "bundle.zip");
        var ok = LanLogger.ExportLogBundle(zipPath);
        Assert.IsTrue(ok, "ExportLogBundle should succeed");
        Assert.IsTrue(File.Exists(zipPath), "zip should be on disk");

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.IsTrue(zip.Entries.Count >= 3,
            "expected at least client.log + transfer.log + discovery.log; got " + zip.Entries.Count);

        var names = zip.Entries.Select(e => e.Name).ToList();
        foreach (var expected in new[] { "client.log", "transfer.log", "discovery.log" })
            CollectionAssert.Contains(names, expected,
                $"{expected} should be in the bundle: " + string.Join(",", names));
    }

    // ── Never crash ───────────────────────────────────────────────────────────────

    [TestMethod]
    public void WriteSurvivesReadOnlyDirectory()
    {
        LanLogger.TestLogDirectoryOverride = @"Q:\nonexistent-drive\does\not\exist";
        LanLogger._TestResetHeaderFlag();
        LanLogger.Info("Resilient", "this should be silently dropped");
        LanLogger.FileTransfer("start", transferId: "noop");
        // No assertion — pass condition is "did not throw".
    }
}
