import CryptoKit
import Foundation
import AppKit

struct UpdateInfo: Equatable {
    let version: String
    let notes: String
    let downloadURL: URL
    let sha256URL: URL?     // nil for releases that pre-date SHA256 sidecars
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
    case sha256Mismatch
    case verifyFailed
    case installFailed(String)
    case alreadyRunning

    var errorDescription: String? {
        switch self {
        case .noAsset:               return "No macOS download was published for this release."
        case .downloadFailed(let m): return "Download failed: \(m)"
        case .sha256Mismatch:        return "Download integrity check failed — file may be corrupt."
        case .verifyFailed:          return "Downloaded update failed verification."
        case .installFailed(let m):  return "Install failed: \(m)"
        case .alreadyRunning:        return "An update is already in progress."
        }
    }
}

// Fetches updates from the project's GitHub Releases page, picks the macOS asset,
// downloads it, verifies SHA256 (when a sidecar is present), and swaps the running
// app bundle via a detached helper script so the relaunch completes after we exit.
//
// Layout assumptions (matching .github/workflows/release.yml):
//   - Combined tag:      release-winX.Y.Z-macA.B.C   ← preferred
//   - Per-platform tag:  macos-vX.Y.Z                ← fallback
//   - Asset filename:    LanMessenger-macOS-X.Y.Z.zip (top-level: LanMessenger.app)
//   - Sidecar filename:  LanMessenger-macOS-X.Y.Z.zip.sha256  (hex SHA256, optional)
final class UpdateService {

    static let shared = UpdateService()
    static let appVersion: String =
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "1.0.0"

    private var inFlight = false
    private let lock = NSLock()
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

