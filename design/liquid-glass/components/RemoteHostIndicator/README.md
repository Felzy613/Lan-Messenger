# RemoteHostIndicator

The always-on-top capsule that tells a host their screen is being viewed or controlled, with the buttons to stop it.

## States
- **Viewed:** a solid `warning` capsule with `on-warning` text. "Priya is viewing your screen", then **Stop Sharing**.
- **Controlled:** a solid `danger` capsule with `ink-inverse` text. "Priya is controlling your Mac", then **Stop Control** and **Stop Sharing**.

## Rules
- This is signal glass: a solid tint with the glass rim and sheen, never translucent. The desktop must not wash out a security signal.
- The headline is `label-strong`, and the elapsed time `meta` with tabular figures. The dot breathes on a 1.6s cycle.
- Buttons are translucent-white capsules inside the tint, `caption-strong`.
- It stays up for the whole session and never hides on deactivate. The kill shortcut ⌃⌥⌘⎋ works whether or not it is visible.
