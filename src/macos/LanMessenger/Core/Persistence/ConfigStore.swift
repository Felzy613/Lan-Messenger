import Foundation

struct ContactConfig: Codable, Identifiable {
    var id: String { publicKeyB64 }
    var publicKeyB64: String
    var username: String
    var lastIP: String
    // Base64-encoded profile picture (PNG/JPEG). Optional.
    var photoB64: String?
    // SHA256(relay_id) for this peer — persisted so the relay mailbox address
    // is known even when the peer hasn't been seen in the current session.
    var relayIdHash: String?

    enum CodingKeys: String, CodingKey {
        case publicKeyB64 = "public_key_b64"
        case username
        case lastIP = "last_ip"
        case photoB64 = "photo_b64"
        case relayIdHash = "relay_id_hash"
    }

    init(publicKeyB64: String, username: String, lastIP: String, photoB64: String? = nil, relayIdHash: String? = nil) {
        self.publicKeyB64 = publicKeyB64
        self.username = username
        self.lastIP = lastIP
        self.photoB64 = photoB64
        self.relayIdHash = relayIdHash
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        publicKeyB64 = try c.decode(String.self, forKey: .publicKeyB64)
        username = try c.decode(String.self, forKey: .username)
        lastIP = try c.decode(String.self, forKey: .lastIP)
        photoB64 = try c.decodeIfPresent(String.self, forKey: .photoB64)
        relayIdHash = try c.decodeIfPresent(String.self, forKey: .relayIdHash)
    }
}

struct PendingMessageConfig: Codable {
    var messageId: String
    var peerPublicKeyB64: String
    var peerUsername: String
    var text: String
    var timestamp: Double

    enum CodingKeys: String, CodingKey {
        case messageId = "message_id"
        case peerPublicKeyB64 = "peer_public_key_b64"
        case peerUsername = "peer_username"
        case text, timestamp
    }
}

// File queued for an offline peer; delivered when the peer comes online.
struct PendingFileConfig: Codable {
    var filePath: String
    var peerPublicKeyB64: String
    var peerUsername: String
    var timestamp: Double

    enum CodingKeys: String, CodingKey {
        case filePath = "file_path"
        case peerPublicKeyB64 = "peer_public_key_b64"
        case peerUsername = "peer_username"
        case timestamp
    }
}

struct AppConfig: Codable {
    var username: String = "User"
    var contacts: [ContactConfig] = []
    var hiddenConversations: [String] = []
    var archivedConversations: [String] = []
    var pendingMessages: [PendingMessageConfig] = []
    var pendingFiles: [PendingFileConfig] = []
    var updateServerURL: String = ""
    var inboxDir: String = ""
    // Default true: hide from dock (menu-bar-only mode) out of the box.
    var hideFromDock: Bool = true
    // User's preference for SMAppService-managed Login Item registration.
    // The OS-side status is the source of truth at runtime; this is just what
    // the user asked for so we can re-apply it if SMAppService loses our
    // registration (e.g. after an app-bundle move).
    var launchAtLogin: Bool = false
    // GitHub repo to source updates from (owner/repo).
    var updateRepo: String = "felzy613/lan-messenger"
    // Last update check time (Unix seconds). Used to throttle background checks.
    var lastUpdateCheck: Double = 0
    // When true, file-transfer and networking events are written to the log file.
    var verboseLogging: Bool = false
    // When true, undeliverable messages are posted to relayWorkerURL so the
    // recipient can pick them up when they come back online.
    var relayEnabled: Bool = false
    // URL of a Cloudflare Worker (or compatible endpoint) that stores offline
    // messages. Empty by default — users supply their own Worker URL.
    var relayWorkerURL: String = ""
    // User-chosen folder for screenshots. Empty means default (Downloads/LAN Messenger Screenshots).
    var screenshotDir: String = ""

    enum CodingKeys: String, CodingKey {
        case username, contacts
        case hiddenConversations = "hidden_conversations"
        case archivedConversations = "archived_conversations"
        case pendingMessages = "pending_messages"
        case pendingFiles = "pending_files"
        case updateServerURL = "update_server_url"
        case inboxDir = "inbox_dir"
        case hideFromDock = "hide_from_dock"
        case launchAtLogin = "launch_at_login"
        case updateRepo = "update_repo"
        case lastUpdateCheck = "last_update_check"
        case verboseLogging = "verbose_logging"
        case relayEnabled = "relay_enabled"
        case relayWorkerURL = "relay_worker_url"
        case screenshotDir = "screenshot_dir"
    }

