import Foundation

struct ContactConfig: Codable, Identifiable {
    var id: String { publicKeyB64 }
    var publicKeyB64: String
    var username: String
    var lastIP: String

    enum CodingKeys: String, CodingKey {
        case publicKeyB64 = "public_key_b64"
        case username
        case lastIP = "last_ip"
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

struct AppConfig: Codable {
    var username: String = "User"
    var contacts: [ContactConfig] = []
    var hiddenConversations: [String] = []
    var pendingMessages: [PendingMessageConfig] = []
    var updateServerURL: String = ""
    var inboxDir: String = ""
    var hideFromDock: Bool = false

    enum CodingKeys: String, CodingKey {
        case username, contacts
        case hiddenConversations = "hidden_conversations"
        case pendingMessages = "pending_messages"
        case updateServerURL = "update_server_url"
        case inboxDir = "inbox_dir"
        case hideFromDock = "hide_from_dock"
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

    var logsDirectory: URL {
        FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask).first!
            .appendingPathComponent("Logs/LanMessenger")
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
