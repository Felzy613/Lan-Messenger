# Architecture

This document explains how LAN Messenger works end to end: platform structure,
runtime lifecycle, networking, encryption, persistence, UI state, updates,
packaging, and the key failure modes developers need to keep in mind.

## System Overview

LAN Messenger is a local-network peer-to-peer chat system. Every app instance is
both a discovery broadcaster/listener and a TCP server/client. There is no central
server in the messaging path.

```text
+------------------+          UDP 54231           +------------------+
| macOS app         | <--------------------------> | Windows app      |
| Swift/SwiftUI     |       raw discovery JSON     | C#/WinUI 3       |
+------------------+                              +------------------+
          |                                                   |
          |                  TCP 54232                        |
          +<------------------------------------------------->+
                     framed JSON + encrypted payloads
```

The two implementations are intentionally parallel:

- `Core/Protocol` defines packet types, frame codec, and validation.
- `Core/Crypto` owns X25519, HKDF, AES-GCM, and key storage.
- `Core/Networking` owns interface monitoring, UDP discovery, TCP listener, and
  peer sessions.
- `Core/Persistence` owns config, encrypted history, pending queues, and transfer
  state.
- `Core/Services` owns messaging, file transfer, notifications, updates, and
  diagnostics.
- `UI` owns view models and native views.

## Primary Design Constraints

- Discovery must work on multi-interface machines.
- Protocol fields must remain stable across platforms.
- UDP discovery must remain unframed.
- TCP payloads must remain length-prefixed JSON.
- Message and file content must remain encrypted end to end.
- Private keys must never live in config JSON.
- Local history must remain decryptable across app upgrades.
- UI must stay responsive during large file transfers.
- Packaging and update behavior must be testable in CI.

## Platform Matrix

| Concern | macOS | Windows |
|---|---|---|
| Language/runtime | Swift 5.9 | C#/.NET 8 |
| UI | SwiftUI | WinUI 3 |
| App lifecycle | SwiftUI `App`, `Window`, `MenuBarExtra`, AppKit delegate | `Application`, `MainWindow`, H.NotifyIcon tray |
| Build for dev | SwiftPM | Visual Studio MSBuild |
| App packaging | XcodeGen + xcodebuild + shell packaging | MSBuild publish + Inno Setup |
| Private key store | Keychain | DPAPI |
| Notifications | UserNotifications | Windows App Notifications |
| Launch at login | `SMAppService.mainApp` | Installer/registry/tray preferences |
| Update install | Download ZIP, verify, helper script replaces `.app` | Download EXE, verify, silent elevated Inno installer |
| Logs | Application Support logs plus os_log | `%APPDATA%\LanMessenger\Logs` plus debugger |

## Source Tree

```text
src/macos/
  Package.swift
  project.yml
  LanMessenger/
    App/
    Core/
      Protocol/
      Crypto/
      Networking/
        Media/            remote-desktop media transport and codec
      Persistence/
      Services/
    UI/
  LanMessengerTests/

src/windows-native/
  LanMessenger.sln
  LanMessenger/
    Core/
      Networking/
        Media/            mirror of the macOS media transport
    UI/
  LanMessenger.Tests/
  LanMessenger.iss

spikes/                   throwaway diagnostics, not part of either app
```

Use [FILE_MAP.md](FILE_MAP.md) for the detailed inventory.

## Runtime Lifecycle

### macOS

1. `LanMessengerApp` creates `AppModel` as a `@StateObject`.
2. `LanMessengerAppDelegate.applicationWillFinishLaunching` applies the
   persisted dock policy before SwiftUI creates windows.
3. `LanMessengerAppDelegate.applicationDidFinishLaunching` starts
   `DockPolicyGuard` and calls `WindowController.showMainWindow()`, which
   surfaces the main window and pulls the app to the front. Without the explicit
   surface, a launch lands behind whatever was already on screen, and a launch at
   login can come up with the `Window` scene never materialised at all.
4. `AppModel.init` wires service delegates and calls `start`.
5. `start` sets a non-default username from `NSFullUserName`, starts networking,
   requests notification permission, loads history, starts timers, checks legacy
   migration, applies dock/login-item policy, and schedules update checks.
6. The main `Window` hosts `ContentView`, which contains `NavigationSplitView`
   with `SidebarView` and `ChatView`.
7. `MenuBarExtra` remains available after the main window closes and can reopen
   the app. Both `ContentView` and the `MenuBarExtra` label capture SwiftUI's
   `openWindow` action into `WindowController`; the menu-bar copy is the one
   that always runs, because the status item renders even when no window does.

### Windows

1. `App.OnLaunched` creates and activates `MainWindow`.
2. `MainWindow` creates `AppModel` with the current `DispatcherQueue`.
3. `AppModel.Start` logs crypto runtime diagnostics, sets a non-default username
   from `Environment.UserName`, starts networking, registers notifications, loads
   history, starts timers, checks migration, and schedules update checks.
4. `MainWindow` owns sidebar, content frame, contacts/settings dialogs, archived
   page, and tray commands.
5. `AppWindow.Closing` hides to tray when `close_to_tray` is enabled unless the
   user explicitly quits.

## Core Data Flow

```text
DiscoveryService
  -> NetworkCoordinator
  -> AppModel.upsertPeer
  -> contacts/history migration
  -> refresh sidebar conversations

TCP listener / PeerSession
  -> FrameCodec
  -> PacketValidator
  -> NetworkCoordinator callbacks
  -> MessagingService or FileTransferService
  -> HistoryStore / FileTransferStore
  -> AppModel published state
  -> native UI
```

## Network Architecture

### Interface Monitoring

`NetworkInterfaceMonitor` publishes eligible IPv4 adapters.

Eligibility:

- interface is up;
- not loopback;
- has IPv4 unicast address;
- not APIPA `169.254/16`;
- not `0.0.0.0`.

macOS uses `NWPathMonitor` plus a 5-second polling safety net and `getifaddrs`.
Windows uses `NetworkChange` events plus a 5-second polling safety net and
`NetworkInterface.GetAllNetworkInterfaces`.

The monitor intentionally tracks LAN availability, not internet availability.

### Discovery Service

Discovery owns:

- one receive socket bound to `0.0.0.0:54231`;
- one send socket per eligible interface;
- multicast membership joined per interface;
- a 1.5-second beacon timer.

Every beacon includes the current username, TCP port, public key, and local IPs.
The username is read fresh from config on each beacon so Settings changes do not
require restarting networking.

The service sends to:

- directed subnet broadcast;
- multicast group;
- limited broadcast;
- extra unicast targets.

