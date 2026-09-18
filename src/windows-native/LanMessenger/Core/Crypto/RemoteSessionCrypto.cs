using NSec.Cryptography;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace LanMessenger.Core.Crypto;

// Key exchange and frame encryption for the remote-desktop media channel.
// Mirror of the macOS RemoteSessionCrypto.swift — the two MUST agree byte for
// byte, which is what remote_handshake_vector.json exists to prove.
//
// This is deliberately NOT SessionCrypto. Messages use a static X25519
// agreement with a random nonce per packet; a screen stream needs two things
// that construction cannot give:
//
//   1. Forward secrecy. A long-term key compromised next year must not decrypt
//      a screen recording captured today.
//   2. A counter nonce. At 30 fps across five sub-channels, random 96-bit
//      nonces are both slower and strictly weaker than a deterministic counter.
//
// The handshake is Noise `KK` in shape: mix a static-ephemeral agreement in
// each direction (which authenticates both peers using the identity keys the
// contacts list already pins) with an ephemeral-ephemeral agreement (which
// provides the forward secrecy). There is no signing key anywhere in this
// protocol, so this mixing IS the authentication. See PROTOCOL.md -> Remote
// Desktop -> Media Session Key Derivation, which is authoritative.
//
// Three rules here are load-bearing and each one is a miserable bug if broken:
//
//   * Role is assigned by the protocol, not chosen. The peer that sent
//     `remote_invite` is the initiator. `es` and `se` are defined relative to
//     role, so two peers that both believe they are the initiator derive
//     swapped keys and fail to decrypt each other with no useful error.
//   * The transcript binds the negotiated parameters into the key.
//   * Keys are per-session and never survive a reconnect. Reusing a key with a
//     sequence counter reset to zero is catastrophic AES-GCM nonce reuse.
//
// HKDF comes from the BCL rather than NSec: we need a single derivation over
// three concatenated shared secrets with a 72-byte output, which NSec's
// secret-oriented KDF surface does not express. NSec is used only for X25519,
// exactly as SessionCrypto already does. AES-GCM is the BCL's for the same
// reason SessionCrypto gives — it works on CPUs without AES-NI.
public static class RemoteSessionCrypto
{
    public const string ProtocolLabel = "lan-messenger-remote-v1";
    public const int KeySize    = 32;
    public const int SaltSize   = 4;
    public const int NonceSize  = 12;
    public const int TagSize    = 16;
    /// 4-byte length + 1 channel + 1 flags + 8 sequence + 8 capture_us.
    public const int HeaderSize = 22;

    private static readonly X25519 _x25519 = KeyAgreementAlgorithm.X25519;
    private static readonly byte[] _label  = Encoding.UTF8.GetBytes(ProtocolLabel);

    // MARK: - Handshake

