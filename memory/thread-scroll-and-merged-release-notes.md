---
name: Thread scroll reliability and merged release notes
description: 2026-09-19 — the thread now reliably lands on the newest message, and the update panel shows every version you are skipping
type: project
---

Two user-reported issues, fixed together on branch
`claude/release-docs-message-scroll-efa976`.

## 1. "Doesn't always scroll to the bottom"

Reported for new messages and for opening the window. Three separate causes,
all present on both platforms:

1. **One scroll is not enough.** The "a message arrived" callback runs before
   the new row is laid out, so the scroll lands on the *old* bottom. Media
   bubbles make it worse: they start at a placeholder size (220×160 on macOS,
   an empty tile on Windows) and grow when the thumbnail decodes, hundreds of
   milliseconds later.
2. **Re-showing the window re-ran nothing.** Neither platform unloads the chat
   view when the window is hidden/minimized, so nothing re-ran the "land on the
   newest message" step.
3. **Measuring "am I at the bottom?" at event time is self-defeating.** Taken
   mid-scroll, or in the same pass as content that just grew, the reading says
   "adrift by exactly what changed" — the thread unpins itself and then never
   follows another message.

The fix, symmetric on both sides:

- A latched `pinnedToBottom` / `_pinnedToBottom` flag replaces the live
  measurement as the gate. Only a reading taken at rest, with the content the
  size it already was, may clear it.
- `ChatView.pinToBottom(proxy:animated:)` / `ChatPage.ScrollToBottomSettled()`
  repeat the scroll for ~0.6 s (macOS: `settleDelays` via `asyncAfter`;
  Windows: a `DispatcherQueueTimer`, 8 × 80 ms).
- Content growth while pinned re-pins. macOS gets it from the geometry
  preference; Windows from `SizeChanged` on the scroll viewer's content plus an
  `ExtentHeight` comparison in `OnScrollViewChanged`.
- Window re-show re-pins: macOS `onChange(of: controlActiveState)`, Windows
  `ChatPage.OnWindowShown()` called from `MainWindow` on un-minimize and tray
  restore.
- Windows also gained the documented "sending always jumps to the newest
  message" rule, which it never had — it only followed `wasAtBottom`. Gated on
  rows actually having been appended, because `MergeMessages` also runs for
  status-only and edit-only updates.

**The trap worth remembering (macOS).** The first attempt measured content
height through a `.background(GeometryReader)` `onChange` and the
distance-to-bottom through the existing `ContentBottomKey` preference. The
preference lands *first*, so `pinnedToBottom` went false before the height
change was seen, and the thread never followed. Both edges now travel in one
`ScrollGeometryKey` preference (`top` from a sentinel before the message
`VStack`, `bottom` from one after it), so a single callback sees height and
position from the same layout pass. The scroll content is also wrapped in
`VStack(spacing: 0)` so the implicit container's default spacing does not leave
the scroll short.

## 2. Merged release notes

The update panel showed only the newest release's body, so a user on 1.1.1
offered 1.1.3 never saw what 1.1.2 changed. `mergedReleaseNotes` (Swift) /
`MergedReleaseNotes` (C#) now merge every release above the installed version
and at or below the offered one, newest first, each under `## Version X.Y.Z`
when there is more than one.

Details that matter:

- The **upper bound is not optional**: a newer release whose platform asset has
  not been published yet would otherwise advertise changes the download does
  not contain.
- Versions are **de-duplicated** — each build appears twice, as a platform
  pre-release and inside the combined release — preferring the first body that
  survives stripping, so an empty duplicate never shadows the real one.
- `stripReleasePageSections` / `StripReleasePageSections` moved into
  `UpdateService` (from `SettingsView.trimmedNotes` and
  `MarkdownHelper.TrimNotes`, both deleted) because the merge needs it per
  release. It also drops a leading `## What's New`, which only combined
  releases carry. The settings UIs now render `info.notes` as-is.
- Covered by `UpdateNotesTests.swift` / `UpdateNotesTests.cs` (12 and 13 cases).

## Verification actually performed

- macOS: `swift build` + `swift test` → 175/175.
- macOS scroll: a throwaway SwiftPM harness put the real mechanism in an
  `NSWindow` and read the backing `NSScrollView`'s clip-view offset over time.
  Old logic ended 316 pt below the fold after a late image decode and an
  arrival; new logic stays 12 pt (the `VStack`'s bottom padding, inside the
  40 pt slack) through every step, and still does *not* yank a reader who has
  scrolled up.
- Windows: full solution built with real VS MSBuild on the Dell over SSH and
  `dotnet vstest` → 138/138, including the 13 new tests. The whole app tree
  also type-checks on macOS via the shim-csproj recipe.

## 3. Release picker ordering (fixed in the same change, on request)

`PickLatestWindows` sorted the feed by publish date and scanned *all* combined
releases before *any* platform release, so an older combined release carrying
an `.exe` beat a newer `windows-vX.Y.Z` pre-release. The combined release for a
build is only created once both platforms have published, so in the window
between a Windows build and its combined release the updater said "up to date"
with a newer installer already in the feed. macOS had fixed the same bug in
`pickLatestMac`; Windows never got it. Now both sort by semantic version
descending, with "combined first, then newest published" as a tiebreak *within*
one version (which also decides which body `MergedReleaseNotes` keeps).

While fixing it: `ExtractVersion`/`extractVersion` now return `""` for a tag
that names only the other platform. The two platforms version independently,
and the old fallback ("first number in the tag") read `macos-v1.9.0` as Windows
1.9.0 — harmless for the asset picker (no `.exe` there) but not for the merged
notes this change introduced, which would have shown macOS's changelog in the
Windows panel under a version Windows never had.