It also emits a one-shot `goodbye` datagram on departure (quit, sleep, network
loss) and can `probe` a single peer with a unicast `discovery` to reconfirm
liveness. Incoming `goodbye` packets are routed to a separate departed callback —
they are never replied to and never refresh `last_seen`.

Windows additionally disables UDP connection reset behavior with
`SIO_UDP_CONNRESET`.

**Threading invariant.** The blocking receive loop must run on a queue of its
own, never one shared with the beacon timer or the interface-change socket
rebuild. The loop never returns, so sharing a serial queue starves every item
behind it for the process lifetime: beacons stop (the host answers probes but
never announces itself, so peers cannot discover it) and sockets are never
rebuilt after a DHCP change, leaving every send failing `EADDRNOTAVAIL`. macOS
uses a dedicated `recvQueue`; Windows runs the loop as its own async `Task`.
Both are covered by `DiscoveryServiceQueueTests` on macOS.

### Presence

Online/offline status is LAN-local and driven by a per-peer state machine, not a
single timestamp comparison. The pure decision core is `PresenceEvaluator`
(mirrored on both platforms and unit-tested): given `last_seen` and `now` it
returns Online (`< 5 s`), Probing (`5–12 s`, still shown online but unicast-probed
each tick), or Offline (`≥ 12 s`). `AppModel` runs the evaluator about once a
second, issues probes for quiet peers, flips an explicit `presence` field on
transitions, and prunes non-contact peers that stay offline beyond five minutes.

A heartbeat (discovery/reply or any inbound TCP packet) marks a peer online; a
`goodbye` marks it offline immediately; losing the local network marks every peer
offline at once. Offline peers are retained in the dictionary (their public key is
needed to queue/relay), so callers that need reachability test `IsOnline`/
`isOnline` rather than mere presence in the map. The cloud relay carries messages
only and never participates in presence.

Windows also treats a successful *outbound* TCP send (`OnPeerReachable` on
`MessagingService`/`FileTransferService`) as a heartbeat, synthesizing a live
peer entry from saved contact/session-cache data if one doesn't exist yet. This
covers machines where discovery *reception* is broken (multicast/UDP blocked, a
Hyper-V/WSL virtual adapter confusing the Windows Firewall network-profile
classification) but direct TCP delivery still works — otherwise presence
flickers offline during any lull between exchanges even though the peer is
reachable the whole time. See [PROTOCOL.md](../PROTOCOL.md#presence).

### Network Coordinator

`NetworkCoordinator` owns the high-level network lifecycle:

- starts/stops `NetworkInterfaceMonitor`;
- starts/stops `DiscoveryService`;
- listens on TCP `54232`;
- validates inbound TCP frames;
- exposes packet/discovery callbacks to `AppModel`;
- optionally manages persistent `PeerSession` instances.

Most current message/file sends use one-shot TCP connections for protocol
simplicity. `PeerSession` remains available for persistent queued frame sending.

### Peer Sessions

`PeerSession` is one persistent connection to one peer IP/port. It reconnects
with backoff and serializes outgoing frames. It is not the only send path; services
also fire one-shot TCP frames.

## Protocol Architecture

`Core/Protocol` is intentionally small and mirrored:

- `PacketTypes` contains Codable/JsonSerializer packet definitions.
- `PacketValidator` converts untrusted JSON into validated packet objects.
- `FrameCodec` handles TCP frame encode/decode.

Protocol invariants are documented in [../PROTOCOL.md](../PROTOCOL.md).

Critical boundaries:

- UDP datagrams are raw JSON.
- TCP packets are framed JSON.
- discovery uses `public_key_b64`;
- TCP packets use `sender_public_key_b64`;
- reply metadata is optional;
- invalid input is dropped rather than repaired.

## Cryptography Architecture

### Identity Keys

Each installation generates one X25519 identity keypair.

- Public key is advertised in discovery and packet metadata.
- Private key stays local and protected by platform secure storage.

`KeyManager` loads or creates the key at startup and exposes public key bytes and
base64 for other services.

### Session Encryption

`SessionCrypto` derives a symmetric key per peer using:

```text
X25519(my_private, peer_public)
HKDF-SHA256(empty salt, info="lan-messenger", length=32)
AES-256-GCM
```

The AES-GCM tag is appended to ciphertext before base64 encoding so Swift,
Windows, and legacy Python agree on byte layout.

### History Encryption

`HistoryCrypto` derives an encryption key from the raw local private key:

```text
HKDF-SHA256(empty salt, info="lan-messenger-history", length=32)
AAD = "history-v1"
```

The history file is local-only; it is not sent across the network.

## Persistence Architecture

### ConfigStore

Config is JSON under the platform app-data directory. It stores:

- username;
- saved contacts;
- hidden and archived conversation IPs;
- pending messages;
- pending files;
- inbox path;
- update source;
- platform preferences such as dock/tray/login behavior.

It does not store private keys.

### HistoryStore

`HistoryStore` loads and saves encrypted history through `HistoryCrypto`.

History shape:

```text
peer IP -> list of MessageEntry
```

This IP-keyed model is a compatibility constraint. `AppModel` compensates by
migrating history when a saved contact reappears with the same public key at a
new IP.

The message status update path is rank-aware. This prevents race conditions where
a late local "Sent" update overwrites a remote `sent_receipt` or `read_receipt`.

UI rendering of the status field is intentionally lossy: every pre-delivery
state (`Sending`, `Queued`, `Sent`, or unset) collapses to a single grey
checkmark in both apps. `Delivered` renders as a double grey check, `Read` as
double blue, and `Failed` as a red `✗`/`!`. There are no clock or pending
glyphs — modern messengers (WhatsApp, iMessage, Signal) all use this
collapsed convention, and keeping the icon set small avoids confusing flips
between "in flight" representations. The underlying model still stores the
distinct states so the rank-aware logic can reason about them.

### FileTransferStore

The store tracks:

- in-progress incoming transfers by `(sender IP, transfer_id)`;
- per-peer outgoing file queues;
- active outgoing peer set.

Incoming transfers write to `.part` files until `file_end`, then rename to a
deduplicated final path.

## Messaging Service

`MessagingService` handles text, typing, receipts, pending message retry, and
cloud relay dispatch.

Send flow:

1. Create `message_id`.
2. Append outgoing history entry with `Sending`.
3. Encrypt text with `message_id` AAD.
4. Build `text` packet with optional reply metadata.
5. Send a one-shot TCP frame. On Windows the one-shot send makes two
   attempts (a short pause between them) before it is considered failed —
   a single lost SYN no longer strands the message.
6. Mark `Sent` or queue pending and mark `Queued`.
7. If the peer's `relay_id_hash` is known and the send failed, **also POST the
   ciphertext to the cloud relay Worker** so delivery can proceed even if this
   device goes offline before the peer reconnects.

Queued messages are retried on every discovery heartbeat from the peer, not
only on the offline→online transition, so a transient TCP failure against an
online peer self-heals within a beacon interval. Both platforms guard this
with a per-message in-flight set and a retry backoff (~10 s) so heartbeat-
driven retries can't produce duplicate concurrent sends or connect-timeout
pileups.

Receive flow:

1. If `message_id` already exists in history, treat as a duplicate from a
   sender retry: re-send `sent_receipt` and stop.
2. Decrypt with `message_id` AAD.
3. Append incoming history entry.
4. Clear typing state.
5. Send `sent_receipt`.

Read flow:

1. `AppModel.markConversationRead` sends `read_receipt` for incoming unread
   messages.
2. It marks `read_receipt_sent` in memory and history.

Delete flow:

1. "Delete for me" (any message, either direction) is local-only: the entry is
   removed from history and the in-memory conversation; no packet is sent.
2. "Delete for everyone" applies only to the sender's own outgoing messages
   with a `message_id`. The local history entry is marked `deleted` (text and
   reply-preview fields cleared) and a `delete_message` packet — same shape as
   `sent_receipt`/`read_receipt`, unencrypted — is sent to the peer over a
   one-shot TCP connection.
3. On receipt, the peer marks its matching history entry `deleted` the same
   way and the UI renders a "This message was deleted" placeholder. The
   conversation list preview shows the same placeholder text when the last
   message in a thread is deleted.

## Cloud Relay

`RelayClient` (singleton, one per platform) provides an HTTP fallback delivery
path using a Cloudflare Workers endpoint backed by KV storage.

**Why:** The existing LAN queue (in `config.json`) re-delivers messages only when
the sender's app is running and online. If Alice's machine is off when Bob
reconnects, Bob never receives Alice's queued messages. The cloud relay closes
this gap.

**How:**

1. Each device derives `relay_id = SHA256(private_key || "relay-v1")` at startup.
2. `relay_id_hash = SHA256(relay_id)` is published in every discovery packet so
   peers know where to address cloud-relay messages.
3. When a message send fails, `RelayClient.store()` POSTs the ciphertext to the
   Worker under the recipient's `relay_id_hash`. A `Send/TCP failed … falling
   back to relay` log line is emitted to make the fallback visible in
   `client.log`.
4. `RelayClient.fetchPending()` retrieves any waiting ciphertext blobs from the
   Worker (authenticated by presenting `relay_id` and letting the Worker verify
   `SHA256(relay_id) == relay_id_hash`). Polls run on three triggers:
   - immediately at app startup;
   - every 30 s on a foreground timer;
   - whenever the LAN transitions from unavailable to available.
   Each fetch logs `Relay fetch start reason=<startup|poll|network-up>` and the
   outcome (count of pending messages or empty).
5. Retrieved messages are decrypted in `MessagingService.handleRelayMessage()` and
   removed from the Worker via `RelayClient.delete()`.

**Privacy:** The Worker stores only ciphertext already encrypted to the
recipient's X25519 key. Cloudflare cannot read message content. Metadata
(ciphertext size, hashed mailbox address, timing) is visible to Cloudflare.

**Graceful degradation:** If `relayWorkerURL` is empty in config, or any HTTP
call times out (6-second connect / 10-second total), the relay path is silently
skipped and the app behaves identically to before this feature was added.

**Worker endpoint:** `https://lan-messenger-relay.shoparmorex.com` (Workers Custom
Domain; the shared `workers.dev` subdomain is avoided because Cloudflare's
account-wide Bot Fight Mode cannot be exempted per hostname on it)
**KV namespace:** `lan-messenger-relay` (TTL 72 h per message)