    /// <summary>
    /// Derives the media session keys. <paramref name="myRole"/> says which half
    /// we are performing; every other argument is labelled by role rather than
    /// by ownership, so both peers pass the same values in the same slots and
    /// arrive at the same keys.
    /// </summary>
    public static RemoteSessionKeys DeriveKeys(
        RemoteSessionRole myRole,
        Key myEphemeralPrivate,
        Key myStaticPrivate,
        string peerEphemeralPublicKeyB64,
        string peerStaticPublicKeyB64,
        string sessionId,
        RemoteHandshakeParams parameters)
    {
        byte[] sessionIdBytes = SessionIdBytes(sessionId);
        byte[] peerEphemeralRaw = PublicKeyBytes(peerEphemeralPublicKeyB64);
        byte[] peerStaticRaw    = PublicKeyBytes(peerStaticPublicKeyB64);
        var peerEphemeral = ImportPublic(peerEphemeralRaw);
        var peerStatic    = ImportPublic(peerStaticRaw);

        // es authenticates the responder, se authenticates the initiator, ee
        // supplies forward secrecy. Which agreement we can actually compute
        // depends on our role -- we hold only our own private keys -- but the
        // concatenation order is fixed by the protocol, not by role.
        byte[] es, se;
        if (myRole == RemoteSessionRole.Initiator)
        {
            es = Agree(myEphemeralPrivate, peerStatic);     // eph_i  x static_r
            se = Agree(myStaticPrivate, peerEphemeral);     // static_i x eph_r
        }
        else
        {
            es = Agree(myStaticPrivate, peerEphemeral);     // same secret, other side
            se = Agree(myEphemeralPrivate, peerStatic);
        }
        byte[] ee = Agree(myEphemeralPrivate, peerEphemeral);

        byte[] myEphemeralPub = myEphemeralPrivate.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        byte[] myStaticPub    = myStaticPrivate.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        bool initiator = myRole == RemoteSessionRole.Initiator;

        byte[] transcript = TranscriptHash(
            sessionIdBytes,
            staticInitiator:    initiator ? myStaticPub    : peerStaticRaw,
            staticResponder:    initiator ? peerStaticRaw  : myStaticPub,
            ephemeralInitiator: initiator ? myEphemeralPub : peerEphemeralRaw,
            ephemeralResponder: initiator ? peerEphemeralRaw : myEphemeralPub,
            parameters);

        byte[] ikm = new byte[es.Length + se.Length + ee.Length];
        es.CopyTo(ikm, 0);
        se.CopyTo(ikm, es.Length);
        ee.CopyTo(ikm, es.Length + se.Length);

        byte[] okm = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm:    ikm,
            outputLength: KeySize * 2 + SaltSize * 2,
            salt:   sessionIdBytes,
            info:   transcript);

        CryptographicOperations.ZeroMemory(ikm);
        CryptographicOperations.ZeroMemory(es);
        CryptographicOperations.ZeroMemory(se);
        CryptographicOperations.ZeroMemory(ee);

