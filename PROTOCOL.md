# LAN Messenger Wire Protocol

Version: 1.0  
Reference implementation: `main.py` (Python/Tkinter app, v1.5.0)

This document is the authoritative specification for the LAN Messenger wire
protocol. All native implementations (macOS Swift, Windows C#) must conform to
it exactly so they interoperate with the Python reference app.

---

## Table of Contents

1. [Constants](#constants)
2. [Framing (TCP)](#framing-tcp)
3. [Discovery (UDP)](#discovery-udp)
4. [Packet Types](#packet-types)
   - [text](#text)
   - [typing](#typing)
   - [sent_receipt / read_receipt](#sent_receipt--read_receipt)
   - [file_start](#file_start)
   - [file_chunk](#file_chunk)
   - [file_end](#file_end)
5. [Cryptography](#cryptography)
6. [History File Format](#history-file-format)
7. [Validation Rules](#validation-rules)
8. [Config File Format](#config-file-format)

---

## Constants

| Name | Value |
|---|---|
| UDP discovery port | `54231` |
| TCP message port | `54232` |
| Multicast group | `239.255.42.99` |
| Multicast TTL | `1` |
| Discovery interval | `1.5 s` |
| Peer timeout | `7 s` (no heartbeat received) |
| File chunk size | `65536` bytes (64 KiB) |
| Max frame size | `52428800` bytes (50 MiB) |

---

## Framing (TCP)

All TCP communication uses a simple length-prefix framing protocol:

```
┌─────────────────────────┬──────────────────────────────┐
│  4-byte length (uint32) │  UTF-8 JSON body             │
│  big-endian             │  (length bytes)              │
└─────────────────────────┴──────────────────────────────┘
```

- The length field is an **unsigned 32-bit integer in big-endian byte order**
  (`struct.pack("!I", n)` in Python; `BinaryPrimitives.WriteUInt32BigEndian` in C#;
  `withUnsafeMutableBytes` + CFByteOrder in Swift).
- The body is the UTF-8 encoding of a JSON object.
- **Discard and close the connection** if the declared length is 0 or greater
  than 50 MiB (`52_428_800` bytes). Do not read the body in that case.
- Every packet is a single framed JSON object. There is no multiplexing; each
  logical packet occupies one frame.

### Python reference

```python
def send_frame(sock, payload):
    data = json.dumps(payload).encode("utf-8")
    sock.sendall(struct.pack("!I", len(data)))
    sock.sendall(data)

def recv_frame(sock):
    header = recv_exact(sock, 4)
    if not header:
        return None
    size = struct.unpack("!I", header)[0]
    if size <= 0 or size > 50 * 1024 * 1024:
        raise ValueError("Invalid frame size")
    payload = recv_exact(sock, size)
    if not payload:
        return None
    return json.loads(payload.decode("utf-8"))
```

---

## Discovery (UDP)

Discovery packets are **raw UTF-8 JSON** (no length prefix) sent and received on
UDP port `54231`.

### Sending

Every `1.5 s` each node broadcasts a `discovery` packet to:

1. `255.255.255.255:54231` — subnet broadcast
2. `239.255.42.99:54231` — multicast group
3. `x.x.x.255:54231` for each local IPv4 address (per-subnet broadcast)
4. Last-known IP of each saved contact (unicast; helps cross-subnet reach)
5. Current IP of each known peer (unicast)

The UDP socket is created with `SO_BROADCAST = 1` and
`IP_MULTICAST_TTL = 1`.

### Receiving

Bind to `0.0.0.0:54231` with `SO_REUSEADDR = 1` (and `SO_REUSEPORT` if
available). Join the multicast group via `IP_ADD_MEMBERSHIP` for
`239.255.42.99`.

On receipt of a `discovery` packet from a remote IP, immediately reply with a
`discovery_reply` packet sent directly to `{source_ip}:54231` (UDP, not TCP).

### Packet format

Both `discovery` and `discovery_reply` share the same JSON shape:

```json
{
  "type": "discovery",
  "username": "Alice",
  "port": 54232,
  "public_key_b64": "<base64url-safe? no — standard RFC 4648 base64>",
  "ips": ["192.168.1.42", "10.0.0.5"]
}
```

| Field | Type | Notes |
|---|---|---|
| `type` | string | `"discovery"` or `"discovery_reply"` |
| `username` | string | Display name |
| `port` | integer | TCP listen port (usually `54232`) |
| `public_key_b64` | string | Standard base64 (RFC 4648) of raw 32-byte X25519 public key |
| `ips` | array of strings | All local IPv4 addresses of the sender |

### Self-suppression

Discard any discovery packet whose `public_key_b64` equals your own, or whose
source IP is one of your own local IPs.

---

## Packet Types

All TCP packets share these common fields where applicable:

| Field | Present in | Notes |
|---|---|---|
| `type` | all | String identifying the packet type |
| `sender` | all except discovery | Display name of the sender |
| `sender_public_key_b64` | all except discovery | Standard base64 of sender's 32-byte X25519 public key |
| `port` | all except discovery | Sender's TCP listen port |

### text

Sends an encrypted text message.

```json
{
  "type": "text",
  "message_id": "a3f1b2c4d5e6f7a8b9c0d1e2f3a4b5c6",
  "timestamp": 1715000000.123,
  "sender": "Alice",
  "sender_public_key_b64": "<base64>",
  "port": 54232,
  "nonce": "<base64 of 12 random bytes>",
  "ciphertext": "<base64 of AES-256-GCM ciphertext + 16-byte tag>"
}
```

| Field | Type | Notes |
|---|---|---|
| `message_id` | string | 32 hex characters (`uuid4().hex`, no dashes) |
| `timestamp` | float | Unix epoch seconds with sub-second precision |
| `nonce` | string | Standard base64 of 12 random bytes |
| `ciphertext` | string | Standard base64 of `ciphertext ‖ tag` (AES-GCM tag appended) |

**AAD**: `message_id.encode("utf-8")` — the raw ASCII bytes of the 32-hex string.

**Plaintext**: UTF-8 encoded message text.

**On receipt**: decrypt, emit `sent_receipt` back to sender immediately.

---

### typing

Signals that the sender is or is not actively typing. Not encrypted.

```json
{
  "type": "typing",
  "active": true,
  "sender": "Alice",
  "sender_public_key_b64": "<base64>",
  "port": 54232
}
```

| Field | Type | Notes |
|---|---|---|
| `active` | boolean | `true` = user is typing; `false` = stopped |

---

### sent_receipt / read_receipt

Delivery acknowledgements. Not encrypted.

```json
{
  "type": "sent_receipt",
  "message_id": "a3f1b2c4d5e6f7a8b9c0d1e2f3a4b5c6",
  "sender": "Alice",
  "sender_public_key_b64": "<base64>",
  "port": 54232
}
```

- `sent_receipt`: sent by the **receiver** immediately on decrypting a `text`
  packet successfully.
- `read_receipt`: sent by the **receiver** when the user opens/views the
  conversation containing the message.

| Field | Type | Notes |
|---|---|---|
| `message_id` | string | The `message_id` from the original `text` packet |

---

### file_start

Opens a file transfer. Not encrypted (metadata only).

```json
{
  "type": "file_start",
  "transfer_id": "c1d2e3f4a5b6c7d8e9f0a1b2c3d4e5f6",
  "filename": "photo.jpg",
  "size": 1048576,
  "sender": "Alice",
  "sender_public_key_b64": "<base64>",
  "port": 54232
}
```

| Field | Type | Notes |
|---|---|---|
| `transfer_id` | string | 32 hex characters (`uuid4().hex`, no dashes) |
| `filename` | string | Must be sanitized by receiver (see [Validation Rules](#validation-rules)) |
| `size` | integer | Total file size in bytes |

---

### file_chunk

One chunk of encrypted file data. Each chunk is at most 64 KiB of plaintext.

```json
{
  "type": "file_chunk",
  "transfer_id": "c1d2e3f4a5b6c7d8e9f0a1b2c3d4e5f6",
  "sender": "Alice",
  "sender_public_key_b64": "<base64>",
  "port": 54232,
  "nonce": "<base64 of 12 random bytes>",
  "ciphertext": "<base64 of AES-256-GCM ciphertext + 16-byte tag>"
}
```

**AAD**: `transfer_id.encode("utf-8")` — the raw ASCII bytes of the 32-hex string.

A fresh 12-byte random nonce is generated for **each chunk**.

---

### file_end

Signals that all chunks have been sent.

```json
{
  "type": "file_end",
  "transfer_id": "c1d2e3f4a5b6c7d8e9f0a1b2c3d4e5f6",
  "sender": "Alice",
  "sender_public_key_b64": "<base64>",
  "port": 54232
}
```

On receipt: close and rename the temp file (`{transfer_id}_{filename}.part`)
to its final name, deduplicating if necessary.

---

## Cryptography

### Key Generation

Generate an **X25519** private key on first launch. The raw 32-byte
representation is the canonical form used everywhere.

- **Python**: `x25519.X25519PrivateKey.generate()`
- **macOS**: `Curve25519.KeyAgreement.PrivateKey()`
- **Windows**: BouncyCastle `X25519KeyPairGenerator`

Store the private key securely:

| Platform | Storage |
|---|---|
| Python | `~/.lan_messenger/config.json` → `private_key_b64` (plain base64 — insecure, native apps must migrate) |
| macOS | Keychain — service `com.dave.lanmessenger`, account `privateKey` |
| Windows | `%APPDATA%\LanMessenger\private.key.dpapi` (DPAPI-protected) |

### Shared Key Derivation (per-peer)

```
shared_secret = X25519(my_private_key, peer_public_key)   # raw 32 bytes
symmetric_key = HKDF-SHA256(
    ikm  = shared_secret,
    salt = b"" (empty),
    info = b"lan-messenger",
    len  = 32
)
```

### Encryption

```
nonce      = random(12)                          # fresh per message/chunk
tag_and_ct = AES-256-GCM.seal(
    key          = symmetric_key,
    nonce        = nonce,
    plaintext    = <message or file chunk>,
    aad          = <see per-packet AAD above>
)
```

AES-GCM tag size is **16 bytes** (standard). The ciphertext stored/transmitted
is `ciphertext_bytes ‖ tag_bytes` concatenated, then base64-encoded.

### Decryption

```
nonce      = base64_decode(nonce_b64)          # must be exactly 12 bytes
ct_and_tag = base64_decode(ciphertext_b64)    # last 16 bytes = tag
plaintext  = AES-256-GCM.open(
    key   = symmetric_key,
    nonce = nonce,
    ct    = ct_and_tag,
    aad   = <see per-packet AAD above>
)
```

Decryption must **raise / throw** on authentication failure (wrong key, wrong
AAD, or tampered ciphertext). Never silently accept a bad tag.

### History Key Derivation (local)

The history file is encrypted with a key derived from the **raw private key
bytes** (not a shared secret):

```
history_key = HKDF-SHA256(
    ikm  = raw_private_key_bytes (32 bytes),
    salt = b"" (empty),
    info = b"lan-messenger-history",
    len  = 32
)
```

AAD for history encryption/decryption: `b"history-v1"` (literal UTF-8 bytes).

---

## History File Format

File path:
- Python / macOS migration source: `~/.lan_messenger/history.enc`
- macOS native: `~/Library/Application Support/LanMessenger/history.enc`
- Windows native: `%APPDATA%\LanMessenger\history.enc`

Outer file (JSON):

```json
{
  "nonce": "<base64 of 12-byte nonce>",
  "ciphertext": "<base64 of AES-GCM ciphertext + 16-byte tag>"
}
```

Inner plaintext (JSON, compact — no extra whitespace):

```json
{
  "<peer_ip>": [
    {
      "sender": "Alice",
      "text": "Hello!",
      "incoming": true,
      "timestamp": 1715000000.123,
      "message_id": "a3f1b2c4d5e6f7a8b9c0d1e2f3a4b5c6",
      "status": "",
      "read_receipt_sent": false
    }
  ]
}
```

- The outer dict is keyed by **peer IP address** (not public key).
- Each conversation stores at most **200** most-recent entries.
- `incoming`: `true` = received, `false` = sent by this user.
- `status`: one of `""`, `"Sending"`, `"Sent"`, `"Queued"`, `"Failed"`, `"Read"`.
- `read_receipt_sent`: whether a read receipt has already been emitted for this
  entry (prevents duplicate sends on restart).

---

## Validation Rules

All implementations **must** enforce these rules. Silently drop or close on
violation — do not crash.

| Rule | Action on violation |
|---|---|
| Frame length ≤ 0 or > 50 MiB | Close connection |
| Missing `type` field | Drop packet |
| `type` not in known set | Drop packet |
| `sender_public_key_b64` equals own public key | Drop packet |
| `public_key_b64` (discovery) equals own | Drop packet |
| Nonce not exactly 12 bytes after base64-decode | Drop / decryption failure |
| `filename` containing path separators (`/`, `\`) or `..` | Sanitize: take only `Path(name).name`, strip leading/trailing whitespace, replace null bytes |
| `size` < 0 or > 2 GiB | Drop `file_start` packet |
| Ciphertext authentication failure (wrong tag) | Drop packet; do not deliver plaintext |
| Discovery from own IP | Drop packet |

### Filename sanitization (reference)

```python
def sanitize_filename(name: str) -> str:
    safe = Path(name).name.strip() or "file"
    return safe.replace("\x00", "")
```

The native apps must produce equivalent output. Any filename that resolves to
empty after sanitization becomes `"file"`.

**Important platform note:** On macOS/Linux, backslash (`\`) is **not** a path
separator (POSIX). A filename like `..\..\evil.exe` received from a Windows peer
is treated as a single filename component — the backslashes are kept. Only
forward slashes (`/`) are stripped as path separators. The Windows native app
must apply equivalent Windows path sanitization using `Path.GetFileName()` which
handles both separators.

Swift/macOS equivalent:

```swift
static func sanitizeFilename(_ name: String) -> String {
    let component = name.components(separatedBy: "/").last ?? ""
    let stripped = component.trimmingCharacters(in: .whitespaces)
                            .replacingOccurrences(of: "\0", with: "")
    return stripped.isEmpty ? "file" : stripped
}
```

---

## Config File Format

Python config (stored in `~/.lan_messenger/config.json`):

```json
{
  "username": "Alice",
  "private_key_b64": "<base64 of raw 32-byte X25519 private key>",
  "contacts": [
    {
      "public_key_b64": "<base64>",
      "username": "Bob",
      "last_ip": "192.168.1.55"
    }
  ],
  "hidden_conversations": ["192.168.1.55"],
  "pending_messages": [
    {
      "message_id": "...",
      "peer_public_key_b64": "...",
      "peer_username": "Bob",
      "text": "Hey!",
      "timestamp": 1715000000.0
    }
  ],
  "update_server_url": "https://example.com/lan-messenger-update.json",
  "inbox_dir": "/Users/alice/Downloads/LanMessenger"
}
```

Native apps **must not** store `private_key_b64` in a plain JSON file. On first
launch, if a Python config is found, prompt the user whether to import the
existing key into secure storage (Keychain / DPAPI) or generate a fresh one.

---

## Known Packet Set

The complete set of valid `type` values:

| Value | Transport | Direction |
|---|---|---|
| `discovery` | UDP | broadcast/multicast |
| `discovery_reply` | UDP | unicast reply |
| `text` | TCP | peer → peer |
| `typing` | TCP | peer → peer |
| `sent_receipt` | TCP | receiver → sender |
| `read_receipt` | TCP | receiver → sender |
| `file_start` | TCP | sender → receiver |
| `file_chunk` | TCP | sender → receiver |
| `file_end` | TCP | sender → receiver |

Any packet with a `type` value not in this table must be silently dropped.
