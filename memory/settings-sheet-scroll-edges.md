---
name: Settings sheet scroll edges (Windows)
description: 2026-09-25 — why the Windows Settings sheet fades its rows at the scroll edges, why it is not acrylic, and what still needs checking on the Dell
type: project
---

**What happened.** After Windows 2.2.3 the user sent a screenshot of Settings,
scrolled, from the Dell and called it a disaster. The viewport cut rows with a
hard edge: "Reset" and "Change…" sliced in half right under the "Settings"
title, and the Cloud relay panel sheared flat above Done with a band of empty
sheet under it. At the startup window size (960×700) the sheet is 632 tall and
shows about 500px of roughly 1000px of content, so some row is always cut.

**What changed.**
- `UI/SheetScrollEdges.cs` (new): a gradient over each end of the Settings
  scroll area, in the sheet's own colour, eased over `space-24`. Each shows only
  while content lies beyond its edge (opacity = distance / depth). Focus and
  caret `BringIntoViewRequested` are inflated by the same depth so a focused
  field never parks inside a fade. `SheetEdgeColors` is the XAML-free maths,
  tested in `SheetScrollEdgesTests.cs`.
- `Styles/Glass.xaml`: `GlassScrollingDialogStyle` (BasedOn `GlassDialogStyle`,
  opaque `TokenGlassRegularFallbackBrush` ground); the template's sheen Border
  is now named `SheenElement` so the edges can read it.
- `GlassDialog.Apply(..., scrollingBody: true)` picks that style; MainWindow's
  Settings dialog passes it. `SettingsPage.xaml` wraps its ScrollViewer in a
  Grid with the two rectangles (`TopScrollEdge`, `BottomScrollEdge`).

**Decisions and why.**
- WinUI 3 has no `OpacityMask`, so content cannot be faded itself. A colour
  overlay must match the sheet to the level or it draws a line. Measured on the
  user's screenshot, the acrylic sheet ran 243 at the bottom to 249 under the
  title (backdrop-dependent), so no fixed colour matches it: the sheet had to go
  opaque.
- Ground = `glass-regular-fallback` (#f0f2f5 / #111b21), not
  `glass-thick-fallback`: on the thick fallback the `glass-thick` groups are
  the same colour as the sheet and read as outlines only. Compared in an HTML
  mock of the 640×632 sheet with token values (light and dark, at rest and
  scrolled) before choosing.
- The edge colour is read at runtime from `BackgroundElement.Background` and
  `SheenElement.Background` sampled at the edge's height, not computed from
  tokens: the sheen adds ~12 levels under the title in dark mode, and the
  sheet's height (hence the sheen there) varies with the window. Anything the
  code cannot read (acrylic ground, unknown brush, parts missing) turns the
  edges off, i.e. the old hard edge, never a guessed colour.
- No divider line at the edges: a line across the Settings sheet was an earlier
  complaint (fixed in 2.2.3 by replacing Fluent's ContentDialog template).
- The fade depth uses the existing `space-24` token rather than a new size
  token, which would have regenerated `GlassTokens.swift` and bumped a macOS
  release with no macOS change.

**Gotchas.**
- Spacing tokens in XAML are `TokenSpaceS24` (with the S), not `TokenSpace24`.
  A wrong `StaticResource` key is a runtime `XamlParseException` when the page
  loads; CI builds and runs MSTest but never instantiates XAML pages, so it
  cannot catch it. Grep every key against `Styles/GlassTokens.xaml`.
- This session had no Windows machine: the change is compiled and tested only
  by CI. Nothing here has been seen running.

**Next.** On the Dell: open Settings at the startup size and scroll through it
in light, dark, and High Contrast; check the top edge under the title (the
sheen makes it lighter there than the bottom), the bottom edge above Done, the
at-rest look (first header fully visible, no fade), Tab through the fields to
see focus land clear of the edges, and toggle the theme with the sheet open.
