# Memory Index

- [LAN Messenger Native Rewrite](project_native_rewrite.md) — Active project: replacing Python/Tkinter with Swift/SwiftUI + C#/WinUI 3 native apps. Phase 1 done; Phase 2 (macOS Xcode scaffold) is next.
- [Repo file layout](project_file_layout.md) — Where existing Python files live vs. where native rewrite files go (lan-messenger-native/)
- [Protocol implementation gotchas](project_protocol_gotchas.md) — Non-obvious facts verified by reading main.py: discovery has no framing, history keyed by IP, HKDF uses empty salt, AES-GCM tag layout, temp file naming, etc.
- [Document all work for future sessions](feedback_document_all_work.md) — User wants comprehensive memory updated after every session so future Claude instances don't re-derive context
- [Swift build gotchas](project_swift_build_notes.md) — Compiler errors fixed in Phase 2: Darwin.bind, InputStream pointer arithmetic, @MainActor protocol delegation, ConfigStore mutability, filename sanitization via string split, test vector resource paths
