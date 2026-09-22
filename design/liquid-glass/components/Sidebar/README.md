# Sidebar

The conversation list as a floating glass panel: a title and toolbar, an Archived row, then the conversation rows.

## Rules
- A `glass-regular` panel with `radius-panel` (20), inset 8px from the window, 8px inner padding.
- The title "Messenger" is set in `title-pane` beside a flat glass toolbar group holding New message, Contacts and Settings.
- The Archived row comes first when anything is archived: a 32px `accent-wash` disc, "Archived", "2 conversations", and a chevron.
- Show only conversations with saved contact or message history. Hidden conversations are reopened from New message and never deleted.

## Build
- **macOS 26:** `.listStyle(.sidebar)` already floats as glass. No change needed.
- **Windows:** replace `SidebarControl`'s `LayerFillColorDefaultBrush` root with a glass `Border`, inset 8px, over the `MicaKind.BaseAlt` window backdrop.
