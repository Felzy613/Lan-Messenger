using LanMessenger.Core.Crypto;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LanMessenger.Tests;

// Guards the remote-desktop media handshake. Mirror of the macOS
// RemoteSessionCryptoTests.swift. Every test here corresponds to a failure mode
// that is either silent, catastrophic, or both:
//
//   * Swapped roles derive swapped keys, and the only symptom is "decryption
//     randomly fails" once video is already flowing.
//   * An unbound transcript lets a man in the middle downgrade negotiated
//     parameters without breaking the handshake at all.
//   * Canonical parameter bytes that differ by one byte across platforms turn
//     into a key confirmation failure that looks like a crypto bug rather than
//     a serialization bug.
//   * A nonce reused across a reconnect is catastrophic AES-GCM failure, not a
//     degradation.
//
// See PROTOCOL.md -> Remote Desktop -> Media Session Key Derivation.
[TestClass]
public class RemoteSessionCryptoTests
{
    private const string SessionId = "9f2c4a6e8b0d1f3a5c7e9b1d3f5a7c9e";

    private Key _initiatorStatic = null!;
    private Key _responderStatic = null!;
    private Key _initiatorEph    = null!;
    private Key _responderEph    = null!;

    [TestInitialize]
    public void Setup()
    {
        _initiatorStatic = NewKey();
        _responderStatic = NewKey();
        _initiatorEph    = NewKey();
        _responderEph    = NewKey();
    }

    [TestCleanup]
    public void Teardown()
    {
        _initiatorStatic?.Dispose();
        _responderStatic?.Dispose();
        _initiatorEph?.Dispose();
        _responderEph?.Dispose();
    }

    // Keys must be exportable so the tests can derive from both sides.
    private static Key NewKey() => new(
        KeyAgreementAlgorithm.X25519,
        new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

    private static Key ImportKey(string rawB64) => Key.Import(
        KeyAgreementAlgorithm.X25519,
        Convert.FromBase64String(rawB64),
        KeyBlobFormat.RawPrivateKey,
        new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

    private static string Pub(Key k) => Convert.ToBase64String(k.PublicKey.Export(KeyBlobFormat.RawPublicKey));

    private static RemoteHandshakeParams Params() => new(new Dictionary<string, RemoteParamValue>
    {
        ["protocol"] = RemoteParamValue.Int(1),
        ["video"]    = RemoteParamValue.String("h264"),
        ["display"]  = RemoteParamValue.Int(0),
    });

    private RemoteSessionKeys Derive(
        RemoteSessionRole role, string? sessionId = null, RemoteHandshakeParams? parameters = null)
    {
        string sid = sessionId ?? SessionId;
        var p = parameters ?? Params();
        return role == RemoteSessionRole.Initiator
            ? RemoteSessionCrypto.DeriveKeys(
                RemoteSessionRole.Initiator, _initiatorEph, _initiatorStatic,
                Pub(_responderEph), Pub(_responderStatic), sid, p)
            : RemoteSessionCrypto.DeriveKeys(
                RemoteSessionRole.Responder, _responderEph, _responderStatic,
                Pub(_initiatorEph), Pub(_initiatorStatic), sid, p);
    }

    private static byte[] Header(byte channel, byte flags, ulong sequence, ulong captureUs)
    {
        byte[] h = new byte[RemoteSessionCrypto.HeaderSize];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0), 100);
        h[4] = channel;
        h[5] = flags;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(h.AsSpan(6), sequence);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(h.AsSpan(14), captureUs);
        return h;
    }

    /// Asserts the action fails authentication. Deliberately accepts any
    /// CryptographicException subtype: AES-GCM on macOS raises
    /// AuthenticationTagMismatchException while other backends raise the base
    /// type, and MSTest's ThrowsException&lt;T&gt; matches exactly.
    private static void AssertFailsAuthentication(Action action, string message = "")
    {
        try
        {
            action();
        }
        catch (CryptographicException)
        {
            return;
        }
        Assert.Fail($"expected authentication to fail. {message}");
    }

