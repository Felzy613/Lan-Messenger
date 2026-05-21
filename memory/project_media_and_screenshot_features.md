# Inline media, Open File Location, and Screenshot Send

These three features were added together because they share one design rule:
**no protocol changes.** Everything flows through the existing FileTransferService.

## Design rule

`MessageEntry.text` already uses the `__FILE__:<absolute path>` convention for
file messages, on both platforms.  The UI classifies the file by extension at
render time and decides whether to draw a generic document tile, an inline
image, or a video poster.  The wire format is untouched, so older clients keep
working unchanged.

`MediaKind` lives in:

- `src/macos/LanMessenger/UI/Chat/MediaTypes.swift`
- `src/windows-native/LanMessenger/UI/Chat/MediaTypes.cs`

Image extensions: jpg, jpeg, png, gif, webp, heic, heif, bmp, tiff
Video extensions: mp4, mov, m4v, webm, mkv, avi

## Open File Location

- macOS uses `NSWorkspace.shared.selectFile(_:inFileViewerRootedAtPath:)` with a
  fallback to `activateFileViewerSelecting(_:)`.  All of this runs inside a
  `Task.detached(.userInitiated)` block so Finder coming forward never stalls
  the chat list.  Errors surface as a SwiftUI alert.  Helper: `FinderReveal`
  in `MediaTypes.swift`.
- Windows uses `explorer.exe /select,"<path>"` via `Process.Start` on a
  background `Task.Run`.  Errors come back as a `string?` from
  `FileReveal.RevealAsync` and are shown via a `ContentDialog`.

Both platforms gate the action on `File.Exists` and surface a clear
"file moved or deleted" message rather than failing silently.

## Inline media bubbles

- **macOS**: `MediaBubbleView` decodes images via `NSImage(contentsOfFile:)`
  and video first-frames via `AVAssetImageGenerator`, all off the main thread.
  Thumbnails are cached by absolute path in an `NSCache`-backed
  `ThumbnailCache`.  Tap opens a `MediaPreviewSheet` with the full image in a
  scrollable container or an `AVPlayer` (`VideoPlayer`) with controls.  Bubble
  size is capped at 280×320 pt and aspect ratio is preserved.
- **Windows**: `MessageBubbleControl` uses a `BitmapImage` with
  `DecodePixelWidth = 560` to cap memory cost.  Videos render as a static
  poster tile (filename + play icon) in the chat list; tapping opens
  `MediaPreviewDialog` which instantiates a `MediaPlayerElement` only inside
  the dialog so the chat list itself never hosts a video decoder.  The dialog
  tears down `MediaPlayer` (Pause + Source=null) on close so file handles are
  released promptly.

If the local file disappears between history append and render, both
platforms show a "file no longer available" tile rather than a crash-prone
empty preview.

## Screenshot send

- **macOS**: `ScreenshotService.capturePrimaryDisplay()` uses
  `CGPreflightScreenCaptureAccess` / `CGRequestScreenCaptureAccess` to gate
  the action, then runs a short-lived `SCStream` from **ScreenCaptureKit**
  with an `SCStreamConfiguration` matched to the primary display's pixel
  dimensions.  The `FrameCollector` (an `SCStreamOutput`/`SCStreamDelegate`)
  awaits the first `.complete` sample buffer, converts it via `CIContext` to
  a `CGImage`, and tears the stream down.  A 5-second timeout protects the
  caller if the GPU pipeline stalls.  We deliberately avoided the deprecated
  `CGDisplayCreateImage` so the build is warning-clean on macOS 14+.
  ScreenCaptureKit is available from macOS 12.3, which is below this
  project's 13.0 deployment target.  The PNG is written to
  `NSTemporaryDirectory()/LanMessenger-Screenshots/` and then handed to
  `AppModel.sendFile(path:toPeerIP:)`.  Info.plist adds
  `NSScreenCaptureUsageDescription`.
- **Windows**: `ScreenshotService.CapturePrimaryDisplayAsync()` uses
  `Graphics.CopyFromScreen` from `System.Drawing.Common` (added to the
  csproj) over the primary monitor metrics from `GetSystemMetrics`.  The
  PNG is written to `%TEMP%\LanMessenger-Screenshots\` and routed through
  `AppModel.SendFile()`.  No runtime permission gate is required for
  ordinary user-mode captures.

Both implementations run capture and PNG encoding on a background task and
disable the composer's screenshot button while the action is in flight.

## What did NOT change

- Protocol (`PROTOCOL.md`) — no new packet types, no new fields, no field
  rename.  Cross-platform clients remain wire-compatible.
- Persistence — history, config, and pending queues still use the same
  `__FILE__:<path>` convention.
- File transfer pipeline — chunk size, encryption, framing, and progress all
  unchanged.
- Existing UI for plain documents — still shows a paperclip + filename + Open
  button (Windows also gains a "Show in folder" button next to it).
