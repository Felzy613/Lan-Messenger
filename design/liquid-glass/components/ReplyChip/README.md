# ReplyChip

A quoted message at the top of a bubble; tapping it scrolls to the original and highlights it.

## You provide
Sender ("You" or the peer's name), a one-line preview, and optionally a thumbnail when the original was media.

## Rules
- `inset-fill` background, `radius-chip` (6, concentric with the bubble's 16 minus its padding), 3px `brand` bar.
- Sender in `caption-strong` `accent-ink` (`accent-ink-out` in an outgoing bubble). Preview in `caption`, one line, ellipsised.
- Thumbnail: 36px with `radius-tail`.
- The highlight after the jump is `accent-wash` behind the target row.
