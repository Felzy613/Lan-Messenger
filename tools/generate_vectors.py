#!/usr/bin/env python3
"""
Generate deterministic cross-platform crypto test vectors.

Output: test_vectors/known_good_exchange.json

The JSON file is consumed by the macOS (Swift/XCTest) and Windows (C#/xUnit)
unit test suites to verify that their X25519 + HKDF + AES-GCM implementations
produce the same results as the Python reference app.

Run from the repo root:
    pip install cryptography
    python tools/generate_vectors.py
"""

import base64
import json
import os
import struct
import sys
from pathlib import Path

# ---------------------------------------------------------------------------
# We need the same crypto primitives as main.py — import them directly.
# ---------------------------------------------------------------------------
try:
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import x25519
    from cryptography.hazmat.primitives.kdf.hkdf import HKDF
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM
except ImportError:
    sys.exit("ERROR: Install dependencies first:  pip install cryptography")


# ---------------------------------------------------------------------------
# Helpers matching main.py
# ---------------------------------------------------------------------------
def b64e(data: bytes) -> str:
    return base64.b64encode(data).decode("ascii")


def b64d(value: str) -> bytes:
    return base64.b64decode(value)


def derive_session_key(private_key: x25519.X25519PrivateKey,
                       peer_public_key: x25519.X25519PublicKey) -> bytes:
    shared = private_key.exchange(peer_public_key)
    return HKDF(
        algorithm=hashes.SHA256(),
        length=32,
        salt=None,
        info=b"lan-messenger",
    ).derive(shared)


def derive_history_key(private_key: x25519.X25519PrivateKey) -> bytes:
    raw = private_key.private_bytes(
        encoding=serialization.Encoding.Raw,
        format=serialization.PrivateFormat.Raw,
        encryption_algorithm=serialization.NoEncryption(),
    )
    return HKDF(
        algorithm=hashes.SHA256(),
        length=32,
        salt=None,
        info=b"lan-messenger-history",
    ).derive(raw)


def encrypt(key: bytes, nonce: bytes, plaintext: bytes, aad: bytes) -> bytes:
    return AESGCM(key).encrypt(nonce, plaintext, aad)


def decrypt(key: bytes, nonce: bytes, ciphertext: bytes, aad: bytes) -> bytes:
    return AESGCM(key).decrypt(nonce, ciphertext, aad)


def make_frame(packet: dict) -> bytes:
    body = json.dumps(packet, separators=(",", ":")).encode("utf-8")
    return struct.pack("!I", len(body)) + body


# ---------------------------------------------------------------------------
# Deterministic key material (fixed seeds so vectors never change)
# ---------------------------------------------------------------------------
# Alice = sender, Bob = receiver
ALICE_PRIVATE_BYTES = bytes.fromhex(
    "a0a1a2a3a4a5a6a7a8a9aaabacadaeafb0b1b2b3b4b5b6b7b8b9babbbcbdbebf"
)
BOB_PRIVATE_BYTES = bytes.fromhex(
    "b0b1b2b3b4b5b6b7b8b9babbbcbdbebfc0c1c2c3c4c5c6c7c8c9cacbcccdcecf"
)

# Fixed nonces — never do this in production; fine for test vectors
TEXT_NONCE = bytes.fromhex("010203040506070809101112")
FILE_NONCE = bytes.fromhex("131415161718191a1b1c1d1e")
HISTORY_NONCE = bytes.fromhex("2021222324252627282930313233")[:12]

# Fixed IDs
MESSAGE_ID = "aabbccddeeff00112233445566778899"
TRANSFER_ID = "11223344556677889900aabbccddeeff"


