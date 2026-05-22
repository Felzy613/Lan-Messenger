using LanMessenger.Core.Persistence;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace LanMessenger.Core.Services;

// Structured lifecycle logger for the LAN Messenger pipeline.
//
// Wire-format goals
// -----------------
// • Every line has a millisecond-precision UTC timestamp, a fixed-width level,
//   a short category, and a free-form message.
// • Each fresh log file opens with a "# Session" line containing OS version,
//   app version, architecture, and hostname so a log attached to a bug
//   report is self-describing.
// • Specialised helpers (FileTransfer, Screenshot, Peer, …) produce key=value
//   tail strings so logs can be grepped by `transfer_id=...`, `bytes=...`,
//   `fps=...` etc. without inventing a parser.
//
// Rotation
// --------
// • Active log is `client.log`, capped at MaxBytes (5 MiB by default).
// • On overflow the active log is gzipped to `client.1.log.gz`, prior
//   `client.N.log.gz` files shift to `client.(N+1).log.gz`, up to
//   `MaxArchives` (4) older generations.  The oldest is deleted.
// • Compression uses System.IO.Compression.GZipStream, producing standard
//   gzip files that open in 7-Zip, gunzip and zcat without help.
//
// Safety
// ------
// • All disk I/O is wrapped in try/catch so a full disk, locked file, or
//   read-only profile can never crash the app.
// • Debug-level events are gated by AppConfig.VerboseLogging so high-rate
//   per-chunk events don't fill the rotation budget.
// • Mirrors to Debug.Write so devs see live output without opening files.
public static class LanLogger
{
    private static readonly object _lock = new();
    private static readonly string _defaultLogDir =
        Path.Combine(ConfigStore.Shared.AppDataDirectory, "Logs");

    // Tunables — public for tests that want to drive rotation deterministically.
    public static long MaxBytes { get; set; } = 5 * 1024 * 1024;   // 5 MiB
    public static int  MaxArchives { get; set; } = 4;

    // Tests can redirect the log directory here without touching %APPDATA%.
    public static string? TestLogDirectoryOverride { get; set; }

    private static bool _headerWritten;

    public static string LogsDirectory
    {
        get
        {
            var dir = TestLogDirectoryOverride ?? _defaultLogDir;
            try { Directory.CreateDirectory(dir); } catch { /* read-only profile */ }
            return dir;
        }
    }

    public static string LogPath => Path.Combine(LogsDirectory, "client.log");

    static LanLogger() { /* directory creation deferred to first use */ }

    // MARK: - Level API

    public static void Debug(string category, string message)
    {
        if (!IsVerboseEnabled()) return;
        Write("DEBUG", category, message);
    }

    public static void Info(string category, string message)     => Write("INFO",  category, message);
    public static void Warn(string category, string message)     => Write("WARN",  category, message);
    public static void Warning(string category, string message)  => Write("WARN",  category, message);
    public static void Error(string category, string message)    => Write("ERROR", category, message);
    public static void Error(string category, Exception ex)      => Write("ERROR", category, ex.ToString());
    public static void Error(string category, string message, Exception ex) =>
        Write("ERROR", category, $"{message}{Environment.NewLine}{ex}");
    public static void Critical(string category, string message) => Write("CRIT",  category, message);

    // MARK: - Structured event helpers

    /// <summary>Records a file-transfer lifecycle event.</summary>
    /// <remarks>
    /// `event` examples: "queued", "start", "progress", "complete", "failed",
    /// "cancelled", "retry".  Pass any subset of metadata that applies.
    /// </remarks>
    public static void FileTransfer(
        string event_,
        string? transferId    = null,
        string? peer          = null,
        string? direction     = null,        // "outgoing" | "incoming"
        string? filename      = null,
        long?   size          = null,
        string? mime          = null,
        long?   bytesSent     = null,
        long?   bytesReceived = null,
        int?    durationMs    = null,
        double? bytesPerSec   = null,
        int?    retries       = null,
        string? reason        = null)
    {
        var kv = new List<(string, string)> { ("event", event_) };
        if (direction is not null)     kv.Add(("dir", direction));
        if (transferId is not null)    kv.Add(("transfer_id", transferId));
        if (peer is not null)          kv.Add(("peer", peer));
        if (filename is not null)      kv.Add(("file", Quote(filename)));
        if (size.HasValue)             kv.Add(("size", size.Value.ToString(CultureInfo.InvariantCulture)));
        if (mime is not null)          kv.Add(("mime", mime));
        if (bytesSent.HasValue)        kv.Add(("sent", bytesSent.Value.ToString(CultureInfo.InvariantCulture)));
        if (bytesReceived.HasValue)    kv.Add(("recv", bytesReceived.Value.ToString(CultureInfo.InvariantCulture)));
        if (durationMs.HasValue)       kv.Add(("ms", durationMs.Value.ToString(CultureInfo.InvariantCulture)));
        if (bytesPerSec.HasValue)      kv.Add(("bps", ((long)Math.Round(bytesPerSec.Value)).ToString(CultureInfo.InvariantCulture)));
        if (retries.HasValue)          kv.Add(("retries", retries.Value.ToString(CultureInfo.InvariantCulture)));
        if (reason is not null)        kv.Add(("reason", Quote(reason)));

        var level = event_ is "failed" or "cancelled" or "error" ? "ERROR" : "INFO";
        Write(level, "FileTransfer", Format(kv));
    }

