using NSec.Cryptography;
using System.Text;

namespace LanMessenger.Core.Crypto;

// Handles per-peer message encryption/decryption.
//
// Protocol:
//   shared_secret = X25519(my_private, peer_public)
//   symmetric_key = HKDF-SHA256(ikm: shared_secret, salt: [], info: "lan-messenger", len: 32)
//   nonce         = random(12 bytes)
//   ciphertext    = AES-256-GCM.Encrypt(key, nonce, plaintext, aad)
//   transmitted   = base64(nonce) + base64(ciphertext ‖ 16-byte tag)
//
// The 16-byte AES-GCM tag is appended to the ciphertext bytes before base64-encoding,
// matching the Python cryptography library's output format.
public static class SessionCrypto
{
    private static readonly X25519      _x25519   = KeyAgreementAlgorithm.X25519;
    private static readonly HkdfSha256  _hkdf     = KeyDerivationAlgorithm.HkdfSha256;
    private static readonly Aes256Gcm   _aes      = AeadAlgorithm.Aes256Gcm;

    private static readonly byte[] _hkdfInfo    = Encoding.UTF8.GetBytes("lan-messenger");
    private static readonly byte[] _emptyBytes  = [];

    // Derive the shared symmetric key for a peer.
    public static Key SymmetricKey(Key myPrivateKey, string peerPublicKeyB64)
    {
        byte[] peerRaw = Convert.FromBase64String(peerPublicKeyB64);
        if (peerRaw.Length != 32) throw new ArgumentException("Public key must be 32 bytes");

        var peerPub = PublicKey.Import(_x25519, peerRaw, KeyBlobFormat.RawPublicKey);
        var sharedSecret = _x25519.Agree(myPrivateKey, peerPub)
            ?? throw new CryptographicException("X25519 agreement returned null");

        return _hkdf.DeriveKey(sharedSecret, _emptyBytes, _hkdfInfo, _aes,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
    }

    // Encrypt. Returns (nonceB64, ciphertextB64) where ciphertextB64 encodes ciphertext ‖ 16-byte tag.
    public static (string NonceB64, string CiphertextB64) Encrypt(Key key, byte[] plaintext, byte[] aad)
    {
        byte[] nonce = new byte[_aes.NonceSize]; // 12 bytes
        System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);

        // NSec Aes256Gcm.Encrypt appends the tag: output = ciphertext ‖ tag
        byte[] ciphertextWithTag = _aes.Encrypt(key, nonce, aad, plaintext);

        return (Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertextWithTag));
    }

    // Decrypt. ciphertextB64 must include the 16-byte tag appended.
    public static byte[] Decrypt(Key key, string nonceB64, string ciphertextB64, byte[] aad)
    {
        byte[] nonce = Convert.FromBase64String(nonceB64);
        if (nonce.Length != 12) throw new CryptographicException("Nonce must be 12 bytes");

        byte[] ciphertextWithTag = Convert.FromBase64String(ciphertextB64);
        if (ciphertextWithTag.Length < 16) throw new CryptographicException("Ciphertext too short");

        // NSec Aes256Gcm.Decrypt expects ciphertext ‖ tag together (same format we wrote)
        if (!_aes.Decrypt(key, nonce, aad, ciphertextWithTag, out byte[]? plaintext) || plaintext is null)
            throw new CryptographicException("Decryption failed (authentication error)");

        return plaintext;
    }

    // Convenience: encrypt for a specific peer.
    public static (string NonceB64, string CiphertextB64) EncryptForPeer(
        Key myPrivateKey, string peerPublicKeyB64, byte[] plaintext, byte[] aad)
    {
        using var symKey = SymmetricKey(myPrivateKey, peerPublicKeyB64);
        return Encrypt(symKey, plaintext, aad);
    }

    // Convenience: decrypt from a specific peer.
    public static byte[] DecryptFromPeer(
        Key myPrivateKey, string peerPublicKeyB64, string nonceB64, string ciphertextB64, byte[] aad)
    {
        using var symKey = SymmetricKey(myPrivateKey, peerPublicKeyB64);
        return Decrypt(symKey, nonceB64, ciphertextB64, aad);
    }
}
