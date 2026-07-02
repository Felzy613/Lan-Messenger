---
name: macOS reliability fixes
description: Ported the Windows reliability audit's macOS follow-ups (2026-07-01) — presence probe replies, message dedup, accept-loop spin, dead discovery hints, file-retry cooldown
type: project
---

# macOS reliability fixes (2026-07-01)

Follow-up to [Windows reliability + UI audit](windows-reliability-audit.md).
That audit found two bugs shared with macOS and flagged three more while
reading macOS source for comparison. All five are now fixed in
`src/macos/`, verified with `swift build` and `swift test` (both pass).

## Fixes

**Presence flapping** — `DiscoveryService.swift` `handleReceivedData` replied
to `discovery` packets AFTER the beacon dedup gate. A peer probing us with a
unicast `discovery` (to reconfirm before declaring us offline) shares a dedup
key with our own regular 1.5 s beacon from that peer, so the reply was mostly
eaten and the probing peer flipped us offline while we were alive — identical
to the Windows bug. Reply now happens before the dedup gate, with its own
independent 400 ms throttle (`lastReplied`) so the 2-3 duplicate copies of one
beacon still produce a single reply.

**Duplicate messages on retry** — `MessagingService.swift` `handleText` now
checks `HistoryStore.shared.entries(forPeerIP:)` for an existing `messageId`
before appending; a match re-sends `sent_receipt` only. Needed because
`deliverPending` (below) can legitimately re-send a message the receiver
already has.

**Pending-message queue had no in-flight guard** — unlike Windows (which only
drained on the offline→online transition), macOS's `upsertPeer` already calls
`deliverPending`/`deliverPendingFiles` unconditionally on every discovery
heartbeat (~1.5 s) — better base behavior, but `deliverPending` had zero
de-dup: a message still in flight from the previous heartbeat would be
re-sent, potentially firing several concurrent TCP connections for the same
message before any receipt came back. Added `pendingInFlight: Set<String>` +
`pendingLastTry: [String: Date]` with a 10 s backoff, mirroring the Windows
fix.

**TCP accept-loop spin** — `NetworkCoordinator.swift`'s inbound accept loop did
`guard clientSocket >= 0 else { continue }` with no backoff. A dead/invalidated
listener socket (or a failed bind at startup, which the original code didn't
even treat as fatal) would spin the loop at ~100% CPU while silently accepting
zero inbound connections — the Windows "can't receive anything until restart"
bug, present here too. Refactored `startTCPListener` to a reusable
`createListenerSocket(port:)` helper; the accept loop now backs off
(0.5 s × consecutive failures, capped at 5 s) and rebuilds the listener socket
after 3 consecutive failures.

**Dead unicast discovery hints** — `discovery.extraTargets` was wired to
`sessions.keys`, which is always empty because `ensureSession`/`PeerSession`
is dead code on both platforms (nothing ever calls `ensureSession`). This
mirrors the exact bug found and fixed on Windows. Added
`NetworkCoordinator.unicastHints: (() -> [String])?`, wired from
`AppModel.start()` to every saved contact's last-known IP (deduped, capped at
32). Unlike the Windows version, this does NOT filter out already-online
peers — `AppModel.peers` is `@MainActor`-isolated and reading it from
`DiscoveryService`'s background queue would be an actor-isolation violation.
Sending an extra unicast copy to an already-online peer is harmless (it's
deduped like the other broadcast/multicast copies), so the closure only reads
`ConfigStore.shared.config.contacts` — a plain non-actor singleton already
read cross-thread elsewhere in this codebase (e.g. `discovery.buildPayload`).

**File-transfer retry hammering** — `FileTransferService.swift`
`startNextIfIdle` is invoked via `retryQueue` on every heartbeat too. A peer
reachable by UDP discovery but not by TCP would get a fresh 10 s
connect-timeout attempt every ~1.5 s. Added `lastSendFailureAt: [String: Date]`
with a 15 s cooldown, mirroring the Windows fix.

## What did NOT need fixing (checked, found already correct)

- `HistoryStore.swift` `save()` — already encodes on the calling (MainActor)
  thread before handing an immutable `Data` blob to a background queue for
  encrypt+write; no "collection mutated during background serialization" race
  like Windows had. `String.write(to:atomically:true)` is already atomic.
- `ConfigStore.swift` `save()` — `Data.write(to:options:.atomic)` is already
  atomic; all mutation call sites are `@MainActor`-confined already, so no
  concurrent-writer race like Windows had.
- Inbound idle timeout — `SO_RCVTIMEO` on the raw blocking `recv()` socket
  actually works on Darwin (unlike .NET's `Socket.ReceiveTimeout`, which is a
  silent no-op for async reads). No fix needed.
- No timer "crash shield" equivalent was added: Swift's `throws`/`try` only
  covers explicitly-thrown `Error`s, not runtime traps (force-unwrap, index out
  of range), so a .NET-style try/catch wrapper around timer callbacks
  wouldn't actually protect against the same class of failures. Not
  attempted.
- Sleep/resume socket rebuild — macOS's `NetworkInterfaceMonitor` already uses
  `NWPathMonitor` + a 5 s safety-net poll (Apple's recommended, more reliable
  API vs. Windows's documented-as-best-effort `NetworkChange` events), so no
  explicit rebuild-on-wake was added; the existing poll already covers it.

## Validation

- `swift build` — clean, only pre-existing warnings (unrelated Sendable/
  never-mutated warnings in other files).
- `swift test --parallel --skip "MessageStatusTests/testHistoryStoreUpdateStatusIsRankAware"`
  — all 106 remaining tests pass (exit 0, no failure output).

### Local dev-machine test hazard (not a code bug — do not "fix" by editing KeyManager/HistoryStore)

`MessageStatusTests.testHistoryStoreUpdateStatusIsRankAware` calls
`HistoryStore.shared`, whose lazy singleton init reads
`~/Library/Application Support/LanMessenger/history.enc` and, if it exists,
decrypts it via `KeyManager.shared.privateKey` — a real macOS Keychain item
(service `com.dave.lanmessenger`, account `privateKey`). On a machine that has
actually run the shipped, differently-signed `LanMessenger.app` (true for this
dev machine — a real `history.enc` and Keychain entry exist here), the
`swift test` binary is a different, ad-hoc-signed executable, so Keychain's
ACL requires a one-time interactive authorization prompt. In a non-interactive
session there's no one to click "Allow", so the process hangs indefinitely
(confirmed: 6+ min elapsed, ~0.1 s CPU time — genuinely blocked, not slow).
`swift test --parallel` masks this well: only the one worker process holding
that test hangs, so the run stalls at ~103/106 instead of failing fast.

To run the full suite on a machine with real app data, either delete/rename
`~/Library/Application Support/LanMessenger/history.enc` first, or (safer)
always pass `--skip "MessageStatusTests/testHistoryStoreUpdateStatusIsRankAware"`
and treat that one test as covered by CI (which runs on a clean machine with
no pre-existing Keychain item, so it isn't affected). Do not change
`HistoryStore`/`KeyManager` to work around this — it is a property of this
specific developer machine, not the code.
