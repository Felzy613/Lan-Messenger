# Button

A 32px capsule button in five kinds: primary, glass, destructive, danger and plain.

## Kinds
- **Primary** (`lm-button--primary`): `brand` with `on-brand` text and a sheen. Use one per view, for the action the view exists for (Share Screen, Save, Install, New message).
- **Glass** (`lm-button--glass lm-glass`): the neutral alternative (Decline, Cancel).
- **Destructive** (`lm-button--destructive lm-glass`): the glass button with `danger-ink` text, for the action in a confirmation that removes data (Delete, Remove). It sits beside a glass Cancel, which stays the default.
- **Danger** (`lm-button--danger`): `danger` with `ink-inverse` text. Only for Stop Sharing on the screen-sharing indicator, where it is the one filled colour. A red fill in a confirmation reads as a toy, not a warning.
- **Plain** (`lm-button--plain`): `accent-ink` text with no fill, for inline actions in settings rows (Change…, Reset, Show more).

## Rules
Labels are Title Case verbs saying exactly what happens. Disabled drops to 50% opacity and takes no hover.
