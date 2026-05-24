import Foundation
import Compression
import os.log
#if canImport(Darwin)
import Darwin
#endif

// Structured lifecycle logger for the LAN Messenger pipeline.
//
// Wire-format goals
// -----------------
// • Every line has a millisecond-precision UTC timestamp, a fixed-width level,
//   a short category, and a free-form message.
// • Each fresh log file opens with a single "Session" line containing OS
//   version, app version, architecture, and hostname so a log attached to a
//   bug report is self-describing without the user adding any context.
// • Specialised helpers (fileTransfer, screenshot, peer, …) produce key=value
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
// • Active log is `{channel}.log`, capped at `maxBytes` (5 MiB by default).
// • On overflow the active log rotates to `{channel}.1.log.gz`, the prior
//   `{channel}.1.log.gz` shifts to `{channel}.2.log.gz`, etc. up to
//   `maxArchives` (4) older generations.  The oldest is deleted.
// • Compression uses the system Compression framework's raw-DEFLATE encoder
//   plus a manually-constructed gzip wrapper so the resulting files open in
//   `gunzip`, `zcat`, Finder Quick Look, and `less -R` without help.
//
// Safety
// ------
// • All disk I/O runs on a dedicated serial dispatch queue at utility QoS;
//   logging never blocks the caller.
// • Every disk operation is wrapped in `try?` so a full disk, locked file,
//   or read-only profile can never crash the app.
// • `verbose`/`debug` are gated by the user's `verboseLogging` preference so
//   high-rate per-chunk events don't fill the rotation budget.
//
// Mirrors to `os_log` so live tail via Console.app works without opening files.
enum NetLogger {

    // MARK: - Subsystem channels

    // Each case maps to its own log file.  `.app` uses the legacy "client" name
    // so existing `client.log` files are not orphaned on upgrade.
    enum LogChannel: String, CaseIterable {
        case app        = "client"      // general events    → client.log
        case transfer   = "transfer"    // file transfers    → transfer.log
        case screenshot = "screenshot"  // screen capture    → screenshot.log
        case discovery  = "discovery"   // LAN discovery     → discovery.log
        case peer       = "peer"        // peer connections  → peer.log
        case crypto     = "crypto"      // crypto/handshakes → crypto.log
        case ui         = "ui"          // UI state changes  → ui.log
        case retry      = "retry"       // retries/recovery  → retry.log

        var logName: String    { "\(rawValue).log" }
        var archivePrefix: String { rawValue }
    }

    private static let log = OSLog(subsystem: "com.dave.lanmessenger", category: "net")
    private static let queue = DispatchQueue(label: "com.dave.lanmessenger.logger", qos: .utility)

    // Tunables — exposed as `static var` so tests can shrink them without
    // touching production behaviour.  Reads happen on `queue` only.
    static var maxBytes: Int = 5 * 1024 * 1024     // 5 MiB active log cap
    static var maxArchives: Int = 4                 // 4 older generations + active

    // Per-channel header state (access on `queue` only).
    // Using String keys (rawValue) because Dictionary<LogChannel,Bool> would
    // require LogChannel: Hashable which is automatic for enums, but this
    // is explicit and identical to the Windows pattern.
    private static var headerWritten: [String: Bool] = {
        Dictionary(uniqueKeysWithValues: LogChannel.allCases.map { ($0.rawValue, false) })
    }()

    // Tests can override the log directory by assigning `_testLogDirectoryOverride`.
    // In production the path resolves to Application Support / LanMessenger / Logs.
    static var _testLogDirectoryOverride: URL?

    // Backward-compat: the primary (app/client) log URL.
    static var logURL: URL { logURL(for: .app) }

    static func logURL(for channel: LogChannel) -> URL {
        logsDirectory.appendingPathComponent(channel.logName)
    }

