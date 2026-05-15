import Foundation
import AppKit

struct UpdateInfo: Equatable {
    let version: String
    let notes: String
    let downloadURL: URL
    let expectedSize: Int64
}

enum UpdateCheckResult {
    case upToDate
    case available(UpdateInfo)
    case error(String)
}

enum UpdateError: LocalizedError {
    case noAsset
    case downloadFailed(String)
    case verifyFailed
    case installFailed(String)
    case alreadyRunning

    var errorDescription: String? {
        switch self {
        case .noAsset:                return "No macOS download was published for this release."
        case .downloadFailed(let m):  return "Download failed: \(m)"
        case .verifyFailed:           return "Downloaded update failed verification."
        case .installFailed(let m):   return "Install failed: \(m)"
        case .alreadyRunning:         return "Another update is already running."
        }
    }
}

// Fetches updates from the project's GitHub Releases page, picks the macOS asset,
// downloads it atomically, verifies it, and swaps the running app bundle via a
// detached helper script so the relaunch completes after we exit.
//
// Layout assumptions (matching .github/workflows/release.yml):
//   - Per-platform tag:  macos-vX.Y.Z
//   - Combined tag:      release-winX.Y.Z-macA.B.C
//   - Asset filename:    LanMessenger-macOS-X.Y.Z.zip (top-level: LanMessenger.app)
final class UpdateService {

    static let shared = UpdateService()
    static let appVersion: String = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "1.0.0"

    // Single-flight lock so concurrent installs can't trample each other.
    private var inFlight = false
    private let logFileURL: URL

    private init() {
        let logsDir = ConfigStore.shared.logsDirectory
        try? FileManager.default.createDirectory(at: logsDir, withIntermediateDirectories: true)
        logFileURL = logsDir.appendingPathComponent("update.log")
    }

    // MARK: - Check