def main() -> None:
    out_dir = Path(__file__).parent.parent / "test_vectors"
    out_dir.mkdir(exist_ok=True)
    out_path = out_dir / "known_good_exchange.json"

    # ------------------------------------------------------------------
    # Build key objects
    # ------------------------------------------------------------------
    alice_priv = x25519.X25519PrivateKey.from_private_bytes(ALICE_PRIVATE_BYTES)
    bob_priv   = x25519.X25519PrivateKey.from_private_bytes(BOB_PRIVATE_BYTES)

    alice_pub = alice_priv.public_key()
    bob_pub   = bob_priv.public_key()

    alice_pub_raw = alice_pub.public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw)
    bob_pub_raw   = bob_pub.public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw)

    alice_priv_raw = ALICE_PRIVATE_BYTES
    bob_priv_raw   = BOB_PRIVATE_BYTES

    # Both sides must derive the same session key
    key_alice_to_bob = derive_session_key(alice_priv, bob_pub)
    key_bob_from_alice = derive_session_key(bob_priv, alice_pub)
    assert key_alice_to_bob == key_bob_from_alice, "ECDH keys must match"

    session_key = key_alice_to_bob

    # ------------------------------------------------------------------
    # Vector 1: text message (Alice → Bob)
    # ------------------------------------------------------------------
    plaintext_text = b"Hello, Bob! This is a test message."
    aad_text = MESSAGE_ID.encode("utf-8")
    ct_text = encrypt(session_key, TEXT_NONCE, plaintext_text, aad_text)

    text_packet = {
        "type": "text",
        "message_id": MESSAGE_ID,
        "timestamp": 1715000000.0,
        "sender": "Alice",
        "sender_public_key_b64": b64e(alice_pub_raw),
        "port": 54232,
        "nonce": b64e(TEXT_NONCE),
        "ciphertext": b64e(ct_text),
    }
    text_frame = make_frame(text_packet)

    # Verify Bob can decrypt
    recovered_text = decrypt(session_key, TEXT_NONCE, ct_text, aad_text)
    assert recovered_text == plaintext_text

    # ------------------------------------------------------------------
    # Vector 2: file_chunk (Alice → Bob, single chunk)
    # ------------------------------------------------------------------
    plaintext_chunk = b"binary file data goes here " * 100   # 2700 bytes
    aad_chunk = TRANSFER_ID.encode("utf-8")
    ct_chunk = encrypt(session_key, FILE_NONCE, plaintext_chunk, aad_chunk)

    file_chunk_packet = {
        "type": "file_chunk",
        "transfer_id": TRANSFER_ID,
        "sender": "Alice",
        "sender_public_key_b64": b64e(alice_pub_raw),
        "port": 54232,
        "nonce": b64e(FILE_NONCE),
        "ciphertext": b64e(ct_chunk),
    }
    file_chunk_frame = make_frame(file_chunk_packet)

    # Verify
    recovered_chunk = decrypt(session_key, FILE_NONCE, ct_chunk, aad_chunk)
    assert recovered_chunk == plaintext_chunk

    # ------------------------------------------------------------------
    # Vector 3: history encryption (Alice's local history key)
    # ------------------------------------------------------------------
    history_key = derive_history_key(alice_priv)
    plaintext_history = b'{"192.168.1.2":[{"sender":"Alice","text":"Hi","incoming":false,"timestamp":1715000000.0,"message_id":null,"status":"","read_receipt_sent":false}]}'
    ct_history = encrypt(history_key, HISTORY_NONCE, plaintext_history, b"history-v1")

    # Verify
    recovered_history = decrypt(history_key, HISTORY_NONCE, ct_history, b"history-v1")
    assert recovered_history == plaintext_history

    # ------------------------------------------------------------------
    # Compose output
    # ------------------------------------------------------------------
    vectors = {
        "_comment": (
            "Deterministic test vectors for LAN Messenger cross-platform crypto tests. "
            "Generated by tools/generate_vectors.py. "
            "Alice (sender) has ALICE_PRIVATE_BYTES; Bob (receiver) has BOB_PRIVATE_BYTES. "
            "All implementations must produce identical decrypt results."
        ),
        "keys": {
            "alice_private_b64":  b64e(alice_priv_raw),
            "alice_public_b64":   b64e(alice_pub_raw),
            "bob_private_b64":    b64e(bob_priv_raw),
            "bob_public_b64":     b64e(bob_pub_raw),
            "session_key_b64":    b64e(session_key),
            "alice_history_key_b64": b64e(history_key),
        },
        "text_message": {
            "_comment": "Alice → Bob. AAD = message_id UTF-8 bytes.",
            "message_id":       MESSAGE_ID,
            "plaintext_utf8":   plaintext_text.decode("utf-8"),
            "nonce_b64":        b64e(TEXT_NONCE),
            "ciphertext_b64":   b64e(ct_text),
            "aad_hex":          aad_text.hex(),
            "packet":           text_packet,
            "frame_hex":        text_frame.hex(),
        },
        "file_chunk": {
            "_comment": "Alice → Bob. AAD = transfer_id UTF-8 bytes.",
            "transfer_id":      TRANSFER_ID,
            "plaintext_b64":    b64e(plaintext_chunk),
            "nonce_b64":        b64e(FILE_NONCE),
            "ciphertext_b64":   b64e(ct_chunk),
            "aad_hex":          aad_chunk.hex(),
            "packet":           file_chunk_packet,
            "frame_hex":        file_chunk_frame.hex(),
        },
        "history": {
            "_comment": "Alice's local history. Key = HKDF(alice_private, info=lan-messenger-history). AAD = history-v1.",
            "plaintext_utf8":   plaintext_history.decode("utf-8"),
            "nonce_b64":        b64e(HISTORY_NONCE),
            "ciphertext_b64":   b64e(ct_history),
            "aad_hex":          b"history-v1".hex(),
            "file_json":        json.dumps({"nonce": b64e(HISTORY_NONCE), "ciphertext": b64e(ct_history)}),
        },
    }

    out_path.write_text(json.dumps(vectors, indent=2), encoding="utf-8")
    print(f"Written: {out_path}")
    print(f"  text ciphertext length : {len(ct_text)} bytes")
    print(f"  file chunk ct length   : {len(ct_chunk)} bytes")
    print(f"  history ct length      : {len(ct_history)} bytes")
    print("All internal assertions passed.")


if __name__ == "__main__":
    main()