    static var logsDirectory: URL {
        let dir: URL
        if let override = _testLogDirectoryOverride {
            dir = override
        } else {
            let fm = FileManager.default
            dir = (fm.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
                    ?? URL(fileURLWithPath: NSTemporaryDirectory()))
                .appendingPathComponent("LanMessenger", isDirectory: true)
                .appendingPathComponent("Logs", isDirectory: true)
        }
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    // MARK: - Level API (generic → client.log)

    // The trailing API takes a category + message for legacy call-sites.
    // New code should prefer the structured helpers below.
    static func debug(_ category: String, _ message: String) {
        guard isVerboseEnabled() else { return }
        write("DEBUG", category, message, channel: .app)
    }
    static func info(_ category: String, _ message: String)     { write("INFO",  category, message, channel: .app) }
    static func warn(_ category: String, _ message: String)     { write("WARN",  category, message, channel: .app) }
    static func warning(_ category: String, _ message: String)  { write("WARN",  category, message, channel: .app) }
    static func error(_ category: String, _ message: String)    { write("ERROR", category, message, channel: .app) }
    static func critical(_ category: String, _ message: String) { write("CRIT",  category, message, channel: .app) }

    // Verbose: legacy name kept for callers that haven't migrated to debug().
    static func verbose(_ category: String, _ message: String) {
        guard isVerboseEnabled() else { return }
        write("DEBUG", category, message, channel: .app)
    }

    // MARK: - Structured event helpers
    //
    // Each helper produces the canonical k=v tail the support workflow greps
    // against and routes to its own subsystem log file.

    /// Records a file-transfer lifecycle event (→ transfer.log).
    /// `event` examples: "queued", "start", "progress", "complete", "failed",
    /// "cancelled", "retry".  Pass any subset of metadata that applies.
    static func fileTransfer(
        event: String,
        transferId: String?   = nil,
        peer: String?         = nil,
        direction: String?    = nil,           // "outgoing" | "incoming"
        filename: String?     = nil,
        size: Int64?          = nil,
        mime: String?         = nil,
        bytesSent: Int64?     = nil,
        bytesReceived: Int64? = nil,
        durationMs: Int?      = nil,
        bytesPerSec: Double?  = nil,
        retries: Int?         = nil,
        reason: String?       = nil
    ) {
        var kv: [(String, String)] = [("event", event)]
        if let direction   = direction   { kv.append(("dir",         direction)) }
        if let transferId  = transferId  { kv.append(("transfer_id", transferId)) }
        if let peer        = peer        { kv.append(("peer",        peer)) }
        if let filename    = filename    { kv.append(("file",        quote(filename))) }
        if let size        = size        { kv.append(("size",        String(size))) }
        if let mime        = mime        { kv.append(("mime",        mime)) }
        if let sent        = bytesSent   { kv.append(("sent",        String(sent))) }
        if let recv        = bytesReceived { kv.append(("recv",      String(recv))) }
        if let ms          = durationMs  { kv.append(("ms",          String(ms))) }
        if let bps         = bytesPerSec { kv.append(("bps",         String(Int(bps.rounded())))) }
        if let retries     = retries     { kv.append(("retries",     String(retries))) }
        if let reason      = reason      { kv.append(("reason",      quote(reason))) }

        let level = ["failed", "cancelled", "error"].contains(event) ? "ERROR" : "INFO"
        write(level, "FileTransfer", format(kv), channel: .transfer)
    }

    /// Records a screen-capture / screenshot event (→ screenshot.log).
    static func screenshot(
        event: String,
        display: String?            = nil,
        widthPx: Int?               = nil,
        heightPx: Int?              = nil,
        fps: Double?                = nil,
        permission: String?         = nil,     // "granted" | "denied" | "unknown"
        initMs: Int?                = nil,     // ms from request → first frame
        interruptionReason: String? = nil,
        path: String?               = nil,
        reason: String?             = nil
    ) {
        var kv: [(String, String)] = [("event", event)]
        if let d = display   { kv.append(("display", d)) }
        if let w = widthPx, let h = heightPx { kv.append(("res", "\(w)x\(h)")) }
        if let f = fps       { kv.append(("fps",      String(format: "%.1f", f))) }
        if let p = permission { kv.append(("perm",   p)) }
        if let i = initMs    { kv.append(("init_ms", String(i))) }
        if let r = interruptionReason { kv.append(("interrupt", quote(r))) }
        if let p = path      { kv.append(("path",    quote(p))) }
        if let r = reason    { kv.append(("reason",  quote(r))) }

        let level: String
        switch event {
        case "permission_denied":      level = "WARN"
        case "failed", "interrupted":  level = "ERROR"
        default:                       level = "INFO"
        }
        write(level, "Screenshot", format(kv), channel: .screenshot)
    }

    /// Records a peer-connection lifecycle event (→ peer.log).
    /// `event` examples: "discover", "connect", "connected", "disconnect",
    /// "reconnect", "handshake_fail".
    static func peer(
        event: String,
        peer: String?      = nil,
        publicKey: String? = nil,
        durationMs: Int?   = nil,
        reason: String?    = nil
    ) {
        var kv: [(String, String)] = [("event", event)]
        if let p = peer      { kv.append(("peer",   p)) }
        if let k = publicKey { kv.append(("pubkey", shortKey(k))) }
        if let ms = durationMs { kv.append(("ms",   String(ms))) }
        if let r = reason    { kv.append(("reason", quote(r))) }
        let level = ["disconnect", "handshake_fail", "reconnect_fail"].contains(event) ? "WARN" : "INFO"
        write(level, "Peer", format(kv), channel: .peer)
    }

    /// Records a LAN discovery event (→ discovery.log).
    /// `event` examples: "started", "stopped", "beacon_sent", "peer_found",
    /// "reply_sent", "reply_received", "suppressed", "rebuild_sockets".
    static func discovery(
        event: String,
        peer: String?        = nil,
        publicKey: String?   = nil,
        ip: String?          = nil,
        interfaces: Int?     = nil,
        port: Int?           = nil,
        reason: String?      = nil
    ) {
        var kv: [(String, String)] = [("event", event)]
        if let p = peer      { kv.append(("peer",       p)) }
        if let k = publicKey { kv.append(("pubkey",     shortKey(k))) }
        if let ip = ip       { kv.append(("ip",         ip)) }
        if let n = interfaces { kv.append(("interfaces", String(n))) }
        if let p = port      { kv.append(("port",       String(p))) }
        if let r = reason    { kv.append(("reason",     quote(r))) }
        let level = ["error", "failed", "socket_error"].contains(event) ? "ERROR" : "INFO"
        write(level, "Discovery", format(kv), channel: .discovery)
    }

    /// Records a crypto / key-derivation / handshake event (→ crypto.log).
    /// `event` examples: "key_generated", "key_loaded", "session_key_derived",
    /// "encrypt", "decrypt", "decrypt_failed", "invalid_key".
    static func crypto(
        event: String,
        peer: String?      = nil,
        algorithm: String? = nil,
        durationMs: Int?   = nil,
        reason: String?    = nil
    ) {
        var kv: [(String, String)] = [("event", event)]
        if let p = peer      { kv.append(("peer",   p)) }
        if let a = algorithm { kv.append(("alg",    a)) }
        if let ms = durationMs { kv.append(("ms",   String(ms))) }
        if let r = reason    { kv.append(("reason", quote(r))) }
        let level = ["failed", "error", "invalid_key", "decrypt_failed"].contains(event) ? "ERROR" : "INFO"
        write(level, "Crypto", format(kv), channel: .crypto)
    }

    /// Records a UI state-change event (→ ui.log).
    /// `event` examples: "window_shown", "window_hidden", "conversation_opened",
    /// "conversation_closed", "settings_opened", "theme_changed".
    static func ui(
        event: String,
        screen: String? = nil,
        peer: String?   = nil,
        detail: String? = nil
    ) {
        var kv: [(String, String)] = [("event", event)]
        if let s = screen { kv.append(("screen", s)) }
        if let p = peer   { kv.append(("peer",   p)) }
        if let d = detail { kv.append(("detail", quote(d))) }
        write("INFO", "UI", format(kv), channel: .ui)
    }

    /// Records a retry / failure-recovery event (→ retry.log).
    /// `event` examples: "retry", "backoff", "exhausted", "recovered".
    static func retry(
        event: String,
        subsystem: String? = nil,
        attempt: Int?      = nil,
        maxAttempts: Int?  = nil,
        peer: String?      = nil,
        durationMs: Int?   = nil,
        reason: String?    = nil
    ) {
        var kv: [(String, String)] = [("event", event)]
        if let s = subsystem   { kv.append(("subsystem", s)) }
        if let a = attempt     { kv.append(("attempt",   String(a))) }
        if let m = maxAttempts { kv.append(("max",       String(m))) }
        if let p = peer        { kv.append(("peer",      p)) }
        if let ms = durationMs { kv.append(("ms",        String(ms))) }
        if let r = reason      { kv.append(("reason",    quote(r))) }
        let level = event == "exhausted" ? "ERROR" : "WARN"
        write(level, "Retry", format(kv), channel: .retry)
    }

    // MARK: - File-bundle export
    //
    // Returns all log files across all channels (active + archives), newest first.
    // Used by Settings → Export Logs to bundle everything into one zip the user
    // can attach to a bug report.
    static func archivedLogURLs() -> [URL] {
        let fm = FileManager.default
        guard let entries = try? fm.contentsOfDirectory(at: logsDirectory,
                                                        includingPropertiesForKeys: [.contentModificationDateKey],
                                                        options: [.skipsHiddenFiles]) else {
            return []
        }

        // Gather all {channel}.log and {channel}.N.log.gz files for every channel.
        let knownPrefixes = Set(LogChannel.allCases.map { $0.archivePrefix })
        let logs = entries.filter { url in
            let name = url.lastPathComponent
            let ext  = url.pathExtension          // "log" or "gz"
            guard ext == "log" || ext == "gz" else { return false }
            for prefix in knownPrefixes {
                if name == "\(prefix).log" { return true }
                if name.hasPrefix("\(prefix).") && name.hasSuffix(".log.gz") { return true }
            }
            return false
        }

        return logs.sorted { a, b in
            let aD = (try? a.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast
            let bD = (try? b.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast
            return aD > bD
        }
    }

    // MARK: - Internals

    private static func isVerboseEnabled() -> Bool {
        // ConfigStore.shared.config.verboseLogging is a Bool read on the main
        // actor; single-word loads are atomic on every Apple platform, so this
        // is safe from any thread.
        return ConfigStore.shared.config.verboseLogging
    }

    private static func write(_ level: String, _ category: String, _ message: String,
                               channel: LogChannel) {
        let line = "[\(timestamp())] \(level.padding(toLength: 5, withPad: " ", startingAt: 0)) \(category): \(message)\n"
        os_log("%{public}@", log: log, type: levelToOSType(level), line)

        queue.async {
            ensureHeader(channel: channel)
            rotateIfNeeded(channel: channel)
            appendLine(line, channel: channel)
        }
    }

    // Writes the per-file header exactly once after the active log file is
    // created (either at first launch or immediately after rotation).  The
    // header opens with `# Session` so log aggregators can split files on it.
    private static func ensureHeader(channel: LogChannel) {
        let fm  = FileManager.default
        let url = logURL(for: channel)
        let key = channel.rawValue

        if headerWritten[key] == true && fm.fileExists(atPath: url.path) { return }

        let header = sessionHeaderLine()
        if !fm.fileExists(atPath: url.path) {
            try? header.data(using: .utf8)?.write(to: url)
        } else if headerWritten[key] != true {
            // Existing file from a previous run — append a session boundary.
            appendLineToFile(header, url: url)
        }
        headerWritten[key] = true
    }

    private static func sessionHeaderLine() -> String {
        let info = ProcessInfo.processInfo
        let v = info.operatingSystemVersion
        let osVersion = "macOS \(v.majorVersion).\(v.minorVersion).\(v.patchVersion)"
        let appVersion = (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String) ?? "dev"
        let appBuild   = (Bundle.main.infoDictionary?["CFBundleVersion"] as? String) ?? "0"
        let arch: String = {
            #if arch(arm64)
            return "arm64"
            #elseif arch(x86_64)
            return "x86_64"
            #else
            return "unknown"
            #endif
        }()
        let host = Host.current().localizedName ?? info.hostName

        let parts = [
            "# Session",
            "ts=\(timestamp())",
            "os=\(quote(osVersion))",
            "app=\(appVersion)+\(appBuild)",
            "arch=\(arch)",
            "host=\(quote(host))",
        ]
        return parts.joined(separator: " ") + "\n"
    }

    private static func rotateIfNeeded(channel: LogChannel) {
        let url = logURL(for: channel)
        guard let attrs = try? FileManager.default.attributesOfItem(atPath: url.path),
              let size = attrs[.size] as? Int, size > maxBytes else {
            return
        }

        let fm     = FileManager.default
        let dir    = logsDirectory
        let prefix = channel.archivePrefix

        // Shift archives: {prefix}.{n-1}.log.gz → {prefix}.n.log.gz
        if maxArchives > 0 {
            for i in stride(from: maxArchives, through: 2, by: -1) {
                let src = dir.appendingPathComponent("\(prefix).\(i - 1).log.gz")
                let dst = dir.appendingPathComponent("\(prefix).\(i).log.gz")
                if fm.fileExists(atPath: src.path) {
                    try? fm.removeItem(at: dst)
                    try? fm.moveItem(at: src, to: dst)
                }
            }

            // Compress current active log into {prefix}.1.log.gz.
            let archive = dir.appendingPathComponent("\(prefix).1.log.gz")
            try? fm.removeItem(at: archive)
            if let raw = try? Data(contentsOf: url),
               let gz  = gzip(raw) {
                try? gz.write(to: archive, options: .atomic)
            }
        }

        // Drop older-than-maxArchives generations.
        if let entries = try? fm.contentsOfDirectory(atPath: dir.path) {
            for name in entries {
                let suffix = ".log.gz"
                let pfxDot = "\(prefix)."
                guard name.hasPrefix(pfxDot) && name.hasSuffix(suffix) else { continue }
                let middle = name.dropFirst(pfxDot.count).dropLast(suffix.count)
                if let n = Int(middle), n > maxArchives {
                    try? fm.removeItem(at: dir.appendingPathComponent(name))
                }
            }
        }

        try? fm.removeItem(at: url)
        headerWritten[channel.rawValue] = false
        ensureHeader(channel: channel)
    }

    private static func appendLine(_ line: String, channel: LogChannel) {
        appendLineToFile(line, url: logURL(for: channel))
    }

    private static func appendLineToFile(_ line: String, url: URL) {
        guard let data = line.data(using: .utf8) else { return }
        if FileManager.default.fileExists(atPath: url.path),
           let h = try? FileHandle(forWritingTo: url) {
            defer { try? h.close() }
            try? h.seekToEnd()
            try? h.write(contentsOf: data)
        } else {
            try? data.write(to: url)
        }
    }

    // MARK: - Formatting helpers

    private static func format(_ pairs: [(String, String)]) -> String {
        return pairs.map { "\($0)=\($1)" }.joined(separator: " ")
    }

    private static func quote(_ s: String) -> String {
        if s.contains(" ") || s.contains("\t") || s.contains("\"") {
            let escaped = s.replacingOccurrences(of: "\"", with: "\\\"")
            return "\"\(escaped)\""
        }
        return s
    }

    private static func shortKey(_ key: String) -> String {
        // Public keys are 44-char base64 strings; show the first 8 for log
        // correlation without leaking the entire identity.
        return String(key.prefix(8))
    }

    private static func timestamp() -> String {
        // Millisecond precision UTC.  DateFormatter caches inside Foundation.
        let now = Date()
        let interval = now.timeIntervalSince1970
        let ms = Int((interval - floor(interval)) * 1000)
        let base = Self.tsFormatter.string(from: now)
        return String(format: "%@.%03dZ", base, ms)
    }

    private static let tsFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HH:mm:ss"
        f.timeZone = TimeZone(identifier: "UTC")
        f.locale = Locale(identifier: "en_US_POSIX")
        return f
    }()

    private static func levelToOSType(_ level: String) -> OSLogType {
        switch level {
        case "ERROR", "CRIT": return .error
        case "WARN":          return .default
        case "DEBUG":         return .debug
        default:              return .info
        }
    }

    // MARK: - Gzip

    // Wraps raw DEFLATE output (from Compression's COMPRESSION_ZLIB encoder,
    // which produces *raw* deflate with no zlib header) in the 10-byte gzip
    // header + 8-byte trailer described by RFC 1952.  Returns nil on failure
    // so callers can silently skip compression and leave the active log alone.
    static func gzip(_ data: Data) -> Data? {
        guard !data.isEmpty else {
            // Empty input → gzip of empty is well-defined; emit it.
            return makeGzip(deflate: Data(), crc: 0, isize: 0)
        }

        // Worst case for raw deflate is input.count + ~5 bytes per 16 KiB; pad
        // a comfortable margin so the encoder never returns 0 on legal input.
        let dstSize = data.count + 64 + (data.count / 1024)
        let dst = UnsafeMutablePointer<UInt8>.allocate(capacity: dstSize)
        defer { dst.deallocate() }

        let produced = data.withUnsafeBytes { (src: UnsafeRawBufferPointer) -> Int in
            guard let srcBase = src.bindMemory(to: UInt8.self).baseAddress else { return 0 }
            return compression_encode_buffer(
                dst, dstSize,
                srcBase, data.count,
                nil, COMPRESSION_ZLIB
            )
        }
        guard produced > 0 else { return nil }

        let deflated = Data(bytes: dst, count: produced)
        let crc = crc32(of: data)
        let isize = UInt32(data.count & 0xFFFF_FFFF)
        return makeGzip(deflate: deflated, crc: crc, isize: isize)
    }

    private static func makeGzip(deflate: Data, crc: UInt32, isize: UInt32) -> Data {
        var out = Data()
        // RFC 1952 §2.3 header: magic, method=deflate, flags=0, mtime=0×4,
        // xfl=0, os=255 (unknown).
        out.append(contentsOf: [0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF])
        out.append(deflate)
        var crcLE  = crc.littleEndian
        var sizeLE = isize.littleEndian
        withUnsafeBytes(of: &crcLE)  { out.append(contentsOf: $0) }
        withUnsafeBytes(of: &sizeLE) { out.append(contentsOf: $0) }
        return out
    }

    // RFC 1952-compatible CRC32 (poly 0xEDB88320).  Used by gzip().
    static func crc32(of data: Data) -> UInt32 {
        var crc: UInt32 = 0xFFFF_FFFF
        for byte in data {
            crc ^= UInt32(byte)
            for _ in 0..<8 {
                let mask: UInt32 = (crc & 1) != 0 ? 0xEDB8_8320 : 0
                crc = (crc >> 1) ^ mask
            }
        }
        return crc ^ 0xFFFF_FFFF
    }

    // MARK: - Test hooks
    //
    // Tests use these to drive rotation deterministically without touching the
    // user's real Application Support directory.  Always compiled — SPM does
    // not define DEBUG and we don't want to gate them on Xcode configuration.

    /// Resets the in-memory header flag for ALL channels.  Tests call this
    /// after manipulating the log directory directly.
    static func _testResetHeaderFlag() {
        for channel in LogChannel.allCases {
            headerWritten[channel.rawValue] = false
        }
    }

    /// Returns the log URL for a specific channel (for test assertions).
    static func _testLogURL(for channel: LogChannel) -> URL {
        logURL(for: channel)
    }

    /// Synchronously drains pending log writes — waits for the serial queue.
    /// Returns when every enqueued line has hit disk.
    static func _testFlush() {
        queue.sync { /* drain */ }
    }
}
