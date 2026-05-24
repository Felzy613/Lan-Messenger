using LanMessenger.Core.Persistence;
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
// Subsystem log files
// -------------------
// Each subsystem writes to its own file so operators can tail the subsystem
// they care about without wading through unrelated events.
//
//   client.log     — general application and runtime events (the "primary" log)
//   transfer.log   — file-transfer lifecycle events
//   screenshot.log — screen-capture events
//   discovery.log  — LAN discovery / peer advertisement
//   peer.log       — peer connection and handshake lifecycle
//   crypto.log     — encryption key derivation and session handshake events
//   ui.log         — UI state transitions
//   retry.log      — retry / failure-recovery events
//
// Rotation
// --------
// • Active log is `{channel}.log`, capped at MaxBytes (5 MiB by default).
// • On overflow the active log is gzipped to `{channel}.1.log.gz`, prior
//   `{channel}.N.log.gz` files shift to `{channel}.(N+1).log.gz`, up to
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
    // ── Subsystem channels ───────────────────────────────────────────────────────

    /// Identifies which log file a structured event routes to.
    public enum LogChannel
    {
        App,        // general events    → client.log  (legacy name kept for compat)
        Transfer,   // file transfers    → transfer.log
        Screenshot, // screen capture    → screenshot.log
        Discovery,  // LAN discovery     → discovery.log
        Peer,       // peer connections  → peer.log
        Crypto,     // crypto/handshakes → crypto.log
        UI,         // UI state changes  → ui.log
        Retry,      // retries/recovery  → retry.log
    }

    private static string ChannelPrefix(LogChannel ch) => ch switch
    {
        LogChannel.App        => "client",
        LogChannel.Transfer   => "transfer",
        LogChannel.Screenshot => "screenshot",
        LogChannel.Discovery  => "discovery",
        LogChannel.Peer       => "peer",
        LogChannel.Crypto     => "crypto",
        LogChannel.UI         => "ui",
        LogChannel.Retry      => "retry",
        _                     => "client",
    };

    private static string ChannelLogName(LogChannel ch) => ChannelPrefix(ch) + ".log";

    private static readonly LogChannel[] AllChannels = (LogChannel[])Enum.GetValues(typeof(LogChannel));

    // ── State ────────────────────────────────────────────────────────────────────

    private static readonly object _lock = new();

    private static readonly string _defaultLogDir =
        Path.Combine(ConfigStore.Shared.AppDataDirectory, "Logs");

    // Per-channel header-written flags (access under _lock).
    private static readonly Dictionary<LogChannel, bool> _headerWritten =
        new(AllChannels.Select(c => KeyValuePair.Create(c, false)));

    // Tunables — public for tests that want to drive rotation deterministically.
    public static long MaxBytes    { get; set; } = 5 * 1024 * 1024;   // 5 MiB
    public static int  MaxArchives { get; set; } = 4;

    // Tests can redirect the log directory here without touching %APPDATA%.
    public static string? TestLogDirectoryOverride { get; set; }

    public static string LogsDirectory
    {
        get
        {
            var dir = TestLogDirectoryOverride ?? _defaultLogDir;
            try { Directory.CreateDirectory(dir); } catch { /* read-only profile */ }
            return dir;
        }
    }

    // Backward-compat: path to the primary (app/client) log.
    public static string LogPath => LogPathFor(LogChannel.App);

    public static string LogPathFor(LogChannel channel) =>
        Path.Combine(LogsDirectory, ChannelLogName(channel));

    // ── Level API (generic → client.log) ─────────────────────────────────────────

    public static void Debug(string category, string message)
    {
        if (!IsVerboseEnabled()) return;
        Write("DEBUG", category, message, LogChannel.App);
    }

    public static void Info(string category, string message)     => Write("INFO",  category, message, LogChannel.App);
    public static void Warn(string category, string message)     => Write("WARN",  category, message, LogChannel.App);
    public static void Warning(string category, string message)  => Write("WARN",  category, message, LogChannel.App);
    public static void Error(string category, string message)    => Write("ERROR", category, message, LogChannel.App);
    public static void Error(string category, Exception ex)      => Write("ERROR", category, ex.ToString(), LogChannel.App);
    public static void Error(string category, string message, Exception ex) =>
        Write("ERROR", category, $"{message}{Environment.NewLine}{ex}", LogChannel.App);
    public static void Critical(string category, string message) => Write("CRIT",  category, message, LogChannel.App);

    // ── Structured event helpers ──────────────────────────────────────────────────

    /// <summary>Records a file-transfer lifecycle event (→ transfer.log).</summary>
    /// <remarks>
    /// `event_` examples: "queued", "start", "progress", "complete", "failed",
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
        if (direction is not null)     kv.Add(("dir",         direction));
        if (transferId is not null)    kv.Add(("transfer_id", transferId));
        if (peer is not null)          kv.Add(("peer",        peer));
        if (filename is not null)      kv.Add(("file",        Quote(filename)));
        if (size.HasValue)             kv.Add(("size",        size.Value.ToString(CultureInfo.InvariantCulture)));
        if (mime is not null)          kv.Add(("mime",        mime));
        if (bytesSent.HasValue)        kv.Add(("sent",        bytesSent.Value.ToString(CultureInfo.InvariantCulture)));
        if (bytesReceived.HasValue)    kv.Add(("recv",        bytesReceived.Value.ToString(CultureInfo.InvariantCulture)));
        if (durationMs.HasValue)       kv.Add(("ms",          durationMs.Value.ToString(CultureInfo.InvariantCulture)));
        if (bytesPerSec.HasValue)      kv.Add(("bps",         ((long)Math.Round(bytesPerSec.Value)).ToString(CultureInfo.InvariantCulture)));
        if (retries.HasValue)          kv.Add(("retries",     retries.Value.ToString(CultureInfo.InvariantCulture)));
        if (reason is not null)        kv.Add(("reason",      Quote(reason)));

        var level = event_ is "failed" or "cancelled" or "error" ? "ERROR" : "INFO";
        Write(level, "FileTransfer", Format(kv), LogChannel.Transfer);
    }

    /// <summary>Records a screen-capture event (→ screenshot.log).</summary>
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
        if (display is not null)            kv.Add(("display",   display));
        if (widthPx.HasValue && heightPx.HasValue) kv.Add(("res", $"{widthPx.Value}x{heightPx.Value}"));
        if (fps.HasValue)                   kv.Add(("fps",       fps.Value.ToString("F1", CultureInfo.InvariantCulture)));
        if (permission is not null)         kv.Add(("perm",      permission));
        if (initMs.HasValue)                kv.Add(("init_ms",   initMs.Value.ToString(CultureInfo.InvariantCulture)));
        if (interruptionReason is not null) kv.Add(("interrupt", Quote(interruptionReason)));
        if (path is not null)               kv.Add(("path",      Quote(path)));
        if (reason is not null)             kv.Add(("reason",    Quote(reason)));

        var level = event_ switch
        {
            "permission_denied"         => "WARN",
            "failed" or "interrupted"   => "ERROR",
            _                           => "INFO",
        };
        Write(level, "Screenshot", Format(kv), LogChannel.Screenshot);
    }

    /// <summary>Records a peer-connection lifecycle event (→ peer.log).</summary>
    /// <remarks>
    /// `event_` examples: "discover", "connect", "connected", "disconnect",
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
        if (peer is not null)       kv.Add(("peer",   peer));
        if (publicKey is not null)  kv.Add(("pubkey", ShortKey(publicKey)));
        if (durationMs.HasValue)    kv.Add(("ms",     durationMs.Value.ToString(CultureInfo.InvariantCulture)));
        if (reason is not null)     kv.Add(("reason", Quote(reason)));
        var level = event_ is "disconnect" or "handshake_fail" or "reconnect_fail" ? "WARN" : "INFO";
        Write(level, "Peer", Format(kv), LogChannel.Peer);
    }

    /// <summary>Records a LAN discovery event (→ discovery.log).</summary>
    /// <remarks>
    /// `event_` examples: "started", "stopped", "beacon_sent", "peer_found",
    /// "reply_sent", "reply_received", "suppressed", "rebuild_sockets".
    /// </remarks>
    public static void Discovery(
        string event_,
        string? peer       = null,
        string? publicKey  = null,
        string? ip         = null,
        int?    interfaces = null,
        int?    port       = null,
        string? reason     = null)
    {
        var kv = new List<(string, string)> { ("event", event_) };
        if (peer is not null)        kv.Add(("peer",       peer));
        if (publicKey is not null)   kv.Add(("pubkey",     ShortKey(publicKey)));
        if (ip is not null)          kv.Add(("ip",         ip));
        if (interfaces.HasValue)     kv.Add(("interfaces", interfaces.Value.ToString(CultureInfo.InvariantCulture)));
        if (port.HasValue)           kv.Add(("port",       port.Value.ToString(CultureInfo.InvariantCulture)));
        if (reason is not null)      kv.Add(("reason",     Quote(reason)));
        var level = event_ is "error" or "failed" or "socket_error" ? "ERROR" : "INFO";
        Write(level, "Discovery", Format(kv), LogChannel.Discovery);
    }

    /// <summary>Records a crypto / key-derivation / handshake event (→ crypto.log).</summary>
    /// <remarks>
    /// `event_` examples: "key_generated", "key_loaded", "session_key_derived",
    /// "encrypt", "decrypt", "decrypt_failed", "invalid_key".
    /// </remarks>
    public static void Crypto(
        string event_,
        string? peer      = null,
        string? algorithm = null,
        int?    durationMs = null,
        string? reason    = null)
    {
        var kv = new List<(string, string)> { ("event", event_) };
        if (peer is not null)      kv.Add(("peer",   peer));
        if (algorithm is not null) kv.Add(("alg",    algorithm));
        if (durationMs.HasValue)   kv.Add(("ms",     durationMs.Value.ToString(CultureInfo.InvariantCulture)));
        if (reason is not null)    kv.Add(("reason", Quote(reason)));
        var level = event_ is "failed" or "error" or "invalid_key" or "decrypt_failed" ? "ERROR" : "INFO";
        Write(level, "Crypto", Format(kv), LogChannel.Crypto);
    }

    /// <summary>Records a UI state-change event (→ ui.log).</summary>
    /// <remarks>
    /// `event_` examples: "window_shown", "window_hidden", "conversation_opened",
    /// "conversation_closed", "settings_opened", "theme_changed".
    /// </remarks>
    public static void UI(
        string event_,
        string? screen = null,
        string? peer   = null,
        string? detail = null)
    {
        var kv = new List<(string, string)> { ("event", event_) };
        if (screen is not null) kv.Add(("screen", screen));
        if (peer is not null)   kv.Add(("peer",   peer));
        if (detail is not null) kv.Add(("detail", Quote(detail)));
        Write("INFO", "UI", Format(kv), LogChannel.UI);
    }

    /// <summary>Records a retry / failure-recovery event (→ retry.log).</summary>
    /// <remarks>
    /// `event_` examples: "retry", "backoff", "exhausted", "recovered".
    /// </remarks>
    public static void Retry(
        string event_,
        string? subsystem   = null,
        int?    attempt     = null,
        int?    maxAttempts = null,
        string? peer        = null,
        int?    durationMs  = null,
        string? reason      = null)
    {
        var kv = new List<(string, string)> { ("event", event_) };
        if (subsystem is not null)   kv.Add(("subsystem", subsystem));
        if (attempt.HasValue)        kv.Add(("attempt",   attempt.Value.ToString(CultureInfo.InvariantCulture)));
        if (maxAttempts.HasValue)    kv.Add(("max",       maxAttempts.Value.ToString(CultureInfo.InvariantCulture)));
        if (peer is not null)        kv.Add(("peer",      peer));
        if (durationMs.HasValue)     kv.Add(("ms",        durationMs.Value.ToString(CultureInfo.InvariantCulture)));
        if (reason is not null)      kv.Add(("reason",    Quote(reason)));
        var level = event_ == "exhausted" ? "ERROR" : "WARN";
        Write(level, "Retry", Format(kv), LogChannel.Retry);
    }

    // ── File-bundle export ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns every log file across all channels (active + archives), ordered
    /// so active logs come before their archives, newest channel first.
    /// </summary>
    /// <remarks>
    /// Sort key: active log (channel.log) = 0, archives channel.N.log.gz = N.
    /// This is deterministic; LastWriteTimeUtc is unreliable when rotations happen
    /// in rapid succession (e.g. in tests) because the OS may give equal timestamps
    /// to files created within the same timer tick.
    /// </remarks>
    public static IReadOnlyList<string> ArchivedLogPaths()
    {
        try
        {
            var knownPrefixes = AllChannels.Select(ChannelPrefix).ToHashSet();

            return Directory.EnumerateFiles(LogsDirectory)
                .Where(p =>
                {
                    var n = Path.GetFileName(p);
                    foreach (var pfx in knownPrefixes)
                    {
                        if (n == $"{pfx}.log") return true;
                        if (n.StartsWith($"{pfx}.", StringComparison.Ordinal) &&
                            n.EndsWith(".log.gz", StringComparison.Ordinal)) return true;
                    }
                    return false;
                })
                .OrderBy(p =>
                {
                    var name = Path.GetFileName(p);
                    // Active log sorts before archives.
                    foreach (var pfx in knownPrefixes)
                    {
                        if (name == $"{pfx}.log") return (0, pfx, 0);
                        if (name.StartsWith($"{pfx}.", StringComparison.Ordinal) &&
                            name.EndsWith(".log.gz", StringComparison.Ordinal))
                        {
                            var middle = name.Substring(
                                pfx.Length + 1,
                                name.Length - pfx.Length - 1 - ".log.gz".Length);
                            var n = int.TryParse(middle, out var gen) ? gen : int.MaxValue;
                            return (1, pfx, n);
                        }
                    }
                    return (2, name, 0);
                })
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Creates a zip of every log file at <paramref name="destinationZipPath"/>. Best-effort.</summary>
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

    // ── Internals ─────────────────────────────────────────────────────────────────

    private static bool IsVerboseEnabled()
    {
        try { return ConfigStore.Shared.Config.VerboseLogging; }
        catch { return false; }
    }

    private static void Write(string level, string category, string message, LogChannel channel)
    {
        var line = new StringBuilder()
            .Append('[').Append(Timestamp()).Append("] ")
            .Append(level.PadRight(5)).Append(' ')
            .Append(category).Append(": ")
            .Append(message)
            .Append(Environment.NewLine)
            .ToString();

        System.Diagnostics.Debug.Write(line);

        lock (_lock)
        {
            try
            {
                EnsureHeader(channel);
                RotateIfNeeded(channel);
                File.AppendAllText(LogPathFor(channel), line);
            }
            catch { /* full disk / locked file — best-effort logging only */ }
        }
    }

    private static void EnsureHeader(LogChannel channel)
    {
        var path = LogPathFor(channel);
        if (_headerWritten[channel] && File.Exists(path)) return;
        var header = SessionHeaderLine();
        try
        {
            if (!File.Exists(path))
                File.WriteAllText(path, header);
            else if (!_headerWritten[channel])
                File.AppendAllText(path, header);
            _headerWritten[channel] = true;
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

    private static void RotateIfNeeded(LogChannel channel)
    {
        var path = LogPathFor(channel);
        if (!File.Exists(path)) return;
        long size;
        try { size = new FileInfo(path).Length; } catch { return; }
        if (size <= MaxBytes) return;

        var dir    = LogsDirectory;
        var prefix = ChannelPrefix(channel);

        if (MaxArchives > 0)
        {
            // Shift archives: {prefix}.{n-1}.log.gz → {prefix}.n.log.gz
            for (int i = MaxArchives; i >= 2; i--)
            {
                var src = Path.Combine(dir, $"{prefix}.{i - 1}.log.gz");
                var dst = Path.Combine(dir, $"{prefix}.{i}.log.gz");
                if (!File.Exists(src)) continue;
                try
                {
                    if (File.Exists(dst)) File.Delete(dst);
                    File.Move(src, dst);
                }
                catch { /* skip — best-effort */ }
            }

            // Compress current active log into {prefix}.1.log.gz.
            var archive = Path.Combine(dir, $"{prefix}.1.log.gz");
            try
            {
                if (File.Exists(archive)) File.Delete(archive);
                using var srcStream = File.OpenRead(path);
                using var dstStream = File.Create(archive);
                using var gz = new GZipStream(dstStream, CompressionLevel.Fastest);
                srcStream.CopyTo(gz);
            }
            catch { /* skip compression — still roll active */ }

            // Drop older-than-MaxArchives generations.
            try
            {
                foreach (var p in Directory.EnumerateFiles(dir, $"{prefix}.*.log.gz"))
                {
                    var name   = Path.GetFileName(p);
                    var middle = name.Substring(
                        prefix.Length + 1,
                        name.Length - prefix.Length - 1 - ".log.gz".Length);
                    if (int.TryParse(middle, out var n) && n > MaxArchives)
                        try { File.Delete(p); } catch { /* skip */ }
                }
            }
            catch { /* skip */ }
        }

        try { File.Delete(path); } catch { /* skip */ }
        _headerWritten[channel] = false;
        EnsureHeader(channel);
    }

    // ── Formatting helpers ────────────────────────────────────────────────────────

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

    // ── Test hooks ────────────────────────────────────────────────────────────────

    /// <summary>Resets the in-memory header flag for ALL channels; tests call this
    /// after wiping the log directory.</summary>
    public static void _TestResetHeaderFlag()
    {
        lock (_lock)
        {
            foreach (var ch in AllChannels)
                _headerWritten[ch] = false;
        }
    }

    /// <summary>Returns the log path for a specific channel (for test assertions).</summary>
    public static string _TestLogPathFor(LogChannel channel) => LogPathFor(channel);
}
