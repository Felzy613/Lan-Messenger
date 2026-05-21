using NSec.Cryptography;
using System.Security.Cryptography;
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
//
// AES-GCM uses System.Security.Cryptography.AesGcm (BCL) rather than NSec's
// Aes256Gcm so it runs on CPUs without AES-NI (e.g. older Windows 10 machines
// and VMs without hardware AES passthrough). NSec is retained only for X25519
// key agreement and HKDF key derivation, which have no AES-NI dependency.
public static class SessionCrypto
{
    private static readonly X25519     _x25519  = KeyAgreementAlgorithm.X25519;
    private static readonly HkdfSha256 _hkdf    = KeyDerivationAlgorithm.HkdfSha256;

    private static readonly byte[] _hkdfInfo   = Encoding.UTF8.GetBytes("lan-messenger");
    private static readonly byte[] _emptyBytes = [];

    private const int KeySize   = 32;
    private const int NonceSize = 12;
    private const int TagSize   = 16;

    // Derive the 32-byte shared symmetric key for a peer.
    public static byte[] SymmetricKey(Key myPrivateKey, string peerPublicKeyB64)
    {
        byte[] peerRaw = Convert.FromBase64String(peerPublicKeyB64);
        if (peerRaw.Length != 32) throw new ArgumentException("Public key must be 32 bytes");

        var peerPub = PublicKey.Import(_x25519, peerRaw, KeyBlobFormat.RawPublicKey);
        var sharedSecret = _x25519.Agree(myPrivateKey, peerPub)
            ?? throw new CryptographicException("X25519 agreement returned null");

        return _hkdf.DeriveBytes(sharedSecret, _emptyBytes, _hkdfInfo, KeySize);
    }

    // Encrypt. Returns (nonceB64, ciphertextB64) where ciphertextB64 encodes ciphertext ‖ 16-byte tag.
    public static (string NonceB64, string CiphertextB64) Encrypt(byte[] keyBytes, byte[] plaintext, byte[] aad)
    {
        byte[] nonce      = new byte[NonceSize];
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag        = new byte[TagSize];
        RandomNumberGenerator.Fill(nonce);

        using var aesGcm = new AesGcm(keyBytes, TagSize);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, aad);

        byte[] ciphertextWithTag = new byte[ciphertext.Length + TagSize];
        ciphertext.CopyTo(ciphertextWithTag, 0);
        tag.CopyTo(ciphertextWithTag, ciphertext.Length);

        return (Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertextWithTag));
    }

    // Decrypt. ciphertextB64 must include the 16-byte tag appended.
    public static byte[] Decrypt(byte[] keyBytes, string nonceB64, string ciphertextB64, byte[] aad)
    {
        byte[] nonce = Convert.FromBase64String(nonceB64);
        if (nonce.Length != NonceSize) throw new CryptographicException("Nonce must be 12 bytes");

        byte[] ciphertextWithTag = Convert.FromBase64String(ciphertextB64);
        if (ciphertextWithTag.Length < TagSize) throw new CryptographicException("Ciphertext too short");

        int    ciphertextLen = ciphertextWithTag.Length - TagSize;
        byte[] ciphertext    = ciphertextWithTag[..ciphertextLen];
        byte[] tag           = ciphertextWithTag[ciphertextLen..];
        byte[] plaintext     = new byte[ciphertextLen];

        using var aesGcm = new AesGcm(keyBytes, TagSize);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        return plaintext;
    }

    // Convenience: encrypt for a specific peer.
    public static (string NonceB64, string CiphertextB64) EncryptForPeer(
        Key myPrivateKey, string peerPublicKeyB64, byte[] plaintext, byte[] aad)
    {
        var keyBytes = SymmetricKey(myPrivateKey, peerPublicKeyB64);
        return Encrypt(keyBytes, plaintext, aad);
    }

    // Convenience: decrypt from a specific peer.
    public static byte[] DecryptFromPeer(
        Key myPrivateKey, string peerPublicKeyB64, string nonceB64, string ciphertextB64, byte[] aad)
    {
        var keyBytes = SymmetricKey(myPrivateKey, peerPublicKeyB64);
        return Decrypt(keyBytes, nonceB64, ciphertextB64, aad);
    }
}
