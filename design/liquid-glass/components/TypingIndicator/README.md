# TypingIndicator

Three dots that swell in a staggered wave: the shape every messenger uses for "typing…".

## Three placements
- **Thread:** an incoming bubble with the tail, as the last row where the next message will land. The dots are 8px with a 5px gap, in `ink-secondary`.
- **Header:** replaces the Online or Offline line. The dots are 5px with a 3px gap, so the header keeps its height.
- **Sidebar row:** replaces the preview inside an `accent-wash` capsule. The dots are 6px with a 4px gap, in `accent-ink`.

## Motion
Each dot runs 0.6s ease-in-out, scale 0.82 to 1.18 and opacity 0.38 to 1, auto-reversing, each 0.2s behind the one before. With Reduce Motion the dots sit still at 0.8 opacity.

## Accessibility
The dots are decorative. Whoever hosts them supplies the label "Priya is typing".
