# QA Checklist — LAN Messenger Native Apps

Manual two-device integration test plan.  
Run this checklist for every release candidate before shipping.

---

## Setup

**Devices required**

- [ ] **Device A**: macOS — running the new native Swift/SwiftUI app (fresh install, no prior config)
- [ ] **Device B**: Windows — running the new native C#/WinUI 3 app (fresh install, no prior config)
- [ ] (Optional) **Device C**: any OS — running the Python reference app (v1.5.0+)
- [ ] All devices on the **same LAN subnet**
- [ ] Firewall rules: allow **UDP 54231** inbound and **TCP 54232** inbound on both devices

**Before starting**

- [ ] Confirm no stale `~/.lan_messenger/` (macOS) or `%APPDATA%\LanMessenger\` (Windows) from previous test runs, or start with a clean user profile
- [ ] Note each device's local IP address

---

## 1 — Discovery

| # | Test | Expected |
|---|---|---|
| 1.1 | Launch A. Launch B. Wait 3 s. | A's sidebar shows B. B's sidebar shows A. |
| 1.2 | Both apps show peer status. | Status indicator is "Online" / green for the visible peer. |
| 1.3 | Quit B. Wait 8 s. | B disappears from A's sidebar (peer timeout = 7 s). |
| 1.4 | Relaunch B. Wait 3 s. | B reappears in A's sidebar. |
| 1.5 | Tray menu on A. | B is listed as an online peer. |

---

## 2 — Text Messaging (A → B)

| # | Test | Expected |
|---|---|---|
| 2.1 | A sends "Hello from A" to B. | Message appears instantly on B with the correct incoming bubble style. |
| 2.2 | B's conversation with A. | Status on A's sent message changes to "Sent" (✓). |
| 2.3 | B opens the conversation. | Status on A's message changes to "Read" (✓✓ in accent color). |
| 2.4 | B replies "Hello from B". | A receives it with incoming bubble. Status reaches "Read" after A opens chat. |
| 2.5 | Multi-line message: Shift+Enter inserts newline. Enter sends. | Correct behavior on both A and B. |
| 2.6 | Long message (500+ chars). | Displayed in full, bubble wraps correctly. |
| 2.7 | Send 10 messages rapidly from A. | All 10 appear on B in correct order with no duplicates. |

---

## 3 — Typing Indicator

| # | Test | Expected |
|---|---|---|
| 3.1 | A starts typing in composer. | B's chat header shows "A is typing…" within 2 s. |
| 3.2 | A stops typing (clears composer or idles). | Typing indicator disappears from B within 5 s. |
| 3.3 | A sends the message. | Typing indicator disappears immediately on B. |

---

## 4 — Offline Message Queue

| # | Test | Expected |
|---|---|---|
| 4.1 | Quit B. A sends a message to B. | A shows message with "Queued" status. |
| 4.2 | Relaunch B. | A's queued message is delivered to B. Status on A updates to "Sent". |
| 4.3 | B opens conversation. | A's message reaches "Read" status. |

---

## 5 — Unread Badges and Counts

| # | Test | Expected |
|---|---|---|
| 5.1 | A receives a message while viewing a different conversation. | Unread badge increments on A's sidebar row for B's conversation. |
| 5.2 | A opens B's conversation. | Badge clears to zero. |
| 5.3 | Multiple messages received while backgrounded. | Badge count matches number of unread messages. |

---

## 6 — File Transfer (A → B)

| # | Test | Expected |
|---|---|---|
| 6.1 | A drag-drops a 1 MB file onto the chat area. | Progress bar visible on both A and B during transfer. |
| 6.2 | Transfer completes. | File appears in B's configured inbox directory. Filename matches original. |
| 6.3 | B receives a system notification. | Notification shows sender name and filename. |
| 6.4 | Queue: A drag-drops 3 files in succession. | All 3 transfer in order; each completes before the next starts. |
| 6.5 | Large file (50 MB). | Transfer completes without corruption. SHA-256 of received file matches source. |
| 6.6 | File transfer B → A (reverse direction). | Same pass criteria as 6.1–6.3. |

---

## 7 — App Restart Persistence

| # | Test | Expected |
|---|---|---|
| 7.1 | Restart A. | Contacts, full message history, and settings are preserved. |
| 7.2 | Restart A. | Private key unchanged (same public key shown in settings). |
| 7.3 | Restart A. | History is decryptable; all prior messages visible. |
| 7.4 | Restart B. | Same persistence checks. |

---

## 8 — Notifications

| # | Test | Expected |
|---|---|---|
| 8.1 | A is backgrounded or minimized. B sends a message. | macOS system notification appears with sender name and message preview (≤80 chars). |
| 8.2 | Click the notification. | A's window comes to foreground, correct conversation is selected. |
| 8.3 | A is backgrounded. B sends a file. | Notification appears with sender name and filename. |

---

## 9 — Settings

| # | Test | Expected |
|---|---|---|
| 9.1 | Change A's display name in Settings. | After 2 discovery cycles (≤4 s), B sees A's new name in sidebar. |
| 9.2 | Change inbox folder on B (Settings → pick a custom path). | Next file received from A goes to the new folder. |
| 9.3 | Check for updates (manual). | Returns "up to date" or correct update dialog with version and notes. |

---

## 10 — Python Interoperability (Device C)

| # | Test | Expected |
|---|---|---|
| 10.1 | Python app discovers native macOS app. | Python app shows native app peer in its sidebar within 3 s. |
| 10.2 | Python app discovers native Windows app. | Same. |
| 10.3 | Python → native macOS: send text message. | Native app receives and decrypts correctly. |
| 10.4 | Native macOS → Python: send text message. | Python app receives and decrypts correctly. |
| 10.5 | Python → native Windows: send text message. | Native app receives and decrypts correctly. |
| 10.6 | Native Windows → Python: send text message. | Python app receives and decrypts correctly. |
| 10.7 | Python → native: file transfer. | Transfer completes, file is uncorrupted. |
| 10.8 | Native → Python: file transfer. | Transfer completes, file is uncorrupted. |
| 10.9 | Read receipts round-trip: Python sends, native opens conversation. | Python app shows "Read" status. |

---

## 11 — Migration from Python App

| # | Test | Expected |
|---|---|---|
| 11.1 | macOS native app launched with existing `~/.lan_messenger/config.json`. | App prompts to import config (username, contacts, history). |
| 11.2 | Accept import of existing private key. | Key moved to Keychain; public key matches original. History is decryptable. |
| 11.3 | Generate fresh key instead. | New key generated; contacts and settings imported; history from old key is not decryptable (expected — different key). |
| 11.4 | Windows native app with existing `~\.lan_messenger\config.json`. | Same import flow as macOS. Key moved to DPAPI store. |
| 11.5 | After migration, app can communicate with Python peer. | End-to-end message exchange works (only if same key was imported). |

---

## 12 — Security and Robustness

| # | Test | Expected |
|---|---|---|
| 12.1 | Send malformed JSON to TCP port 54232 (`echo "not json" \| nc <ip> 54232`). | App does not crash; connection is closed cleanly. |
| 12.2 | Send a frame with a 4-byte length header declaring 60 MB (`\xFF\xFF\xFF\xFF`). | Connection closed; no memory exhaustion; no crash. |
| 12.3 | Send a `file_start` with `filename = "../../../evil.sh"`. | File is NOT written outside inbox directory; sanitized name is `"evil.sh"`. |
| 12.4 | Send a `text` packet with a tampered ciphertext (flip one byte). | Message is silently dropped; no error shown to user; no crash. |
| 12.5 | Send a `discovery` packet with `public_key_b64` matching the receiving app's own key. | Packet is silently dropped; no peer appears. |
| 12.6 | Replay an old `text` packet to the app. | Delivered as a duplicate (no replay protection by design — acceptable). Document this limitation. |
| 12.7 | Quit and relaunch both apps. | Private keys are unchanged; history remains decryptable. |

---

## 13 — Cross-Platform Crypto Test Vectors

Run before live testing. These tests use the file `test_vectors/known_good_exchange.json`.

| # | Test | Expected |
|---|---|---|
| 13.1 | macOS unit test `CryptoTests.testDecryptKnownVector`. | PASS — decrypts Python-generated ciphertext to correct plaintext. |
| 13.2 | Windows unit test `CryptoTests.DecryptKnownVector`. | PASS — same. |
| 13.3 | macOS unit test `CryptoTests.testFrameRoundTrip`. | PASS — encodes and decodes the reference frame exactly. |
| 13.4 | Windows unit test `FrameCodecTests.RoundTrip`. | PASS — same. |

---

## Sign-Off

| Role | Name | Date | Signature |
|---|---|---|---|
| Tester | | | |
| Developer | | | |

All items in sections 1–12 must be checked before a release is marked **Ready to Ship**.  
Section 13 must be green (automated) before beginning sections 1–12.