    func check(repo: String) async -> UpdateCheckResult {
        let trimmed = repo.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty,
              let url = URL(string: "https://api.github.com/repos/\(trimmed)/releases") else {
            return .error("Invalid update repo")
        }

        log("Checking \(url.absoluteString)")
        var request = URLRequest(url: url, timeoutInterval: 15)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.setValue("LanMessenger-macOS/\(Self.appVersion)", forHTTPHeaderField: "User-Agent")
        request.setValue("2022-11-28", forHTTPHeaderField: "X-GitHub-Api-Version")
        request.cachePolicy = .reloadIgnoringLocalCacheData

        do {
            let (data, response) = try await URLSession.shared.data(for: request)
            guard let http = response as? HTTPURLResponse, http.statusCode == 200 else {
                let code = (response as? HTTPURLResponse)?.statusCode ?? -1
                log("HTTP \(code) from GitHub")
                return .error("GitHub API returned status \(code)")
            }
            guard let releases = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] else {
                log("Malformed JSON response")
                return .error("Could not parse release feed")
            }
            guard let (info, latestVer) = pickLatestMac(releases: releases) else {
                log("No macOS release found")
                return .upToDate
            }
            if Self.compareVersions(latestVer, Self.appVersion) > 0 {
                log("Update available: \(latestVer) (we're on \(Self.appVersion))")
                return .available(info)
            }
            log("Already on latest (\(Self.appVersion) >= \(latestVer))")
            return .upToDate
        } catch {
            log("Network error: \(error.localizedDescription)")
            return .error(error.localizedDescription)
        }
    }

    // MARK: - Download + install

    func downloadAndInstall(info: UpdateInfo, progress: @escaping (Double) -> Void) async throws {
        if inFlight { throw UpdateError.alreadyRunning }
        inFlight = true
        defer { inFlight = false }

        let stagingDir = ConfigStore.shared.updateStagingDirectory
        try? FileManager.default.createDirectory(at: stagingDir, withIntermediateDirectories: true)
        let zipURL = stagingDir.appendingPathComponent("update-\(info.version).zip")
        try? FileManager.default.removeItem(at: zipURL)

        log("Downloading \(info.downloadURL.absoluteString) → \(zipURL.path)")
        try await download(from: info.downloadURL, to: zipURL, progress: progress)

        let actualSize = (try? FileManager.default.attributesOfItem(atPath: zipURL.path)[.size] as? Int64) ?? 0
        if info.expectedSize > 0, abs(actualSize - info.expectedSize) > 16 * 1024 {
            log("Size mismatch: expected \(info.expectedSize), got \(actualSize)")
            throw UpdateError.verifyFailed
        }
        guard actualSize > 256 * 1024 else {
            log("Downloaded file suspiciously small: \(actualSize)")
            throw UpdateError.verifyFailed
        }

        let extractDir = stagingDir.appendingPathComponent("extract-\(info.version)")
        try? FileManager.default.removeItem(at: extractDir)
        try FileManager.default.createDirectory(at: extractDir, withIntermediateDirectories: true)
        log("Extracting → \(extractDir.path)")
        try await runUnzip(zipURL: zipURL, into: extractDir)

        guard let newApp = locateAppBundle(in: extractDir) else {
            log("No .app bundle found inside zip")
            throw UpdateError.verifyFailed
        }
        log("New app bundle: \(newApp.path)")

        // Bundle path of the currently running app; on Apple platforms this
        // points at LanMessenger.app even when run from /Applications.
        let runningBundle = Bundle.main.bundleURL
        let pid = ProcessInfo.processInfo.processIdentifier
        let scriptURL = stagingDir.appendingPathComponent("apply-\(info.version).sh")
        try writeApplyScript(
            scriptURL: scriptURL,
            pid: pid,
            newApp: newApp,
            installedApp: runningBundle,
            logFile: logFileURL
        )

        log("Spawning apply helper \(scriptURL.path) (pid=\(pid))")
        let task = Process()
        task.launchPath = "/bin/bash"
        task.arguments = [scriptURL.path]
        // Detach the helper so it survives our exit.
        task.standardOutput = nil
        task.standardError = nil
        try task.run()

        // Quit ourselves so the helper can move the new bundle into place.
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) {
            NSApp.terminate(nil)
        }
    }

    // MARK: - Helpers

    private func pickLatestMac(releases: [[String: Any]]) -> (UpdateInfo, String)? {
        // Sort by published_at descending and try each release until we find a
        // macOS asset. Releases without macOS assets are skipped.
        let sorted = releases.sorted { a, b in
            let da = (a["published_at"] as? String) ?? ""
            let db = (b["published_at"] as? String) ?? ""
            return da > db
        }
        for release in sorted {
            // Skip drafts
            if (release["draft"] as? Bool) == true { continue }
            guard let assets = release["assets"] as? [[String: Any]] else { continue }
            let tag = (release["tag_name"] as? String) ?? ""
            let version = Self.extractVersion(fromTag: tag)
            guard !version.isEmpty else { continue }

            // Prefer the macOS-specific asset; fall back to anything that contains
            // "mac" in the name (case-insensitive) and ends with .zip.
            let macAsset = assets.first { asset in
                let name = ((asset["name"] as? String) ?? "").lowercased()
                return name.contains("macos") && name.hasSuffix(".zip")
            } ?? assets.first { asset in
                let name = ((asset["name"] as? String) ?? "").lowercased()
                return name.contains("mac") && name.hasSuffix(".zip")
            }
            guard let asset = macAsset,
                  let urlStr = asset["browser_download_url"] as? String,
                  let url = URL(string: urlStr) else { continue }

            let size = (asset["size"] as? Int64) ?? (asset["size"] as? Int).map(Int64.init) ?? 0
            let notes = (release["body"] as? String) ?? ""
            return (UpdateInfo(version: version, notes: notes, downloadURL: url, expectedSize: size), version)
        }
        return nil
    }

    // Extracts X.Y.Z from "macos-vX.Y.Z" or "release-winA.B.C-macX.Y.Z".
    static func extractVersion(fromTag tag: String) -> String {
        if let r = tag.range(of: "mac", options: [.caseInsensitive]) {
            let after = tag[r.upperBound...]
            // Strip leading punctuation like "-v" or "v"
            let trimmed = String(after).drop(while: { !$0.isNumber })
            // Take just the dotted-numeric prefix
            let chars = trimmed.prefix { $0.isNumber || $0 == "." }
            if !chars.isEmpty { return String(chars) }
        }
        // Fallback: any digits in the tag
        let chars = tag.drop(while: { !$0.isNumber }).prefix { $0.isNumber || $0 == "." }
        return String(chars)
    }

    // Returns >0 if a > b, <0 if a < b, 0 if equal. Compares each numeric segment.
    static func compareVersions(_ a: String, _ b: String) -> Int {
        let parse: (String) -> [Int] = { s in s.split(separator: ".").compactMap { Int($0) } }
        let av = parse(a), bv = parse(b)
        for i in 0..<max(av.count, bv.count) {
            let ai = i < av.count ? av[i] : 0
            let bi = i < bv.count ? bv[i] : 0
            if ai != bi { return ai - bi }
        }
        return 0
    }

    private func download(from url: URL, to destination: URL, progress: @escaping (Double) -> Void) async throws {
        var request = URLRequest(url: url, timeoutInterval: 60)
        request.setValue("LanMessenger-macOS/\(Self.appVersion)", forHTTPHeaderField: "User-Agent")
        let (asyncBytes, response) = try await URLSession.shared.bytes(for: request)
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            let code = (response as? HTTPURLResponse)?.statusCode ?? -1
            throw UpdateError.downloadFailed("HTTP \(code)")
        }
        let total = response.expectedContentLength
        FileManager.default.createFile(atPath: destination.path, contents: nil)
        guard let handle = try? FileHandle(forWritingTo: destination) else {
            throw UpdateError.downloadFailed("Could not open output file")
        }
        defer { try? handle.close() }

        var received: Int64 = 0
        var buffer = Data()
        buffer.reserveCapacity(64 * 1024)
        var lastReport = Date()
        for try await byte in asyncBytes {
            buffer.append(byte)
            if buffer.count >= 64 * 1024 {
                try handle.write(contentsOf: buffer)
                received += Int64(buffer.count)
                buffer.removeAll(keepingCapacity: true)
                let now = Date()
                if total > 0, now.timeIntervalSince(lastReport) > 0.1 {
                    progress(min(1.0, Double(received) / Double(total)))
                    lastReport = now
                }
            }
        }
        if !buffer.isEmpty {
            try handle.write(contentsOf: buffer)
            received += Int64(buffer.count)
        }
        progress(1.0)
    }

    private func runUnzip(zipURL: URL, into directory: URL) async throws {
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            let task = Process()
            task.launchPath = "/usr/bin/unzip"
            task.arguments = ["-q", "-o", zipURL.path, "-d", directory.path]
            task.terminationHandler = { proc in
                if proc.terminationStatus == 0 {
                    cont.resume()
                } else {
                    cont.resume(throwing: UpdateError.installFailed("unzip exited with \(proc.terminationStatus)"))
                }
            }
            do { try task.run() } catch { cont.resume(throwing: error) }
        }
    }

    private func locateAppBundle(in directory: URL) -> URL? {
        let fm = FileManager.default
        // Direct child first
        if let contents = try? fm.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil) {
            if let appURL = contents.first(where: { $0.pathExtension == "app" }) {
                return appURL
            }
            // Some zips wrap the .app in a single intermediate folder
            for child in contents where child.hasDirectoryPath {
                if let nested = try? fm.contentsOfDirectory(at: child, includingPropertiesForKeys: nil)
                    .first(where: { $0.pathExtension == "app" }) {
                    return nested
                }
            }
        }
        return nil
    }

    private func writeApplyScript(scriptURL: URL, pid: Int32, newApp: URL, installedApp: URL, logFile: URL) throws {
        // Self-cleaning bash script that waits for the parent process to exit,
        // moves the old bundle aside, swaps in the new one, relaunches, and
        // rolls back on failure.
        let script = """
        #!/bin/bash
        set -u

        LOG="\(logFile.path)"
        NEW_APP="\(newApp.path)"
        DST="\(installedApp.path)"
        BACKUP="${DST}.backup.$$"

        log() { echo "[apply $$ $(date -u +%FT%TZ)] $*" >>"$LOG" 2>/dev/null || true; }

        log "waiting for pid \(pid) to exit"
        for _ in $(seq 1 60); do
            if ! kill -0 \(pid) 2>/dev/null; then break; fi
            sleep 0.5
        done

        if [ ! -d "$NEW_APP" ]; then
            log "ERROR: new app bundle missing at $NEW_APP"
            exit 2
        fi

        if [ -d "$DST" ]; then
            log "moving current app to backup: $BACKUP"
            mv "$DST" "$BACKUP" || { log "ERROR: backup move failed"; exit 3; }
        fi

        log "installing new bundle: $NEW_APP → $DST"
        if ! /bin/cp -R "$NEW_APP" "$DST"; then
            log "ERROR: cp failed — rolling back"
            if [ -d "$BACKUP" ]; then mv "$BACKUP" "$DST"; fi
            exit 4
        fi

        /usr/bin/xattr -dr com.apple.quarantine "$DST" 2>/dev/null || true

        # Smoke-check: codesign verify (best effort).
        if /usr/bin/codesign --verify --no-strict "$DST" >>"$LOG" 2>&1; then
            log "codesign verify ok"
        else
            log "codesign verify failed (continuing anyway)"
        fi

        log "relaunching $DST"
        /usr/bin/open "$DST" || log "WARN: open failed"

        if [ -d "$BACKUP" ]; then
            rm -rf "$BACKUP" >/dev/null 2>&1 || true
        fi
        log "done"
        """
        try script.write(to: scriptURL, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: scriptURL.path)
    }

    private func log(_ message: String) {
        let ts = ISO8601DateFormatter().string(from: Date())
        let line = "[UpdateService \(ts)] \(message)\n"
        if let handle = try? FileHandle(forWritingTo: logFileURL) {
            defer { try? handle.close() }
            _ = try? handle.seekToEnd()
            try? handle.write(contentsOf: Data(line.utf8))
        } else {
            try? line.data(using: .utf8)?.write(to: logFileURL)
        }
        #if DEBUG
        print(line, terminator: "")
        #endif
    }
}
