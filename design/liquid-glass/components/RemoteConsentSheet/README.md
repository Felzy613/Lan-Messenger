# RemoteConsentSheet

The thick-glass sheet asking the host to accept or decline a screen-share or control request before a countdown runs out.

## Anatomy
- Title in `title-sheet` ("Priya Raman wants to see your screen"), then the explanation in `label` `ink-secondary`.
- An address and key card: `inset-fill`, `radius-card`, keys in `code`.
- A warning note: `warning-wash` with a `warning-ink` icon, telling the host to compare the key.
- Footer: "Declines automatically in 24s", then **Decline** (glass) and the primary action (**Share Screen** or **Allow Control**).

## Rules
- A permission problem (for example a missing Accessibility grant) is a plain `inset-fill` note, not the warning treatment. A permissions note dressed as a security warning teaches people to dismiss security warnings.
- The sheet opens before `remote_accept` is sent, never after.

## Build
- **Windows:** a separate window with `DesktopAcrylicBackdrop`. The sheet is real glass over the desktop.
- **macOS 26:** `.glassEffect(.regular, in: .rect(cornerRadius: 26))`, replacing today's opaque `Color(white:)` background.
