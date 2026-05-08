---
name: Protocol implementation gotchas
description: Non-obvious facts about the LAN Messenger wire protocol discovered by reading main.py, important for native implementations
type: project
---

These are things that the implementation plan document describes at a high level but
the actual code reveals specifically. Native app developers must know all of these.

**Why:** Subtle protocol mismatches will silently break interoperability. These were
verified by reading main.py source, not inferred from the plan.

**How to apply:** Check each item when implementing the corresponding feature in native code.

## Discovery

- Discovery packets are raw UTF-8 JSON on UDP — **no length prefix framing**. Framing only applies to TCP.
- Discovery reply is sent back to `{source_ip}:54231` (the UDP discovery port), NOT to the TCP port.
- Receiver rejects discovery packets where source IP is in its own local_ips list (not just where public_key matches).
- The `ips` field in the discovery payload is the sender's local IPv4 list (may be multiple, e.g. Ethernet + Wi-Fi).
- Discovery also sends to last-known IP of saved contacts (unicast) to reach peers across subnets.

## Framing

- Size check is `size <= 0 or size > 50 * 1024 * 1024` — the ≤0 case must also be rejected (not just >50 MiB).

## Crypto

- HKDF salt is `None` in Python's cryptography library, which means empty bytes. Native implementations must use empty salt (`b""` / `Data()` / `[]`), not a null pointer that might be treated differently.
- AES-GCM ciphertext includes the 16-byte tag appended at the end: the Python library's `.encrypt()` returns `ciphertext + tag`. Native apps must concatenate before base64-encoding and split before decrypting.
- CryptoKit on macOS returns ciphertext and tag separately via `AES.GCM.SealedBox`; use `sealed.ciphertext + sealed.tag` to produce the correct byte layout.

## History File

- History is keyed by **IP address** (string), not by public key. This means if a peer's IP changes, their history appears as a new conversation. This is a known limitation of the Python app carried forward.
- History AAD is the literal bytes `b"history-v1"` (10 bytes). Native apps must use exactly this, not a string comparison or different encoding.
- Inner JSON uses compact separators: `json.dumps(..., separators=(",", ":"))`. The format doesn't need to match exactly for reading (JSON parsers accept any whitespace), but the 200-message cap and IP-keyed structure must be preserved.

## Receipts

- `sent_receipt` is sent by the **receiver** (not the sender) immediately after successfully decrypting a `text` packet.
- `read_receipt` is sent by the **receiver** when the user opens the conversation — only once per message (`read_receipt_sent` flag in history prevents duplicates across restarts).

## File Transfer

- Temp file naming: `{inbox_dir}/{transfer_id}_{filename}.part` — exactly this format.
- On `file_end`: rename temp file to final name; if final name exists, try `{stem}_1{suffix}`, `{stem}_2{suffix}`, ... up to 999, then fall back to random 8-hex suffix.
- File transfer uses a **separate TCP connection** per transfer (not the persistent peer session). The sender opens `socket.create_connection((peer.ip, peer.port), timeout=5)` for each file transfer.

## Message IDs and Transfer IDs

- Both are `uuid.uuid4().hex` — 32 lowercase hex characters with no dashes. Not a UUID string with dashes. Native apps must generate in the same format.

## Config Migration

- Python stores the private key as `private_key_b64` in plain JSON. Native apps must NOT do this. On import from Python config, move the key to Keychain (macOS) or DPAPI (Windows) and remove it from the JSON.