        return new RemoteSessionKeys(
            InitiatorToResponder:     okm[..32],
            ResponderToInitiator:     okm[32..64],
            InitiatorToResponderSalt: okm[64..68],
            ResponderToInitiatorSalt: okm[68..72],
            Transcript:               transcript);
    }

    /// <summary>
    /// SHA-256 over the protocol label, session id, both static publics, both
    /// ephemeral publics, and the canonical parameters, in that fixed order.
    /// Fed to HKDF as `info`, which is what makes the negotiated parameters
    /// tamper-evident: change any of them and both sides derive different keys.
    /// </summary>
    public static byte[] TranscriptHash(
        byte[] sessionIdBytes,
        byte[] staticInitiator,
        byte[] staticResponder,
        byte[] ephemeralInitiator,
        byte[] ephemeralResponder,
        RemoteHandshakeParams parameters)
    {
        // Fully qualified: NSec also exports an `IncrementalHash`, and with both
        // namespaces imported the bare name is ambiguous.
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(_label);
        sha.AppendData(sessionIdBytes);
        sha.AppendData(staticInitiator);
        sha.AppendData(staticResponder);
        sha.AppendData(ephemeralInitiator);
        sha.AppendData(ephemeralResponder);
        sha.AppendData(parameters.CanonicalBytes());
        return sha.GetHashAndReset();
    }

    /// <summary>
    /// Constant-time comparison for key confirmation. Timing is not a realistic
    /// attack surface here, but a confirmation check is exactly the kind of code
    /// that gets copied somewhere it does matter.
    /// </summary>
    public static bool TranscriptsMatch(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        => a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);

    // MARK: - Frame crypto

    /// <summary>
    /// <c>direction_salt(4) || sequence(8)</c>, big-endian. Deterministic rather
    /// than random, which is safe only because sequence is strictly increasing
    /// within a session and a session's keys never outlive its socket. Both
    /// halves of that sentence are enforced elsewhere; if either stops being
    /// true this construction becomes a vulnerability.
    /// </summary>
    public static byte[] Nonce(ReadOnlySpan<byte> salt, ulong sequence)
    {
        if (salt.Length != SaltSize) throw new ArgumentException("Direction salt must be 4 bytes", nameof(salt));
        byte[] nonce = new byte[NonceSize];
        salt.CopyTo(nonce);
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(SaltSize), sequence);
        return nonce;
    }

    /// <summary>
    /// Seals one media payload. <paramref name="header"/> is the 22 plaintext
    /// header bytes, authenticated as AAD so the framing itself cannot be
    /// tampered with -- including the length prefix, so a truncation attack
    /// fails the tag check. Returns <c>ciphertext || 16-byte tag</c>.
    /// </summary>
    public static byte[] Seal(
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> header,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> salt,
        ulong sequence)
    {
        byte[] nonce = Nonce(salt, sequence);
        byte[] output = new byte[payload.Length + TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, payload, output.AsSpan(0, payload.Length), output.AsSpan(payload.Length), header);
        return output;
    }

    /// <summary>
    /// Opens one media payload. <paramref name="body"/> is
    /// <c>ciphertext || 16-byte tag</c>.
    /// </summary>
    public static byte[] Open(
        ReadOnlySpan<byte> body,
        ReadOnlySpan<byte> header,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> salt,
        ulong sequence)
    {
        if (body.Length < TagSize) throw new CryptographicException("Media frame shorter than the GCM tag");
        byte[] nonce = Nonce(salt, sequence);
        int cipherLength = body.Length - TagSize;
        byte[] plaintext = new byte[cipherLength];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, body[..cipherLength], body[cipherLength..], plaintext, header);
        return plaintext;
    }

    // MARK: - Identity fingerprint

    /// <summary>
    /// Short fingerprint of an identity key, for the consent prompt. A display
    /// name is trivially spoofable by any peer on the LAN; the pinned public key
    /// is not. Four space-separated groups of four hex characters -- long enough
    /// to be meaningful, short enough to be read aloud, which is the only way it
    /// ever gets verified in practice. Returns null for a malformed key.
    /// </summary>
    public static string? Fingerprint(string publicKeyB64)
    {
        byte[] raw;
        try { raw = Convert.FromBase64String(publicKeyB64); }
        catch (FormatException) { return null; }
        if (raw.Length != 32) return null;

        byte[] digest = SHA256.HashData(raw);
        string hex = Convert.ToHexString(digest, 0, 8).ToLowerInvariant();
        return string.Join(' ', hex[..4], hex[4..8], hex[8..12], hex[12..16]);
    }

    // MARK: - Session ids

    /// <summary>A session_id shares message_id's shape: 32 lowercase hex characters.</summary>
    public static bool IsSessionId(string value)
    {
        if (value.Length != 32) return false;
        foreach (char c in value)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        return true;
    }

    public static string NewSessionId() => Guid.NewGuid().ToString("N").ToLowerInvariant();

    // MARK: - Private helpers

    private static byte[] SessionIdBytes(string sessionId)
    {
        if (!IsSessionId(sessionId))
            throw new ArgumentException("Session id must be 32 lowercase hex characters", nameof(sessionId));
        return Convert.FromHexString(sessionId);
    }

    private static byte[] PublicKeyBytes(string b64)
    {
        byte[] raw;
        try { raw = Convert.FromBase64String(b64); }
        catch (FormatException) { throw new ArgumentException("Public key is not valid base64"); }
        if (raw.Length != 32) throw new ArgumentException("Public key must be 32 raw bytes");
        return raw;
    }

    private static PublicKey ImportPublic(byte[] raw)
        => PublicKey.Import(_x25519, raw, KeyBlobFormat.RawPublicKey);

    /// <summary>
    /// Raw X25519 output. NSec hides shared-secret bytes by default to push
    /// callers through a KDF; here we genuinely need the raw value, because the
    /// protocol concatenates three agreements before a single HKDF.
    /// </summary>
    private static byte[] Agree(Key privateKey, PublicKey peerPublic)
    {
        var creationParameters = new SharedSecretCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        };
        using var secret = _x25519.Agree(privateKey, peerPublic, in creationParameters)
            ?? throw new CryptographicException("X25519 agreement returned null");
        return secret.Export(SharedSecretBlobFormat.RawSharedSecret);
    }
}

/// <summary>
/// Which half of the handshake this client is performing. Assigned by the
/// protocol, never chosen: the peer that sent <c>remote_invite</c> is
/// <see cref="Initiator"/>. It is also the peer that will be viewing; the
/// responder is the host whose screen is shared.
/// </summary>
public enum RemoteSessionRole
{
    Initiator,
    Responder,
}

