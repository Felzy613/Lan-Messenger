# Toggle

A switch for on/off settings; switched on, it fills with brand green.

## Rules
- A 40×24 track. Off is `inset-fill` with a `glass-edge` outline. On is `brand` with a rim highlight. The knob is white, 20px, and moves in 0.18s (instantly with Reduce Motion).
- The label says what "on" means: "Launch at login", "Enable cloud relay".

## Build
- **Windows:** `GlassRowToggleStyle` in a settings row, with `Width="40" MinWidth="0" HorizontalAlignment="Right"` repeated on the instance (Fluent's template otherwise keeps room for an On/Off caption, and the switch sat 100px in from the edge). Set `ToggleSwitchFillOn*` and `ToggleSwitchStrokeOn*` to the brand brushes as per-instance `ToggleSwitch.Resources`. A `Style` setter for `Resources` does not compile.
- **macOS:** `Toggle` with `.tint(brand)`.
