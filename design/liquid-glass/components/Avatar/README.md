# Avatar

A contact's photo, or their initials on one of eight colours, with an optional presence dot.

## You provide
The display name, a photo if there is one, and the size (`size-avatar-row` 44, `size-avatar-header` 36, or 28 in lists).

## Rules
- The colour is `avatar-*`, chosen by FNV-1a of the name modulo 8, the same on both platforms (see the brand book). Never use a per-launch hash.
- Initials are `ink-inverse` at 0.38 × size, semibold. Every `avatar-*` gives them 4.8:1 or better.
- A soft glass rim (a light inset top, a dark inset bottom) makes the disc read as a bead rather than a sticker.
- The presence dot is `size-presence` (11), offset 2px out from the bottom-trailing edge, in `presence-online` or `presence-offline`, ringed 2px in `presence-ring`.
