---
name: relay-system-audit
description: Relay system design, bugs found and fixed, delivery tracking, and known limitations after the 2026-05-29 audit.
metadata:
  type: project
---

Comprehensive relay audit and hardening completed 2026-05-29.

**Architecture**: Cloud relay uses Cloudflare Worker + KV. Each device's mailbox is `SHA256(relay_id)` where `relay_id = SHA256(private_key_bytes || "relay-v1")`. Mailbox address is published in every UDP discovery beacon. Relay is opt-in; Worker URL must be supplied by the user.

**Fixes applied:**

1. **Relay only when offline** — `AppModel.sendMessage` (both platforms) now checks `peerIsOnline` before passing `relayIdHash` to `MessagingService`. If the peer is currently in the live-peer dictionary AND `isOnline`, relay hash is set to `nil` so TCP failure falls back to local queue only (not cloud upload). Previously, any TCP failure to an online peer would incorrectly upload to the relay.

2. **Delivery-mode tracking** — `MessageEntry` gained `deliveryPath: String?` (persisted as `"delivery_path"` JSON key). Value is `"relay"` for messages that transited the cloud Worker; `nil` for direct LAN delivery. Set in:
   - Outgoing: `MessagingService.queuePendingMessage` when relay upload is attempted
   - Incoming: `MessagingService.handleRelayMessage` for all relay-received messages

3. **UI relay indicator** — Small "☁ via relay" badge appears next to the timestamp in both text and file bubbles (macOS: SwiftUI `relayBadge` view; Windows: `RelayBadge` StackPanel in XAML + `MessageRowViewModel.DeliveredViaRelay`). Shown for both incoming and outgoing relay messages.

4. **sent_receipt for relay messages** — `handleRelayMessage` now sends `sent_receipt` back to the sender's IP if the IP is a real address (not a synthetic `"relay-..."` placeholder). This upgrades the sender's message status from "Queued" → "Delivered" when both peers happen to be online simultaneously.

5. **Synthetic IP migration** — When a relay message arrives from a sender never seen on the LAN, it is stored under `"relay-{first8charsOfPublicKey}"`. When that peer later appears via discovery, `upsertPeer`/`UpsertPeer` calls `migrateSyntheticRelayHistory` to merge that history into the real IP. Also triggered on "came back online with same IP" (Windows only, since macOS purges offline peers).

6. **Settings Clear button** — Both platforms now show a "Clear" button next to the relay Worker URL field when a URL is set. Addresses the reported "auto-fill" issue (URL persists from previous sessions — no code bug, just a persistence behavior). Button clears the saved URL.

7. **Worker hardening** — `MAX_CT_LEN` raised from 4096 to 16384 bytes (covers large text messages). Inbox index TTL always refreshed to `TTL_SECONDS` on new message store (previously inherited the shorter expiry of the existing index, which could cause the index to expire before the messages).

**Known limitations:**
- File/media relay: files are NOT relayed through the cloud (too large for KV; ~25 MB limit but latency/cost would be prohibitive). Files are queued locally and delivered via direct TCP when both peers are on the LAN simultaneously.
- Receipt for relay from unknown peer: if the sender's public key isn't in contacts or peers dict, their IP is synthetic and no `sent_receipt` is sent.
- Windows `knownPeerKeys` cache: Windows doesn't implement the macOS `knownPeerKeys` session cache, so relay message IP resolution falls back to contacts only (no session-only IP cache).

**Why:** [[feedback_document_all_work]]
