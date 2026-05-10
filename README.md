# LAN Messenger

A peer-to-peer local-network chat app with end-to-end encryption. No servers, no accounts — just people on the same network.

---

## Platforms

| Platform | Stack | Status |
|---|---|---|
| **macOS** | Swift / SwiftUI | Active |
| **Windows** | C# / WinUI 3 | Active |
| **Python (reference)** | Python / Tkinter | Stable (wire-protocol reference) |

All three implementations speak the same wire protocol and interoperate freely. See [`PROTOCOL.md`](PROTOCOL.md) for the spec.

---

## Features

- **Zero-config discovery** — peers appear automatically via UDP multicast
- **End-to-end encryption** — X25519 key exchange + AES-256-GCM per session
- **File transfer** — encrypted, streamed over a dedicated TCP connection
- **Persistent history** — conversations are stored encrypted on disk and survive restarts
- **Typing indicators**, **sent/read receipts**
- **Native UIs** — SwiftUI on macOS, WinUI 3 on Windows; no Electron

---

## Getting Started

### macOS

Requires macOS 13+.

```bash
cd src/macos
swift build
swift run
```

Or open `LanMessenger.xcodeproj` in Xcode and press Run.

### Windows

Requires Windows 10 (build 19041+), Visual Studio 2022, and Windows App SDK 1.5.

```powershell
cd src\windows-native
dotnet build LanMessenger.sln
```

Run the resulting binary, or press F5 in Visual Studio.

### Python reference app

```bash
cd src/windows
pip install -r requirements.txt
python main.py
```

---

## Running Tests

### macOS

```bash
cd src/macos
swift test
```

40 unit tests covering crypto, framing, and protocol correctness. The test suite validates against `known_good_exchange.json` — three cross-platform vectors (text message, file chunk, history file) that guarantee interoperability.

### Windows

```powershell
cd src\windows-native
dotnet test LanMessenger.Tests
```

---

## Protocol Overview

- **UDP port 54231** — peer discovery via multicast (`239.255.42.99`), raw JSON (no framing)
- **TCP port 54232** — messages and file transfers, 4-byte big-endian length prefix + UTF-8 JSON body
- **Crypto** — X25519 + HKDF-SHA256 (empty salt, `"lan-messenger"` info) → AES-256-GCM (12-byte nonce, 16-byte tag)
- **Keys** — stored in Keychain (macOS) or DPAPI (Windows), never plain JSON

Full spec: [`PROTOCOL.md`](PROTOCOL.md)

---

## Repository Layout

```
PROTOCOL.md                   # Wire-protocol spec — read before touching networking/crypto
src/
  windows/main.py             # Python/Tkinter reference implementation
  macos/                      # Swift/SPM native app
    Package.swift
    LanMessenger/
      App/                    # Entry point, NavigationSplitView, menu-bar tray
      Core/
        Protocol/             # Packet types, frame codec, validation
        Crypto/               # Key manager (Keychain), session & history crypto
        Networking/           # UDP discovery, TCP peer sessions, coordinator
        Persistence/          # Config, history, file-transfer stores
        Services/             # Messaging, file transfer, notifications, updates
      UI/                     # AppModel, theme, sidebar, chat, settings
    LanMessengerTests/        # 40 unit tests + known_good_exchange.json
  windows-native/             # C#/WinUI 3 native app
    LanMessenger/             # Mirrors macOS Core/ + UI/ structure
    LanMessenger.Tests/       # MSTest unit tests + known_good_exchange.json
```

---

## License

MIT