public static class RemoteSessionRoleExtensions
{
    public static RemoteSessionRole Opposite(this RemoteSessionRole role)
        => role == RemoteSessionRole.Initiator ? RemoteSessionRole.Responder : RemoteSessionRole.Initiator;
}

/// <summary>
/// The output of a successful handshake. Holds both directional keys because
/// either peer may need to seal in one direction and open in the other;
/// <see cref="SealingKey"/> and <see cref="OpeningKey"/> pick correctly from the
/// role so no call site ever reasons about "mine vs theirs", which is the
/// mistake that produces swapped keys.
/// </summary>
public sealed record RemoteSessionKeys(
    byte[] InitiatorToResponder,
    byte[] ResponderToInitiator,
    byte[] InitiatorToResponderSalt,
    byte[] ResponderToInitiatorSalt,
    // Transcript: SHA-256 over the handshake transcript, exchanged as key confirmation.
    byte[] Transcript)
{
    public byte[] SealingKey(RemoteSessionRole role)
        => role == RemoteSessionRole.Initiator ? InitiatorToResponder : ResponderToInitiator;

    public byte[] OpeningKey(RemoteSessionRole role)
        => role == RemoteSessionRole.Initiator ? ResponderToInitiator : InitiatorToResponder;

    public byte[] SealingSalt(RemoteSessionRole role)
        => role == RemoteSessionRole.Initiator ? InitiatorToResponderSalt : ResponderToInitiatorSalt;

    public byte[] OpeningSalt(RemoteSessionRole role)
        => role == RemoteSessionRole.Initiator ? ResponderToInitiatorSalt : InitiatorToResponderSalt;
}

/// <summary>
/// One value in the handshake parameter object. Restricted to strings and
/// integers on purpose: canonical serialization of floating point is where every
/// "both sides must produce identical bytes" scheme goes to die -- 0.1 has no
/// single canonical spelling. If a parameter ever needs a fraction, send it as a
/// scaled integer.
/// </summary>
public readonly struct RemoteParamValue : IEquatable<RemoteParamValue>
{
    private readonly string? _string;
    private readonly long _int;
    public bool IsString { get; }

    private RemoteParamValue(string? s, long i, bool isString)
    {
        _string = s; _int = i; IsString = isString;
    }

    public static RemoteParamValue String(string value) => new(value, 0, true);
    public static RemoteParamValue Int(long value) => new(null, value, false);

    public string AsString => IsString ? _string! : throw new InvalidOperationException("Not a string value");
    public long AsInt => IsString ? throw new InvalidOperationException("Not an integer value") : _int;

    public bool Equals(RemoteParamValue other)
        => IsString == other.IsString && (IsString ? _string == other._string : _int == other._int);
    public override bool Equals(object? obj) => obj is RemoteParamValue v && Equals(v);
    public override int GetHashCode() => IsString ? (_string?.GetHashCode() ?? 0) : _int.GetHashCode();
}

/// <summary>
/// The parameters both peers agree on, bound into the handshake transcript.
///
/// Canonical form is hand-rolled rather than delegated to System.Text.Json /
/// JSONEncoder: neither platform guarantees key ordering or escaping identical
/// to the other's, and a one-byte difference here surfaces as a key confirmation
/// failure that looks like a crypto bug rather than a serialization bug.
/// </summary>
public sealed class RemoteHandshakeParams
{
    private readonly Dictionary<string, RemoteParamValue> _values;

    public RemoteHandshakeParams() => _values = [];

    public RemoteHandshakeParams(IDictionary<string, RemoteParamValue> values)
        => _values = new Dictionary<string, RemoteParamValue>(values);

    public RemoteParamValue this[string key]
    {
        get => _values[key];
        set => _values[key] = value;
    }

    public bool TryGetValue(string key, out RemoteParamValue value) => _values.TryGetValue(key, out value);

