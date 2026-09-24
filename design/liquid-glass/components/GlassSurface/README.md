# GlassSurface

The one material every piece of chrome is made of, in three thicknesses: clear, regular and thick.

## Use it
- `lm-glass lm-glass--clear` (`glass-clear`, `blur-clear`) for controls that hold only an icon over busy content: jump-to-latest, toolbar groups, empty-state discs.
- `lm-glass` (`glass-regular`, `blur-regular`) for chrome holding a line of text: chat header, composer, sidebar panel, transfer banner.
- `lm-glass lm-glass--thick` (`glass-thick`, `blur-thick`) for anything read at length: sheets, menus, settings groups, the drop label.
- Add `lm-glass--flat` to drop the float shadow when glass sits inside other glass (a toolbar group inside the sidebar).

## You provide
A shape (the `radius-*` token that fits the role) and content. The surface supplies the fill, blur, sheen, rim and edge.

## Don't
- Don't put body text on clear glass.
- Don't separate two panes of glass with a divider. Use space and `glass-edge`.
- Don't use glass for a security signal. See RemoteHostIndicator.

## Build
- **macOS 26:** `.glassEffect(.clear / .regular, in: shape)`. For thick, use `.regular` over a `glass-thick` tint, or a sheet's system material. Before 26: `.ultraThinMaterial`, `.regularMaterial`, `.thickMaterial`.
- **Windows:** an in-app `AcrylicBrush` with the `acrylic-*` tint and luminosity values, `FallbackColor` = the `-fallback` token, a 1px `GlassRimBrush` border, the sheen `Rectangle`, and `ThemeShadow` at Z 16 (Z 32 for thick).