## File Transfer Service

`FileTransferService` sends and receives encrypted files while keeping UI work
off the hot path.

macOS:

- public API and callbacks are on the main actor;
- outgoing blocking I/O runs on a dedicated serial dispatch queue;
- incoming chunk decrypt/write runs on a serial queue to preserve chunk order;
- progress callbacks are throttled to roughly 12 Hz.

Windows:

- packet handling is on the UI thread through `DispatcherQueue`;
- each incoming transfer owns a channel consumed by one background task;
- finalization is queued through the same channel to run after all writes;
- outgoing I/O runs in background tasks;
- progress callbacks are throttled to roughly 12 Hz.

A failed outgoing file remains queued and is retried when the peer reconnects.

## AppModel

`AppModel` is the root state object on both platforms.

It owns:

- discovered peers keyed by public key;
- active and archived conversation lists;
- selected peer IP;
- message lists keyed by peer IP;
- typing states;
- active transfer banners;
- migration prompt state;
- update availability/progress;
- LAN availability.

It wires callbacks from services to UI state and persistence. It also owns
conversation actions:

- archive/unarchive;
- delete/hide conversation;
- delete contact;
- add/update contact;
- start a new conversation;
- mark conversation read;
- queue or send files;
- deliver pending messages/files after peer discovery.

Important rule: random discovered peers do not automatically become conversations.
Conversation rows are created from saved contacts or existing history.

## UI Architecture

### Thread Scrolling

Both platforms follow the same rule, so a conversation behaves identically on
either side:

- Opening a conversation, re-showing the window, and sending a message always
  land on the newest message.
- An *incoming* message only scrolls the thread when the newest message is
  already on screen (within 40 pt / px of the bottom). Someone reading back
  through history is never yanked away from what they are reading.
- Whenever the newest message is off screen, a floating chevron button appears
  over the bottom-right of the thread and jumps back to it.

Two things make that harder than it reads, and both platforms handle them the
same way.

**One scroll is not enough.** When the "a message arrived" callback runs, the
new row has not been laid out, so the scroll lands on the *old* bottom and
leaves the message that caused it just off screen. Worse, a media bubble starts
at a placeholder size and grows when its thumbnail finishes decoding, which can
be hundreds of milliseconds later. Both platforms therefore *repeat* the scroll
for about 0.6 s after it is asked for — macOS from
`ChatView.pinToBottom(proxy:animated:)`, Windows from `ChatPage`'s settle
`DispatcherQueueTimer` — and separately follow the content down whenever it
grows while the thread is pinned.

**"Am I at the bottom?" cannot be asked at any moment.** Measured while a
scroll is settling, or in the same pass as content that just grew, the answer
is "adrift by exactly the amount that changed" — and a thread that believes
that stops following new messages altogether. So neither platform re-measures
per event: each keeps a latched `pinnedToBottom` / `_pinnedToBottom` flag, set
when the thread is scrolled to the bottom and cleared only by a reading taken
at rest with the content at the size it already was.

