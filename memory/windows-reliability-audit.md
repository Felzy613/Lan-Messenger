---
name: Windows reliability + UI audit
description: Full Windows-client bug/UX audit and fixes (2026-07-01) — presence, delivery to macOS, discovery, crash shields, dark mode
type: project
---

# Windows reliability + UI audit (2026-07-01)

Full audit of `src/windows-native/` triggered by user reports: unreliable,
slow, online status wrong, random failures texting macOS, weak contact
discovery, dated UI. All fixes are Windows-side and wire-compatible; PROTOCOL.md
invariants untouched (one clarifying paragraph added, see below).

## Root causes found and fixed

Reliability / delivery (the "randomly can't text macOS" cluster):

- `MessagingService.FireTcpAsync` made a single TCP attempt; one lost SYN →
  message stuck "Queued". Now two attempts, 300 ms apart
  (`FireTcpOnceAsync`). Safe because a failed attempt at worst delivered a
  partial frame, which receivers discard.
- Pending queue drained ONLY on offline→online transition (macOS drains on
  every heartbeat). A transient failure against an online peer stranded the
  message until the peer bounced. `AppModel.UpsertPeer` now calls
  `DeliverPending` + `DeliverPendingFiles` on every heartbeat;
  `MessagingService` grew a per-message in-flight guard (`_pendingInFlight`)
  and 10 s retry backoff (`_pendingLastTry`) so heartbeat-driven retries can't
  double-send.
- `HandleText` now suppresses duplicate `message_id`s (re-sends `sent_receipt`
  only) — required once retries exist. Documented as SHOULD in PROTOCOL.md
  "text" section. macOS does NOT implement this yet (candidate follow-up).
- `FileTransferService.SendFileAsync` disposed the socket right after writing
  `file_end` — RST could drop tail frames (the exact bug previously fixed for
  text sends). Now mirrors FireTcp's linger + Shutdown(Send) + 2 s drain.
  Also added 15 s per-peer failure cooldown so heartbeat-driven `RetryQueue`
  can't pile up 10 s connect timeouts.

Presence ("online status very off"):

- `DiscoveryService.HandleReceived` deduped by `pubkey:type` BEFORE replying.
  A peer probing us (unicast `discovery`) shares a dedup key with its own
  1.5 s beacon, so probe replies were mostly eaten → the probing peer flipped
  us offline while we were alive. Reply now happens BEFORE the dedup gate with
  its own 400 ms throttle (`_lastReplied`). macOS has the same flaw
  (candidate follow-up in `handleReceivedData`).
- On resume from sleep, sockets are force-rebuilt (`Discovery.RebuildNow`)
  before re-beaconing.

Crash/process-death shields ("unreliable"):

- Beacon/heal timers are `System.Threading.Timer` — an escaped exception kills
  the process. Wrapped in `Guarded()`.
- `ExtraTargets` was wired to `_sessions.Keys` — always empty (EnsureSession is
  never called; PeerSession is dead code on both platforms) AND unsynchronized
  enumeration. Replaced with `NetworkCoordinator.UnicastHints`, set by AppModel
  to offline contacts' last IPs (≤32) → real cross-subnet/broadcast-filtered
  discovery. Guarded via `SafeExtraTargets()`.
- `App.xaml.cs` now logs `TaskScheduler.UnobservedTaskException` and
  `AppDomain.UnhandledException`; runtime errors no longer captioned
  "Startup Error".
- TCP accept loop: backoff on repeated failures + listener rebuild (dead
  listener used to spin at 100% CPU and silently kill all inbound messaging).
- Inbound idle timeout: `client.ReceiveTimeout` does nothing for async reads;
  replaced with per-frame linked-CTS 60 s timeout.

Data integrity:

- `HistoryStore.Save()` serialized live lists on a background thread → 
  "Collection was modified" swallowed → save silently lost. Now snapshots on
  the calling (mutating) thread, coalesces via `Interlocked.Exchange`d
  `_dirtySnapshot`, and writes temp-file + `File.Move(overwrite)` atomically.
- `ConfigStore.Save()` now lock-serialized + atomic replace (was racy from
  UI + background callers; corrupt config = silently lost contacts).

Perf:

- `EvaluatePresence` no longer allocates a dict copy every 1 s tick (only on
  actual change).

## UI/UX modernization

- Dark mode actually works now: `Theme.cs` dark palette was defined but never
  used. `Theme.Initialize(isDark)` swaps the code-behind brushes; MainWindow
  calls it at startup + on `ActualThemeChanged`. XAML-side colors moved to
  `App.xaml` ThemeDictionaries (`AppChatBackgroundBrush`,
  `AppReplyAccentBrush`, `AppReplyAccentTextBrush`, `AppOnlineDotStrokeBrush`,
  + Light/Dark/HighContrast).
- Hardcoded `#E5DDD5` chat backgrounds and `#18000000` hover fills (invisible
  in dark mode) replaced with theme resources / alpha-gray
  (`AppIconHoverBrush`, `AppIconPressedBrush`).
- All `Segoe MDL2 Assets` → `{ThemeResource SymbolThemeFontFamily}` (Fluent
  icons on Win11, MDL2 fallback on Win10); code-behind `FontIcon`s rely on the
  default family.
- Offline presence dot was 45%-alpha black (invisible on dark) → neutral gray
  `Theme.OfflineDotBrush`; dot stroke follows theme.
- Avatar colors: `string.GetHashCode()` is per-process randomized → colors
  reshuffled every launch. Now FNV-1a (`Theme.StableHash`). Note: macOS also
  uses randomized `hashValue`, so cross-platform color parity still doesn't
  exist — only per-launch stability.
- Empty-state placeholder added to MainWindow's right pane ("Select a
  conversation…").

## Validation status

- No Windows toolchain on the Mac used for this session: all 11 modified XAML
  files verified well-formed via XML parse; C# reviewed carefully but NOT
  compiled. Windows CI (`Build Windows`) must be the compile gate.
- Tests unaffected: they exercise model-level APIs (Append/Entries/Delete,
  MessageStatus, PacketValidator) that kept their signatures.

## Follow-up candidates (not done)

- Remove dead `PeerSession`/`EnsureSession` on both platforms, or actually use
  persistent sessions.

All macOS follow-ups below were completed in a same-day pass — see
[macOS reliability fixes](macos-reliability-fixes.md).
