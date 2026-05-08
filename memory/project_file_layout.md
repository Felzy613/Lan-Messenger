---
name: Repo file layout
description: Where files live in the LAN Messenger repo, including native rewrite additions
type: project
---

The repo root is the Python app. Native rewrite files are added alongside the existing structure.

## Existing Python App (do not delete)

```
main.py                          # ~4,417 lines — the reference implementation
requirements.txt
LanMessenger.spec                # PyInstaller spec
PACKAGING.md
macos/                           # Python build scripts for macOS DMG
windows/                         # Python build scripts for Windows installer
update_server/                   # Update manifest server files
assets/                          # Icons (.icns, .png, .ico)
```

## Native Rewrite Additions (branch: claude/hardcore-ride-3aec16)

```
PROTOCOL.md                      # Canonical wire format spec (Phase 1)
QA_CHECKLIST.md                  # Manual 87-case test plan (Phase 1)
tools/
  generate_vectors.py            # Deterministic test vector generator (Phase 1)
test_vectors/
  known_good_exchange.json       # Pre-computed crypto vectors (Phase 1)
lan-messenger-native/            # (planned — Phase 2+)
  macos/
    LanMessenger.xcodeproj
    LanMessenger/                # Swift sources
    LanMessengerTests/
  windows/
    LanMessenger.sln
    LanMessenger/                # C# WinUI 3 sources
    LanMessenger.Tests/
```

**Why:** PROTOCOL.md and QA_CHECKLIST.md live at repo root so they're equally
accessible for both platforms. Native app code goes under lan-messenger-native/
to avoid collision with the existing Python macos/ and windows/ directories.