    func downloadAndInstall(info: UpdateInfo, onProgress: @escaping (Double) -> Void) async throws {
        let claimed = lock.withLock { () -> Bool in
            guard !inFlight else { return false }
            inFlight = true
            return true
        }
        guard claimed else { throw UpdateError.alreadyRunning }
        defer { lock.withLock { inFlight = false } }

        let stagingDir = ConfigStore.shared.updateStagingDirectory
        try? FileManager.default.createDirectory(at: stagingDir, withIntermediateDirectories: true)
        let zipURL = stagingDir.appendingPathComponent("update-\(info.version).zip")
        try? FileManager.default.removeItem(at: zipURL)

        // Step 1: fetch the expected SHA256 (if sidecar was published)
        var expectedSHA256: String? = nil
        if let sha256URL = info.sha256URL {
            log("Fetching SHA256 sidecar: \(sha256URL.absoluteString)")
            expectedSHA256 = await fetchSHA256Sidecar(url: sha256URL)
            if expectedSHA256 != nil {
                log("Expected SHA256: \(expectedSHA256!)")
            } else {
                log("SHA256 sidecar unavailable — integrity check will use size only")
            }
        }

        // Step 2: download the zip
        log("Downloading \(info.downloadURL.absoluteString) → \(zipURL.path)")
        try await downloadFile(from: info.downloadURL, to: zipURL, onProgress: { p in
            onProgress(p * 0.9) // reserve last 10% for verify+extract
        })

        // Step 3: verify
        let actualSize = (try? FileManager.default.attributesOfItem(atPath: zipURL.path)[.size] as? Int64) ?? 0
        guard actualSize > 256 * 1024 else {
            log("Downloaded file suspiciously small: \(actualSize) bytes")
            throw UpdateError.verifyFailed
        }
        if info.expectedSize > 0, abs(actualSize - info.expectedSize) > 64 * 1024 {
            log("Size mismatch: expected \(info.expectedSize), got \(actualSize)")
            throw UpdateError.verifyFailed
        }

        if let expected = expectedSHA256 {
            log("Verifying SHA256…")
            let actual = try sha256HexOf(url: zipURL)
            log("Actual SHA256:   \(actual)")
            guard actual == expected else {
                log("SHA256 mismatch — refusing to install")
                throw UpdateError.sha256Mismatch
            }
            log("SHA256 verified ✓")
        }
        onProgress(0.92)

        // Step 4: extract
        let extractDir = stagingDir.appendingPathComponent("extract-\(info.version)")
        try? FileManager.default.removeItem(at: extractDir)
        try FileManager.default.createDirectory(at: extractDir, withIntermediateDirectories: true)
        log("Extracting → \(extractDir.path)")
        try await runUnzip(zipURL: zipURL, into: extractDir)
        onProgress(0.97)

        guard let newApp = locateAppBundle(in: extractDir) else {
            log("No .app bundle found inside zip")
            throw UpdateError.verifyFailed
        }
        log("New app bundle: \(newApp.path)")

        // Step 5: write and launch the apply helper
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
        task.standardOutput = FileHandle.nullDevice
        task.standardError  = FileHandle.nullDevice
        try task.run()

        onProgress(1.0)
        // Quit ourselves so the helper can move the new bundle into place.
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) {
            NSApp.terminate(nil)
        }
    }

    // MARK: - Release picker

    private func pickLatestMac(releases: [[String: Any]]) -> (UpdateInfo, String)? {
        // Sort non-draft releases newest-first, then prefer combined tags over per-platform ones.
        let nonDraft = releases.filter { ($0["draft"] as? Bool) != true }
        let sorted = nonDraft.sorted { a, b in
            ((a["published_at"] as? String) ?? "") > ((b["published_at"] as? String) ?? "")
        }
        for wantCombined in [true, false] {
            for release in sorted {
                let tag = (release["tag_name"] as? String) ?? ""
                let isCombined = tag.lowercased().hasPrefix("release-")
                if isCombined != wantCombined { continue }

                guard let assets = release["assets"] as? [[String: Any]] else { continue }
                let version = Self.extractVersion(fromTag: tag)
                guard !version.isEmpty else { continue }

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

                let assetName = (asset["name"] as? String) ?? ""
                let sha256URL = assets.first { a in
                    (a["name"] as? String) == "\(assetName).sha256"
                }.flatMap { a in
                    (a["browser_download_url"] as? String).flatMap { URL(string: $0) }
                }

                let size = (asset["size"] as? Int64) ?? (asset["size"] as? Int).map(Int64.init) ?? 0
                let notes = (release["body"] as? String) ?? ""
                return (UpdateInfo(
                    version: version,
                    notes: notes,
                    downloadURL: url,
                    sha256URL: sha256URL,
                    expectedSize: size
                ), version)
            }
        }
        return nil
    }

    // MARK: - Helpers

    // Extracts X.Y.Z from "macos-vX.Y.Z" or "release-winA.B.C-macX.Y.Z".
    static func extractVersion(fromTag tag: String) -> String {
        if let r = tag.range(of: "mac", options: [.caseInsensitive]) {
            let after = String(tag[r.upperBound...]).drop(while: { !$0.isNumber })
            let chars = after.prefix { $0.isNumber || $0 == "." }
            if !chars.isEmpty { return String(chars) }
        }
        let chars = tag.drop(while: { !$0.isNumber }).prefix { $0.isNumber || $0 == "." }
        return String(chars)
    }

    // Returns >0 if a > b, <0 if a < b, 0 if equal.
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

    // Downloads a small text sidecar containing the hex SHA256 of the release asset.
    private func fetchSHA256Sidecar(url: URL) async -> String? {
        var request = URLRequest(url: url, timeoutInterval: 10)
        request.setValue("LanMessenger-macOS/\(Self.appVersion)", forHTTPHeaderField: "User-Agent")
        guard let (data, resp) = try? await URLSession.shared.data(for: request),
              let http = resp as? HTTPURLResponse, http.statusCode == 200,
              let text = String(data: data, encoding: .utf8) else { return nil }
        // The sidecar is "<hex>  <filename>\n" (sha256sum format) or just "<hex>\n".
        let hex = text.components(separatedBy: .whitespaces).first { $0.count == 64 } ?? ""
        return hex.isEmpty ? nil : hex.lowercased()
    }

    // Compute the lowercase hex SHA256 of a local file.
    private func sha256HexOf(url: URL) throws -> String {
        var hasher = SHA256()
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        let chunkSize = 1024 * 1024
        while case let chunk = handle.readData(ofLength: chunkSize), !chunk.isEmpty {
            hasher.update(data: chunk)
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    // Downloads a file using URLSession.download (efficient, OS-managed buffering)
    // with synthesized progress from content-length.
    private func downloadFile(from url: URL, to destination: URL, onProgress: @escaping (Double) -> Void) async throws {
        var request = URLRequest(url: url, timeoutInterval: 120)
        request.setValue("LanMessenger-macOS/\(Self.appVersion)", forHTTPHeaderField: "User-Agent")

        // Use bytes API so we get both a stream and content-length for progress.
        let (asyncBytes, response) = try await URLSession.shared.bytes(for: request)
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            let code = (response as? HTTPURLResponse)?.statusCode ?? -1
            throw UpdateError.downloadFailed("HTTP \(code)")
        }

        let total = response.expectedContentLength
        FileManager.default.createFile(atPath: destination.path, contents: nil)
        let handle = try FileHandle(forWritingTo: destination)
        defer { try? handle.close() }

        var received: Int64 = 0
        var buf = Data(capacity: 65_536)
        var lastReport = Date()

        for try await byte in asyncBytes {
            buf.append(byte)
            if buf.count >= 65_536 {
                try handle.write(contentsOf: buf)
                received += Int64(buf.count)
                buf.removeAll(keepingCapacity: true)
                let now = Date()
                if total > 0, now.timeIntervalSince(lastReport) > 0.15 {
                    onProgress(min(0.99, Double(received) / Double(total)))
                    lastReport = now
                }
            }
        }
        if !buf.isEmpty {
            try handle.write(contentsOf: buf)
        }
        onProgress(1.0)
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
                    cont.resume(throwing: UpdateError.installFailed(
                        "unzip exited with \(proc.terminationStatus)"))
                }
            }
            do { try task.run() } catch { cont.resume(throwing: error) }
        }
    }

    private func locateAppBundle(in directory: URL) -> URL? {
        let fm = FileManager.default
        if let contents = try? fm.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil) {
            if let app = contents.first(where: { $0.pathExtension == "app" }) { return app }
            for child in contents where child.hasDirectoryPath {
                if let nested = try? fm.contentsOfDirectory(at: child, includingPropertiesForKeys: nil)
                    .first(where: { $0.pathExtension == "app" }) { return nested }
            }
        }
        return nil
    }

    private func writeApplyScript(
        scriptURL: URL, pid: Int32,
        newApp: URL, installedApp: URL, logFile: URL
    ) throws {
        // Self-cleaning bash script: waits for the parent process to exit, backs up
        // the running bundle, installs the new one with ditto (preserves all macOS
        // metadata and code-signing data), relaunches, and rolls back on failure.
        let script = """
        #!/bin/bash
        set -u

        LOG="\(logFile.path)"
        NEW_APP="\(newApp.path)"
        DST="\(installedApp.path)"
        BACKUP="${DST}.backup.$$"

        log() { echo "[apply $$ $(date -u +%FT%TZ)] $*" >> "$LOG" 2>/dev/null || true; }

        log "waiting for pid \(pid) to exit"
        for i in $(seq 1 120); do
            if ! kill -0 \(pid) 2>/dev/null; then break; fi
            sleep 0.5
        done
        if kill -0 \(pid) 2>/dev/null; then
            log "process \(pid) still running after 60s — sending SIGTERM"
            kill -TERM \(pid) 2>/dev/null || true
            sleep 2
            if kill -0 \(pid) 2>/dev/null; then
                log "process \(pid) still alive — sending SIGKILL"
                kill -9 \(pid) 2>/dev/null || true
                sleep 1
            fi
        fi

        # Determine the on-disk executable name from Info.plist so we handle
        # bundles whose folder is "LAN Messenger.app" (display name) but whose
        # executable is "LanMessenger" (target name). Falls back to filename.
        APP_EXEC=""
        if [ -f "$DST/Contents/Info.plist" ]; then
            APP_EXEC=$(/usr/libexec/PlistBuddy -c "Print :CFBundleExecutable" \
                "$DST/Contents/Info.plist" 2>/dev/null || true)
        fi
        [ -z "$APP_EXEC" ] && APP_EXEC="$(basename "$DST" .app)"

        OTHERS=$(pgrep -x "$APP_EXEC" 2>/dev/null || true)
        if [ -n "$OTHERS" ]; then
            log "killing remaining $APP_EXEC instances: $OTHERS"
            pkill -TERM -x "$APP_EXEC" 2>/dev/null || true
            sleep 1
            pkill -9 -x "$APP_EXEC" 2>/dev/null || true
            sleep 0.5
        fi

        if [ ! -d "$NEW_APP" ]; then
            log "ERROR: new app bundle missing at $NEW_APP"
            exit 2
        fi

        if [ -d "$DST" ]; then
            log "backing up current app to: $BACKUP"
            mv "$DST" "$BACKUP" || { log "ERROR: backup move failed"; exit 3; }
        fi

        log "installing new bundle via ditto: $NEW_APP → $DST"
        if ! /usr/bin/ditto "$NEW_APP" "$DST"; then
            log "ERROR: ditto failed — rolling back"
            [ -d "$BACKUP" ] && mv "$BACKUP" "$DST"
            exit 4
        fi

        /usr/bin/xattr -dr com.apple.quarantine "$DST" 2>/dev/null || true

        if /usr/bin/codesign --verify --no-strict "$DST" >> "$LOG" 2>&1; then
            log "codesign verify ok"
        else
            log "codesign verify failed (continuing anyway)"
        fi

        # Re-register with Launch Services so Spotlight/Finder/Dock pick up
        # the new bundle metadata (icon, version, capabilities) without
        # waiting for the periodic Launch Services rebuild.
        /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister \
            -f -R "$DST" >/dev/null 2>&1 || true

        log "relaunching $DST"
        /usr/bin/open "$DST" || log "WARN: open failed"

        [ -d "$BACKUP" ] && rm -rf "$BACKUP" 2>/dev/null || true
        log "done"
        """
        try script.write(to: scriptURL, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: scriptURL.path)
    }

    private func log(_ message: String) {
        let ts = ISO8601DateFormatter().string(from: Date())
        let line = "[UpdateService \(ts)] \(message)\n"
        if let data = line.data(using: .utf8) {
            if FileManager.default.fileExists(atPath: logFileURL.path),
               let handle = try? FileHandle(forWritingTo: logFileURL) {
                defer { try? handle.close() }
                _ = try? handle.seekToEnd()
                try? handle.write(contentsOf: data)
            } else {
                try? data.write(to: logFileURL)
            }
        }
        #if DEBUG
        print(line, terminator: "")
        #endif
    }
}