    // ---- The core property -------------------------------------------------

    [TestMethod]
    public void BothPeersDeriveIdenticalKeys()
    {
        var a = Derive(RemoteSessionRole.Initiator);
        var b = Derive(RemoteSessionRole.Responder);

        CollectionAssert.AreEqual(a.Transcript, b.Transcript,
            "transcripts must match - this is the key confirmation value");
        CollectionAssert.AreEqual(a.InitiatorToResponder, b.InitiatorToResponder);
        CollectionAssert.AreEqual(a.ResponderToInitiator, b.ResponderToInitiator);
        CollectionAssert.AreEqual(a.InitiatorToResponderSalt, b.InitiatorToResponderSalt);
        CollectionAssert.AreEqual(a.ResponderToInitiatorSalt, b.ResponderToInitiatorSalt);
        Assert.AreEqual(4, a.InitiatorToResponderSalt.Length);
    }

    [TestMethod]
    public void DirectionalKeysDifferFromEachOther()
    {
        var keys = Derive(RemoteSessionRole.Initiator);
        CollectionAssert.AreNotEqual(keys.InitiatorToResponder, keys.ResponderToInitiator,
            "one key in both directions would let a reflected frame authenticate");
        CollectionAssert.AreNotEqual(keys.InitiatorToResponderSalt, keys.ResponderToInitiatorSalt);
    }

    [TestMethod]
    public void SealingKeySelectionIsRoleSymmetric()
    {
        var initiator = Derive(RemoteSessionRole.Initiator);
        var responder = Derive(RemoteSessionRole.Responder);

        // What the initiator seals with, the responder must open with.
        CollectionAssert.AreEqual(
            initiator.SealingKey(RemoteSessionRole.Initiator),
            responder.OpeningKey(RemoteSessionRole.Responder));
        CollectionAssert.AreEqual(
            initiator.OpeningKey(RemoteSessionRole.Initiator),
            responder.SealingKey(RemoteSessionRole.Responder));
        CollectionAssert.AreEqual(
            initiator.SealingSalt(RemoteSessionRole.Initiator),
            responder.OpeningSalt(RemoteSessionRole.Responder));
    }

    // ---- Role assignment ---------------------------------------------------

    [TestMethod]
    public void BothPeersClaimingInitiatorDeriveDifferentKeys()
    {
        // The bug this guards: if role is decided locally ("I started it")
        // rather than by the protocol, both peers can believe they are the
        // initiator. They then agree on nothing, and the failure appears as GCM
        // errors during video rather than at handshake time.
        var honest = Derive(RemoteSessionRole.Initiator);
        var confused = RemoteSessionCrypto.DeriveKeys(
            RemoteSessionRole.Initiator,            // wrong: responder claiming initiator
            _responderEph, _responderStatic,
            Pub(_initiatorEph), Pub(_initiatorStatic), SessionId, Params());

        CollectionAssert.AreNotEqual(honest.Transcript, confused.Transcript,
            "a role mismatch must be caught by key confirmation, not discovered mid-stream");
    }

    [TestMethod]
    public void RoleOpposite()
    {
        Assert.AreEqual(RemoteSessionRole.Responder, RemoteSessionRole.Initiator.Opposite());
        Assert.AreEqual(RemoteSessionRole.Initiator, RemoteSessionRole.Responder.Opposite());
    }

    // ---- Transcript binding ------------------------------------------------

    [TestMethod]
    public void ChangedParameterChangesTheKeys()
    {
        // This is the whole point of binding the transcript into HKDF's info: a
        // MITM that rewrites a negotiated parameter must break the handshake
        // rather than silently downgrade it.
        var a = Derive(RemoteSessionRole.Initiator);
        var tampered = Params();
        tampered["video"] = RemoteParamValue.String("h265");
        var b = Derive(RemoteSessionRole.Initiator, parameters: tampered);

        CollectionAssert.AreNotEqual(a.Transcript, b.Transcript);
        CollectionAssert.AreNotEqual(a.InitiatorToResponder, b.InitiatorToResponder);
    }

