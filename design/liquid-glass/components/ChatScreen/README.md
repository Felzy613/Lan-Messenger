# ChatScreen

The whole chat window composed from the system: the reference the Windows app should be brought up to.

## What it shows
- A floating Sidebar panel beside the chat pane.
- A ChatHeader capsule and a Composer pill floating over a thread that runs edge to edge on `wallpaper` with its glows.
- Bubbles in runs with tail corners, a reply chip, a RemoteAuditRow and the typing bubble.

## Rules
The thread is padded by the header height + 16 at the top and the composer height + 16 at the bottom, so at rest nothing is covered. Once the reader scrolls, content passes under the glass.
