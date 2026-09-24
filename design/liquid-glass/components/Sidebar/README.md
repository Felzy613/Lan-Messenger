# Sidebar

The conversation list as a floating glass panel: a title and toolbar, the conversation rows, and an Archived row at the end.

## Rules
- A `glass-regular` panel with `radius-panel` (20), inset 8px from the window, 8px inner padding.
- The title "Chats" is set in `title-pane` beside a flat glass toolbar group holding New message, Contacts and Settings. On macOS the sidebar's title is the window's navigation title, and the toolbar items stay in the window toolbar.
- The Archived row comes **after** the conversations, only when something is archived: a 32px `accent-wash` disc, "Archived", "2 conversations", and a chevron. Both apps already place it last. Keep it there.
- Show only conversations with saved contact or message history. Hidden conversations are reopened from New message and never deleted.

## Build
- **macOS 26:** `.listStyle(.sidebar)` already floats as glass. No change needed.
- **Windows:** the sidebar column in `MainWindow.xaml` becomes one glass panel, inset 8px, holding both the title bar row and `SidebarControl`. It sits directly on the `MicaKind.BaseAlt` backdrop, so it uses a translucent `glass-regular` solid brush plus rim and sheen, not an `AcrylicBrush`: Mica is already the blur.