    [TestMethod]
    public void AddedParameterChangesTheKeys()
    {
        var a = Derive(RemoteSessionRole.Initiator);
        var extra = Params();
        extra["input"] = RemoteParamValue.Int(1);   // a future capability field
        var b = Derive(RemoteSessionRole.Initiator, parameters: extra);
        CollectionAssert.AreNotEqual(a.Transcript, b.Transcript);
    }

    [TestMethod]
    public void ChangedSessionIdChangesTheKeys()
    {
        var a = Derive(RemoteSessionRole.Initiator);
        var b = Derive(RemoteSessionRole.Initiator, sessionId: "00112233445566778899aabbccddeeff");
        CollectionAssert.AreNotEqual(a.Transcript, b.Transcript);
        CollectionAssert.AreNotEqual(a.InitiatorToResponder, b.InitiatorToResponder,
            "a reconnect must never reuse the previous session's keys");
    }

    [TestMethod]
    public void TranscriptsMatchIsCorrect()
    {
        byte[] a = [1, 2, 3, 4];
        Assert.IsTrue(RemoteSessionCrypto.TranscriptsMatch(a, [1, 2, 3, 4]));
        Assert.IsFalse(RemoteSessionCrypto.TranscriptsMatch(a, [1, 2, 3, 5]));
        Assert.IsFalse(RemoteSessionCrypto.TranscriptsMatch(a, [1, 2, 3]));
        Assert.IsFalse(RemoteSessionCrypto.TranscriptsMatch(a, []));
    }

    // ---- Canonical parameter bytes -----------------------------------------

    private static string Canonical(RemoteHandshakeParams p) => Encoding.UTF8.GetString(p.CanonicalBytes());

    [TestMethod]
    public void CanonicalBytesAreIndependentOfInsertionOrder()
    {
        // Dictionary iteration order is not stable, so without explicit sorting
        // two runs on the SAME machine can disagree, never mind two platforms.
        var a = new RemoteHandshakeParams();
        a["video"] = RemoteParamValue.String("h264");
        a["protocol"] = RemoteParamValue.Int(1);
        a["display"] = RemoteParamValue.Int(0);

        var b = new RemoteHandshakeParams();
        b["display"] = RemoteParamValue.Int(0);
        b["protocol"] = RemoteParamValue.Int(1);
        b["video"] = RemoteParamValue.String("h264");

        Assert.AreEqual(Canonical(a), Canonical(b));
    }

    [TestMethod]
    public void CanonicalBytesShape()
    {
        var p = new RemoteHandshakeParams(new Dictionary<string, RemoteParamValue>
        {
            ["b"] = RemoteParamValue.Int(2),
            ["a"] = RemoteParamValue.String("x"),
        });
        Assert.AreEqual("{\"a\":\"x\",\"b\":2}", Canonical(p));
    }

    [TestMethod]
    public void CanonicalBytesEmptyParams()
        => Assert.AreEqual("{}", Canonical(new RemoteHandshakeParams()));

    [TestMethod]
    public void CanonicalBytesEscapingMatchesRfc8259Minimum()
    {
        // System.Text.Json escapes "/" and non-ASCII by default and Foundation
        // escapes "/" too. Any divergence here is a cross-platform key
        // confirmation failure, so the escaping is hand-rolled and pinned.
        var p = new RemoteHandshakeParams(new Dictionary<string, RemoteParamValue>
        {
            ["k"] = RemoteParamValue.String("a/b\"c\\d\ne"),
        });
        Assert.AreEqual("{\"k\":\"a/b\\\"c\\\\d\\ne\"}", Canonical(p));
    }