Windows reads the position straight off the `ListView`'s inner `ScrollViewer`
(`ChatPage._scroll`, cached once after layout). `OnScrollViewChanged` compares
`ExtentHeight` against the last one it saw to tell growth from scrolling, and
an intermediate (drag/wheel) event cancels an in-flight settle so the reader
always wins. Content growth that raises no `ViewChanged` at all arrives as
`SizeChanged` on the scroll viewer's content.

macOS has to measure it. SwiftUI exposes no scroll offset for a `ScrollView`
before macOS 15, so `ChatView` reads the viewport height from a `GeometryReader`
in the scroll view's `.background`, and both content edges from zero-height
`GeometryReader` sentinels placed before and after the message `VStack`. They
report through a *single* `ScrollGeometryKey` preference carrying `top` and
`bottom` together: `bottom - viewportHeight` is the distance still to scroll and
`bottom - top` is the content height, and read through separate callbacks the
distance can arrive before the height it belongs to — at which point growth is
indistinguishable from the reader scrolling away. The sentinels have to be real
siblings of the content: a preference raised inside a `.background()` subtree
never reaches `onPreferenceChange` (verified on macOS 13/14 — the value sits at
its default forever), which is also why the viewport height is written from
`onAppear` / `onChange` rather than through a second preference key.

Re-showing the window is its own case on both sides, because the view is never
unloaded: macOS re-pins from `onChange(of: controlActiveState)`, Windows from
`ChatPage.OnWindowShown()`, called by `MainWindow` on un-minimize and on restore
from the tray.

### Typing Indicator

An inbound `typing` packet with `active: true` shows the same thing on both
platforms: three dots that swell and brighten in a staggered wave — 0.6 s out,
0.6 s back, each dot 0.2 s behind the one before it. They appear in three
places:

- at the end of the thread, inside an incoming-style bubble, sitting where the
  peer's message is about to land;
- under the peer's name in the chat header, in place of the Online/Offline
  caption, so the state is still visible when the thread is scrolled up;
- in the sidebar row, as an accent-tinted capsule covering the message preview.

The thread bubble grows the thread, so it follows the scrolling rule above: it
only pulls the view down when the reader is already at the bottom.

macOS: `UI/TypingIndicatorView.swift` (`TypingDotsView`, `TypingBubbleView`).
The bubble lives in a wrapper that is always in the message `VStack` — empty and
zero-height when nobody is typing — which both scopes the insert/remove
animation and gives `ChatView` a stable `threadEndID` to scroll to. Reduce
Motion drops the wave and leaves the dots standing.

Windows: `UI/TypingIndicatorControl.xaml(.cs)`, driven by
`ChatPage.UpdateTypingIndicator`. The thread copy is the `ListView`'s `Footer`,
so it scrolls with the messages and never enters the item collection or any
merge path. The storyboard animates `Opacity` and a `ScaleTransform` — both
independent targets — and is stopped whenever the control is hidden or unloaded
rather than left ticking behind a collapsed element.

### Design Tokens

Both apps take their colours, radii, sizes and type ramp from one file,
`design/liquid-glass/tokens.json`, the source of the LAN Messenger Glass design
system. `scripts/design/gen_tokens.py` turns it into:

- `src/macos/LanMessenger/UI/GlassTokens.swift`: `Color`s that follow the view's
  appearance (a dynamic `NSColor` per token), plus `GlassTokens.Raw` pairs of
  ARGB values for code holding an explicit `ColorScheme`.
- `src/windows-native/LanMessenger/UI/GlassTokens.cs`: `<Name>Light` and
  `<Name>Dark` `Color`s and constants, for code-behind (`Theme.cs`).
- `src/windows-native/LanMessenger/Styles/GlassTokens.xaml`: theme dictionaries
  for XAML `ThemeResource` lookups, including the acrylic brushes. High
  Contrast has no acrylic or gradients and maps every token to a system colour
  by role.

The generated files are committed, and PR checks run `gen_tokens.py --check` on
both jobs, so a token edited without regenerating, or a generated file edited
by hand, fails CI. `GlassTokensTests` on both platforms asserts WCAG contrast
floors for every text-on-surface pairing the design uses, compositing each
translucent surface over the wallpaper and over its glow and taking the worse,
so a colour tuned below legibility fails a test.

### Glass Chrome Over The Thread

On Windows the chat pane is one cell, not a stack of opaque rows. Back to
front: `ThreadBackground` (the wallpaper and its two radial glows),
`MessagesList` on a transparent background, the top chrome (header capsule and
transfer banner), the jump button, the composer, and the drop overlay. The
header, composer, transfer banner and jump button are in-app acrylic because
the thread moves under them; bubbles and the sidebar panel are translucent
solid brushes (Mica is already the sidebar's blur, and only the wallpaper is
behind a bubble). `ChatPage.UpdateThreadInsets` keeps the list's `Padding`
equal to the chrome's size plus 16: the ListView template puts that padding
on the `ItemsPresenter` inside the `ScrollViewer`, so it scrolls with the
content and the existing pin-to-bottom logic lands the last message clear of
the composer without knowing the composer exists.

On macOS 26 the header and composer are glass too, but they stay rows above
and below the thread: `ChatView`'s scroll geometry assumes the viewport is the
visible area, and letting the thread run under them (`.safeAreaInset`) needs
that geometry corrected first (plan step M5). Before macOS 26 both keep `.bar`.

