# ConversationRow

One conversation in the sidebar: avatar with presence, name, time, a two-line preview or typing capsule, unread badge, and an options menu.

## Rules
- `size-row` (72) tall with `radius-row` (12) corners. Hover `glass-hover`. Selected is a `glass-thick` lozenge with the rim, replacing the platform's accent bar.
- The name is `name`. The time is `caption` in `ink-secondary`; with unread messages it becomes semibold `accent-ink`.
- The preview is `preview`, two lines at most. Attachments and remote-desktop records show their human summary, never the stored `__FILE__:` or `__REMOTE__:` text.
- The unread badge is a `brand` capsule with `on-brand` numerals in `badge`.
- The options menu (Archive or Unarchive, then Delete conversation) draws at 22px with a 32px hit area.
