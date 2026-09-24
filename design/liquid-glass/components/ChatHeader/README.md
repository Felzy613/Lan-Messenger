# ChatHeader

A floating glass capsule over the top of the thread: avatar, name with presence, status line, and the remote-desktop button.

## You provide
The peer's name, photo or initials, online state, typing state, and whether remote desktop is available (with the reason if not).

## Rules
- 56px tall (`size-header`), `radius-pill`, `glass-regular`, 8px from the pane edges. The thread scrolls under it.
- Avatar `size-avatar-header` (36). The name is `name`, followed by an 8px presence dot. The status line is `caption`: "Online" or "Offline", or the typing dots.
- The remote-desktop button is always present. When unavailable it is disabled with its reason as the tooltip, never hidden.
- No divider below it.
