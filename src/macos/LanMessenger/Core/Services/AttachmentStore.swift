import Foundation

/// Durable on-disk home for attachments the app generates itself — screen
/// captures and images pasted or dropped as raw bitmap data.
///
/// Deliberately NOT `NSTemporaryDirectory()`: chat history stores the absolute
/// path of every sent file, and macOS sweeps the temp directory between reboots
/// and during periodic maintenance, so a temp path renders as "File no longer
/// available" a day later. The default is the same user-visible folder the
/// screenshot flow has always used, and the `screenshot_dir` preference
/// relocates both.
enum AttachmentStore {

    /// Creates (if needed) and returns the directory generated attachments are
    /// written to. `customPath` is the `screenshot_dir` preference; empty means
    /// use the default under ~/Downloads.
    static func directory(customPath: String = "") throws -> URL {
        let fm = FileManager.default
        let base: URL
        if !customPath.isEmpty {
            base = URL(fileURLWithPath: customPath)
        } else {
            let downloads = fm.urls(for: .downloadsDirectory, in: .userDomainMask).first
                ?? fm.urls(for: .documentDirectory, in: .userDomainMask)[0]
            base = downloads.appendingPathComponent("LAN Messenger Screenshots", isDirectory: true)
        }
        if !fm.fileExists(atPath: base.path) {
            try fm.createDirectory(at: base, withIntermediateDirectories: true)
        }
        return base
    }

    /// `2026-09-11 at 14.05.09` — matches the macOS screenshot naming
    /// convention, and avoids `:` and `/` so it is a legal filename component.
    static func timestampComponent(_ date: Date = Date()) -> String {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd 'at' HH.mm.ss"
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone.current
        return f.string(from: date)
    }

    /// A path in `directory` that no file occupies yet. Two images pasted inside
    /// the same second would otherwise collide — the timestamp only has
    /// one-second resolution — and the second paste would overwrite the first,
    /// retroactively changing what an already-sent bubble points at.
    static func uniqueURL(in dir: URL,
                          basename: String,
                          ext: String,
                          fileExists: (String) -> Bool = { FileManager.default.fileExists(atPath: $0) }) -> URL {
        let first = dir.appendingPathComponent("\(basename).\(ext)")
        guard fileExists(first.path) else { return first }
        for n in 1...999 {
            let candidate = dir.appendingPathComponent("\(basename)-\(n).\(ext)")
            if !fileExists(candidate.path) { return candidate }
        }
        // Same shape as the file-transfer receiver's dedup fallback.
        let suffix = String(UUID().uuidString.replacingOccurrences(of: "-", with: "").prefix(8)).lowercased()
        return dir.appendingPathComponent("\(basename)-\(suffix).\(ext)")
    }
}
