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
//
// AES-GCM uses System.Security.Cryptography.AesGcm (BCL) rather than NSec's
// Aes256Gcm so it runs on CPUs without AES-NI (e.g. older Windows 10 machines).
public static class HistoryCrypto
{
    private static readonly HkdfSha256 _hkdf  = KeyDerivationAlgorithm.HkdfSha256;

    private static readonly byte[] _info  = Encoding.UTF8.GetBytes("lan-messenger-history");
    public  static readonly byte[] Aad    = Encoding.UTF8.GetBytes("history-v1");
    private static readonly byte[] _empty = [];

    private const int KeySize   = 32;
    private const int NonceSize = 12;
    private const int TagSize   = 16;

    // Derive the 32-byte history symmetric key from the private key bytes.
    public static byte[] HistoryKey(Key privateKey)
    {
        byte[] raw = privateKey.Export(KeyBlobFormat.RawPrivateKey);
        // Use BCL HKDF directly since we have raw IKM bytes here.
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, raw, KeySize, salt: _empty, info: _info);
    }

    // Encrypt plaintext bytes. Returns the outer file JSON string.
    public static string EncryptHistory(byte[] plaintext, Key privateKey)
    {
        var keyBytes  = HistoryKey(privateKey);
        byte[] nonce      = new byte[NonceSize];
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag        = new byte[TagSize];
        RandomNumberGenerator.Fill(nonce);

        using var aesGcm = new AesGcm(keyBytes, TagSize);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, Aad);

        byte[] ciphertextWithTag = new byte[ciphertext.Length + TagSize];
        ciphertext.CopyTo(ciphertextWithTag, 0);
        tag.CopyTo(ciphertextWithTag, ciphertext.Length);

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

        byte[] nonce            = Convert.FromBase64String(root.GetProperty("nonce").GetString()!);
        byte[] ciphertextWithTag = Convert.FromBase64String(root.GetProperty("ciphertext").GetString()!);

        if (nonce.Length != NonceSize)          throw new CryptographicException("Invalid nonce length");
        if (ciphertextWithTag.Length < TagSize) throw new CryptographicException("Ciphertext too short");

        int    ciphertextLen = ciphertextWithTag.Length - TagSize;
        byte[] ciphertext    = ciphertextWithTag[..ciphertextLen];
        byte[] tag           = ciphertextWithTag[ciphertextLen..];
        byte[] plaintext     = new byte[ciphertextLen];

        var keyBytes = HistoryKey(privateKey);
        using var aesGcm = new AesGcm(keyBytes, TagSize);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, Aad);
        return plaintext;
    }
}
