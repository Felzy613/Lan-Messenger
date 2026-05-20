# LAN Messenger Protocol Specification

Protocol version: 1.1  
Current implementations: macOS Swift/SwiftUI and Windows C#/WinUI 3  
Compatibility target: native clients must remain interoperable with each other
and with any legacy client that implements the version 1.0 packet set.

This file is authoritative for wire format, encryption, validation, and local
persistence compatibility. Update it before or with any behavior change that
crosses a platform boundary.

## Table Of Contents

1. [Design Goals](#design-goals)
2. [Constants](#constants)
3. [Transport](#transport)
4. [Discovery](#discovery)
5. [Framing](#framing)
6. [Packet Fields](#packet-fields)
7. [Packet Types](#packet-types)
8. [Cryptography](#cryptography)
9. [History Format](#history-format)
10. [Config Format](#config-format)
11. [Validation Rules](#validation-rules)
12. [Operational Flows](#operational-flows)
13. [Compatibility Notes](#compatibility-notes)

## Design Goals

- Peer-to-peer LAN messaging with no server dependency.
- Zero-config discovery across common home and office networks.
- Cross-platform compatibility between macOS and Windows.
- End-to-end encryption for message text and file chunks.
- Local encrypted history that survives restarts.
- Backward-compatible packet evolution: new optional fields must be safe for
  older clients to ignore.

## Constants

| Name | Value |
|---|---|
| UDP discovery port | `54231` |
| TCP message/file port | `54232` |
| Multicast group | `239.255.42.99` |
| Multicast TTL | `1` |
| Discovery interval | `1.5 s` |
| Peer timeout | macOS UI: `20 s`; Windows UI: `7 s` |
| TCP frame max size | `52_428_800` bytes (`50 MiB`) |
| File chunk plaintext size | `65_536` bytes (`64 KiB`) |
| Max advertised file size | `2 GiB` |
| History cap | `200` messages per peer IP |
| AES-GCM nonce size | `12` bytes |
| AES-GCM tag size | `16` bytes |

The peer-timeout difference is UI state only. It does not change packet format.

## Transport

LAN Messenger uses two fixed ports:

- UDP `54231` for discovery datagrams.
- TCP `54232` for all framed message, receipt, typing, and file-transfer packets.

There is no server, rendezvous service, account system, or relay. A peer's
current LAN IP address is learned from discovery or saved contact state.

## Discovery

Discovery packets are raw UTF-8 JSON datagrams sent to UDP port `54231`.
They are never TCP-framed.

### Discovery Targets

Each running client periodically sends the same discovery payload to:

- each interface's directed subnet broadcast address, for example
  `192.168.1.255`;
- multicast group `239.255.42.99`;
- limited broadcast `255.255.255.255`;
- unicast hints such as current peer IPs or saved last-known contact IPs.

Discovery uses every eligible IPv4 interface rather than relying on the OS
default route. This matters on machines with VPN, Ethernet, Wi-Fi, Hyper-V, WSL,
or other virtual adapters.

### Discovery Packet

`discovery` and `discovery_reply` have the same shape:

```json
{
  "type": "discovery",
  "username": "Alice",
  "port": 54232,
  "public_key_b64": "base64-of-32-byte-x25519-public-key",
  "ips": ["192.168.1.42", "10.0.0.5"]
}
```

| Field | Type | Required | Notes |
|---|---|---|---|
| `type` | string | yes | `discovery` or `discovery_reply` |
| `username` | string | yes | Sender display name |
| `port` | integer | yes | Sender TCP port, normally `54232` |
| `public_key_b64` | string | yes | Standard base64 of raw 32-byte X25519 public key |
| `ips` | array of strings | yes | Sender's local IPv4 addresses |

### Discovery Reply

When a client receives `discovery`, it sends exactly one `discovery_reply` back
to `{source_ip}:54231` over UDP. Do not send discovery replies to TCP port
`54232`. Do not reply to `discovery_reply`; that creates a ping-pong loop.

### Self Suppression

Drop discovery packets if:

- source IP is one of this machine's current local IPv4 addresses;
- `public_key_b64` equals this client's public key;
- `public_key_b64` is empty or malformed.

## Framing

All TCP packets use the same length-prefixed frame:

```text
+-----------------------------+---------------------------+
| 4-byte uint32 big-endian len | UTF-8 JSON body           |
+-----------------------------+---------------------------+
```

Rules:

- Length is an unsigned 32-bit integer in network byte order.
- Body is exactly `length` bytes of UTF-8 JSON.
- Reject and close on length `<= 0` or `> 50 MiB`.
- One logical packet equals one frame.
- No multiplexing or stream-level compression exists.

## Packet Fields

Most TCP packets include:

| Field | Type | Notes |
|---|---|---|
| `type` | string | Packet discriminator |
| `sender` | string | Display name |
| `sender_public_key_b64` | string | Standard base64 raw X25519 public key |
| `port` | integer | Sender's TCP port |

Discovery uses `public_key_b64` rather than `sender_public_key_b64`.

## Packet Types

### text

Encrypted text message.

```json
{
  "type": "text",
  "message_id": "a3f1b2c4d5e6f7a8b9c0d1e2f3a4b5c6",
  "timestamp": 1715000000.123,
  "sender": "Alice",
  "sender_public_key_b64": "base64-public-key",
  "port": 54232,
  "nonce": "base64-12-byte-nonce",
  "ciphertext": "base64-ciphertext-plus-tag",
  "reply_to_message_id": "optional-32-hex-message-id",
  "reply_to_preview": "optional preview",
  "reply_to_sender": "optional sender label"
}
```

| Field | Type | Required | Notes |
|---|---|---|---|
| `message_id` | string | yes | 32 lowercase hex chars, no dashes |
| `timestamp` | number | yes | Unix epoch seconds |
| `nonce` | string | yes | Base64 of 12 bytes |
| `ciphertext` | string | yes | Base64 of AES-GCM ciphertext plus 16-byte tag |
| `reply_to_message_id` | string | no | Native reply extension |
| `reply_to_preview` | string | no | Native reply extension, unencrypted metadata |
| `reply_to_sender` | string | no | Native reply extension, unencrypted metadata |

AAD: raw UTF-8 bytes of `message_id`.  
Plaintext: UTF-8 message text.

On successful decrypt, the receiver appends history and sends `sent_receipt`.
When the user opens the conversation, the receiver sends `read_receipt` for
incoming unread messages.

### typing

Unencrypted typing indicator.

```json
{
  "type": "typing",
  "active": true,
  "sender": "Alice",
  "sender_public_key_b64": "base64-public-key",
  "port": 54232
}
```

| Field | Type | Required | Notes |
|---|---|---|---|
| `active` | boolean | yes | `true` when typing, `false` when stopped |

Clients throttle repeated `active=true` sends to avoid flooding.

### sent_receipt

Unencrypted delivery acknowledgement from receiver to sender.

```json
{
  "type": "sent_receipt",
  "message_id": "a3f1b2c4d5e6f7a8b9c0d1e2f3a4b5c6",
  "sender": "Alice",
  "sender_public_key_b64": "base64-public-key",
  "port": 54232
}
```

`sent_receipt` means the receiver successfully decrypted the original `text`
packet. UI maps this to `Delivered`.

### read_receipt

Same shape as `sent_receipt`, but `type` is `read_receipt`.

`read_receipt` means the receiver opened/read the conversation. UI maps this to
`Read`. Clients should send it once per incoming message and persist
`read_receipt_sent` to avoid duplicate receipts after restart.

### file_start

Starts an encrypted file transfer. Metadata is not encrypted.

```json
{
  "type": "file_start",
  "transfer_id": "c1d2e3f4a5b6c7d8e9f0a1b2c3d4e5f6",
  "filename": "photo.jpg",
  "size": 1048576,
  "sender": "Alice",
  "sender_public_key_b64": "base64-public-key",
  "port": 54232
}
```

| Field | Type | Required | Notes |
|---|---|---|---|
| `transfer_id` | string | yes | 32 lowercase hex chars, no dashes |
| `filename` | string | yes | Receiver sanitizes before writing |
| `size` | integer | yes | Total plaintext bytes, `0..2 GiB` |

Receiver creates:

```text
{inbox_dir}/{transfer_id}_{sanitized_filename}.part
```

### file_chunk

Encrypted chunk for the active transfer.

```json
{
  "type": "file_chunk",
  "transfer_id": "c1d2e3f4a5b6c7d8e9f0a1b2c3d4e5f6",
  "sender": "Alice",
  "sender_public_key_b64": "base64-public-key",
  "port": 54232,
  "nonce": "base64-12-byte-nonce",
  "ciphertext": "base64-ciphertext-plus-tag"
}
```

AAD: raw UTF-8 bytes of `transfer_id`.  
Plaintext: up to 64 KiB of file bytes.

Chunks do not carry sequence numbers. Receivers must preserve TCP arrival order
while decrypting and writing. The current macOS app uses a serial dispatch queue;
the Windows app uses a per-transfer channel with a single reader.

### file_end

Completes a file transfer.

```json
{
  "type": "file_end",
  "transfer_id": "c1d2e3f4a5b6c7d8e9f0a1b2c3d4e5f6",
  "sender": "Alice",
  "sender_public_key_b64": "base64-public-key",
  "port": 54232
}
```

The receiver closes the temp file and renames it to a deduplicated final path.
If `photo.jpg` exists, try `photo_1.jpg` through `photo_999.jpg`, then use an
8-hex fallback suffix.

## Cryptography

### Key Agreement

Each client owns a persistent X25519 keypair.

- macOS stores the raw private key in Keychain service
  `com.dave.lanmessenger`, account `privateKey`.
- Windows stores the raw private key encrypted with DPAPI at
  `%APPDATA%\LanMessenger\private.key.dpapi`.

Public keys are sent as standard RFC 4648 base64 of the raw 32-byte X25519
public key.

### Session Key Derivation

For message and file content:

```text
shared_secret = X25519(my_private, peer_public)
session_key = HKDF-SHA256(
  ikm = shared_secret,
  salt = empty bytes,
  info = "lan-messenger",
  length = 32
)
```

The empty salt is intentional. Do not replace it with a random salt unless the
packet format is versioned to carry that salt.

### Message/File Encryption

```text
nonce = random 12 bytes
sealed = AES-256-GCM(session_key, nonce, plaintext, aad)
wire_ciphertext = ciphertext || 16-byte tag
```

Transmit:

- `nonce`: base64(nonce)
- `ciphertext`: base64(wire_ciphertext)

### History Key Derivation

Local history encryption does not use peer key agreement.

```text
history_key = HKDF-SHA256(
  ikm = raw 32-byte local private key,
  salt = empty bytes,
  info = "lan-messenger-history",
  length = 32
)
```

History AAD: raw UTF-8 bytes of `history-v1`.

## History Format

History is stored as encrypted JSON at:

- macOS: `~/Library/Application Support/LanMessenger/history.enc`
- Windows: `%APPDATA%\LanMessenger\history.enc`

Outer encrypted file:

```json
{
  "nonce": "base64-12-byte-nonce",
  "ciphertext": "base64-ciphertext-plus-tag"
}
```

Inner plaintext JSON:

```json
{
  "192.168.1.42": [
    {
      "sender": "Alice",
      "text": "Hello",
      "incoming": true,
      "timestamp": 1715000000.123,
      "message_id": "a3f1b2c4d5e6f7a8b9c0d1e2f3a4b5c6",
      "status": "",
      "read_receipt_sent": false,
      "reply_to_message_id": null,
      "reply_to_preview": null,
      "reply_to_sender": null
    }
  ]
}
```

Rules:

- Top-level keys are peer IP addresses, not public keys.
- Each peer list is capped to 200 entries.
- File messages are represented as `text` values prefixed with `__FILE__:`.
- Reply fields are optional and must decode cleanly if absent.
- Status strings are UI lifecycle values: `Sending`, `Queued`, `Sent`,
  `Delivered`, `Read`, `Failed`, or empty for incoming/no-status.

## Config Format

Config is plain JSON, but private keys are not stored there.

macOS path:

```text
~/Library/Application Support/LanMessenger/config.json
```

Windows path:

```text
%APPDATA%\LanMessenger\config.json
```

Fields used by one or both platforms:

| Field | Type | Notes |
|---|---|---|
| `username` | string | Local display name |
| `contacts` | array | Saved contacts by public key |
| `hidden_conversations` | array of strings | Peer IPs hidden after delete |
| `archived_conversations` | array of strings | Peer IPs in archive |
| `pending_messages` | array | Offline text queue |
| `pending_files` | array | Offline file queue |
| `update_server_url` | string | Legacy/custom update source field |
| `update_repo` | string | GitHub repo used by native update checks |
| `last_update_check` | number | Unix seconds |
| `inbox_dir` | string | Empty means platform default |
| `photo_b64` | string | Optional per-contact avatar image |
| `hide_from_dock` | boolean | macOS only |
| `launch_at_login` | boolean | macOS only |
| `start_in_tray` | boolean | Windows only |
| `close_to_tray` | boolean | Windows only |

Contacts:

```json
{
  "public_key_b64": "base64-public-key",
  "username": "Alice",
  "last_ip": "192.168.1.42",
  "photo_b64": "optional-base64-image"
}
```

Pending messages:

```json
{
  "message_id": "32-hex-id",
  "peer_public_key_b64": "base64-public-key",
  "peer_username": "Alice",
  "text": "Message to retry",
  "timestamp": 1715000000.123
}
```

Pending files:

```json
{
  "file_path": "/path/to/file",
  "peer_public_key_b64": "base64-public-key",
  "peer_username": "Alice",
  "timestamp": 1715000000.123
}
```

## Validation Rules

Drop packets that violate these rules:

| Condition | Action |
|---|---|
| Missing `type` | Drop |
| Unknown `type` | Drop |
| Sender public key equals own key | Drop |
| Discovery source IP is one of own local IPs | Drop |
| Base64 nonce does not decode to exactly 12 bytes | Drop |
| TCP frame length is `<= 0` or `> 50 MiB` | Close connection/drop |
| `file_start.size < 0` or `> 2 GiB` | Drop |
| Decryption/authentication fails | Drop content; do not send receipt |
| Malformed JSON | Drop frame/datagram |

Filename sanitization:

- macOS follows POSIX behavior: split on `/`, trim whitespace, remove null bytes,
  default to `file`.
- Windows follows Windows behavior: treat both `/` and `\` as separators, trim
  whitespace, remove null bytes, default to `file`.

Do not write a sender-supplied path directly to disk.

## Operational Flows

### Text Send

```text
UI -> AppModel -> MessagingService
  -> append outgoing history status=Sending
  -> encrypt using message_id AAD
  -> one-shot TCP frame
  -> status Sent on write success, Queued on write failure
```

If write fails, the message is stored in `pending_messages`. When the peer is
discovered again, the pending message is re-encrypted and retried.

### Text Receive

```text
TCP listener -> PacketValidator -> MessagingService
  -> decrypt using message_id AAD
  -> append incoming history
  -> notify UI
  -> send sent_receipt
```

### Read Receipts

When a conversation is visible/opened, the client sends `read_receipt` for every
incoming entry whose `read_receipt_sent` flag is false, then persists the flag.

### File Send

```text
UI -> AppModel -> FileTransferService
  -> queue per peer
  -> open dedicated TCP connection
  -> file_start
  -> encrypted file_chunk frames
  -> file_end
  -> append local file bubble on success
```

If the peer is offline, the file path is stored in `pending_files`. The file must
still exist when retry occurs.

### File Receive

```text
TCP listener -> PacketValidator -> FileTransferService
  -> file_start creates temp file
  -> chunks decrypt and append in-order off the UI thread
  -> file_end finalizes temp file
  -> append incoming file bubble and show notification
```

### Contact IP Migration

Contacts are keyed by public key, but history is keyed by IP for compatibility.
When a saved contact broadcasts the same public key from a new IP, clients migrate
history, hidden conversation state, archived state, and selected conversation from
old IP to new IP.

## Compatibility Notes

- New packet fields must be optional unless the protocol version is explicitly
  bumped and both platforms are updated together.
- `reply_to_*` fields are intentionally optional and unencrypted.
- Existing history keyed by IP is a compatibility constraint. Do not switch to
  public-key keys without a migration plan.
- Combined GitHub releases may expose only public installers. In-app updaters
  also inspect per-platform releases for ZIP/EXE assets and SHA256 sidecars.
- Legacy Python config migration may import non-key config fields and optionally
  import a raw base64 private key into the platform secure store.
