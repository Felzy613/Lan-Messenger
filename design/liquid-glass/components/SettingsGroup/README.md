# SettingsGroup

Settings as grouped rows on thick glass, one group per section.

## Rules
- The section header is `label-strong` in `ink-secondary`, outside the glass: Identity, Appearance, Startup, Files, Logging, Remote desktop, Cloud relay, Updates.
- A group is `glass-thick` with `radius-panel` (20). Rows are at least 44px, with a `divider` between them and none above the first.
- A row holds a label (an optional hint below in `label` `ink-secondary`) and a trailing control: a Toggle, a value in `code` with a plain "Change…" button, or a TextField.
- Explanations that matter for safety (the remote-desktop kill shortcut, relay behaviour) go in the row hint, not in a tooltip.
- In a sheet whose body scrolls, rows dissolve into the sheet over `space-24` as they pass under the title or come down to the buttons, and only while there is content beyond that edge (the scroll edge effect). Never a hard cut through a row, and never a divider line across the sheet.

## Build
- **Windows:** the Settings sheet is `GlassScrollingDialogStyle`, the dialog sheet on an opaque `glass-regular-fallback` ground, and its page attaches `SheetScrollEdges`: a gradient in the sheet's own colour over each end of the scroll area, read from the ground and the sheen the sheet actually paints at that height. WinUI has no opacity mask, and a fade that ends even a few levels off the sheet draws a line where the cut was, which is why the ground is not acrylic.
