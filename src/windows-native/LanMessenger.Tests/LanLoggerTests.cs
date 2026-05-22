using System.IO.Compression;
using System.Text;
using LanMessenger.Core.Persistence;
using LanMessenger.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanMessenger.Tests;

// Tests for the structured logger.
//
// Each test points the logger at a per-test temp directory; the user's real
// %APPDATA% directory is never touched.  Rotation behaviour is forced by
// shrinking MaxBytes so we don't have to actually log megabytes of garbage.
[TestClass]
public sealed class LanLoggerTests
{
    private string _tempDir = "";
    private long _originalMaxBytes;
    private int _originalMaxArchives;
    private bool _originalVerbose;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"LanLoggerTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        LanLogger.TestLogDirectoryOverride = _tempDir;
        LanLogger._TestResetHeaderFlag();
        _originalMaxBytes = LanLogger.MaxBytes;
        _originalMaxArchives = LanLogger.MaxArchives;
        _originalVerbose = ConfigStore.Shared.Config.VerboseLogging;
    }

    [TestCleanup]
    public void Teardown()
    {
        LanLogger.MaxBytes = _originalMaxBytes;
        LanLogger.MaxArchives = _originalMaxArchives;
        LanLogger.TestLogDirectoryOverride = null;
        LanLogger._TestResetHeaderFlag();
        ConfigStore.Shared.Config.VerboseLogging = _originalVerbose;
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* may have been deleted by gunzip etc. */ }
    }

    // MARK: - Basic write & format

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
        var body = File.ReadAllText(LanLogger.LogPath);
        // [yyyy-MM-dd HH:mm:ss.fffZ]
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

    // MARK: - Verbose gating

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

    // MARK: - Structured events

    [TestMethod]
    public void FileTransferEventEmitsKeyValuePairs()
    {
        LanLogger.FileTransfer(
            "start", transferId: "abc123", peer: "10.0.0.5",
            direction: "outgoing", filename: "report.pdf",
            size: 1024, mime: "application/pdf");
        var body = File.ReadAllText(LanLogger.LogPath);
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
        var body = File.ReadAllText(LanLogger.LogPath);
        StringAssert.Contains(body, "ERROR FileTransfer:",
            "failed events should log at ERROR level");
        StringAssert.Contains(body, "reason=\"disk full\"",
            "reason values with spaces must be quoted");
    }

    [TestMethod]
    public void ScreenshotEventCarriesResolutionAndPermission()
    {
        LanLogger.Screenshot(
            "captured", display: "primary",
            widthPx: 2880, heightPx: 1864,
            permission: "granted", initMs: 42, path: @"C:\temp\shot.png");
        var body = File.ReadAllText(LanLogger.LogPath);
        StringAssert.Contains(body, "res=2880x1864");
        StringAssert.Contains(body, "perm=granted");
        StringAssert.Contains(body, "init_ms=42");
    }

    [TestMethod]
    public void PeerEventShortensPublicKey()
    {
        var fullKey = "AAAAAAAA-BBBBBBBB-CCCCCCCC-DDDDDDDD-EEEEEEEE-FFFFFFFF";
        LanLogger.Peer("connect", peer: "10.0.0.7", publicKey: fullKey);
        var body = File.ReadAllText(LanLogger.LogPath);
        StringAssert.Contains(body, "pubkey=AAAAAAAA",
            "public key should be shortened to first 8 chars");
        Assert.IsFalse(body.Contains("FFFFFFFF"),
            "full key should never appear in logs");
    }

    // MARK: - Rotation & gzip

    [TestMethod]
    public void RotationProducesValidGzipArchive()
    {
        LanLogger.MaxBytes = 256;            // force rotation quickly
        LanLogger.MaxArchives = 2;

        for (int i = 0; i < 60; i++)
            LanLogger.Info("Rot", $"line number {i} padded with extra characters to bulk it up");

        var archive = Path.Combine(_tempDir, "client.1.log.gz");
        Assert.IsTrue(File.Exists(archive),
            "expected client.1.log.gz to exist after rotation");

        // Verify it's a valid gzip stream by round-tripping through GZipStream.
        using var fs = File.OpenRead(archive);
        Assert.IsTrue(fs.Length >= 18, "archive should be at least header+trailer");
        var firstByte = fs.ReadByte();
        var secondByte = fs.ReadByte();
        Assert.AreEqual(0x1F, firstByte);
        Assert.AreEqual(0x8B, secondByte);
        fs.Position = 0;

        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var sr = new StreamReader(gz, Encoding.UTF8);
        var body = sr.ReadToEnd();
        StringAssert.Contains(body, "line number",
            "decompressed archive should contain original log lines");
    }

    [TestMethod]
    public void ArchivedLogPathsListsNewestFirst()
    {
        LanLogger.MaxBytes = 256;
        LanLogger.MaxArchives = 3;
        for (int i = 0; i < 200; i++)
            LanLogger.Info("List", $"padding padding padding line {i}");
        var paths = LanLogger.ArchivedLogPaths();
        Assert.IsTrue(paths.Count > 0, "should have at least the active log");
        Assert.AreEqual("client.log", Path.GetFileName(paths[0]),
            "newest file should be the active log");
    }

    // MARK: - Export bundle

    [TestMethod]
    public void ExportLogBundleProducesReadableZip()
    {
        LanLogger.MaxBytes = 256;
        LanLogger.MaxArchives = 2;
        for (int i = 0; i < 60; i++)
            LanLogger.Info("Export", $"line {i} packed with text so the rotation actually triggers");

        var zipPath = Path.Combine(_tempDir, "bundle.zip");
        var ok = LanLogger.ExportLogBundle(zipPath);
        Assert.IsTrue(ok, "ExportLogBundle should succeed");
        Assert.IsTrue(File.Exists(zipPath), "zip should be on disk");

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.IsTrue(zip.Entries.Count >= 2,
            "expected at least active + 1 archive entry; got " + zip.Entries.Count);
        var names = zip.Entries.Select(e => e.Name).ToList();
        CollectionAssert.Contains(names, "client.log",
            "active log should be in the bundle: " + string.Join(",", names));
    }

    // MARK: - Never crash

    [TestMethod]
    public void WriteSurvivesReadOnlyDirectory()
    {
        // Point the logger at a path we know can't be created — root drive on
        // Windows is read-protected for unprivileged processes.  The call
        // must not throw, and the test framework would catch any uncaught
        // exception bubbling out of LanLogger.
        LanLogger.TestLogDirectoryOverride = @"Q:\nonexistent-drive\does\not\exist";
        LanLogger._TestResetHeaderFlag();
        LanLogger.Info("Resilient", "this should be silently dropped");
        // No assertion — pass condition is "did not throw".
    }
}
