using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanMessenger.Core.Persistence;

public sealed class ContactConfig
{
    [JsonPropertyName("public_key_b64")] public string PublicKeyB64 { get; set; } = "";
    [JsonPropertyName("username")]       public string Username     { get; set; } = "";
    [JsonPropertyName("last_ip")]        public string LastIP       { get; set; } = "";
}

public sealed class PendingMessageConfig
{
    [JsonPropertyName("message_id")]         public string MessageId        { get; set; } = "";
    [JsonPropertyName("peer_public_key_b64")] public string PeerPublicKeyB64 { get; set; } = "";
    [JsonPropertyName("peer_username")]      public string PeerUsername     { get; set; } = "";
    [JsonPropertyName("text")]               public string Text             { get; set; } = "";
    [JsonPropertyName("timestamp")]          public double Timestamp        { get; set; }
}

public sealed class AppConfig
{
    [JsonPropertyName("username")]             public string Username            { get; set; } = "User";
    [JsonPropertyName("contacts")]             public List<ContactConfig> Contacts { get; set; } = [];
    [JsonPropertyName("hidden_conversations")] public List<string> HiddenConversations { get; set; } = [];
    [JsonPropertyName("pending_messages")]     public List<PendingMessageConfig> PendingMessages { get; set; } = [];
    [JsonPropertyName("update_server_url")]    public string UpdateServerURL    { get; set; } = "";
    [JsonPropertyName("inbox_dir")]            public string InboxDir           { get; set; } = "";
}

// Manages reading/writing config.json in %APPDATA%\LanMessenger\.
// The private key is NOT stored here — it lives in a DPAPI-protected file (KeyManager).
public sealed class ConfigStore
{
    public static ConfigStore Shared { get; } = new();

    private static readonly string _appDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LanMessenger");

    private readonly string _configPath;
    // Python app's config path for migration detection
    private readonly string _pythonConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".lan_messenger", "config.json");

    public AppConfig Config { get; private set; } = new();

    private ConfigStore()
    {
        Directory.CreateDirectory(_appDataDir);
        _configPath = Path.Combine(_appDataDir, "config.json");
        Load();
    }

    // MARK: - Default paths

    public string InboxDirectory =>
        string.IsNullOrEmpty(Config.InboxDir)
            ? Path.Combine(_appDataDir, "Received")
            : Config.InboxDir;

    public string HistoryFilePath => Path.Combine(_appDataDir, "history.enc");

    // MARK: - Persistence

    public void Load()
    {
        if (!File.Exists(_configPath)) return;
        try
        {
            var json = File.ReadAllText(_configPath);
            Config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        catch { Config = new AppConfig(); }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configPath, json);
        }
        catch { }
    }

    // MARK: - Migration from Python app

    public bool NeedsMigration =>
        !File.Exists(_configPath) && File.Exists(_pythonConfigPath);

    // Import non-key fields from the Python config.json.
    // Returns the raw private key base64 string if present (caller decides what to do with it).
    public string? ImportPythonConfig()
    {
        if (!File.Exists(_pythonConfigPath)) return null;
        try
        {
            var json = File.ReadAllText(_pythonConfigPath);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("username", out var u)) Config.Username = u.GetString() ?? Config.Username;
            if (root.TryGetProperty("update_server_url", out var url)) Config.UpdateServerURL = url.GetString() ?? "";
            if (root.TryGetProperty("inbox_dir", out var inbox)) Config.InboxDir = inbox.GetString() ?? "";

            if (root.TryGetProperty("hidden_conversations", out var hc) && hc.ValueKind == JsonValueKind.Array)
                Config.HiddenConversations = hc.EnumerateArray()
                    .Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();

            if (root.TryGetProperty("contacts", out var contacts) && contacts.ValueKind == JsonValueKind.Array)
                Config.Contacts = contacts.EnumerateArray().Select(c =>
                {
                    c.TryGetProperty("public_key_b64", out var k);
                    c.TryGetProperty("username", out var n);
                    c.TryGetProperty("last_ip", out var ip);
                    return new ContactConfig
                    {
                        PublicKeyB64 = k.GetString() ?? "",
                        Username     = n.GetString() ?? "",
                        LastIP       = ip.GetString() ?? "",
                    };
                }).Where(c => c.PublicKeyB64.Length > 0).ToList();

            Save();

            // Copy history.enc if present
            var pyHistory = Path.Combine(Path.GetDirectoryName(_pythonConfigPath)!, "history.enc");
            if (File.Exists(pyHistory) && !File.Exists(HistoryFilePath))
                File.Copy(pyHistory, HistoryFilePath);

            // Return raw private key b64 if present
            if (root.TryGetProperty("private_key_b64", out var key))
                return key.GetString();

            return null;
        }
        catch { return null; }
    }
}
