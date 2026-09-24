# RemoteHostIndicator

The always-on-top capsule that tells a host their screen is being viewed or controlled, with the buttons to stop it: a neutral dark HUD, not a coloured alarm.

## Anatomy
- A `hud-surface` capsule, 44px tall, with a 1px `hud-rim` edge. It is always dark and identical in light and dark themes, like the system's own screen-recording HUDs, so it reads as system chrome over any desktop.
- An 8px status dot that breathes on a 1.6s cycle: `hud-signal-view` (amber) while viewing, `hud-signal-control` (coral) while controlling.
- One line of text: the headline in `label-strong` `hud-ink` ("Priya is viewing your screen" / "Priya is controlling your screen"), then the elapsed time ("4:12") in `hud-ink-secondary` with tabular figures.
- A hairline separator, then the actions: **Stop Control** (neutral `hud-button`, controlled state only) and **Stop Sharing** (solid `danger` with `ink-inverse`).

## Rules
- **Colour is spent in two places only:** the status dot and the Stop Sharing button. The surface never takes the state colour. A full amber or red bar reads as an alarm, and an alarm that stays up for a whole session teaches people to stop seeing it.
- Control is the riskier state, so it also gets a 1px `hud-signal-control` outline, which is noticeable without shouting. The state is always in the words as well, never colour alone.
- Solid, never translucent. The desktop behind must not wash out a security signal. It keeps the rim and a faint sheen, but no blur.
- Salience comes from position (top centre, above everything, never hidden on deactivate), the breathing dot and the red Stop button, not from a coloured bar.
- The kill shortcut (⌃⌥⌘⎋ on macOS, Ctrl+Alt+Shift+Esc on Windows) works whether or not the indicator is visible.

## Build
- **Windows:** `Strip` fills the borderless window with `TokenHudSurfaceBrush`, and the window asks DWM for rounded corners (`DWMWA_WINDOW_CORNER_PREFERENCE` = `DWMWCP_ROUND`). The controlled outline is the **system border**: `DWMWA_BORDER_COLOR` (34), set to `hud-signal-control` as a COLORREF (`0x00BBGGRR`), and set back to `DWMWA_COLOR_DEFAULT` (`0xFFFFFFFF`) for viewing. It follows the rounded corners, which a XAML border inside a clipped window cannot.
- **macOS:** `RemoteHostIndicatorView` fills a `Capsule` with `hud-surface`, strokes it with `hud-rim` (or `hud-signal-control` when controlled), and pins `.environment(\.colorScheme, .dark)` so the system controls inside match. It stays solid: no `glassEffect`.
