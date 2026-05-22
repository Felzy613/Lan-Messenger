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
//   tail strings so logs can be greppe'd by `transfer_id=...`, `bytes=...`,
//   `fps=...` etc. without inventing a parser.
//
// Rotation
// --------
// • Active log is `client.log`, capped at `maxBytes` (5 MiB by default).
// • On overflow, the active log rotates to `client.1.log.gz`, the prior
//   `client.1.log.gz` shifts to `client.2.log.gz`, etc. up to
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

    private static let log = OSLog(subsystem: "com.dave.lanmessenger", category: "net")
    private static let queue = DispatchQueue(label: "com.dave.lanmessenger.logger", qos: .utility)

    // Tunables — exposed as `static var` so tests can shrink them without
    // touching production behaviour.  Reads happen on `queue` only.
    static var maxBytes: Int = 5 * 1024 * 1024     // 5 MiB active log cap
    static var maxArchives: Int = 4                 // 4 older generations + active

    // First-time header (per-file) — written on file creation OR rotation.
    private static var headerWritten = false

    // Tests can override the log directory by assigning `_testLogDirectoryOverride`.
    // In production the path resolves to Application Support / LanMessenger / Logs.
    static var _testLogDirectoryOverride: URL?

    static var logURL: URL { logsDirectory.appendingPathComponent("client.log") }

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

    // MARK: - Level API

    // The trailing API takes a category + message for legacy call-sites.
    // New code should prefer the structured helpers below.
    static func debug(_ category: String, _ message: String) {
        guard isVerboseEnabled() else { return }
        write("DEBUG", category, message)
    }
    static func info(_ category: String, _ message: String)     { write("INFO",  category, message) }
    static func warn(_ category: String, _ message: String)     { write("WARN",  category, message) }
    static func warning(_ category: String, _ message: String)  { write("WARN",  category, message) }
    static func error(_ category: String, _ message: String)    { write("ERROR", category, message) }
    static func critical(_ category: String, _ message: String) { write("CRIT",  category, message) }

    // Verbose: legacy name kept for callers that haven't migrated to debug().
    static func verbose(_ category: String, _ message: String) {
        guard isVerboseEnabled() else { return }
        write("DEBUG", category, message)
    }

    // MARK: - Structured event helpers
    //
    // Each helper produces the canonical k=v tail the support workflow greps
    // against.  Values are quoted only when they contain spaces so that the
    // common case (`bytes=12345`) stays scannable.

    /// Records a file-transfer lifecycle event.
    /// `event` examples: "queued", "start", "progress", "complete", "failed",
    /// "cancelled", "retry".  Pass any subset of metadata that applies.
    static func fileTransfer(
        event: String,
        transferId: String?  = nil,
        peer: String?        = nil,
        direction: String?   = nil,            // "outgoing" | "incoming"
        filename: String?    = nil,
        size: Int64?         = nil,
        mime: String?        = nil,
        bytesSent: Int64?    = nil,
        bytesReceived: Int64? = nil,
        durationMs: Int?     = nil,
        bytesPerSec: Double? = nil,
        retries: Int?        = nil,
        reason: String?      = nil
    ) {
        var kv: [(String, String)] = [("event", event)]
        if let direction = direction { kv.append(("dir", direction)) }
        if let transferId = transferId { kv.append(("transfer_id", transferId)) }
        if let peer = peer { kv.append(("peer", peer)) }
        if let filename = filename { kv.append(("file", quote(filename))) }
        if let size = size { kv.append(("size", String(size))) }
        if let mime = mime { kv.append(("mime", mime)) }
        if let sent = bytesSent { kv.append(("sent", String(sent))) }
        if let recv = bytesReceived { kv.append(("recv", String(recv))) }
        if let ms = durationMs { kv.append(("ms", String(ms))) }
        if let bps = bytesPerSec { kv.append(("bps", String(Int(bps.rounded())))) }
        if let retries = retries { kv.append(("retries", String(retries))) }
        if let reason = reason { kv.append(("reason", quote(reason))) }

        let level = ["failed", "cancelled", "error"].contains(event) ? "ERROR" : "INFO"
        write(level, "FileTransfer", format(kv))
    }

    /// Records a screen-capture / screenshot event.
    static func screenshot(
        event: String,
        display: String?       = nil,
        widthPx: Int?          = nil,
        heightPx: Int?         = nil,
        fps: Double?           = nil,
        permission: String?    = nil,          // "granted" | "denied" | "unknown"
        initMs: Int?           = nil,          // ms from request → first frame
        interruptionReason: String? = nil,
        path: String?          = nil,
        reason: String?        = nil
    ) {
        var kv: [(String, String)] = [("event", event)]
        if let d = display { kv.append(("display", d)) }
        if let w = widthPx, let h = heightPx { kv.append(("res", "\(w)x\(h)")) }
        if let f = fps { kv.append(("fps", String(format: "%.1f", f))) }
        if let p = permission { kv.append(("perm", p)) }
        if let i = initMs { kv.append(("init_ms", String(i))) }
        if let r = interruptionReason { kv.append(("interrupt", quote(r))) }
        if let p = path { kv.append(("path", quote(p))) }
        if let r = reason { kv.append(("reason", quote(r))) }

        let level: String
        switch event {
        case "permission_denied": level = "WARN"
        case "failed", "interrupted": level = "ERROR"
        default: level = "INFO"
        }
        write(level, "Screenshot", format(kv))
    }

    /// Records a peer-connection lifecycle event.
    /// `event` examples: "discover", "connect", "connected", "disconnect",
    /// "reconnect", "handshake_fail".
    static func peer(
        event: String,
        peer: String?       = nil,
        publicKey: String?  = nil,
        durationMs: Int?    = nil,
        reason: String?     = nil
    ) {
        var kv: [(String, String)] = [("event", event)]
        if let p = peer { kv.append(("peer", p)) }
        if let k = publicKey { kv.append(("pubkey", shortKey(k))) }
        if let ms = durationMs { kv.append(("ms", String(ms))) }
        if let r = reason { kv.append(("reason", quote(r))) }
        let level = ["disconnect", "handshake_fail", "reconnect_fail"].contains(event) ? "WARN" : "INFO"
        write(level, "Peer", format(kv))
    }

    // MARK: - File-bundle export
    //
    // Returns the list of every log file in `logsDirectory` (active + archives),
    // newest first.  Used by Settings → Export Logs to bundle all generations
    // into a single zip the user can attach to a bug report.
    static func archivedLogURLs() -> [URL] {
        let fm = FileManager.default
        guard let entries = try? fm.contentsOfDirectory(at: logsDirectory,
                                                        includingPropertiesForKeys: [.contentModificationDateKey],
                                                        options: [.skipsHiddenFiles]) else {
            return []
        }
        let logs = entries.filter { $0.lastPathComponent.hasPrefix("client.") &&
                                     ($0.pathExtension == "log" || $0.pathExtension == "gz") }
        return logs.sorted { (a, b) in
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

    private static func write(_ level: String, _ category: String, _ message: String) {
        let line = "[\(timestamp())] \(level.padding(toLength: 5, withPad: " ", startingAt: 0)) \(category): \(message)\n"
        os_log("%{public}@", log: log, type: levelToOSType(level), line)

        queue.async {
            ensureHeader()
            rotateIfNeeded()
            appendLine(line)
        }
    }

    // Writes the per-file header exactly once after the active log file is
    // created (either at first launch or immediately after rotation).  The
    // header opens with `# Session` so log aggregators can split files on it.
    private static func ensureHeader() {
        let fm = FileManager.default
        if headerWritten && fm.fileExists(atPath: logURL.path) { return }

        let header = sessionHeaderLine()
        if !fm.fileExists(atPath: logURL.path) {
            try? header.data(using: .utf8)?.write(to: logURL)
        } else if !headerWritten {
            // Existing file from a previous run — append a session boundary so
            // each launch is visually distinct.
            appendLineToFile(header)
        }
        headerWritten = true
    }

    private static func sessionHeaderLine() -> String {
        let info = ProcessInfo.processInfo
        let v = info.operatingSystemVersion
        let osVersion = "macOS \(v.majorVersion).\(v.minorVersion).\(v.patchVersion)"
        let appVersion = (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String) ?? "dev"
        let appBuild = (Bundle.main.infoDictionary?["CFBundleVersion"] as? String) ?? "0"
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

    private static func rotateIfNeeded() {
        guard let attrs = try? FileManager.default.attributesOfItem(atPath: logURL.path),
              let size = attrs[.size] as? Int, size > maxBytes else {
            return
        }

        let fm = FileManager.default
        let dir = logsDirectory

        // Shift archives: client.{n-1}.log.gz → client.n.log.gz
        if maxArchives > 0 {
            for i in stride(from: maxArchives, through: 2, by: -1) {
                let src = dir.appendingPathComponent("client.\(i - 1).log.gz")
                let dst = dir.appendingPathComponent("client.\(i).log.gz")
                if fm.fileExists(atPath: src.path) {
                    try? fm.removeItem(at: dst)
                    try? fm.moveItem(at: src, to: dst)
                }
            }

            // Compress current active log into client.1.log.gz.
            let archive = dir.appendingPathComponent("client.1.log.gz")
            try? fm.removeItem(at: archive)
            if let raw = try? Data(contentsOf: logURL),
               let gz = gzip(raw) {
                try? gz.write(to: archive, options: .atomic)
            }
        }

        // Drop any older-than-maxArchives generations the user may have on disk.
        if let entries = try? fm.contentsOfDirectory(atPath: dir.path) {
            for name in entries where name.hasPrefix("client.") && name.hasSuffix(".log.gz") {
                if let n = Int(name.dropFirst("client.".count).dropLast(".log.gz".count)),
                   n > maxArchives {
                    try? fm.removeItem(at: dir.appendingPathComponent(name))
                }
            }
        }

        try? fm.removeItem(at: logURL)
        headerWritten = false
        ensureHeader()
    }

    private static func appendLine(_ line: String) {
        appendLineToFile(line)
    }

    private static func appendLineToFile(_ line: String) {
        guard let data = line.data(using: .utf8) else { return }
        if FileManager.default.fileExists(atPath: logURL.path),
           let h = try? FileHandle(forWritingTo: logURL) {
            defer { try? h.close() }
            try? h.seekToEnd()
            try? h.write(contentsOf: data)
        } else {
            try? data.write(to: logURL)
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
        var crcLE = crc.littleEndian
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

    /// Resets the in-memory state used by `ensureHeader()`.  Tests call this
    /// after manipulating the log directory directly.
    static func _testResetHeaderFlag() { headerWritten = false }

    /// Synchronously drains pending log writes — waits for the serial queue.
    /// Returns when every enqueued line has hit disk.
    static func _testFlush() {
        queue.sync { /* drain */ }
    }
}