WinUI detail worth knowing: Fluent brushes read from inside a generic.xaml
template's visual-state storyboard (a focused field's underline, a toggle's
on-track) cannot be overridden from App.xaml; they resolve from the control's
own tree and then the style's defining dictionary first. Override them per
instance (`ToggleSwitch.Resources`), or replace the template where Fluent's
cannot be talked out of a behaviour. `Styles/Glass.xaml` does that for dialogs
(`GlassDialogStyle`: Fluent's restyles the default button square and accent),
text fields (`GlassTextFieldStyle`, `GlassSearchFieldStyle`) and list rows
(`GlassListItemStyle`: Fluent's `ListViewItemPresenter` draws square
highlights whatever corner radius it is given). The main window draws its own
title bar on the Mica, so the system's accent-coloured one never appears.

### macOS UI

Important files:

- `App/LanMessengerApp.swift`: app entry, AppKit delegate, main window,
  menu-bar extra, migration sheet.
- `UI/AppModel.swift`: root observable state.
- `UI/Sidebar`: conversations, new-message picker, archive sheet.
- `UI/Chat`: header, message list, composer, file transfer banner, message
  bubbles, reply interactions.
- `UI/Settings`: identity, dock policy, login item, inbox, updates, version.
- `UI/Theme.swift`, `UI/Glass.swift`, `UI/GlassTokens.swift`: colours from the
  tokens, the glass helper, and the generated tokens.

The app can hide from the Dock and live in the menu bar. Closing the last window
does not terminate the app.

Dock presence is not a one-shot setting. AppKit promotes an `.accessory` process
back to `.regular` on its own — materialising a `Window` scene, running a modal
panel, being re-activated after the updater relaunches the bundle — and there is
no notification for "the activation policy changed". `DockPolicyGuard`
(`Core/Services/DockPolicyGuard.swift`) re-asserts the `hide_from_dock`
preference on app-activation and window-key notifications plus a 3 s safety-net
tick, and is the single owner of the policy: the Settings toggle
(`AppModel.applyDockPolicy`) and `WindowController` both go through it.

### Windows UI

Important files:

- `App.xaml.cs`: startup exception capture and crash logging.
- `MainWindow.xaml(.cs)`: shell, sidebar/content layout, tray icon, dialogs,
  hide-to-tray lifecycle.
- `UI/AppModel.cs`: root observable state.
- `UI/Sidebar`: conversation list, contacts, archive, contact dialogs.
- `UI/Chat`: chat page, composer, message bubbles, file banner.
- `UI/Settings`: identity, inbox, update settings, tray preferences.
- `UI/Theme.cs`, `UI/GlassTokens.cs`, `Styles/Glass.xaml`,
  `Styles/GlassTokens.xaml`: brushes and styles from the tokens.

The tray icon is always present. Closing can hide to tray based on config.

`AppModel.TotalUnreadCount` is recomputed every time conversations refresh (sum
of unread counts across active, non-archived conversations). `MainWindow`
observes this property and toggles two unread indicators in lockstep:
a small red-dot overlay on the taskbar button via `ITaskbarList3.SetOverlayIcon`
(`Assets/BadgeDot.ico`), cleared (`SetOverlayIcon(hwnd, null, null)`) once the
count returns to zero; and the system tray icon itself, swapped between
`Assets/icon.ico` and a pre-composited `Assets/icon_unread.ico` (the app icon
with a baked-in red dot) via `TrayIcon.IconSource`, since `TaskbarIcon` has no
separate overlay slot for the notification-area icon.

The composer keeps an in-memory per-conversation draft (`AppModel.Drafts`,
keyed by peer IP, not persisted) so switching conversations without sending
restores the typed text; the draft is cleared once the message is sent.

Screenshot capture (`ChatPage.OnScreenshotRequested`) offers "Select region...",
"Full Screen", or a specific window via `ScreenshotWindowPickerDialog`.
"Select region..." captures the whole primary display first, then shows
`RegionSelectOverlayWindow` — a borderless, topmost, full-display overlay —
for a click-and-drag rectangle selection; the backing capture is cropped to
that rectangle via `ScreenshotService.CropToRegionAsync`. A click without a
drag falls back to the full-display capture. This covers the primary display
only; multi-monitor region selection and per-window hover highlighting are
follow-ups.

## Message Editing

A sender can replace the body of a text message they already sent. The flow
mirrors "delete for everyone": the local copy is updated first, then an
`edit_message` packet carries the new body to the peer, encrypted exactly like
the original `text` (AAD = the original `message_id`).

- `HistoryStore.applyEdit` / `ApplyEdit` is the single choke point on both
  platforms and holds all the rules: only text messages, never attachments or
  deleted messages, and — the security-relevant one — an inbound edit may only
  rewrite a message that came *from* that peer. See PROTOCOL.md → edit_message.
- The composer doubles as the editor. Picking "Edit" loads the message into the
  composer, swaps the send glyph for a checkmark, and shows the banner strip
  (shared with reply mode, which it is mutually exclusive with). Escape backs
  out; the in-progress draft that edit mode displaced is restored.
- Edited bubbles render an "edited" marker next to the timestamp. `timestamp`
  keeps the original send time, so the message stays where it was in the thread.
- The LAN write is best-effort — one TCP write, no retry. When it fails, the
  edit (or delete) is carried through the cloud relay as a **control record**:
  an ordinary relay record whose plaintext is a control envelope rather than a
  chat body, stored under its own fresh `message_id`. The peer applies it on
  their next poll. The Worker is unchanged and still sees only ciphertext — it
  cannot tell a control record from a message, and never learns which message
  was edited. See PROTOCOL.md → Relay Control Records.
- The fresh id matters: the Worker dedups `/store` by `message_id` and answers a
  repeat with `{ok:true,duplicate:true}`, so re-posting an edited body under the
  original's id is silently discarded while reporting success. That is also why
  an edit does *not* clear the queued message's `relay_stored` flag — doing so
  looks like it re-uploads the new text and does not.
- If the original is still in the sender's pending queue, its text is rewritten
  in place as well, so a later direct delivery carries the edited version.
- With no `relay_id_hash` for the peer and a failed TCP write, the change stays
  local and the peer keeps the original text.

## Attachment Entry Points

Every route into an attachment converges on the same call — `sendFile` on macOS,
`SendFile` on Windows — so queueing, offline persistence, and history all behave
identically no matter how the file arrived:

- **File picker** — the paperclip button.
- **Screenshot** — capture flow above.
- **Drag and drop** — the drop target is the whole conversation, not just the
  composer strip. macOS: `ChatView.onDrop` with a dashed-border overlay;
  Windows: `AllowDrop` on the `ChatPage` root grid with the `DropOverlay`
  border. `ComposerView`'s `NSTextView` calls `unregisterDraggedTypes()` so a
  file dropped on the text area is sent rather than inserted as a path string.

  On Windows the same handlers are also registered directly on `MessagesList`
  and `Composer` via `AddHandler(..., handledEventsToo: true)`
  (`ChatPage.WireDropTargets`). The drag events bubble, but the ListView covers
  nearly the whole thread and has its own class handling for them, so relying on
  a single handler at the page root leaves the outcome up to whether a child
  marked the event handled. Two further constraints on the Windows side, both
  the kind that fail silently:
  - `DragEnter`/`DragOver` must stay **synchronous**. Awaiting there returns
    control to the drag source, which reads `AcceptedOperation` at that instant
    and treats the not-yet-assigned value as a refusal — the drop is never
    offered, and the data object is left in a state that breaks later drags too
    (microsoft-ui-xaml#8108). Inspect the payload in `Drop`, under a deferral.
  - Nothing in a drag handler may throw. `DragUIOverride` is null for some drag
    sources and throws a bare `COMException` on others
    (microsoft-ui-xaml#9296); an unhandled throw out of one of these handlers
    ends the process.

  `ChatPage` logs one `Attachment` line per drag session with the data package's
  formats. If a drop does nothing and that line is absent, no drag event reached
  the app at all, which is a Windows-side block rather than an app bug — most
  often the app running elevated (Explorer will not hand a drag up an integrity
  level), or UAC disabled machine-wide (`EnableLUA=0`), which breaks drop into
  WinUI 3 apps outright.
- **Paste** — Ctrl/Cmd+V with files or a bitmap on the clipboard. The
  precedence is shared across platforms (`AttachmentPasteboard.decide` /
  `ClipboardAttachments.Decide`): files beat a bitmap, and a bitmap only wins
  when there is no text to paste instead. That last rule keeps ordinary text
  pastes working from browsers, Word, and Outlook, which put an image flavour on
  the clipboard alongside their text.
- **Drag out** — an attachment bubble whose file still exists is itself a drag
  source, so a received or sent file can be dragged into Finder/Explorer, a mail
  compose window, or any other app. macOS uses `.onDrag` with
  `NSItemProvider(contentsOf:)`; Windows sets `Border.CanDrag` and fills the data
  package with a `StorageFile` under a `DragStarting` deferral. Both hand over a
  real file rather than a path string.

A pasted bitmap has no file of its own, so it is written to the configured
screenshot folder (`screenshot_dir`, default
`~/Downloads/LAN Messenger Screenshots`) as `Pasted image <timestamp>.png`.
Deliberately not the system temp directory: history stores absolute paths, and
temp is swept between reboots, which turns the bubble into "File no longer
available" a day later.

## Remote Desktop

Remote desktop lets a peer view a contact's screen and, after a separate grant,
drive its keyboard and mouse. The wire format is specified in PROTOCOL.md →
Remote Desktop; **status, the remaining plan, and the accumulated gotchas live
in [REMOTE_DESKTOP.md](REMOTE_DESKTOP.md)**.

**Shipped on both platforms in v2.0.0.** Capture, encode, transport, decode,
input injection and the consent UI all exist on macOS and Windows, and the
full round trip — invite through video through control grant through
input — has been run between two real machines in both directions.

### Transport

`src/{macos,windows-native}/.../Core/Networking/Media/` implements the media
channel. Layering, chosen so that only the socket adapter is untestable:

- **Pure logic.** `MediaFrame`/`MediaFrameCodec` (22-byte header, seal/open),
  `MediaWriteScheduler` (fragmentation, interleaving, drop policy),
  `MediaReassembler` and `MediaSequenceGate`. No sockets, no clock, no threads.
  Both platforms assert the shared `media_frame_vector.json`.
- **Loops.** `MediaFrameReader`/`MediaFrameWriter` do all I/O through
  `MediaLink`/`IMediaLink` — three methods, the entire socket surface.
- **Socket.** `SocketMediaLink` adopts a detached descriptor, clears the
  inherited read timeout, sets `TCP_NODELAY`, and caps the send buffer at 128 KiB
  so the kernel cannot re-absorb what fragmentation just split up.

`MediaSession` owns three execution contexts — read, write, timers — that never
share. `RemoteSessionRegistry` holds the ~10 s accept window, one in-flight
session per peer, and single-shot `session_id` lookup, with an injected clock.
`RemoteDesktopService` is the app-facing surface and is deliberately not
main-thread affine: `attachInbound` runs synchronously on the inbound socket's
own thread, because the descriptor must leave the JSON loop before that loop
reads again.

The upgrade happens inside each platform's existing `handleInbound`: a validated
`media_attach` hands the socket to the media subsystem and returns, and socket
ownership becomes conditional rather than unconditional so nothing double-closes.
No new port, no firewall rule, no installer change.

### Session crypto

`Core/Crypto/RemoteSessionCrypto.{swift,cs}` derives per-session media keys with
a Noise-KK-shaped triple DH over the long-term X25519 identity keys the app
already pins per contact — there is no signing key in this system to work with.
It is separate from `SessionCrypto` because the properties differ: message
crypto is one-shot and stateless, media crypto is long-lived, directional, and
forward-secret.

The initiator is always the peer that sent `remote_invite`, and the role is
bound into the transcript. Nonces are counters, never random. A dropped socket
ends the crypto session; reconnect performs a fresh handshake with new
ephemerals, because reusing keys with a reset counter is catastrophic GCM nonce
reuse.

Both platforms assert the shared `remote_handshake_vector.json`.

### Video

`H264Bitstream.{swift,cs}` converts between the two H.264 packagings — AVCC
(VideoToolbox: length-prefixed NAL units, parameter sets out-of-band in the
format description) and Annex-B (Media Foundation: start codes, parameter sets
in-band before every IDR). Neither decoder accepts the other's packaging, and
the failure is not a clean error but a picture that never appears. It is pure
byte manipulation with no platform media types, so it is tested against
`windows_h264_sample.h264`, a real Microsoft encoder artefact, rather than
against its own output.

`H264Encoder.swift` is a `VTCompressionSession` configured for low latency:
High profile, no frame reordering, zero frame delay, BT.709 tags, parameter sets
re-read on every keyframe. A stream it produced has been decoded successfully by
Media Foundation on real Windows hardware.

`H264Decoder.swift` is the receive half, and is not a decoder in the obvious
sense: `AVSampleBufferDisplayLayer` does the decoding, so what this owns is the
part the layer cannot do for itself — splitting access units on slices, and
rebuilding the `CMVideoFormatDescription` from the parameter sets that arrive
in-band ahead of every IDR. It emits nothing until it has seen one, because a
P-frame handed to a cold decoder is not a recoverable glitch. `VideoPresenter`
abstracts display so a session can run without a window;
`SampleBufferVideoPresenter` is the layer implementation, and most of it is
`requiresFlushToResumeDecoding` handling. A real Media Foundation stream decodes
here — 60 pictures out of the committed Windows fixture — which closes codec
conformance in both directions.

`Core/Networking/Media/H264Encoder.cs` and `H264Decoder.cs` are the Windows
mirror: an async Media Foundation MFT driven from its own
`IMFMediaEventGenerator` event pump (a hardware MFT refuses `ProcessInput`
outside that protocol with `E_UNEXPECTED`), `DesktopDuplicator.cs` for DXGI
Desktop Duplication capture, `ColorConverter.cs` for BGRA↔NV12, and
`CaptureTargetSelector.cs` for GPU/output/encoder selection as a pure function
of enumerated topology, so the nine machine shapes nobody here owns (Optimus
laptops, AMD boxes, Windows N SKUs with no H.264 MFT, headless machines) are
covered by tests rather than hardware. `ICodecAPI` — the one interface Vortice
does not project — is a hand-rolled COM interop in `CodecApi.cs`, acquired per
thread because the RCW has no proxy/stub.

### Input

`RemoteInputRecord.{swift,cs}` is the wire shape for pointer move/button/scroll
and key events — big-endian, fixed-width, decoded all-or-nothing, because
injecting the prefix of a corrupted burst is worse than injecting nothing.
`HidKeyMap.{swift,cs}` is the single source of truth mapping USB HID usage to
each platform's native key representation; the reverse table used for capture
is generated from it rather than hand-written a second time, which is what
keeps the two directions from silently drifting apart.

`RemoteInputInjector.{swift,cs}` turns decoded records into `CGEvent`s
(macOS) or `SendInput` calls (Windows) — the most dangerous object in the
feature, since everything it does was asked for by another computer. It
re-checks the control grant on every call rather than trusting its caller,
refuses the host's reserved kill shortcut even though the viewer is required
never to send it, and releases every key and button it holds at session
teardown so a session that ends mid-chord cannot leave the host's keyboard
stuck. Injection on macOS needs the **Accessibility** TCC grant, separate from
the Screen Recording grant capture already has; the consent prompt reads
`RemoteInputInjector.hasAccessibilityGrant` and says so when it is missing,
since `CGEvent.post` fails silently without it.

`RemoteInputCapture.swift` (macOS) and the `RemoteViewerWindow` input handling
(Windows) turn the viewer's own mouse and keyboard into wire records,
normalized against the **video rectangle** rather than the window — the
picture is aspect-fitted and letterboxed, and a click in a letterbox bar
returns nothing rather than clamping to an edge. Both sides lift held keys on
focus loss, so a key released outside the window is not lost, and both
consult `RemoteKillSwitch.reserved`/`RemoteSessionStop` to make sure the
host's own escape hatch can never ride the wire.

### Consent and session lifecycle

`RemoteDesktopPolicy.{swift,cs}` is the inbound gate as a pure function: a
stranger's invite is dropped silently rather than declined, trust is checked
before the `remoteDesktopMode` setting is consulted (so turning the feature on
never widens *who* may reach the host), and a changed key at a known address
is logged at `error` rather than treated as routine.

`RemoteGrant.{swift,cs}` is the two-stage ladder — `none → viewing → control`,
with no path to `control` except from `viewing`, and `end()` terminal so a
reconnect is a new state rather than a quietly resumed old one. The consent
prompt (`RemoteConsentView`/`RemoteConsentWindow`) always shows the peer's
identity-key fingerprint alongside its name, since a display name alone is
trivially spoofable, and every accidental way out of the dialog — Return,
Escape, the close button, the countdown — lands on decline.

Once a grant exists, `RemoteHostIndicator.{swift,cs}` puts a persistent,
deliberately immovable on-top strip on the host's screen naming the viewer and
the grant level: immovable because a draggable indicator would let a viewer
holding control drag the host's own warning off-screen, which nothing could
tell apart from a real drag. `RemoteSessionGuard.{swift,cs}` auto-stops the
session on screen lock, sleep, user switch, network loss and app quit, and a
host-reserved kill combination (`⌃⌥⌘⎋` on macOS) is registered through a route
that needs no permission grant, so it still works when the Accessibility grant
that gates injection has not been given. Every exit — hotkey, indicator
button, guard, watchdog, peer disconnect — converges on one teardown path
(`announceEnd`/`AnnounceEnd`) that closes the socket and writes `remote_end`,
because a session ended from six different call sites is how five of them
forget to.

Every session start, stop and control grant is recorded as an audit entry in
chat history — an ordinary message whose `text` carries a `__REMOTE__:`
marker, so the history format needed no migration — with wording written from
whichever side actually held that role, host or viewer, rather than always
from the host's chair.

`ScreenCaptureSource.swift` is the host's `SCStream`, built for a session that
runs for hours rather than for one frame. It restarts on `didStopWithError` and
on screen-parameter changes, because SCK stops silently and sometimes keeps
running at a stale size; it drops the idle and blank frames SCK delivers
alongside real ones; and it treats silence as normal, because capture is
change-driven and a still screen produces nothing at all.

`VideoSendPipeline` and `VideoReceivePipeline` are the seams that join capture,
codec and transport. They are thin on purpose: the send side owns an encoder and
the rule about when to convert to Annex-B, the receive side owns a decoder and
hands sample buffers on without presenting them. `VideoPipelineEndToEndTests`
runs both against two real `MediaSession`s over a paired in-memory link, which is
the first thing in this feature to prove a pipeline rather than a component.

## Update Architecture

Both platforms check GitHub Releases using `update_repo` from config, defaulting
to `felzy613/lan-messenger`.

Release tags:

- macOS platform release: `macos-vX.Y.Z`
- Windows platform release: `windows-vX.Y.Z`
- combined release: `release-winX.Y.Z-macA.B.C`

Both updaters pick the **highest platform version that actually ships the asset
they need**, sorting the whole feed by semantic version descending and using
"combined release first, then newest published" only as a tiebreak within one
version. Tag style and publish date never outrank the version number: a
combined release is created only once *both* platforms have published, so
between a platform build and its combined release an older combined release
still carrying the right asset would otherwise be preferred and the updater
would report "up to date" with a newer installer sitting in the feed.

Sidecars are searched across every release, because the public combined release
intentionally ships only the bare installer.

Version numbers are read from the tag per platform — `macos-vX.Y.Z` and the
`mac…` half of a combined tag on macOS, `windows-vX.Y.Z` and the `win…` half on
Windows — and a tag naming only the *other* platform yields no version at all.
The two platforms version independently, so without that rejection a
`macos-v1.9.0` release reads as Windows 1.9.0 and its changelog lands in the
Windows update panel under a version Windows never had.

### Release Notes Shown In App

The "what you are about to install" panel shows **every** release between the
running build and the one being offered, not just the newest one's body.
Skipping versions is the ordinary case — anyone who has not opened the app for
a week is several releases behind — and showing only the last hop hid every
change made in between.

`UpdateService.mergedReleaseNotes` (macOS) / `UpdateService.MergedReleaseNotes`
(Windows) build it from the release feed the updater already fetched:

1. Keep releases whose version is greater than the installed one and no greater
   than the build being offered. The upper bound matters: a newer release whose
   platform asset has not been published yet must not advertise changes the
   download does not contain.
2. De-duplicate by version — the same build appears twice, once as
   `macos-vX.Y.Z` / `windows-vX.Y.Z` and once inside the combined release — and
   prefer the first body that survives stripping, so an empty duplicate never
   shadows the copy with content.
3. Strip the release-page furniture from each body: everything from the first
   `---` rule or a `## Downloads` / `## Install` heading, plus a leading
   `## What's New` (combined releases carry one, platform pre-releases do not).
4. Emit newest first. A single hop renders bare; two or more get a
   `## Version X.Y.Z` heading each.

The settings UIs render the result directly — `SettingsView.releaseNotesView`
and `MarkdownHelper.PopulateBlocks` — and no longer trim anything themselves.
Covered by `UpdateNotesTests` on both platforms.

### macOS Update Install

1. Query releases (`per_page=100`).
2. Pick the highest-version macOS ZIP asset.
3. Fetch SHA256 sidecar when available.
4. Download to app-data staging.
5. Verify size and SHA256.
6. Extract `.app`.
7. Write helper shell script.
8. Spawn helper, terminate current app, replace bundle, clear quarantine,
   verify codesign best-effort, re-register Launch Services, relaunch.

### Windows Update Install

1. Query releases (`per_page=100`).
2. Pick the highest-version Windows installer EXE.
3. Fetch SHA256 sidecar when available.
4. Download to app-data staging.
5. Verify size and SHA256.
6. Acquire install lock.
7. Kill other `LanMessenger` processes.
8. Launch Inno Setup installer with elevation and silent flags.
9. Exit current process so files can be replaced.

## Packaging Architecture

### macOS Packaging

Canonical script: `scripts/macos/package.sh`.

It:

- generates Xcode project from `src/macos/project.yml`;
- builds Release with xcodebuild;
- signs with Developer ID, the local `LAN Messenger Dev` certificate, or
  ad-hoc, in that order of preference;
- optionally notarizes;
- stages `LAN Messenger.app`;
- produces ZIP, DMG, and PKG;
- writes SHA256 sidecars;
- preserves diagnostics in `src/macos/build/package.log`.

Validation scripts:

- `scripts/macos/validate-bundle.sh`
- `scripts/macos/validate-dmg.sh`
- `scripts/macos/smoke-test.sh`

### Windows Packaging

Windows CI:

- restores and tests with MSBuild/MSTest;
- publishes self-contained x64 WinUI app;
- copies VC++ runtime DLLs app-local;
- downloads `vc_redist.x64.exe`;
- runs Inno Setup using `src/windows-native/LanMessenger.iss`;
- writes SHA256 sidecar;
- smoke-tests install and startup.

The Inno installer:

- installs to Program Files;
- optionally creates desktop icon;
- optionally writes HKCU Run startup entry;
- installs VC++ runtime when needed;
- adds firewall rules for UDP `54231` and TCP `54232`;
- launches/relaunches the app after install/update.

## CI Architecture

Workflows:

- `pr-checks.yml`: macOS and Windows unit tests, plus PR summary comment.
- `build-macos.yml`: full macOS build/test/package/validate/publish.
- `build-windows.yml`: full Windows build/test/package/smoke/publish.
- `release.yml`: creates combined release from latest platform releases.
- `integrity-check.yml`: weekly release/version audit.

Composite actions:

- `report-failure`: fingerprints CI failures and creates or updates GitHub issues.
- `validate-version`: validates semver and checks release existence.

## Diagnostics

Runtime logs:

- macOS networking: app data `LanMessenger/Logs/client.log`, mirrored to
  `os_log`.
- macOS updates: app data or Library logs `update.log`.
- Windows networking: `%APPDATA%\LanMessenger\Logs\client.log`.
- Windows updates: `%APPDATA%\LanMessenger\Logs\update.log`.
- Windows startup crashes: `%APPDATA%\LanMessenger\crash.log`.
- Remote desktop, both platforms: `remote.log`.

Every channel in the `LogChannel` enum is written to its own file and collected
into the bug-report bundle. The bundle is derived from the enum, so a channel
that is missing from it silently never reaches a bug report.

CI diagnostics:

- macOS package log: `src/macos/build/package.log`.
- macOS smoke logs: `smoke.log`, Console log excerpts, crash reports.
- Windows build/test logs: `build-output-windows.txt`,
  `test-output-windows.txt`.
- Windows smoke logs: `smoke.log`, Event Viewer excerpts, crash dumps.

## Common Failure Modes

| Symptom | Likely Area | First Checks |
|---|---|---|
| Peers do not appear | UDP discovery/interface selection/firewall | `client.log`, adapter list, UDP 54231 allowed |
| Message stays at one check | decrypt failure, receipt send failure, TCP close race | sender/receiver `client.log`, `sent_receipt`, key mismatch |
| File transfer freezes UI | progress spam or main-thread file I/O | progress throttling, chunk queues |
| Received file missing | inbox permission, temp rename failure | `FileTransferStore`, inbox path, disk space |
| History disappears after key import | wrong local private key | migration choice, secure store, `history.enc` |
| Windows startup crash on clean machine | VC++ runtime/libsodium/PRI resources | app-local DLLs, Inno redist, `.pri` publish target |
| macOS app launches with generic icon | asset catalog or AppIcon.icns issue | `generate_icon.py`, `validate-bundle.sh` |
| Updater downloads but refuses install | SHA256/size mismatch or wrong asset | update log, release assets/sidecars |
| Combined release missing one platform | sibling build not done or platform release missing | `release.yml`, platform pre-releases |

## Extending The System

Before adding a protocol field:

1. Decide whether it is wire-level or local-only.
2. Add it as optional if older clients can ignore it.
3. Update `PROTOCOL.md`.
4. Update both `PacketTypes` implementations.
5. Add tests on both platforms or update test vectors if crypto/framing changes.

Before changing storage:

1. Make decoders tolerate missing old fields.
2. Add migration or fallback behavior.
3. Update config/history docs.
4. Test existing files when possible.

Before changing networking:

1. Preserve UDP raw JSON and TCP frame format.
2. Consider multi-interface behavior.
3. Preserve self-suppression by IP and public key.
4. Validate on macOS and Windows or document the remaining platform verification.

Before changing packaging/updating:

1. Keep CI scripts and in-app updater asset naming in sync.
2. Keep SHA256 sidecars attached to platform releases.
3. Run the relevant smoke test.
4. Update [RELEASE_AND_OPERATIONS.md](RELEASE_AND_OPERATIONS.md).
