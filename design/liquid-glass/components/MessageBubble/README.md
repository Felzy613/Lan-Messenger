# MessageBubble

A single message in the thread: tinted glass, 16px corners, and a 4px tail corner on the first bubble of each run.

## Use it
- Incoming: `lm-msg lm-msg--in` > `lm-bubble lm-bubble--in`. Outgoing: `lm-msg--out` > `lm-bubble--out`.
- Add `lm-bubble--tail` when the previous row came from the other side (the source's `isFirstInRun`). The tail is bottom-leading when incoming and bottom-trailing when outgoing.
- The meta row (`lm-meta`) sits under the text, right-aligned: "edited", time, "via relay", then StatusTicks on outgoing bubbles only.
- Deleted: `lm-bubble--deleted` with a trash icon and "This message was deleted" in italic `preview`. No reply chip, no actions.

## You provide
The text, time, the delivery path (LAN or relay), edited flag and status. Optionally a ReplyChip as the first child.

## Rules
- Body is `message` in `ink`. Meta is `meta` in `ink-secondary` on incoming and `meta-out` on outgoing. `ink-secondary` fails on the light green.
- Max width is `size-bubble-max` (420), and a bubble always keeps 60px clear on the far side.
- No sender name above incoming bubbles. Every thread is one-to-one and the header already names the peer.
- Context menu: Reply · Copy · Edit (own text only) · Delete for Me · Delete for Everyone (own messages only).

## Build
- **macOS:** `UnevenRoundedRectangle` with `Theme.incomingBubble` / `outgoingBubble` at 86% / 90% opacity. Use the fallback when `accessibilityReduceTransparency` is on.
- **Windows:** `Border` with `CornerRadius="16,16,16,4"` (incoming tail) or `"16,16,4,16"` (outgoing tail), a translucent `SolidColorBrush` and the rim border. No acrylic per bubble.
