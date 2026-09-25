# TextField

A recessed single-line field for use inside glass: display name, update source, contact name.

## Rules
- 32px tall, `radius-card` (8), `inset-fill` with a `glass-edge` outline. Text is 13px `ink` and the placeholder `ink-secondary`.
- Focus is the 2px `focus-ring`. The outline hides while the ring shows.
- The label sits above the field in `label-strong` `ink-secondary`, or it is the settings row's own label.

## Build
- **Windows:** `GlassTextFieldStyle` (Styles/Glass.xaml), a `TextBox` template of its own; `GlassSearchFieldStyle` is the capsule search variant, with the magnifier laid over its leading padding by the page.
