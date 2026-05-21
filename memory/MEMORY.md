# Memory Index

Repo-local memory for LAN Messenger. These notes are documentation for future
sessions and should stay aligned with the current native app tree.

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
- High-detail docs: `docs/`
- Canonical versions: `version/macos.json`, `version/windows.json`
