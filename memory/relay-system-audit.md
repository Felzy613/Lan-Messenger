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

## 2026-07-02 hardening pass: instant badge, no duplicates, no silent drops

User reported three concrete production-readiness bugs; all three had real root causes in the 2026-05-29 design above, fixed symmetrically on both platforms.

1. **"Sending via relay" badge only appeared after the recipient later retrieved the message** (in practice: only after a full history reload / app restart). Root cause: `markRelayDelivery`/`MarkRelayDelivery` was called optimistically the moment a relay upload was *attempted*, and it only mutated `HistoryStore`'s private dict — nothing propagated into the UI-bound `AppModel.messages`/`Messages` copy (there was an `onStatusUpdate` callback but no equivalent for `deliveryPath`). Fix: `RelayClient.store`/`StoreAsync` now returns a confirmed-success `Bool` parsed from `{"ok":true}` in the response body (not just HTTP status). `markRelayDelivery` is only called — and a new `onDeliveryPathUpdate`/`OnDeliveryPathUpdate` callback fired — *after* that confirmation. AppModel wires the callback to patch the matching entry in place, mirroring the existing status-update wiring. On Windows, `MessageRowViewModel.DeliveredViaRelay` had to be converted from `{ get; init; }` to a mutable `INotifyPropertyChanged` property (same pattern as `Status`), and `MessageBubbleControl` needed a new `OnRowPropertyChanged` branch to react to it.

2. **Messages sometimes arrived twice.** Root cause: incoming-relay dedup checked `HistoryStore.entries(forPeerIP:)` — scoped to one IP bucket — but the `ip` used to file a relay message is re-resolved every poll from ephemeral state (live peers, contacts, session cache), and macOS actively purges offline peers from `peers`. Combined with a fire-and-forget `DELETE` with no retry: if delete failed once and the peer's resolved IP changed by the next 30s poll, the per-bucket check missed the earlier copy and re-appended the message. `HistoryStore.migrate`/`Migrate` (synthetic→real IP merge) then just concatenated with no dedup, so the duplicate survived. Fix: added `containsMessageId`/`ContainsMessageId` that scans *all* peer buckets (not just one IP) and used it in `handleRelayMessage`/`HandleRelayMessage`; `migrate`/`Migrate` now dedupes by `message_id` when merging (defense in depth). `markRelayDelivery`/`MarkRelayDelivery` was also changed to scan all buckets by `messageId` rather than requiring a peerIP, since the retry path (below) only knows the messageId.

3. **Messages sometimes never arrived at all.** Root cause: `RelayClient.store`/`StoreAsync` was a single fire-and-forget POST — a transient network blip, Worker cold start, or the Worker's 100-message inbox cap silently and permanently dropped the relay copy; the message just sat hoping both peers were later on the LAN simultaneously. Fix: `PendingMessageConfig`/`PendingMessageConfig.cs` gained a persisted `relay_stored`/`RelayStored` flag (Swift: custom `init(from:)` with `decodeIfPresent(...) ?? false` for back-compat, matching `ContactConfig`'s existing pattern). The relay poll timer (same 30s cadence as the inbox fetch) now also retries the store for every pending message that is still `!relayStored`, re-resolving the relay hash and re-encrypting fresh each attempt (new nonce per try — same pattern the direct-LAN retry already used). When direct LAN delivery succeeds for a message that had already landed on the relay, the client now best-effort deletes the relay copy immediately (tightens the window for bug #2 further and keeps mailboxes tidy).

No changes were needed to the Cloudflare Worker itself — `/store` already deduped via `inbox.includes(message_id)` and returned `{ok:true}`/`{ok:true,duplicate:true}`, which is exactly what the new confirmed-store check and retry idempotency needed.

**Files touched:** `RelayClient.swift`/`.cs`, `MessagingService.swift`/`.cs`, `HistoryStore.swift`/`.cs`, `ConfigStore.swift`/`.cs`, `AppModel.swift`/`.cs`, and on Windows also `ChatPage.xaml.cs` + `MessageBubbleControl.xaml.cs` (mutable `DeliveredViaRelay`). See `PROTOCOL.md` → Cloud Relay Fallback for the updated wire/behavior description.

**Verification:** macOS `swift build && swift test` — 107/107 passed. Windows changes were mirrored carefully and read-through reviewed but **not** compiled/run — no Windows machine was available in that session. Run the MSBuild/vstest steps from `CLAUDE.md` on Windows to confirm, and do a real two-device manual smoke test (send while recipient offline → badge should appear within ~1s of the store confirming, well before the recipient is back → bring recipient online → exactly one copy should arrive).
