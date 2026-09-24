# DropOverlay

Covers the whole thread while files are dragged over it, saying who they will go to.

## Rules
- An `accent-wash` fill with a 2px dashed `accent-ink` outline, inset 10px and `radius-bubble`.
- In the centre, a `glass-thick` capsule: "Drop to send to Priya Raman".
- The whole thread is the target, not only the composer. The overlay never takes hit tests.
- Every drop converges on the same send-file path as the picker, paste and screenshot.
