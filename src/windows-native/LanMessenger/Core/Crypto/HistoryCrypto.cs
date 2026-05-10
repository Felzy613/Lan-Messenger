using NSec.Cryptography;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LanMessenger.Core.Crypto;

// Handles encryption/decryption of the local history file.
//
// Key derivation (does NOT use peer exchange):
//   history_key = HKDF-SHA256(
//       ikm  = raw_private_key_bytes (32 bytes),
//       salt = [] (empty),
//       info = "lan-messenger-history" (UTF-8),
//       len  = 32
//   )
//
// AAD: b"history-v1" (the literal 10 UTF-8 bytes)
//
// File format (JSON): { "nonce": "<base64 12-byte nonce>", "ciphertext": "<base64 ct+tag>" }
public static class HistoryCrypto
{
    private static readonly HkdfSha256 _hkdf   = KeyDerivationAlgorithm.HkdfSha256;
    private static readonly Aes256Gcm  _aes    = AeadAlgorithm.Aes256Gcm;

    private static readonly byte[] _info = Encoding.UTF8.GetBytes("lan-messenger-history");
    public  static readonly byte[] Aad   = Encoding.UTF8.GetBytes("history-v1");
    private static readonly byte[] _empty = [];

    // Derive the history symmetric key from the raw private key bytes.
    public static Key HistoryKey(Key privateKey)
    {
        byte[] raw = privateKey.Export(KeyBlobFormat.RawPrivateKey);
        // NSec 24.x removed the public SharedSecret constructor; use BCL HKDF with raw IKM instead.
        byte[] keyBytes = HKDF.DeriveKey(HashAlgorithmName.SHA256, raw, 32, salt: _empty, info: _info);
        return Key.Import(_aes, keyBytes, KeyBlobFormat.RawSymmetricKey,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
    }

    // Encrypt plaintext bytes. Returns the outer file JSON string.
    public static string EncryptHistory(byte[] plaintext, Key privateKey)
    {
        using var key = HistoryKey(privateKey);

        byte[] nonce = new byte[_aes.NonceSize]; // 12 bytes
        RandomNumberGenerator.Fill(nonce);

        byte[] ciphertextWithTag = _aes.Encrypt(key, nonce, Aad, plaintext);

        var outer = new Dictionary<string, string>
        {
            ["nonce"]      = Convert.ToBase64String(nonce),
            ["ciphertext"] = Convert.ToBase64String(ciphertextWithTag),
        };
        return JsonSerializer.Serialize(outer);
    }

    // Decrypt from the outer file JSON string. Returns inner plaintext bytes.
    public static byte[] DecryptHistory(string fileJson, Key privateKey)
    {
        using var doc = JsonDocument.Parse(fileJson);
        var root = doc.RootElement;

        var nonceB64  = root.GetProperty("nonce").GetString()!;
        var ctB64     = root.GetProperty("ciphertext").GetString()!;

        byte[] nonce           = Convert.FromBase64String(nonceB64);
        byte[] ciphertextWithTag = Convert.FromBase64String(ctB64);

        if (nonce.Length != 12)      throw new CryptographicException("Invalid nonce length");
        if (ciphertextWithTag.Length < 16) throw new CryptographicException("Ciphertext too short");

        using var key = HistoryKey(privateKey);

        var plaintext = _aes.Decrypt(key, nonce, Aad, ciphertextWithTag)
            ?? throw new CryptographicException("History decryption failed");

        return plaintext;
    }
}
