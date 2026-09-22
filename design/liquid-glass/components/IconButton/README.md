# IconButton

A 32px circular button holding one glyph: bare inside glass chrome, grouped on glass in a toolbar, or on its own floating glass.

## Variants
- **Bare** (`lm-icon-button`): inside the composer or header. Hover `glass-hover`, pressed `glass-pressed`.
- **Toolbar group** (`lm-toolbar-group lm-glass`): New message, Contacts and Settings in one capsule.
- **Floating** (`lm-icon-button lm-glass lm-glass--clear`): jump to latest, bottom-right above the composer. It fades in over 0.15s once the reader is more than 40px from the bottom.

## Rules
Always give it an accessible label and a tooltip. Disabled draws the glyph in `ink-secondary` with no fill.
