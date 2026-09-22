# SettingsGroup

Settings as grouped rows on thick glass, one group per section.

## Rules
- The section header is `label-strong` in `ink-secondary`, outside the glass: Identity, Appearance, Startup, Files, Logging, Remote desktop, Cloud relay, Updates.
- A group is `glass-thick` with `radius-panel` (20). Rows are at least 44px, with a `divider` between them and none above the first.
- A row holds a label (an optional hint below in `label` `ink-secondary`) and a trailing control: a Toggle, a value in `code` with a plain "Change…" button, or a TextField.
- Explanations that matter for safety (the remote-desktop kill shortcut, relay behaviour) go in the row hint, not in a tooltip.