    init() {}

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        username = (try c.decodeIfPresent(String.self, forKey: .username)) ?? "User"
        contacts = (try c.decodeIfPresent([ContactConfig].self, forKey: .contacts)) ?? []
        hiddenConversations = (try c.decodeIfPresent([String].self, forKey: .hiddenConversations)) ?? []
        archivedConversations = (try c.decodeIfPresent([String].self, forKey: .archivedConversations)) ?? []
        pendingMessages = (try c.decodeIfPresent([PendingMessageConfig].self, forKey: .pendingMessages)) ?? []
        pendingFiles = (try c.decodeIfPresent([PendingFileConfig].self, forKey: .pendingFiles)) ?? []
        updateServerURL = (try c.decodeIfPresent(String.self, forKey: .updateServerURL)) ?? ""
        inboxDir = (try c.decodeIfPresent(String.self, forKey: .inboxDir)) ?? ""
        // Existing configs that omit this key default to true (hide from dock).
        hideFromDock = (try c.decodeIfPresent(Bool.self, forKey: .hideFromDock)) ?? true
        launchAtLogin = (try c.decodeIfPresent(Bool.self, forKey: .launchAtLogin)) ?? false
        updateRepo = (try c.decodeIfPresent(String.self, forKey: .updateRepo)) ?? "felzy613/lan-messenger"
        lastUpdateCheck = (try c.decodeIfPresent(Double.self, forKey: .lastUpdateCheck)) ?? 0
        verboseLogging = (try c.decodeIfPresent(Bool.self, forKey: .verboseLogging)) ?? false
        relayEnabled = (try c.decodeIfPresent(Bool.self, forKey: .relayEnabled)) ?? false
        relayWorkerURL = (try c.decodeIfPresent(String.self, forKey: .relayWorkerURL)) ?? ""
        screenshotDir = (try c.decodeIfPresent(String.self, forKey: .screenshotDir)) ?? ""
    }
}

// Manages reading/writing config.json in Application Support.
// The private key is NOT stored here — it lives in Keychain (KeyManager).
final class ConfigStore {

    static let shared = ConfigStore()

    private let configDir: URL
    private let configURL: URL

    // Python app's config for migration detection
    private let pythonConfigURL: URL = {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".lan_messenger/config.json")
    }()

    var config: AppConfig = AppConfig()

    private init() {
        let appSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        configDir = appSupport.appendingPathComponent("LanMessenger")
        configURL = configDir.appendingPathComponent("config.json")
        try? FileManager.default.createDirectory(at: configDir, withIntermediateDirectories: true)
        load()
    }

    // MARK: - Default paths

    var inboxDirectory: URL {
        if config.inboxDir.isEmpty {
            return configDir.appendingPathComponent("Received")
        }
        return URL(fileURLWithPath: config.inboxDir)
    }

    var historyFileURL: URL {
        configDir.appendingPathComponent("history.enc")
    }

    var logsDirectory: URL { NetLogger.logsDirectory }

    var updateStagingDirectory: URL {
        configDir.appendingPathComponent("Updates")
    }

    // MARK: - Persistence

    func load() {
        guard let data = try? Data(contentsOf: configURL),
              let decoded = try? JSONDecoder().decode(AppConfig.self, from: data) else {
            return
        }
        config = decoded
    }

    func save() {
        guard let data = try? JSONEncoder().encode(config) else { return }
        try? data.write(to: configURL, options: .atomic)
    }

    // MARK: - Migration from Python app

    // Returns true if a Python config exists and no native config exists yet.
    var needsMigration: Bool {
        !FileManager.default.fileExists(atPath: configURL.path) &&
        FileManager.default.fileExists(atPath: pythonConfigURL.path)
    }

    // Import everything except private_key_b64 from the Python config.
    // Returns the raw private key bytes so the caller can offer the user
    // a choice of importing vs generating fresh.
    func importPythonConfig() -> Data? {
        guard let data = try? Data(contentsOf: pythonConfigURL),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return nil
        }

        if let name = json["username"] as? String { config.username = name }
        if let url = json["update_server_url"] as? String { config.updateServerURL = url }
        if let inbox = json["inbox_dir"] as? String { config.inboxDir = inbox }

        if let raw = json["hidden_conversations"] as? [String] {
            config.hiddenConversations = raw
        }

        if let contacts = json["contacts"] as? [[String: String]] {
            config.contacts = contacts.compactMap { dict in
                guard let key = dict["public_key_b64"], let name = dict["username"] else { return nil }
                return ContactConfig(publicKeyB64: key, username: name, lastIP: dict["last_ip"] ?? "")
            }
        }

        save()

        // Also copy history.enc if it exists alongside the Python config
        let pythonHistoryURL = pythonConfigURL.deletingLastPathComponent().appendingPathComponent("history.enc")
        if FileManager.default.fileExists(atPath: pythonHistoryURL.path),
           !FileManager.default.fileExists(atPath: historyFileURL.path) {
            try? FileManager.default.copyItem(at: pythonHistoryURL, to: historyFileURL)
        }

        // Return raw private key bytes if present (caller decides what to do with it)
        if let keyB64 = json["private_key_b64"] as? String {
            return Data(base64Encoded: keyB64)
        }
        return nil
    }
}
