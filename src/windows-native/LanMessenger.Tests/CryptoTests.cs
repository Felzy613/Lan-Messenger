using LanMessenger.Core.Crypto;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using System.Text;
using System.Text.Json;

namespace LanMessenger.Tests;

[TestClass]
public class CryptoTests
{
    // MARK: - Key agreement symmetry

    [TestMethod]
    public void SharedKeyIsSymmetric()
    {
        using var alice = Key.Create(KeyAgreementAlgorithm.X25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        using var bob = Key.Create(KeyAgreementAlgorithm.X25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        var alicePubB64 = Convert.ToBase64String(alice.PublicKey.Export(KeyBlobFormat.RawPublicKey));
        var bobPubB64   = Convert.ToBase64String(bob.PublicKey.Export(KeyBlobFormat.RawPublicKey));

        var keyFromAlice = SessionCrypto.SymmetricKey(alice, bobPubB64);
        var keyFromBob   = SessionCrypto.SymmetricKey(bob,   alicePubB64);

        Assert.AreEqual(
            Convert.ToBase64String(keyFromAlice),
            Convert.ToBase64String(keyFromBob),
            "Both sides must derive the same symmetric key");
    }

    // MARK: - Encrypt / decrypt round-trip

    [TestMethod]
    public void TextEncryptDecryptRoundTrip()
    {
        using var alice = MakeKey();
        using var bob   = MakeKey();
        var alicePubB64 = PubB64(alice);
        var bobPubB64   = PubB64(bob);

        var plaintext = Encoding.UTF8.GetBytes("Hello, Bob!");
        var aad       = Encoding.UTF8.GetBytes("test-message-id");

        var (nonceB64, ctB64) = SessionCrypto.EncryptForPeer(alice, bobPubB64, plaintext, aad);
        var recovered = SessionCrypto.DecryptFromPeer(bob, alicePubB64, nonceB64, ctB64, aad);

        CollectionAssert.AreEqual(plaintext, recovered);
    }

    [TestMethod]
    public void WrongAadFails()
    {
        using var alice = MakeKey();
        using var bob   = MakeKey();
        var bobPubB64   = PubB64(bob);
        var alicePubB64 = PubB64(alice);

        var (nonceB64, ctB64) = SessionCrypto.EncryptForPeer(
            alice, bobPubB64, Encoding.UTF8.GetBytes("secret"), Encoding.UTF8.GetBytes("correct-aad"));

        Assert.ThrowsException<System.Security.Cryptography.AuthenticationTagMismatchException>(() =>
            SessionCrypto.DecryptFromPeer(bob, alicePubB64, nonceB64, ctB64,
                Encoding.UTF8.GetBytes("wrong-aad")));
    }

    [TestMethod]
    public void TamperedCiphertextFails()
    {
        using var alice = MakeKey();
        using var bob   = MakeKey();
        var bobPubB64   = PubB64(bob);
        var alicePubB64 = PubB64(alice);
        var aad = Encoding.UTF8.GetBytes("aad");

        var (nonceB64, ctB64) = SessionCrypto.EncryptForPeer(alice, bobPubB64,
            Encoding.UTF8.GetBytes("secret"), aad);

        var ctBytes = Convert.FromBase64String(ctB64);
        ctBytes[0] ^= 0xFF;
        var tampered = Convert.ToBase64String(ctBytes);

        Assert.ThrowsException<System.Security.Cryptography.AuthenticationTagMismatchException>(() =>
            SessionCrypto.DecryptFromPeer(bob, alicePubB64, nonceB64, tampered, aad));
    }

    // MARK: - Decrypt known Python-generated vector

    [TestMethod]
    public void DecryptKnownVector()
    {
        var vectors = LoadVectors();
        var keys    = vectors["keys"].Deserialize<Dictionary<string, string>>()!;
        var textVec = vectors["text_message"];

        var alicePrivB64 = keys["alice_private_b64"];
        var alicePubB64  = keys["alice_public_b64"];
        var bobPrivB64   = keys["bob_private_b64"];

        var bobRaw  = Convert.FromBase64String(bobPrivB64);
        using var bobKey = Key.Import(KeyAgreementAlgorithm.X25519, bobRaw, KeyBlobFormat.RawPrivateKey,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        var nonceB64  = textVec.GetProperty("nonce_b64").GetString()!;
        var ctB64     = textVec.GetProperty("ciphertext_b64").GetString()!;
        var msgId     = textVec.GetProperty("message_id").GetString()!;
        var expected  = textVec.GetProperty("plaintext_utf8").GetString()!;

        var recovered = SessionCrypto.DecryptFromPeer(
            bobKey, alicePubB64, nonceB64, ctB64, Encoding.UTF8.GetBytes(msgId));

        Assert.AreEqual(expected, Encoding.UTF8.GetString(recovered));
    }

    // MARK: - History crypto

    [TestMethod]
    public void HistoryEncryptDecryptRoundTrip()
    {
        using var key = MakeKey();
        var plaintext = Encoding.UTF8.GetBytes(@"{""192.168.1.1"":[]}");
        var fileJson  = HistoryCrypto.EncryptHistory(plaintext, key);
        var recovered = HistoryCrypto.DecryptHistory(fileJson, key);
        CollectionAssert.AreEqual(plaintext, recovered);
    }

    [TestMethod]
    public void HistoryKeyIsDeterministic()
    {
        using var key = MakeKey();
        var k1 = HistoryCrypto.HistoryKey(key);
        var k2 = HistoryCrypto.HistoryKey(key);
        Assert.AreEqual(
            Convert.ToBase64String(k1),
            Convert.ToBase64String(k2));
    }

    [TestMethod]
    public void DecryptKnownHistoryVector()
    {
        var vectors  = LoadVectors();
        var keys     = vectors["keys"].Deserialize<Dictionary<string, string>>()!;
        var histVec  = vectors["history"];

        var aliceRaw = Convert.FromBase64String(keys["alice_private_b64"]);
        using var aliceKey = Key.Import(KeyAgreementAlgorithm.X25519, aliceRaw, KeyBlobFormat.RawPrivateKey,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        var fileJson = histVec.GetProperty("file_json").GetString()!;
        var expected = histVec.GetProperty("plaintext_utf8").GetString()!;

        var recovered = HistoryCrypto.DecryptHistory(fileJson, aliceKey);
        Assert.AreEqual(expected, Encoding.UTF8.GetString(recovered));
    }

    // MARK: - Helpers

    private static Key MakeKey() => Key.Create(KeyAgreementAlgorithm.X25519,
        new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

    private static string PubB64(Key k) =>
        Convert.ToBase64String(k.PublicKey.Export(KeyBlobFormat.RawPublicKey));

    private static Dictionary<string, JsonElement> LoadVectors()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        var path = Path.Combine(dir, "known_good_exchange.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    }
}