    /// <summary>The parameters, for callers that have to serialize or copy them.</summary>
    /// <remarks>
    /// Read-only, and deliberately not the canonical form: CanonicalBytes is for
    /// the transcript and must never be reconstructed from this. Mirrors the
    /// Swift type's `values`.
    /// </remarks>
    public IReadOnlyDictionary<string, RemoteParamValue> Values => _values;

    /// <summary>A copy, so answering an invite cannot mutate what arrived.</summary>
    public RemoteHandshakeParams Clone() => new(_values);

    /// <summary>Plain values for JSON serialization.</summary>
    public Dictionary<string, object> ToDictionary()
    {
        var result = new Dictionary<string, object>(_values.Count);
        foreach (var (key, value) in _values)
        {
            result[key] = value.IsString ? value.AsString : value.AsInt;
        }
        return result;
    }

    /// <summary>
    /// Deterministic bytes for the transcript: keys sorted by UTF-8 byte order,
    /// no insignificant whitespace, minimal string escaping.
    /// </summary>
    public byte[] CanonicalBytes()
    {
        // Ordinal string comparison sorts by UTF-16 code unit, which differs
        // from UTF-8 byte order above the BMP. Sorting the encoded bytes keeps
        // this identical to the Swift side for every possible key.
        var keys = _values.Keys.ToList();
        keys.Sort(static (a, b) => CompareUtf8(a, b));

        var output = new MemoryStream();
        output.WriteByte((byte)'{');
        for (int i = 0; i < keys.Count; i++)
        {
            if (i > 0) output.WriteByte((byte)',');
            WriteCanonicalString(output, keys[i]);
            output.WriteByte((byte)':');
            var value = _values[keys[i]];
            if (value.IsString) WriteCanonicalString(output, value.AsString);
            else
            {
                byte[] digits = Encoding.UTF8.GetBytes(value.AsInt.ToString(System.Globalization.CultureInfo.InvariantCulture));
                output.Write(digits, 0, digits.Length);
            }
        }
        output.WriteByte((byte)'}');
        return output.ToArray();
    }

    private static int CompareUtf8(string a, string b)
    {
        byte[] x = Encoding.UTF8.GetBytes(a), y = Encoding.UTF8.GetBytes(b);
        int n = Math.Min(x.Length, y.Length);
        for (int i = 0; i < n; i++)
            if (x[i] != y[i]) return x[i].CompareTo(y[i]);
        return x.Length.CompareTo(y.Length);
    }

    /// <summary>
    /// JSON string escaping limited to exactly what RFC 8259 requires, so both
    /// platforms produce the same bytes. Notably we do NOT escape '/' or
    /// non-ASCII -- System.Text.Json escapes both by default and would silently
    /// diverge from Foundation.
    /// </summary>
    private static void WriteCanonicalString(MemoryStream output, string value)
    {
        output.WriteByte((byte)'"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '"':  WriteAscii(output, "\\\""); break;
                case '\\': WriteAscii(output, "\\\\"); break;
                case '\n': WriteAscii(output, "\\n"); break;
                case '\r': WriteAscii(output, "\\r"); break;
                case '\t': WriteAscii(output, "\\t"); break;
                case '\b': WriteAscii(output, "\\b"); break;
                case '\f': WriteAscii(output, "\\f"); break;
                default:
                    if (c < 0x20)
                    {
                        WriteAscii(output, "\\u" + ((int)c).ToString("x4"));
                    }
                    else
                    {
                        // Encode a surrogate pair as one code point. Encoding
                        // each half separately yields two U+FFFD replacement
                        // characters and silently diverges from the Swift side,
                        // which iterates unicode scalars.
                        int width = char.IsHighSurrogate(c)
                                    && i + 1 < value.Length
                                    && char.IsLowSurrogate(value[i + 1]) ? 2 : 1;
                        byte[] encoded = Encoding.UTF8.GetBytes(value.Substring(i, width));
                        output.Write(encoded, 0, encoded.Length);
                        i += width - 1;
                    }
                    break;
            }
        }
        output.WriteByte((byte)'"');
    }

    private static void WriteAscii(MemoryStream output, string ascii)
    {
        foreach (char c in ascii) output.WriteByte((byte)c);
    }
}
