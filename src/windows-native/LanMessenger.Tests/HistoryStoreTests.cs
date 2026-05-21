using LanMessenger.Core.Crypto;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using System.Text;
using System.Text.Json;

namespace LanMessenger.Tests;

[TestClass]
public class HistoryStoreTests
{
    [TestMethod]
    public void EncryptDecryptRoundTrip()
    {
        using var key = MakeKey();
        var plaintext = Encoding.UTF8.GetBytes(@"{""192.168.1.100"":[]}");

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
    public void WrongKeyFailsDecryption()
    {
        using var key1 = MakeKey();
        using var key2 = MakeKey();
        var plaintext = Encoding.UTF8.GetBytes("{}");
        var fileJson  = HistoryCrypto.EncryptHistory(plaintext, key1);

        Assert.ThrowsException<System.Security.Cryptography.CryptographicException>(
            () => HistoryCrypto.DecryptHistory(fileJson, key2));
    }

    [TestMethod]
    public void AadIsHistoryV1()
    {
        var expected = Encoding.UTF8.GetBytes("history-v1");
        CollectionAssert.AreEqual(expected, HistoryCrypto.Aad);
    }

    [TestMethod]
    public void DecryptKnownHistoryVector()
    {
        var path    = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "known_good_exchange.json");
        var doc     = JsonDocument.Parse(File.ReadAllText(path));
        var keys    = doc.RootElement.GetProperty("keys").Deserialize<Dictionary<string, string>>()!;
        var histVec = doc.RootElement.GetProperty("history");

        var aliceRaw = Convert.FromBase64String(keys["alice_private_b64"]);
        using var aliceKey = Key.Import(KeyAgreementAlgorithm.X25519, aliceRaw, KeyBlobFormat.RawPrivateKey,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        var fileJson = histVec.GetProperty("file_json").GetString()!;
        var expected = histVec.GetProperty("plaintext_utf8").GetString()!;

        var recovered = HistoryCrypto.DecryptHistory(fileJson, aliceKey);
        Assert.AreEqual(expected, Encoding.UTF8.GetString(recovered));
    }

    private static Key MakeKey() => Key.Create(KeyAgreementAlgorithm.X25519,
        new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
}
