using NSec.Cryptography;
using System.Security.Cryptography;

namespace LanMessenger.Core.Crypto;

// Manages the X25519 private key, protected by DPAPI (CurrentUser scope).
// Key is stored at %APPDATA%\LanMessenger\private.key.dpapi
// The raw 32-byte private key is DPAPI-encrypted on disk; reconstructed on every load.
public sealed class KeyManager
{
    public static KeyManager Shared { get; } = new();

    private static readonly X25519 _algorithm = KeyAgreementAlgorithm.X25519;
    private static readonly string _keyPath = Path.Combine(ResolveAppDataDir(), "private.key.dpapi");

    private static string ResolveAppDataDir()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData))
            appData = Environment.GetEnvironmentVariable("APPDATA");
        if (string.IsNullOrEmpty(appData))
            appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData", "Roaming");
        return Path.Combine(appData, "LanMessenger");
    }

    private Key _privateKey;

    private KeyManager()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
        _privateKey = LoadOrCreate();
    }

    public Key PrivateKey => _privateKey;

    public string PublicKeyB64 =>
        Convert.ToBase64String(_privateKey.PublicKey.Export(KeyBlobFormat.RawPublicKey));

    public byte[] PublicKeyBytes =>
        _privateKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);

    // Import a raw 32-byte key from base64 (Python config.json migration).
    public void ImportFromBase64(string b64)
    {
        byte[] raw = Convert.FromBase64String(b64);
        if (raw.Length != 32) throw new ArgumentException("Key must be 32 bytes");
        var key = Key.Import(_algorithm, raw, KeyBlobFormat.RawPrivateKey,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        SaveKey(key);
        _privateKey = key;
    }

    // MARK: - Private helpers

    private Key LoadOrCreate()
    {
        if (File.Exists(_keyPath))
        {
            try
            {
                byte[] encrypted = File.ReadAllBytes(_keyPath);
                byte[] raw = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                if (raw.Length == 32)
                {
                    return Key.Import(_algorithm, raw, KeyBlobFormat.RawPrivateKey,
                        new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
                }
            }
            catch { }
        }

        // Generate fresh key
        var fresh = Key.Create(_algorithm,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        SaveKey(fresh);
        return fresh;
    }

    private void SaveKey(Key key)
    {
        byte[] raw = key.Export(KeyBlobFormat.RawPrivateKey);
        byte[] encrypted = ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_keyPath, encrypted);
    }
}
