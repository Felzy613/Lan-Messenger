# RemoteConsentSheet

The thick-glass sheet that asks the host to allow viewing or control, with **Decline** as the default answer and a countdown that declines by itself.

## Anatomy
- Title in `title-sheet`, naming the peer: "Priya Raman wants to view your screen" or "…wants to control your screen". Then the explanation in `label` `ink-secondary`.
- An address and key card: `inset-fill`, `radius-card`, the key in `code`, selectable and never truncated.
- Footer: "Declines automatically in 24s" in `caption` `ink-secondary`, then **Decline**, then **Allow Viewing** or **Allow Control**.

## Rules
- **Decline is the default.** It takes the primary style and the default-action key: Enter, Escape, closing the window and the countdown all decline. The allow button uses the glass style and has no keyboard shortcut, so accepting has to be aimed at and clicked. Never swap these to make the allow button primary.
- **Colour only when something is wrong.** A pinned key gets a plain sheet with no badge. A green tick on every prompt teaches people to look for the tick instead of reading. The `warning-wash` note with a `warning-ink` icon appears only when the key is not the pinned one ("This is not the key you have saved for Priya Raman…") or the device is not a contact ("This device is not in your contacts.").
- A permission problem, such as a missing Accessibility grant on macOS, is a plain `inset-fill` note, not the warning treatment. A permissions note dressed as a security warning teaches people to dismiss security warnings.
- The countdown is stated as a number, not animated. A shrinking bar reads as pressure to click something.
- The sheet opens before `remote_accept` is sent, never after.

## Build
- **Windows:** `RemoteConsentWindow`, with its root `Grid` background made transparent and `SystemBackdrop = new DesktopAcrylicBackdrop()`, so the window is real glass over the desktop. Keep `DeclineButton` on the accent style and `AcceptButton` plain.
- **macOS 26:** remove the opaque `Color(white:)` background. The panel becomes glass (see the implementation plan for the panel flags). Keep `.keyboardShortcut(.defaultAction)` on Decline and none on the allow button.
