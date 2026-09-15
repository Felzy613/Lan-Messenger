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
5. [Presence](#presence)
6. [Framing](#framing)
7. [Packet Fields](#packet-fields)
8. [Packet Types](#packet-types)
9. [Cryptography](#cryptography)
10. [History Format](#history-format)
11. [Config Format](#config-format)
12. [Validation Rules](#validation-rules)
13. [Operational Flows](#operational-flows)
14. [Remote Desktop](#remote-desktop)
15. [Compatibility Notes](#compatibility-notes)

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
| Media frame max size | `4_194_304` bytes (`4 MiB`) |
| Media video fragment size | `16_384` bytes (`16 KiB`) |
| Media session accept window | `10 s` |
| Media reconnect window | `30 s` |

The peer-timeout difference is UI state only. It does not change packet format.

The media constants apply only to the remote-desktop media channel described in
[Remote Desktop](#remote-desktop). They do not affect the JSON frame format.

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

`discovery`, `discovery_reply`, and `goodbye` all have the same shape:

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
| `type` | string | yes | `discovery`, `discovery_reply`, or `goodbye` |
| `username` | string | yes | Sender display name |
| `port` | integer | yes | Sender TCP port, normally `54232` |
| `public_key_b64` | string | yes | Standard base64 of raw 32-byte X25519 public key |
| `ips` | array of strings | yes | Sender's local IPv4 addresses |
| `relay_id_hash` | string | no | SHA-256 hex of the sender's private `relay_id`; used as the cloud-relay mailbox address. Older clients omit this field and must be tolerated by receivers. |
| `caps` | array of strings | no | Optional capability tokens the sender implements, for example `["remote-desktop-v1"]`. Absent means "assume nothing beyond the base protocol". Receivers must tolerate unknown tokens and a missing field. |

A receiver must also tolerate a **malformed** `caps` — a bare string, a number,
an object, a null, or an array with non-string entries — by treating it as
absent, never by rejecting the datagram. A peer with a broken capability field is
still a peer, and dropping its beacon would remove it from the network entirely
over a field that is optional by definition. Receivers should bound what they
retain: discovery is unauthenticated UDP from anyone on the LAN, and the
reference implementations keep at most 16 tokens of at most 64 characters.

`caps` exists because `PacketValidator` drops unknown packet types silently. A
sender that has no way to know whether a peer implements an extension will wait
forever for a reply that is never coming. Advertising the capability turns that
hang into a disabled menu item and an accurate "this peer's version does not
support it" message. Add a token here when an extension needs to be negotiated
before first use; do not add one for extensions that degrade safely, such as
`reply_to_*`.

### Discovery Reply

When a client receives `discovery`, it sends exactly one `discovery_reply` back
to `{source_ip}:54231` over UDP. Do not send discovery replies to TCP port
`54232`. Do not reply to `discovery_reply` or `goodbye`; replying to either
creates a ping-pong loop or contradicts the departure.

### Goodbye

A `goodbye` is a departure announcement. A client emits it when it is leaving
the LAN — clean quit, system sleep/suspend, or local network loss — so peers can
mark it offline immediately instead of waiting out the silence timeout. It is
sent to the same targets as a beacon (subnet broadcast, multicast, limited
broadcast, and unicast hints), repeated a few times because UDP is lossy and it
is a one-shot signal.

Receivers must:

- never reply to a `goodbye`;
- never treat it as a heartbeat (it must not refresh `last_seen`);
- mark the peer identified by `public_key_b64` offline at once.

`goodbye` is additive and optional. Clients that predate it simply drop the
packet (their validator rejects unknown types) and fall back to the silence
timeout — graceful degradation, never a failure.

### Self Suppression

Drop discovery packets if:

- source IP is one of this machine's current local IPv4 addresses;
- `public_key_b64` equals this client's public key;
- `public_key_b64` is empty or malformed;
- `type` is not one of `discovery`, `discovery_reply`, or `goodbye`.

## Presence

Online/offline status is LAN-local and derived from a per-peer state machine, not
a single timestamp comparison. The cloud relay carries messages only and plays no
part in presence.

Inputs:

- **Heartbeat** — a `discovery`, `discovery_reply`, or any inbound TCP packet
  from a peer refreshes its `last_seen` and marks it online.
- **Goodbye** — marks the peer offline immediately.
- **Liveness probe** — when a peer goes quiet, the observer unicasts a
  `discovery` to each address the peer has advertised (its `ips`) to reconfirm it
  before declaring it offline.
- **Outbound reachability** (Windows) — a successful outbound TCP send (message
  or file) to a peer refreshes its `last_seen` and marks it online, same as an
  inbound heartbeat. A completed connect + write is at least as strong evidence
  of reachability as an inbound packet, and this catches the case where this
  machine's own discovery *reception* is broken or firewalled (multicast/UDP
  blocked, a virtual adapter confusing the network-profile classification) while
  outbound TCP still works fine — without it, presence depended solely on
  inbound traffic and would flicker offline during any lull between exchanges.

State, evaluated about once per second against `now - last_seen`:

| Age since last_seen | State | Behavior |
|---|---|---|
| `< 5 s` | Online | healthy |
| `5 s – 12 s` | Online (probing) | still shown online; unicast probe each tick |
| `≥ 12 s` | Offline | gray |

The probing window is what lets the offline timeout be short without flicker: a
quiet peer is actively reconfirmed rather than passively assumed dead the instant
a few beacons are lost. The 1.5 s beacon interval gives roughly three beacons of
slack before probing begins.

Peers are retained in memory after they go offline (their public key is needed to
queue/relay messages); presence is an explicit field on the record. Non-contact
peers that stay offline for more than five minutes are pruned. Losing the local
network marks every peer offline at once; regaining it triggers an immediate
beacon and relay drain. Timeouts are local policy — each client may choose its
own and they carry no wire dependency.

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

Senders may legitimately re-send the same `text` packet (queue retries after a
transient TCP failure, or a lost `sent_receipt`). Receivers SHOULD treat a
`message_id` that already exists in the conversation's history as a duplicate:
do not append it again, but do re-send `sent_receipt` — a re-send implies the
sender never saw the first acknowledgement. Both native clients implement this;
a future client without duplicate suppression may show a retried message twice.

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

### delete_message

Unencrypted "delete for everyone" notice. Same shape as `sent_receipt`.

```json
{
  "type": "delete_message",
  "message_id": "a3f1b2c4d5e6f7a8b9c0d1e2f3a4b5c6",
  "sender": "Alice",
  "sender_public_key_b64": "base64-public-key",
  "port": 54232
}
```

A sender may only request deletion of their own outgoing messages. On
receipt, the recipient marks the matching history entry as deleted: it clears
`text` and any reply preview fields and sets `deleted` to `true`, leaving a
"this message was deleted" placeholder in the UI. `delete_message` carries no
message content, so it is sent unencrypted.

A receiver applies it only to an entry that is **incoming from that peer** —
the same gate `edit_message` uses, and for the same reason: the peer knows the
`message_id` of every message we sent them, so a `delete_message` naming one of
our own outgoing messages must be refused rather than allowed to blank what we
said.

Like `edit_message`, the LAN write is best-effort and falls back to a relay
control record when it fails.
"Delete for me" (removing a message only from the local copy of a
conversation) is a local-only operation and never sends a packet.

### edit_message

Encrypted replacement text for a message the sender already sent. Shaped like a
`text` packet, but `message_id` is the id of the **original** message rather
than a new one.

```json
{
  "type": "edit_message",
  "message_id": "a3f1b2c4d5e6f7a8b9c0d1e2f3a4b5c6",
  "timestamp": 1715000123.456,
  "sender": "Alice",
  "sender_public_key_b64": "base64-public-key",
  "port": 54232,
  "nonce": "base64-12-byte-nonce",
  "ciphertext": "base64-ciphertext-plus-tag"
}
```

`timestamp` is when the edit was made, not when the original was sent; the
original's timestamp is left untouched so the message keeps its place in the
thread. Encryption is identical to `text` — X25519/HKDF/AES-GCM with the AAD set
to the raw UTF-8 `message_id`, which here is the original message's id.

A receiver applies the edit only to an entry that is **incoming from that
peer**. This is a security rule, not a tidiness one: the peer already knows the
`message_id` of every message we sent them, so an `edit_message` naming one of
our own outgoing messages must be rejected rather than allowed to rewrite what
we said. Attachments (`text` values with the `__FILE__:` prefix) and messages
already marked `deleted` are never editable. An `edit_message` for an unknown
`message_id` is dropped.

On success the entry's `text` is replaced and `edited` / `edited_at` are set.
Reply metadata on the original is left as-is — an edit changes the body, not
what the message was replying to.

`edit_message` itself is best-effort: one TCP write, no queue or retry. When
that write fails the edit is carried through the cloud relay instead, as a
control record (see Relay Control Records), so a peer who was offline applies it
on their next poll. An original still sitting in the sender's pending-message
queue is additionally rewritten in place, so when it finally delivers, the peer
receives the edited text as the message's first and only version.

If the peer has no `relay_id_hash` and the TCP write fails, the edit stays
local — the peer keeps the original text.

Clients that do not implement `edit_message` reject it as an unknown type and
keep showing the original text, which stays consistent with what was sent.

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

### remote_invite

Offers a remote-desktop session. Sent by the peer that wants to view, which the
handshake calls the **initiator**. See [Remote Desktop](#remote-desktop).

```json
{
  "type": "remote_invite",
  "session_id": "9f2c4a6e8b0d1f3a5c7e9b1d3f5a7c9e",
  "sender": "Alice",
  "sender_public_key_b64": "base64-public-key",
  "port": 54232,
  "nonce": "base64-12-byte-nonce",
  "ciphertext": "base64-ciphertext-plus-tag"
}
```

`session_id` is a 32-character lowercase hex value with the same shape as
`message_id`. It is plaintext because the receiver needs it to look up the
session before it can decrypt anything.

The ciphertext is an encrypted JSON body, sealed with the ordinary session key
(X25519/HKDF/AES-GCM, `info = "lan-messenger"`) and AAD set to the raw UTF-8
`session_id`:

```json
{
  "eph_pub_b64": "base64-32-byte-ephemeral-x25519-public-key",
  "params": { "protocol": 1, "video": "h264", "displays": 2 }
}
```

Sealing the ephemeral key rather than sending it in the clear does not stop an
attacker who cannot complete the triple DH anyway; it hardens against
unauthenticated peers making a host do X25519 work, and it authenticates
`params` for free. `params` is canonicalized into the handshake transcript, so a
field added here later cannot be silently downgraded by a man in the middle.

An invite from a peer that is not a saved contact must be dropped without a
prompt. An invite arriving while the receiver already has a live or pending
session with that peer must be declined with `reason: "busy"`.

### remote_accept

Grants a `remote_invite`. The sender of this packet is the **responder** — the
host whose screen will be shared.

```json
{
  "type": "remote_accept",
  "session_id": "9f2c4a6e8b0d1f3a5c7e9b1d3f5a7c9e",
  "sender": "Bob",
  "sender_public_key_b64": "base64-public-key",
  "port": 54232,
  "nonce": "base64-12-byte-nonce",
  "ciphertext": "base64-ciphertext-plus-tag"
}
```

The sealed body mirrors the invite and carries the responder's ephemeral key
plus the parameters it actually agreed to:

```json
{
  "eph_pub_b64": "base64-32-byte-ephemeral-x25519-public-key",
  "params": { "protocol": 1, "video": "h264", "display": 0 }
}
```

Accepting grants **viewing only**. Input control is a separate escalation the
host approves later, over the media channel's control sub-channel — never
implied by `remote_accept`.

After sending this, the host opens a ~10 s window in which it will honour
exactly one `media_attach` bearing this `session_id`.

### remote_decline

Refuses an invite, or reports that a session could not be set up.

```json
{
  "type": "remote_decline",
  "session_id": "9f2c4a6e8b0d1f3a5c7e9b1d3f5a7c9e",
  "reason": "declined",
  "sender": "Bob",
  "sender_public_key_b64": "base64-public-key",
  "port": 54232
}
```

`reason` is an unencrypted, machine-readable token: `declined`, `busy`,
`unsupported`, `disabled`, `no_encoder`, or `timeout`. Receivers must tolerate
unknown values and show a generic message. Nothing here is sensitive — the
initiator already knows it asked.

Sending `declined` for a policy refusal and `disabled` for "the feature is
switched off" leaks slightly more than a single opaque token, and that is the
intended trade: a user who has switched the feature off wants their peer to be
told that, not left guessing.

### remote_end

Terminates a session from either side, at any point, including before the media
channel is attached.

```json
{
  "type": "remote_end",
  "session_id": "9f2c4a6e8b0d1f3a5c7e9b1d3f5a7c9e",
  "reason": "user_stopped",
  "sender": "Alice",
  "sender_public_key_b64": "base64-public-key",
  "port": 54232
}
```

`reason` tokens: `user_stopped`, `idle_timeout`, `screen_locked`, `app_quit`,
`error`. This is best-effort — one TCP write, no retry. Both sides must also
tear down on socket close alone, because a crashing peer never sends it. It is
sent over TCP 54232 rather than the media channel precisely so it still works
when the media channel is what broke.

`remote_end` is never queued for offline delivery and never goes through the
cloud relay. A session that is not live has nothing to end.

### media_attach

The first and only JSON frame on a connection that is about to become a media
channel. See [Media Channel Attach](#media-channel-attach).

```json
{
  "type": "media_attach",
  "session_id": "9f2c4a6e8b0d1f3a5c7e9b1d3f5a7c9e",
  "sender": "Alice",
  "sender_public_key_b64": "base64-public-key",
  "port": 54232
}
```

On acceptance the socket leaves the JSON frame loop permanently and speaks the
binary media framing for the rest of its life. A `media_attach` whose
`session_id` does not match an open accept window is dropped and the connection
closed.

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
      "reply_to_sender": null,
      "deleted": false,
      "edited": false,
      "edited_at": null
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
- `deleted` is optional and defaults to `false` when absent (back-compat with
  older history files). When `true`, `text` and reply preview fields are
  cleared and the UI renders a "this message was deleted" placeholder.
- `edited` is optional and defaults to `false`; `edited_at` is optional and is
  the Unix timestamp of the most recent edit. Both must decode cleanly when
  absent. `timestamp` continues to hold the original send time, so an edited
  message keeps its position in the thread. When `edited` is `true` the UI
  appends an "(edited)" marker; only the latest text is retained — history
  keeps no revision list.

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
| `remote_invite` from a peer that is not a saved contact | Drop without prompting |
| `remote_invite` while a session with that peer is live or pending | Decline with `busy` |
| `media_attach` with no matching open accept window | Drop and close connection |
| Media frame length is `<= 0` or `> 4 MiB` | Close connection; do not allocate |
| Media frame sequence is not strictly greater than the last accepted | Drop and close connection |
| Media frame on the input sub-channel before `control_grant` | Drop |
| Media frame with a reserved `flags` bit set | Ignore the bit; do not reject |
| Key confirmation (`hello`) mismatch | Close connection; do not retry with the same keys |

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

#### Cloud Relay Fallback

If the sender knows the peer's `relay_id_hash` (received in a prior discovery
packet) the encrypted ciphertext is **also posted** to the cloud relay Worker
immediately after being placed in the local queue.

The store attempt is **retried until confirmed**, not fire-and-forget. Each
`PendingMessageConfig`/pending-message entry carries a `relay_stored` flag
(`false` until the Worker responds `{"ok":true}`). A poll timer (the same one
that drains the inbox, every 30s) retries the store for every pending entry
that is still `!relayStored`, re-encrypting fresh each attempt (new nonce per
try, same pattern as the direct-LAN retry). This is what protects against a
message never arriving at all: a transient network blip, a Worker cold start,
or a momentarily full inbox on the first attempt no longer strands the
message — it gets picked back up on the next poll. The UI's "via relay"
badge is driven by this same confirmation, not by the initial (unconfirmed)
attempt, so it now appears within the store round-trip instead of only after
a full history reload.

When any client starts up, it fetches its own pending messages from the Worker:

```
App start -> RelayClient.fetchPending(relay_id)
  -> Worker verifies SHA256(relay_id) == relay_id_hash
  -> returns stored ciphertext blobs
  -> MessagingService decrypts each (same AES-GCM path as LAN delivery)
  -> RelayClient.delete(message_id) to clean up
```

Incoming relay messages are deduplicated **globally by `message_id`** across
the whole local history (all peer-IP buckets), not just the IP bucket the
message happens to resolve to on that particular poll. Peer IP resolution
for a relay sender is ephemeral — it depends on live discovery state,
contacts, and session caches, which can change between polls (e.g. macOS
purges offline peers) — so a per-IP check can miss an earlier delivery
filed under a different bucket and re-append the message. If the client
already has the `message_id` anywhere in history, it does not reprocess or
re-append it; it only issues the mailbox `DELETE` cleanup. History
migration (moving a synthetic `relay-{keyPrefix}` bucket to a peer's real IP
once discovered) also dedupes by `message_id` when merging, as defense in
depth.

If a message is delivered directly over the LAN (`deliverPending` succeeds)
after it was already confirmed-stored on the relay, the client best-effort
`DELETE`s the relay copy immediately, so it doesn't linger for the recipient
to fetch as a redundant (though now-deduplicated) delivery.

This allows Bob to receive messages from Alice even if Alice's machine was
off-network at the time Bob came back online.

**relay_id derivation:**

```
relay_id      = SHA256(private_key_bytes || "relay-v1")   [private, never transmitted]
relay_id_hash = SHA256(relay_id)                           [published in discovery]
```

**Cloud Relay REST API** (Cloudflare Worker at `https://lan-messenger-relay.shoparmorex.com`):

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/store` | Store encrypted message for a peer (body: `relay_id_hash`, `message_id`, `ciphertext_b64`, `nonce_b64`, `sender_username`, `sender_public_key_b64`, `timestamp`, `ttl_s`) |
| `GET` | `/pending?relay_id=<hex>` | Retrieve pending messages; Worker verifies `SHA256(relay_id)` matches stored hash |
| `DELETE` | `/message/:id?relay_id=<hex>` | Delete a delivered message; same ownership check |

Messages expire automatically after 72 hours (KV TTL). The relay cannot read
message contents — it only stores ciphertext already encrypted to the recipient's
X25519 key. The relay is entirely optional: clients that omit `relay_id_hash` from
discovery packets gracefully degrade to LAN-only delivery.

#### Relay Control Records (offline edit / delete)

`edit_message` and `delete_message` are LAN-only, one-shot TCP writes. When the
peer isn't reachable, the same two operations are carried through the relay
mailbox instead, as a **control record**: an ordinary relay record whose
decrypted plaintext is a control envelope rather than a chat body.

```text
__CTRL__:{"op":"edit","target":"<original message_id>","text":"<new body>","at":1715000123.456}
__CTRL__:{"op":"delete","target":"<original message_id>","at":1715000123.456}
```

Rules:

- The record is stored under **its own fresh `message_id`**, never the target's.
  The Worker dedups `/store` by `message_id` and answers a repeat with
  `{"ok":true,"duplicate":true}` — so re-posting under the original's id would
  be silently discarded while reporting success. A fresh id also means this
  works whether or not the original is still sitting in the mailbox.
- The envelope is encrypted exactly like a text message, AAD = the record's own
  fresh id. The Worker is unchanged and still sees only ciphertext: it cannot
  tell a control record from a chat message, and never learns which message was
  edited or deleted.
- `at` is when the edit/delete was made. `target` must be a well-formed
  `message_id` (32 lowercase hex); anything else is dropped before it reaches
  the history store. An `edit` with an empty `text` is rejected — blanking a
  message is what `delete` is for.
- On receipt the envelope is applied through the same history entry points as
  the LAN packets, so the "incoming from that peer only" gate applies
  identically. The record is then `DELETE`d from the mailbox whether it applied
  or not — a spent record would otherwise replay on every poll until its TTL.
- Records are returned by `/pending` in insertion order, so an original and a
  later edit of it arrive in the right order.
- A client older than 1.7 does not recognise the marker and renders the envelope
  as a literal chat message. Both ends need 1.7+ for relayed edits and deletes.

A "delete for everyone" also drops the message from the sender's own pending
queue: delivering a message the sender has since deleted is worse than not
delivering it at all.

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

## Remote Desktop

Remote desktop lets one peer view a contact's screen and, after a separate
grant, drive its keyboard and mouse. It is an extension: a client that does not
implement it drops the new packet types as unknown and is unaffected.

> **Implementation status.** This section is normative and complete. The
> implementation is not: it lives on `feat/remote-desktop-transport` and no
> release contains it. `docs/REMOTE_DESKTOP.md` tracks which parts of this spec
> have code behind them.

Two hard rules frame everything below.

**Remote desktop is LAN-only and peer-to-peer.** It never touches the cloud
relay. The relay is a dumb mailbox for offline text; routing a screen through it
would break the no-server design goal and require NAT traversal that does not
exist here. If the peer is not reachable on the LAN, the feature is unavailable.

**Viewing and control are separate grants.** `remote_accept` grants viewing
only. Control is escalated later over the control sub-channel and approved
separately by the host. A client must never infer control from acceptance.

### Session Overview

```text
Initiator (viewer)                         Responder (host)
------------------                         ----------------
remote_invite  ------- TCP 54232 -------->  consent prompt
                                            (contact check, mode check)
               <------ remote_accept -----  opens 10 s accept window
media_attach   ------- TCP 54232 -------->  matches session_id,
                                            detaches socket from JSON loop
               <====== binary media framing from here on ======>
               <------ ctrl: hello -------  key confirmation
ctrl: hello_ack ------------------------->
               <------ ctrl: video_config
               <------ video frames -------
(user asks for control)
ctrl: control_request ------------------->  second consent prompt
               <------ ctrl: control_grant
input frames   -------------------------->  injected
```

The initiator is always the peer that sent `remote_invite`. This is not a
convention, it is load-bearing: the handshake's directional keys are selected by
role, and two peers that both believe they are the initiator derive swapped keys
and fail to decrypt each other with no useful error.

### Media Channel Attach

The media channel runs on **TCP 54232**, the same port as everything else. There
is no second port, and that is deliberate — a new port means a new Windows
firewall rule, an installer change, and a broken experience on every already
installed client. Multiplexing onto the one port is also what comparable
products do, for the same reason.

The upgrade works like this:

1. The initiator opens a fresh TCP connection to the host on 54232.
2. It writes one ordinary JSON frame: `media_attach`, carrying `session_id`.
3. The host validates it through the normal packet validator.
4. On success the host **detaches the socket from the JSON read loop** and hands
   it to the media subsystem, which owns it for the rest of its life.
5. Both sides then speak only the binary media framing below.

The JSON frame format on 54232 is therefore untouched: the validator never sees
a binary byte, because the socket has already left the loop by the time the
first binary frame arrives. A media socket is exempt from the inbound idle
timeout that ordinary JSON connections use; it has its own keepalive.

The host must reject a `media_attach` that does not match an accept window it
opened, and must allow only one in-flight session per peer.

### Media Framing

The media channel is binary, multiplexed, and shaped like a datagram so a future
UDP transport can reuse the header verbatim.

```text
+--------+---------+-------+------------+---------------+---------------------+
| 4 B    | 1 B     | 1 B   | 8 B        | 8 B           | variable            |
| length | channel | flags | sequence   | capture_us    | encrypted payload   |
+--------+---------+-------+------------+---------------+---------------------+
```

All integers are unsigned big-endian. `length` counts every byte after itself,
header included.

| Sub-channel | Id | Carries |
|---|---|---|
| control | `0` | JSON session control (see below) |
| video | `1` | H.264 access unit, possibly fragmented |
| input | `2` | Keyboard and pointer events |
| cursor | `3` | Cursor position and shape updates |
| stats | `4` | Viewer-to-host quality telemetry |

`flags` bits, from least significant:

| Bit | Meaning |
|---|---|
| 0 | keyframe (video only) |
| 1 | fragmented — this is part of a larger logical frame |
| 2 | final fragment |
| 3-7 | reserved, must be zero, receivers must ignore |

`capture_us` is a monotonic microsecond timestamp taken at capture. It exists so
end-to-end latency is measurable at every stage rather than estimated, and it is
mandatory from the first implementation — retrofitting it means retrofitting
every measurement built on top of it.

There is **no version field in the media header**. Version and capability
negotiation happen once, in the handshake, and are bound into the transcript.
Do not add one; a per-frame version byte is unauthenticated and downgradeable.

Rules:

- Reject a frame whose `length` is `<= 0` or `> 4 MiB`, before allocating.
  The 50 MiB JSON cap does not apply here and would be a memory-exhaustion
  vector at 30 frames per second.
- `sequence` must be strictly increasing per direction. Reject any frame whose
  sequence is not greater than the last accepted one. This closes replay and
  reorder ambiguity, and it is also what keeps the AEAD nonce unique.
- Video is fragmented into segments of at most 16 KiB at the writer, and frames
  on other sub-channels are interleaved between those segments. A 500 KiB
  keyframe must never delay a 20-byte mouse move; on congested Wi-Fi that
  difference is the difference between usable and unusable.
- `TCP_NODELAY` must be set on the media socket. Without it, Nagle plus delayed
  ACK parks small input frames for tens of milliseconds.
- Never buffer more than two video frames anywhere. For remote control, dropping
  a frame is always better than delaying one.

### Media Session Key Derivation

The media channel does **not** reuse the message session key. It performs a
fresh authenticated key exchange per session, giving forward secrecy that the
static message key cannot: a long-term key compromised tomorrow must not decrypt
a screen recording captured today.

There is no signing key in this protocol — the only identity is the long-term
X25519 key, already pinned per contact — so authentication comes from mixing
static and ephemeral agreements, in the shape of a Noise `KK` handshake:

```text
es = X25519(eph_initiator_priv,    static_responder_pub)   # authenticates responder
se = X25519(static_initiator_priv, eph_responder_pub)      # authenticates initiator
ee = X25519(eph_initiator_priv,    eph_responder_pub)      # forward secrecy

transcript = SHA256(
    "lan-messenger-remote-v1"        ||
    session_id_bytes(16)             ||
    static_pub_initiator(32)         || static_pub_responder(32) ||
    eph_pub_initiator(32)            || eph_pub_responder(32)    ||
    canonical_params_bytes
)

okm = HKDF-SHA256(
    ikm    = es || se || ee,
    salt   = session_id_bytes,
    info   = transcript,
    length = 72
)

key_i2r   = okm[0..32]     # initiator -> responder
key_r2i   = okm[32..64]    # responder -> initiator
salt_i2r  = okm[64..68]
salt_r2i  = okm[68..72]
```

`session_id_bytes` is the 16 raw bytes the 32-hex-character `session_id`
represents. `canonical_params_bytes` is the agreed parameter object serialized
with sorted keys, no insignificant whitespace, and UTF-8 encoding — both sides
must produce byte-identical output or the transcripts differ and key
confirmation fails.

Binding the transcript into `info` is what makes the negotiated parameters
tamper-evident. Without it, any field added to `params` later would be
unauthenticated, and an attacker could downgrade a future codec or capability
choice without breaking the handshake.

Per-frame encryption:

```text
nonce  = direction_salt(4) || sequence(8)          # 12 bytes, never random
aad    = the 22 plaintext header bytes, length prefix included
sealed = AES-256-GCM(direction_key, nonce, payload, aad)
wire   = ciphertext || 16-byte tag
```

A counter nonce rather than a random one is deliberate: at 30 frames per second
across several sub-channels, a deterministic counter is both cheaper and
strictly safer than relying on the birthday bound of a 96-bit random nonce.

Including the length prefix in the AAD authenticates the framing itself, not
just its contents.

**Key confirmation is mandatory and must precede control.** Immediately after
derivation the responder sends a control-channel `hello` containing the
transcript hash; the initiator verifies it against its own before anything else
happens, and the input sub-channel must not be armed until it has. Skipping this
means the first symptom of a key mismatch is a stream of GCM failures in the
middle of video, which is a miserable thing to debug.

**A dropped media socket ends the crypto session.** Reconnecting reuses nothing:
new `session_id`, new ephemerals, new keys, sequence restarting from zero
against a key that has never been used. Reusing a key with a reset counter is
catastrophic AES-GCM nonce reuse. The user interface may present a reconnect
within the 30 s window as if the session continued; the cryptography must not.

### Video Sub-Channel

Channel `1` carries H.264 access units, one logical frame per sequence number,
fragmented by the writer when larger than the fragment size.

**The on-wire packaging is Annex-B**, with SPS and PPS emitted **in-band
immediately before every IDR**. Start codes may be three or four bytes; a
receiver must accept both, and a sender should emit four uniformly.

This is normative and it is the one thing both implementations must agree on,
because the two platform codecs disagree by default:

- **Media Foundation** produces and consumes Annex-B natively, with parameter
  sets in-band. A Windows host therefore sends what its encoder emits, unchanged.
- **VideoToolbox** produces and consumes AVCC — length-prefixed NAL units, with
  parameter sets held out-of-band in a `CMVideoFormatDescription`. A macOS host
  must convert on send, and a macOS viewer must convert on receive.

Annex-B was chosen over AVCC because it is self-contained: parameter sets travel
with the picture that needs them, so a viewer that joins late, or that has just
flushed its decoder, recovers on the next IDR with no side channel. Under AVCC
the parameter sets live outside the bitstream, which would mean a second
delivery mechanism and a way for the two to disagree.

A NAL length prefix size must never be assumed to be 4 — read it from the
format description. Access unit delimiters are optional and carry no
information a decoder needs; a receiver must tolerate their presence and their
absence. In particular, **an access unit boundary is a slice**, not an AUD and
not a parameter set: VideoToolbox emits no delimiters at all, so splitting on
them silently collapses a stream into a handful of units.

`video_config` on the control channel carries the dimensions, and must arrive
before the first frame on this channel.

### Control Sub-Channel

Channel `0` carries UTF-8 JSON objects, each with a `t` discriminator. These are
encrypted like any other media payload.

| `t` | Direction | Purpose |
|---|---|---|
| `hello` | host → viewer | Key confirmation; carries `transcript_b64` |
| `hello_ack` | viewer → host | Confirms match; session becomes live |
| `video_config` | host → viewer | `width`, `height`, `scale`, `display_id`, `codec` |
| `keyframe_request` | viewer → host | Ask for an IDR now |
| `control_request` | viewer → host | Ask to escalate from viewing to control |
| `control_grant` | host → viewer | Input accepted; input sub-channel armed |
| `control_revoke` | host → viewer | Input withdrawn; viewer must stop sending |
| `display_list` | host → viewer | Available displays, for selection |
| `display_select` | viewer → host | Switch to another display |
| `host_state` | host → viewer | `secure_desktop`, `elevated_focus`, `locked` |
| `ping` / `pong` | either | Keepalive and round-trip measurement |

`video_config` must arrive before the first video frame and again after any
resolution or display change — the viewer needs dimensions to lay out and to map
input coordinates, and it has no other source for them.

`host_state` is what turns an inexplicable frozen image into an explanation. A
Windows host cannot capture or drive the secure desktop, and cannot inject into
a focused elevated window; when either is true it says so, and the viewer shows
a banner instead of a mystery.

### Input Sub-Channel

Channel `2` carries fixed-shape binary records. The input sub-channel is inert
until `control_grant` and must go inert again on `control_revoke`, session end,
or any error.

Pointer positions are **normalized floats in `[0,1]`, relative to the shared
video surface** — not the viewer's window. The host resolves them to pixels
itself. This keeps display scaling, Retina backing scale, and multi-monitor
offsets entirely on the host side, where the authoritative geometry lives, and
it is why a letterboxed viewer must normalize against the video surface rather
than the window it is drawn in.

Keyboard events carry **USB HID usage IDs (usage page `0x07`)**, a modifier
bitmask, and a repeat flag — never characters, never platform virtual key
codes. A HID usage names a physical key position, so the host's own layout
decides the resulting character. That is correct remote-desktop behaviour: dead
keys, AltGr, and IME all work because the events pass through the host's real
text input path. It also means a viewer on AZERTY typing `a` produces `q` on a
QWERTY host, which is expected and matches every other remote desktop.

Because layout-independent position codes cannot express everything, the input
sub-channel also carries a **Unicode text record**. It injects a string
directly, and it is the escape hatch for layout mismatch, emoji, IME
composition, and paste-as-typing.

Key repeat is forwarded by the viewer from its own operating system. Neither
platform auto-repeats synthetic key-down events, so a host that waits for
repeats it will never generate produces a single character where the user held
a key down.

Some combinations can never be captured by the viewer because its own operating
system consumes them first — Cmd+Tab, Cmd+Space, Ctrl+Alt+Del, Win+L. These are
sent explicitly from a "send special keys" menu rather than forwarded, and the
un-forwardable set is a documented limitation, not a bug to be fixed.

### Stats Sub-Channel

Channel `4` carries viewer-to-host JSON telemetry roughly once per second:
round-trip time, decoded frames per second, dropped frames, decode queue depth,
and end-to-end latency computed from `capture_us`. The host adapts bitrate and
frame rate from this and from its own send-queue depth.

Both capture backends are **change-driven, not fixed-rate**: a completely static
screen legitimately produces no frames at all. The stats channel and the
keepalive are therefore the only way to distinguish "nothing is happening" from
"the stream died", and a viewer must never time out a session purely because no
video arrived.

### Session Lifecycle

A host must end the session and release capture on any of: `remote_end`, socket
close, screen lock, user switch, system sleep, app quit, or a watchdog expiry
when no input, stats, or keepalive has arrived for its timeout. The watchdog is
not optional — without it a viewer that crashes leaves a host's screen being
captured indefinitely, which is the single worst failure this feature can have.

Reconnect within the 30 s window keeps capture warm and preserves the user's
sense of one continuous session, but performs a full fresh handshake as
described above.

### Consent Rules

These are protocol-level requirements, not user-interface suggestions. A client
that does not enforce them is not compatible.

- Remote desktop is **off by default**. It must be switched on deliberately.
- Only **saved contacts** may invite. An invite from an unknown peer is dropped
  without prompting, matching the existing rule that discovered peers do not
  become conversations on their own.
- Consent is **per session**, never remembered, never "always allow". There is
  no unattended access mode.
- The consent prompt must show the peer's name **and identity key fingerprint**,
  and must distinguish a key matching the saved contact from a new or changed
  one. A display name alone is trivially spoofable; the pinned key is not.
- While a session is live the host must show a **persistent indicator** naming
  the viewer and the current grant level, with a stop control.
- The host must reserve a **kill shortcut that is never forwarded to the peer**,
  so a host being actively controlled can always stop the session.
- Session start, stop, and every control grant are recorded in the conversation
  history as an audit trail.

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
- Remote desktop is negotiated through the optional `caps` discovery field. A
  client without `remote-desktop-v1` in its `caps` must be treated as unable to
  participate, and the feature disabled for that peer in the interface rather
  than attempted and timed out.
- The media channel shares TCP 54232 with JSON packets by detaching the socket
  after `media_attach`. The JSON frame format is unchanged, and no new port or
  firewall rule is required. Do not "simplify" this by adding a second listener
  port — that breaks every installed client and needs an elevated firewall
  change on Windows.
- A media session's keys are per-session and never reused across reconnects.
  Treat any change to the handshake as a protocol version bump: both ends derive
  the same transcript or neither works.
