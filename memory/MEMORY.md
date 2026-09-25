# Memory Index

Repo-local memory for LAN Messenger. These notes are documentation for future
sessions and should stay aligned with the current native app tree.

- [Windows reliability + UI audit](windows-reliability-audit.md) — 2026-07-01 full Windows audit: TCP retry + heartbeat-driven pending redelivery, probe-reply-before-dedup presence fix, timer crash shields, atomic history/config saves, dark mode + Fluent icons.
- [macOS reliability fixes](macos-reliability-fixes.md) — 2026-07-01 ported the Windows audit's macOS-side bugs: presence probe-reply-before-dedup, message dedup + pending in-flight guard, TCP accept-loop spin, dead discovery unicast hints, file-retry cooldown. Also documents a local Keychain test hang (not a code bug — skip that one test on this dev machine).

- [Thread scroll reliability and merged release notes](thread-scroll-and-merged-release-notes.md) — 2026-09-19: latched pinnedToBottom + settle repeats so the thread actually lands on the newest message, and the update panel now merges every skipped version's changelog.

- [Settings sheet scroll edges (Windows)](settings-sheet-scroll-edges.md) — 2026-09-25: rows fade into the Settings sheet at the scroll edges instead of being sliced; why the sheet is opaque glass-regular-fallback, why the edge colour is read from live brushes, and what to check on the Dell.

- [Relay system audit & hardening](relay-system-audit.md) — Relay bugs fixed 2026-05-29 (delivery-mode tracking, offline-only gating, synthetic IP migration) and 2026-07-02 (confirmed-store-before-badge, global message-id dedup, durable outbox retry).

- [Remote desktop](remote-desktop.md) — working notes for the unreleased
  remote-desktop feature on `feat/remote-desktop-transport`. The status document
  is `docs/REMOTE_DESKTOP.md`; this one records decisions already settled and
  the environment the Windows half is built against.

- [LAN Messenger Native Rewrite](project_native_rewrite.md) - Current native
  project status, platform scope, tests, and next work themes.
- [Repo file layout](project_file_layout.md) - Current root/docs/scripts/source
  layout and source-of-truth files.
- [Protocol implementation gotchas](project_protocol_gotchas.md) - Non-obvious
  protocol compatibility details to check before wire, crypto, file-transfer, or
  persistence changes.
- [Swift build gotchas](project_swift_build_notes.md) - macOS/Swift build,
  XcodeGen, SPM, and compiler patterns discovered during implementation.
- [Inline media, Open File Location, and screenshot send](project_media_and_screenshot_features.md) -
  Design notes for the cross-platform image/video bubbles, Open-file-location
  helpers, and screenshot-capture flow that all route through the existing
  FileTransferService with no protocol changes.
- [Document all work for future sessions](feedback_document_all_work.md) -
  Standing user preference to keep docs and memory current after project work.

Current source roots:

- macOS native app: `src/macos/`
- Windows native app: `src/windows-native/`
- Protocol spec: `PROTOCOL.md`
- Remote-desktop handoff: `docs/REMOTE_DESKTOP.md`
- High-detail docs: `docs/`
- Canonical versions: `version/macos.json`, `version/windows.json`