    /// <summary>Records a screen-capture event.</summary>
    public static void Screenshot(
        string event_,
        string? display              = null,
        int?    widthPx              = null,
        int?    heightPx             = null,
        double? fps                  = null,
        string? permission           = null,      // "granted" | "denied" | "unknown"
        int?    initMs               = null,
        string? interruptionReason   = null,
        string? path                 = null,
        string? reason               = null)
    {
        var kv = new List<(string, string)> { ("event", event_) };
        if (display is not null)            kv.Add(("display", display));
        if (widthPx.HasValue && heightPx.HasValue) kv.Add(("res", $"{widthPx.Value}x{heightPx.Value}"));
        if (fps.HasValue)                   kv.Add(("fps", fps.Value.ToString("F1", CultureInfo.InvariantCulture)));
        if (permission is not null)         kv.Add(("perm", permission));
        if (initMs.HasValue)                kv.Add(("init_ms", initMs.Value.ToString(CultureInfo.InvariantCulture)));
        if (interruptionReason is not null) kv.Add(("interrupt", Quote(interruptionReason)));
        if (path is not null)               kv.Add(("path", Quote(path)));
        if (reason is not null)             kv.Add(("reason", Quote(reason)));

        var level = event_ switch
        {
            "permission_denied" => "WARN",
            "failed" or "interrupted" => "ERROR",
            _ => "INFO",
        };
        Write(level, "Screenshot", Format(kv));
    }

    /// <summary>Records a peer-connection lifecycle event.</summary>
    /// <remarks>
    /// `event` examples: "discover", "connect", "connected", "disconnect",
    /// "reconnect", "handshake_fail".
    /// </remarks>
    public static void Peer(
        string event_,
        string? peer       = null,
        string? publicKey  = null,
        int?    durationMs = null,
        string? reason     = null)
    {
        var kv = new List<(string, string)> { ("event", event_) };
        if (peer is not null)       kv.Add(("peer", peer));
        if (publicKey is not null)  kv.Add(("pubkey", ShortKey(publicKey)));
        if (durationMs.HasValue)    kv.Add(("ms", durationMs.Value.ToString(CultureInfo.InvariantCulture)));
        if (reason is not null)     kv.Add(("reason", Quote(reason)));
        var level = event_ is "disconnect" or "handshake_fail" or "reconnect_fail" ? "WARN" : "INFO";
        Write(level, "Peer", Format(kv));
    }

