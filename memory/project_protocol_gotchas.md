---
name: Protocol implementation gotchas
description: Non-obvious LAN Messenger wire-protocol and persistence details for native clients
type: project
---

Use this before touching discovery, framing, crypto, receipts, file transfer,
history, or config migration. `PROTOCOL.md` is the authoritative spec.

## Discovery

- UDP discovery packets are raw UTF-8 JSON. They are not length-prefixed.
- Discovery replies are sent to `{source_ip}:54231` over UDP, not to TCP port
  `54232`.
- Do not reply to `discovery_reply`.
- Drop packets from own local IPs and packets with own public key.
- Discovery should send on every eligible IPv4 interface, not only the default
  route.
- Saved/current peer IPs are used as unicast hints for cross-subnet reach.

## Framing

- TCP frames are 4-byte unsigned big-endian length plus UTF-8 JSON body.
- Reject length `<= 0` and length `> 50 MiB`.
- One logical packet is one frame.

## Crypto

- X25519 public keys are raw 32-byte keys encoded with standard base64.
- HKDF salt is empty bytes.
- Session info string is `lan-messenger`.
- History info string is `lan-messenger-history`.
- AES-GCM tag is appended to ciphertext before base64 encoding.
- Text AAD is `message_id` UTF-8 bytes.
- File chunk AAD is `transfer_id` UTF-8 bytes.
- History AAD is `history-v1` UTF-8 bytes.

## IDs

- `message_id` and `transfer_id` are 32 lowercase hex characters with no dashes.

## History

- History is keyed by peer IP, not public key. This is a compatibility constraint.
- Saved contact IP changes are handled by migrating history from old IP to new IP.
- Each conversation is capped at 200 entries.
- Reply fields are optional: `reply_to_message_id`, `reply_to_preview`,
  `reply_to_sender`.
- File bubbles are stored as text with `__FILE__:` prefix.

## Receipts And Status

- `sent_receipt` is sent by the receiver after successful decrypt.
- `read_receipt` is sent by the receiver when the conversation is opened/read.
- Status updates must be monotonic: `Sent` must not overwrite `Delivered` or
  `Read`.

## File Transfer

- Each transfer uses a dedicated TCP connection.
- Packet sequence is `file_start`, ordered encrypted `file_chunk` frames,
  `file_end`.
- Chunks have no sequence number; keep decrypt/write processing ordered.
- Incoming temp file is `{transfer_id}_{filename}.part`.
- Final names dedupe with `_1` through `_999`, then an 8-hex fallback.
- Progress callbacks must be throttled so large files do not flood the UI thread.

## Filename Sanitization

- macOS/POSIX splits only on `/`; backslashes are ordinary filename characters.
- Windows treats both `/` and `\` as path separators.
- Both trim whitespace, remove null bytes, and default to `file`.

## Migration

- Legacy config may include `private_key_b64`; native apps import it into secure
  storage instead of keeping it in config JSON.