    [TestMethod]
    public void CanonicalBytesPreservesNonAsciiVerbatim()
    {
        var p = new RemoteHandshakeParams(new Dictionary<string, RemoteParamValue>
        {
            ["k"] = RemoteParamValue.String("café"),
        });
        Assert.AreEqual("{\"k\":\"café\"}", Canonical(p));
    }

    [TestMethod]
    public void CanonicalBytesEncodesSurrogatePairsAsOneCodePoint()
    {
        // Encoding each UTF-16 half separately yields two U+FFFD replacement
        // characters, which would silently diverge from Swift's unicodeScalars
        // iteration. Pinned by comparing against the framework's own encoder.
        var p = new RemoteHandshakeParams(new Dictionary<string, RemoteParamValue>
        {
            ["k"] = RemoteParamValue.String("a\U0001F600b"),
        });
        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes("{\"k\":\"a\U0001F600b\"}"),
            p.CanonicalBytes());
    }

    [TestMethod]
    public void CanonicalBytesEscapesControlCharacters()
    {
        var p = new RemoteHandshakeParams(new Dictionary<string, RemoteParamValue>
        {
            ["k"] = RemoteParamValue.String("a\u0001b"),
        });
        Assert.AreEqual("{\"k\":\"a\\u0001b\"}", Canonical(p));
    }

    [TestMethod]
    public void CanonicalBytesNegativeInteger()
    {
        var p = new RemoteHandshakeParams(new Dictionary<string, RemoteParamValue>
        {
            ["k"] = RemoteParamValue.Int(-7),
        });
        Assert.AreEqual("{\"k\":-7}", Canonical(p));
    }

    // ---- Nonce construction ------------------------------------------------

    [TestMethod]
    public void NonceIsSaltPlusBigEndianSequence()
    {
        byte[] n = RemoteSessionCrypto.Nonce([0xAA, 0xBB, 0xCC, 0xDD], 1);
        Assert.AreEqual(12, n.Length);
        CollectionAssert.AreEqual(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0, 0, 0, 0, 0, 0, 0, 1 }, n);
    }

    [TestMethod]
    public void NonceIsUniquePerSequence()
    {
        var seen = new HashSet<string>();
        for (ulong seq = 0; seq < 1000; seq++)
            Assert.IsTrue(seen.Add(Convert.ToBase64String(RemoteSessionCrypto.Nonce([1, 2, 3, 4], seq))),
                "counter nonces must never collide within a session");
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void NonceRejectsWrongSaltLength() => RemoteSessionCrypto.Nonce([1, 2, 3], 0);

    // ---- Frame seal / open -------------------------------------------------

    [TestMethod]
    public void SealOpenRoundTrip()
    {
        var keys = Derive(RemoteSessionRole.Initiator);
        byte[] h = Header(1, 1, 42, 123_456);
        byte[] payload = Encoding.UTF8.GetBytes("a frame of h264");

        byte[] sealed_ = RemoteSessionCrypto.Seal(payload, h,
            keys.SealingKey(RemoteSessionRole.Initiator),
            keys.SealingSalt(RemoteSessionRole.Initiator), 42);

        byte[] opened = RemoteSessionCrypto.Open(sealed_, h,
            keys.OpeningKey(RemoteSessionRole.Responder),
            keys.OpeningSalt(RemoteSessionRole.Responder), 42);

        CollectionAssert.AreEqual(payload, opened);
        Assert.AreEqual(payload.Length + RemoteSessionCrypto.TagSize, sealed_.Length);
    }

    [TestMethod]
    public void HeaderLengthMatchesProtocol()
        => Assert.AreEqual(RemoteSessionCrypto.HeaderSize, Header(0, 0, 0, 0).Length);

    [TestMethod]
    public void TamperedHeaderFailsAuthentication()
    {
        // The AAD covers the framing, including the length prefix, so a
        // truncation or a channel swap fails the tag check rather than being
        // delivered to the wrong sub-channel.
        var keys = Derive(RemoteSessionRole.Initiator);
        byte[] h = Header(1, 1, 7, 99);
        byte[] sealed_ = RemoteSessionCrypto.Seal(Encoding.UTF8.GetBytes("x"), h,
            keys.SealingKey(RemoteSessionRole.Initiator),
            keys.SealingSalt(RemoteSessionRole.Initiator), 7);

        foreach (int index in new[] { 0, 4, 5, 12, 21 })
        {
            byte[] tampered = (byte[])h.Clone();
            tampered[index] ^= 0x01;
            AssertFailsAuthentication(() => RemoteSessionCrypto.Open(
                sealed_, tampered,
                keys.OpeningKey(RemoteSessionRole.Responder),
                keys.OpeningSalt(RemoteSessionRole.Responder), 7),
                $"tampering with header byte {index} must fail authentication");
        }
    }

    [TestMethod]
    public void WrongSequenceFailsAuthentication()
    {
        // A replayed frame carries its original sequence in the header, so
        // opening it under any other counter fails. Combined with the
        // strictly-increasing check in the transport, that closes replay.
        var keys = Derive(RemoteSessionRole.Initiator);
        byte[] h = Header(1, 0, 5, 1);
        byte[] sealed_ = RemoteSessionCrypto.Seal(Encoding.UTF8.GetBytes("x"), h,
            keys.SealingKey(RemoteSessionRole.Initiator),
            keys.SealingSalt(RemoteSessionRole.Initiator), 5);

        AssertFailsAuthentication(() => RemoteSessionCrypto.Open(
            sealed_, h,
            keys.OpeningKey(RemoteSessionRole.Responder),
            keys.OpeningSalt(RemoteSessionRole.Responder), 6));
    }

    [TestMethod]
    public void WrongDirectionKeyFails()
    {
        var keys = Derive(RemoteSessionRole.Initiator);
        byte[] h = Header(1, 0, 1, 1);
        byte[] sealed_ = RemoteSessionCrypto.Seal(Encoding.UTF8.GetBytes("x"), h,
            keys.SealingKey(RemoteSessionRole.Initiator),
            keys.SealingSalt(RemoteSessionRole.Initiator), 1);

        // Open with the key the initiator uses for INBOUND frames. A frame the
        // initiator sealed must not authenticate under it - otherwise a peer
        // could reflect our own frames back at us and have them accepted.
        AssertFailsAuthentication(() => RemoteSessionCrypto.Open(
            sealed_, h,
            keys.OpeningKey(RemoteSessionRole.Initiator),
            keys.OpeningSalt(RemoteSessionRole.Initiator), 1));
    }

    [TestMethod]
    public void TruncatedBodyIsRejected()
    {
        var keys = Derive(RemoteSessionRole.Initiator);
        AssertFailsAuthentication(() => RemoteSessionCrypto.Open(
            new byte[] { 1, 2, 3 }, Header(1, 0, 1, 1),
            keys.OpeningKey(RemoteSessionRole.Responder),
            keys.OpeningSalt(RemoteSessionRole.Responder), 1),
            "a body shorter than the GCM tag must be rejected before any crypto");
    }

    [TestMethod]
    public void EmptyPayloadRoundTrips()
    {
        // Keepalives and acks are legitimately empty.
        var keys = Derive(RemoteSessionRole.Initiator);
        byte[] h = Header(0, 0, 1, 1);
        byte[] sealed_ = RemoteSessionCrypto.Seal([], h,
            keys.SealingKey(RemoteSessionRole.Initiator),
            keys.SealingSalt(RemoteSessionRole.Initiator), 1);
        byte[] opened = RemoteSessionCrypto.Open(sealed_, h,
            keys.OpeningKey(RemoteSessionRole.Responder),
            keys.OpeningSalt(RemoteSessionRole.Responder), 1);
        Assert.AreEqual(0, opened.Length);
    }

    // ---- Input validation --------------------------------------------------

    [TestMethod]
    public void RejectsMalformedPublicKey()
    {
        foreach (string bad in new[] { "", "not-base64!!", Convert.ToBase64String(new byte[31]) })
        {
            Assert.ThrowsException<ArgumentException>(() => RemoteSessionCrypto.DeriveKeys(
                RemoteSessionRole.Initiator, _initiatorEph, _initiatorStatic,
                bad, Pub(_responderStatic), SessionId, Params()),
                $"must reject public key: '{bad}'");
        }
    }

    [TestMethod]
    public void RejectsMalformedSessionId()
    {
        foreach (string bad in new[]
        {
            "", "short", "9F2C4A6E8B0D1F3A5C7E9B1D3F5A7C9E", "zz2c4a6e8b0d1f3a5c7e9b1d3f5a7c9e",
        })
        {
            Assert.ThrowsException<ArgumentException>(() => RemoteSessionCrypto.DeriveKeys(
                RemoteSessionRole.Initiator, _initiatorEph, _initiatorStatic,
                Pub(_responderEph), Pub(_responderStatic), bad, Params()),
                $"must reject session id: '{bad}'");
        }
    }

    // ---- Session ids and fingerprints --------------------------------------

    [TestMethod]
    public void NewSessionIdShape()
    {
        for (int i = 0; i < 50; i++)
        {
            string sid = RemoteSessionCrypto.NewSessionId();
            Assert.IsTrue(RemoteSessionCrypto.IsSessionId(sid), $"bad session id: {sid}");
            Assert.AreEqual(32, sid.Length);
        }
    }

    [TestMethod]
    public void IsSessionIdRejectsUppercaseAndWrongLength()
    {
        Assert.IsFalse(RemoteSessionCrypto.IsSessionId("9F2C4A6E8B0D1F3A5C7E9B1D3F5A7C9E"));
        Assert.IsFalse(RemoteSessionCrypto.IsSessionId("9f2c4a6e8b0d1f3a5c7e9b1d3f5a7c9"));
        Assert.IsFalse(RemoteSessionCrypto.IsSessionId("9f2c4a6e-8b0d-1f3a-5c7e-9b1d3f5a7c9e"));
        Assert.IsTrue(RemoteSessionCrypto.IsSessionId("9f2c4a6e8b0d1f3a5c7e9b1d3f5a7c9e"));
    }

    [TestMethod]
    public void FingerprintShapeAndStability()
    {
        string b64 = Pub(_responderStatic);
        string? fp = RemoteSessionCrypto.Fingerprint(b64);
        Assert.IsNotNull(fp);
        Assert.AreEqual(19, fp!.Length, "four groups of four hex plus three spaces");
        Assert.AreEqual(4, fp.Split(' ').Length);
        Assert.AreEqual(fp, RemoteSessionCrypto.Fingerprint(b64));
    }

    [TestMethod]
    public void FingerprintDiffersPerKeyAndRejectsGarbage()
    {
        Assert.AreNotEqual(
            RemoteSessionCrypto.Fingerprint(Pub(_initiatorStatic)),
            RemoteSessionCrypto.Fingerprint(Pub(_responderStatic)));
        Assert.IsNull(RemoteSessionCrypto.Fingerprint("nope"));
        Assert.IsNull(RemoteSessionCrypto.Fingerprint(Convert.ToBase64String(new byte[31])));
    }

    // ---- Cross-platform vector ---------------------------------------------

    // The only test here that can catch the two platforms drifting apart.
    // Everything else verifies internal consistency, which both implementations
    // can satisfy independently while still disagreeing on the wire - the
    // canonical parameter bytes and the HKDF input order are exactly the kind of
    // detail two people implement plausibly and differently.
    //
    // Generated by the macOS implementation; RemoteSessionCryptoTests.swift
    // asserts the same file. If you change the handshake, regenerate it and
    // update BOTH copies (CLAUDE.md -> check both known_good vectors).
    [TestMethod]
    public void MatchesCrossPlatformVector()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(VectorPath()));
        var v = doc.RootElement;
        string S(string key) => v.GetProperty(key).GetString()!;

        var parameters = Params();
        Assert.AreEqual(S("params_canonical"), Canonical(parameters),
            "canonical parameter bytes drifted - both platforms must produce these exact bytes");

        // Derive from each side independently; both must match the vector.
        foreach (var role in new[] { RemoteSessionRole.Initiator, RemoteSessionRole.Responder })
        {
            bool init = role == RemoteSessionRole.Initiator;
            using var eph = ImportKey(S(init ? "initiator_eph_priv_b64" : "responder_eph_priv_b64"));
            using var sta = ImportKey(S(init ? "initiator_static_priv_b64" : "responder_static_priv_b64"));

            var keys = RemoteSessionCrypto.DeriveKeys(
                role, eph, sta,
                S(init ? "responder_eph_pub_b64" : "initiator_eph_pub_b64"),
                S(init ? "responder_static_pub_b64" : "initiator_static_pub_b64"),
                S("session_id"), parameters);

            Assert.AreEqual(S("transcript_b64"), Convert.ToBase64String(keys.Transcript),
                $"transcript mismatch deriving as {role}");
            Assert.AreEqual(S("key_i2r_b64"), Convert.ToBase64String(keys.InitiatorToResponder),
                $"key_i2r mismatch as {role}");
            Assert.AreEqual(S("key_r2i_b64"), Convert.ToBase64String(keys.ResponderToInitiator),
                $"key_r2i mismatch as {role}");
            Assert.AreEqual(S("salt_i2r_b64"), Convert.ToBase64String(keys.InitiatorToResponderSalt));
            Assert.AreEqual(S("salt_r2i_b64"), Convert.ToBase64String(keys.ResponderToInitiatorSalt));
        }

        // And the sealed frame must be byte-identical: AES-GCM with a counter
        // nonce is deterministic, so this pins the nonce layout and the AAD.
        using var iEph = ImportKey(S("initiator_eph_priv_b64"));
        using var iSta = ImportKey(S("initiator_static_priv_b64"));
        var sessionKeys = RemoteSessionCrypto.DeriveKeys(
            RemoteSessionRole.Initiator, iEph, iSta,
            S("responder_eph_pub_b64"), S("responder_static_pub_b64"),
            S("session_id"), parameters);

        byte[] header = Convert.FromBase64String(S("frame_header_b64"));
        ulong sequence = (ulong)v.GetProperty("frame_sequence").GetInt64();
        byte[] payload = Encoding.UTF8.GetBytes(S("frame_payload_utf8"));

        byte[] sealed_ = RemoteSessionCrypto.Seal(payload, header,
            sessionKeys.SealingKey(RemoteSessionRole.Initiator),
            sessionKeys.SealingSalt(RemoteSessionRole.Initiator), sequence);
        Assert.AreEqual(S("frame_sealed_b64"), Convert.ToBase64String(sealed_),
            "sealed frame differs - nonce layout or AAD drifted");

        // Round-trip the vector's own ciphertext, not just our reproduction of
        // it, so a decoder-side regression is caught too.
        byte[] opened = RemoteSessionCrypto.Open(
            Convert.FromBase64String(S("frame_sealed_b64")), header,
            sessionKeys.OpeningKey(RemoteSessionRole.Responder),
            sessionKeys.OpeningSalt(RemoteSessionRole.Responder), sequence);
        CollectionAssert.AreEqual(payload, opened);
    }

    /// Output directory under vstest, source-relative when running from a shim.
    private static string VectorPath()
    {
        const string name = "remote_handshake_vector.json";
        if (File.Exists(name)) return name;
        string beside = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(beside)) return beside;
        return Path.Combine(
            Path.GetDirectoryName(typeof(RemoteSessionCryptoTests).Assembly.Location) ?? ".", name);
    }
}