    // MARK: - File-bundle export
    //
    // Returns every log file in LogsDirectory (active + archives), newest first,
    // for Settings → Export Logs.
    public static IReadOnlyList<string> ArchivedLogPaths()
    {
        try
        {
            return Directory.EnumerateFiles(LogsDirectory)
                .Where(p =>
                {
                    var n = Path.GetFileName(p);
                    return n.StartsWith("client.", StringComparison.Ordinal) &&
                           (n.EndsWith(".log", StringComparison.Ordinal) ||
                            n.EndsWith(".log.gz", StringComparison.Ordinal));
                })
                .OrderByDescending(p => File.GetLastWriteTimeUtc(p))
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Creates a zip of every log file at `destinationZipPath`. Best-effort.</summary>
    public static bool ExportLogBundle(string destinationZipPath)
    {
        try
        {
            if (File.Exists(destinationZipPath)) File.Delete(destinationZipPath);
            using var zip = ZipFile.Open(destinationZipPath, ZipArchiveMode.Create);
            foreach (var path in ArchivedLogPaths())
            {
                try { zip.CreateEntryFromFile(path, Path.GetFileName(path), CompressionLevel.Fastest); }
                catch { /* skip files in use */ }
            }
            return true;
        }
        catch { return false; }
    }

    // MARK: - Internals

    private static bool IsVerboseEnabled()
    {
        try { return ConfigStore.Shared.Config.VerboseLogging; }
        catch { return false; }
    }

    private static void Write(string level, string category, string message)
    {
        var line = new StringBuilder()
            .Append('[').Append(Timestamp()).Append("] ")
            .Append(level.PadRight(5)).Append(' ')
            .Append(category).Append(": ")
            .Append(message)
            .Append(Environment.NewLine)
            .ToString();

        Debug.Write(line);

        lock (_lock)
        {
            try
            {
                EnsureHeader();
                RotateIfNeeded();
                File.AppendAllText(LogPath, line);
            }
            catch { /* full disk / locked file — best-effort logging only */ }
        }
    }

    private static void EnsureHeader()
    {
        if (_headerWritten && File.Exists(LogPath)) return;
        var header = SessionHeaderLine();
        try
        {
            if (!File.Exists(LogPath))
                File.WriteAllText(LogPath, header);
            else if (!_headerWritten)
                File.AppendAllText(LogPath, header);
            _headerWritten = true;
        }
        catch { /* swallow — never crash on logging */ }
    }

    private static string SessionHeaderLine()
    {
        var osVersion = RuntimeInformation.OSDescription;
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var asmVer = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "dev";
        string host;
        try { host = Environment.MachineName; } catch { host = "unknown"; }

        return new StringBuilder()
            .Append("# Session ")
            .Append("ts=").Append(Timestamp())
            .Append(' ').Append("os=").Append(Quote(osVersion))
            .Append(' ').Append("app=").Append(asmVer)
            .Append(' ').Append("arch=").Append(arch)
            .Append(' ').Append("host=").Append(Quote(host))
            .Append(Environment.NewLine)
            .ToString();
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(LogPath)) return;
        long size;
        try { size = new FileInfo(LogPath).Length; } catch { return; }
        if (size <= MaxBytes) return;

        if (MaxArchives > 0)
        {
            // Shift archives: client.{n-1}.log.gz → client.n.log.gz
            for (int i = MaxArchives; i >= 2; i--)
            {
                var src = Path.Combine(LogsDirectory, $"client.{i - 1}.log.gz");
                var dst = Path.Combine(LogsDirectory, $"client.{i}.log.gz");
                if (!File.Exists(src)) continue;
                try
                {
                    if (File.Exists(dst)) File.Delete(dst);
                    File.Move(src, dst);
                }
                catch { /* skip — best-effort */ }
            }

            // Compress current active log into client.1.log.gz.
            var archive = Path.Combine(LogsDirectory, "client.1.log.gz");
            try
            {
                if (File.Exists(archive)) File.Delete(archive);
                using var srcStream = File.OpenRead(LogPath);
                using var dstStream = File.Create(archive);
                using var gz = new GZipStream(dstStream, CompressionLevel.Fastest);
                srcStream.CopyTo(gz);
            }
            catch { /* skip compression — still roll active */ }

            // Drop any older-than-MaxArchives generations the user may have on disk.
            try
            {
                foreach (var p in Directory.EnumerateFiles(LogsDirectory, "client.*.log.gz"))
                {
                    var name = Path.GetFileName(p);
                    var middle = name.Substring("client.".Length, name.Length - "client.".Length - ".log.gz".Length);
                    if (int.TryParse(middle, out var n) && n > MaxArchives)
                        try { File.Delete(p); } catch { /* skip */ }
                }
            }
            catch { /* skip */ }
        }

        try { File.Delete(LogPath); } catch { /* skip */ }
        _headerWritten = false;
        EnsureHeader();
    }

    // MARK: - Formatting helpers

    private static string Format(IEnumerable<(string, string)> pairs) =>
        string.Join(" ", pairs.Select(p => $"{p.Item1}={p.Item2}"));

    private static string Quote(string s)
    {
        if (s.Contains(' ') || s.Contains('\t') || s.Contains('"'))
        {
            var escaped = s.Replace("\"", "\\\"");
            return $"\"{escaped}\"";
        }
        return s;
    }

    private static string ShortKey(string key) =>
        key.Length <= 8 ? key : key.Substring(0, 8);

    private static string Timestamp() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    // MARK: - Test hooks

    /// <summary>Resets the in-memory header flag; tests call this after wiping the log directory.</summary>
    public static void _TestResetHeaderFlag() { _headerWritten = false; }
}
