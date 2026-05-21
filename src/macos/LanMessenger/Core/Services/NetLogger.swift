import Foundation
import os.log

// Structured lifecycle logger for the networking pipeline.
//
// Mirrors the Windows LanLogger so cross-platform interop problems can be
// diagnosed by attaching a single file with consistent fields on either side.
// Writes to ~/Library/Application Support/LanMessenger/Logs/client.log with a
// 2 MiB rolling cap, and also mirrors to os_log for live tail via Console.app.
enum NetLogger {

    private static let log = OSLog(subsystem: "com.dave.lanmessenger", category: "net")
    private static let queue = DispatchQueue(label: "com.dave.lanmessenger.logger", qos: .utility)
    private static let maxBytes: Int = 2 * 1024 * 1024

    static let logURL: URL = {
        let fm = FileManager.default
        let dir = (fm.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
                    ?? URL(fileURLWithPath: NSTemporaryDirectory()))
            .appendingPathComponent("LanMessenger", isDirectory: true)
            .appendingPathComponent("Logs", isDirectory: true)
        try? fm.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir.appendingPathComponent("client.log")
    }()

    static var logsDirectory: URL { logURL.deletingLastPathComponent() }

    static func info(_ category: String, _ message: String)    { write("INFO",  category, message) }
    static func warn(_ category: String, _ message: String)    { write("WARN",  category, message) }
    static func error(_ category: String, _ message: String)   { write("ERROR", category, message) }

    // Verbose: written only when the user has enabled verbose logging in Settings.
    // Reading verboseLogging from a background thread is safe — it's a Bool that
    // is only ever set on the main actor and torn reads are impossible on any
    // Apple platform (atomic single-word loads).
    static func verbose(_ category: String, _ message: String) {
        guard ConfigStore.shared.config.verboseLogging else { return }
        write("VERB",  category, message)
    }

    private static func write(_ level: String, _ category: String, _ message: String) {
        let line = "[\(timestamp())] \(level.padding(toLength: 5, withPad: " ", startingAt: 0)) \(category): \(message)\n"
        os_log("%{public}@", log: log, type: levelToOSType(level), line)

        queue.async {
            do {
                if let attrs = try? FileManager.default.attributesOfItem(atPath: logURL.path),
                   let size = attrs[.size] as? Int, size > maxBytes {
                    try? "[\(timestamp())] INFO  Logger: rolled over (>\(maxBytes/1024) KiB)\n"
                        .data(using: .utf8)?
                        .write(to: logURL)
                }
                if let data = line.data(using: .utf8) {
                    if FileManager.default.fileExists(atPath: logURL.path),
                       let h = try? FileHandle(forWritingTo: logURL) {
                        defer { try? h.close() }
                        try? h.seekToEnd()
                        try? h.write(contentsOf: data)
                    } else {
                        try? data.write(to: logURL)
                    }
                }
            }
        }
    }

    private static func timestamp() -> String {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HH:mm:ss"
        f.timeZone = TimeZone(identifier: "UTC")
        return f.string(from: Date()) + "Z"
    }

    private static func levelToOSType(_ level: String) -> OSLogType {
        switch level {
        case "ERROR": return .error
        case "WARN":  return .default
        default:      return .info
        }
    }
}
