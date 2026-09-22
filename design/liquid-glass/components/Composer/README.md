# Composer

The floating glass pill where messages are written, with attach, screenshot and the send orb.

## Anatomy
- One `glass-regular` pill (radius 22, 4px inner padding) holding the attach and screenshot icon buttons and the text field.
- The send orb sits beside the pill: 36px, `brand` with an `on-brand` arrow. While editing, it shows a checkmark labelled "Save edit".
- Reply and edit banners grow inside the pill, above the field, as an `inset-fill` capsule with a 3px `brand` bar, a title ("Replying to Priya" or "Editing message") in `accent-ink`, a one-line preview, and a cancel button.

## Rules
- The field is `message` 14/20, with the placeholder "Message" in `ink-secondary`. It grows from 32px to 138px, then scrolls.
- With an empty draft the orb is disabled: glass-regular with an `ink-secondary` glyph.
- Return sends and Shift-Return inserts a newline. Paste order is files, then a bitmap with no text, then text.

## Build
- **macOS 26:** `GlassEffectContainer` { pill `.glassEffect(in: .capsule)` + orb `.buttonStyle(.glassProminent)` tinted `brand` }, placed in `.safeAreaInset(edge: .bottom)`.
- **Windows:** a transparent root, the pill as a glass `Border` (`CornerRadius="22"`), and the send button's `FontIcon.Foreground` set to #0B141A. It is hard-coded white today.
