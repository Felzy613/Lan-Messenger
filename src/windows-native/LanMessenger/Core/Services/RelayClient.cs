using LanMessenger.Core.Crypto;
using LanMessenger.Core.Persistence;
using NSec.Cryptography;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace LanMessenger.Core.Services;

// MARK: - Data transfer objects

public sealed class RelayStoreRequest
{
    [JsonPropertyName("relay_id_hash")]       public string RelayIdHash        { get; set; } = "";
    [JsonPropertyName("message_id")]          public string MessageId          { get; set; } = "";
    [JsonPropertyName("ciphertext_b64")]      public string CiphertextB64      { get; set; } = "";
    [JsonPropertyName("nonce_b64")]           public string NonceB64           { get; set; } = "";
    [JsonPropertyName("sender_username")]     public string SenderUsername     { get; set; } = "";
    [JsonPropertyName("sender_public_key_b64")] public string SenderPublicKeyB64 { get; set; } = "";
    [JsonPropertyName("timestamp")]           public double Timestamp          { get; set; }
    [JsonPropertyName("ttl_s")]               public int    TtlS               { get; set; }
}

public sealed class RelayPendingMessage
{
    [JsonPropertyName("message_id")]          public string MessageId          { get; set; } = "";
    [JsonPropertyName("ciphertext_b64")]      public string CiphertextB64      { get; set; } = "";
    [JsonPropertyName("nonce_b64")]           public string NonceB64           { get; set; } = "";
    [JsonPropertyName("sender_username")]     public string SenderUsername     { get; set; } = "";
    [JsonPropertyName("sender_public_key_b64")] public string SenderPublicKeyB64 { get; set; } = "";
    [JsonPropertyName("timestamp")]           public double Timestamp          { get; set; }
}

// MARK: - RelayClient

/// Thin async HTTP client that speaks to the Cloudflare cloud relay Worker.
///
/// All public methods are no-ops when RelayWorkerUrl is empty or the network
/// is unavailable; failures are silently swallowed so they never impact the
/// critical LAN path.
public sealed class RelayClient
{
    public static RelayClient Shared { get; } = new();

    private static readonly HttpClient _http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(6),
    })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private Uri? WorkerBaseUri
    {
        get
        {
            var raw = ConfigStore.Shared.Config.RelayWorkerUrl.Trim();
            return string.IsNullOrEmpty(raw) ? null : new Uri(raw.TrimEnd('/'));
        }
    }

    private RelayClient() { }

    // MARK: - relay_id derivation

    /// Derives the private relay_id from the device's X25519 private key.
    /// relay_id = SHA256(private_key_bytes || "relay-v1")
    /// This is deterministic and never transmitted — only SHA256(relay_id) is.
    public byte[] DeriveRelayId()
    {
        var privateKeyBytes = KeyManager.Shared.PrivateKey.Export(NSec.Cryptography.KeyBlobFormat.RawPrivateKey);
        var info = Encoding.UTF8.GetBytes("relay-v1");
        var input = new byte[privateKeyBytes.Length + info.Length];
        Buffer.BlockCopy(privateKeyBytes, 0, input, 0, privateKeyBytes.Length);
        Buffer.BlockCopy(info, 0, input, privateKeyBytes.Length, info.Length);
        return SHA256.HashData(input);
    }

    /// Returns SHA256(relay_id) as lowercase hex.
    /// This is the value published in discovery packets as the mailbox address.
    public string RelayIdHash()
    {
        var relayId = DeriveRelayId();
        return Convert.ToHexString(SHA256.HashData(relayId)).ToLowerInvariant();
    }

    // MARK: - Store a message for an offline peer

    /// Posts an encrypted message to the relay Worker mailbox for peerRelayIdHash.
    /// Fire-and-forget: call after a message has been placed in the local queue.
    public async Task StoreAsync(
        string peerRelayIdHash,
        string messageId,
        string ciphertextB64,
        string nonceB64,
        double timestamp)
    {
        if (WorkerBaseUri is null || string.IsNullOrEmpty(peerRelayIdHash)) return;
        try
        {
            var body = new RelayStoreRequest
            {
                RelayIdHash        = peerRelayIdHash,
                MessageId          = messageId,
                CiphertextB64      = ciphertextB64,
                NonceB64           = nonceB64,
                SenderUsername     = ConfigStore.Shared.Config.Username,
                SenderPublicKeyB64 = KeyManager.Shared.PublicKeyB64,
                Timestamp          = timestamp,
                TtlS               = 72 * 3600,
            };
            using var resp = await _http.PostAsJsonAsync(
                new Uri(WorkerBaseUri, "store"), body);
            if (resp.IsSuccessStatusCode)
                LanLogger.Info("Relay", $"store msgId={messageId} → HTTP {(int)resp.StatusCode} (stored on Worker)");
            else
                LanLogger.Warn("Relay", $"store msgId={messageId} → HTTP {(int)resp.StatusCode} (Worker rejected upload)");
        }
        catch (Exception ex)
        {
            LanLogger.Warn("Relay", $"store msgId={messageId} failed: {ex.Message}");
        }
    }

    // MARK: - Fetch pending messages for this device

    /// Fetches all messages waiting in the cloud relay inbox for this device.
    public async Task<List<RelayPendingMessage>> FetchPendingAsync()
    {
        if (WorkerBaseUri is null) return [];
        try
        {
            var relayId    = DeriveRelayId();
            var relayIdHex = Convert.ToHexString(relayId).ToLowerInvariant();
            var url = new Uri(WorkerBaseUri, $"pending?relay_id={relayIdHex}");
            using var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                LanLogger.Warn("Relay", $"fetchPending → HTTP {(int)resp.StatusCode} (Worker error)");
                return [];
            }
            var msgs = await resp.Content.ReadFromJsonAsync<List<RelayPendingMessage>>()
                       ?? [];
            if (msgs.Count > 0)
                LanLogger.Info("Relay", $"fetchPending → {msgs.Count} message(s) waiting on relay");
            return msgs;
        }
        catch (Exception ex)
        {
            LanLogger.Warn("Relay", $"fetchPending failed: {ex.Message}");
            return [];
        }
    }

    // MARK: - Delete a delivered message

    /// Deletes a message from the relay after it has been successfully processed.
    public async Task DeleteAsync(string messageId)
    {
        if (WorkerBaseUri is null) return;
        try
        {
            var relayId    = DeriveRelayId();
            var relayIdHex = Convert.ToHexString(relayId).ToLowerInvariant();
            var url = new Uri(WorkerBaseUri, $"message/{messageId}?relay_id={relayIdHex}");
            using var resp = await _http.DeleteAsync(url);
            LanLogger.Info("Relay", $"delete msgId={messageId} → HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            LanLogger.Info("Relay", $"delete msgId={messageId} failed: {ex.Message}");
        }
    }
}
