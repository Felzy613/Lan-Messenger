# Implementation

The tokens are platform-neutral. This section maps them onto WinUI 3 (Windows App SDK 1.5, as the project pins) and SwiftUI. Windows comes first because that is where most of the work is. The step-by-step, agent-ready version is `docs/LIQUID_GLASS_PLAN.md` in the repository, and its resource names are the ones used here.

## Windows (WinUI 3)

### 1. One glass recipe, as resources

Add a `Styles/Glass.xaml` resource dictionary and merge it in `App.xaml`. Give it `Light`, `Dark` and `HighContrast` theme dictionaries. HighContrast maps every glass brush to `SystemColorWindowColor` with no acrylic, the way `AppChatBackgroundBrush` already does. Each thickness is an in-app `AcrylicBrush` whose parameters come straight from the tokens:

```xml
<!-- Light. Dark swaps TintColor to #111B21 and FallbackColor to #111B21. -->
<AcrylicBrush x:Key="GlassRegularAcrylicBrush"
              TintColor="#F8FAFB"
              TintOpacity="0.62"
              TintLuminosityOpacity="0.80"
              FallbackColor="#F0F2F5" />

<!-- The rim: bright at the top edge, settling to glass-edge. -->
<LinearGradientBrush x:Key="GlassRimBrush" StartPoint="0,0" EndPoint="0,1">
    <GradientStop Color="#E6FFFFFF" Offset="0" />
    <GradientStop Color="#1A0B141A" Offset="0.35" />
    <GradientStop Color="#1A0B141A" Offset="1" />
</LinearGradientBrush>

<!-- The sheen: laid over the fill, fading out by 55% of the height. -->
<LinearGradientBrush x:Key="GlassSheenBrush" StartPoint="0,0" EndPoint="0,1">
    <GradientStop Color="#73FFFFFF" Offset="0" />
    <GradientStop Color="#00FFFFFF" Offset="0.55" />
</LinearGradientBrush>
```

A glass surface is then a `Border` with `Background="{ThemeResource GlassRegularAcrylicBrush}"`, `BorderBrush="{ThemeResource GlassRimBrush}"`, `BorderThickness="1"`, a `CornerRadius` from the radius tokens, and `Translation="0,0,16"` with a `ThemeShadow` (Z 32 for sheets). Its first child is a `Rectangle` filled with `GlassSheenBrush`, with `IsHitTestVisible="False"` and the same corner radius.

- `AcrylicBrush` in WinUI 3 is **in-app** acrylic: it blurs whatever this window draws behind the element. That is exactly what the chat header and composer need, because the thread scrolls under them.
- **Acrylic only over moving content.** Use an `AcrylicBrush` where app content moves underneath: the header, composer, transfer banner and jump button. A surface sitting straight on the window backdrop (the sidebar panel) uses a translucent `SolidColorBrush` of the same glass token, because Mica is already the blur. Bubbles also use translucent solid brushes.
- `FallbackColor` is the `-fallback` token. WinUI switches to it by itself when transparency effects are off, on battery saver, or in a remote session, so there is no code path to write.
- Outside popups, `ThemeShadow` casts only onto elements in its `Receivers` collection. Add the thread's background grid as a receiver, or the floating chrome casts nothing.
- Keep brushes as shared resources. `Theme.cs` already explains why per-call `new SolidColorBrush(...)` hurt scrolling. The same applies to acrylic, and more so.

### 2. Restructure ChatPage so the thread runs under the chrome

This is the biggest change, and the one that makes the Windows app read as glass. `ChatPage.xaml` today is a five-row grid: header, transfer banner, list, reply banner, composer. Every row is opaque, so nothing ever passes under anything.

- Collapse it to a single cell. The background grid paints `wallpaper` plus the two glows (two `Ellipse`s with a `RadialGradientBrush` each, `wallpaper-glow` fading to transparent), and the `ListView` fills the whole cell.
- Overlay the header (`VerticalAlignment="Top"`, `Margin="8"`) and the composer (`VerticalAlignment="Bottom"`, `Margin="8"`) on top of the list. The transfer banner stacks under the header, and the jump button sits above the composer.
- Set the `ListView`'s `Padding` to the header height + 16 at the top and the composer's measured height + 16 at the bottom. Update it from the composer's `SizeChanged`, because the composer grows with the reply banner and with multi-line drafts. The existing "pinned to bottom" logic in `ScrollToBottomSettled` measures against the viewport, so re-verify that it still settles with the padding in place.
- Remove the `DividerStrokeColorDefaultBrush` borders between the rows. Glass separates itself.

### 3. Component by component

| Component | Today | Change to |
| --- | --- | --- |
| Window backdrop | `MicaBackdrop { Kind = MicaKind.Base }` | `MicaKind.BaseAlt`. It is more strongly tinted by the wallpaper and sits closer to `glass-regular`. Keep the `DesktopAcrylicBackdrop` fallback. |
| Sidebar | `LayerFillColorDefaultBrush` full-height, Archived as a footer | One glass panel inset 8px, `CornerRadius="20"`, holding the title row and `SidebarControl`. A translucent solid brush, not acrylic (it sits on Mica). Rows use `radius-row` highlights. Archived stays last. |
| Selected row | Fluent accent pill at the leading edge | A `glass-thick` lozenge: on the `ListView`'s own `Resources`, set `ListViewItemSelectionIndicatorVisualEnabled` to `False` and point `ListViewItemBackgroundSelected*` at `TokenGlassThickBrush`. Use per-instance resources, not a `Style` setter (`Resources` is not a dependency property; `WMC0095`). |
| Unread badge | `AppAccentBrush` with a white `TextBlock` | Keep the green. Set the text to `on-brand` #0B141A: white on #25D366 is 2:1. |
| Chat header | Full-width row, 40px avatar, bottom divider | A glass capsule 56px tall, `CornerRadius="28"`, 36px avatar, no divider. |
| Message bubble | Opaque brushes from `Theme.cs`, `MaxWidth="520"` | `SolidColorBrush` with alpha (`#DBFFFFFF` in, `#E6DCF8C6` out; dark `#DB202C33` / `#E0005C4B`), `MaxWidth="420"`. The tail is `CornerRadius="16,16,16,4"` incoming and `"16,16,4,16"` outgoing (TL, TR, BR, BL). Add the 1px rim border. **No acrylic per bubble.** Only the wallpaper is behind a bubble, and dozens of blurred surfaces in a virtualised list cost frames for no visible gain. |
| Read tick | `CheckBlue` #4F9EF7 in both themes | Make it theme-aware in `Theme.Initialize`: `tick-read` #1A7FD4 light, #53BDEB dark. |
| Reply chip | `#1A808080`, radius 4 | `inset-fill`, radius 6. |
| Composer | `LayerFillColorDefaultBrush` strip, field radius 20 | Transparent root. Attach, screenshot and the field share one glass pill (`CornerRadius="22"`, 4px inner padding), and the reply or edit banner sits inside that pill above the field. |
| Send button | 38px, white `FontIcon` | 36px, `CornerRadius="18"`, `brand` fill with a sheen overlay. **The `FontIcon` sets `Foreground="White"` directly**, so the `ButtonForeground` resources never apply: change the icon itself to #0B141A. Disabled: glass-regular with an `ink-secondary` glyph instead of #8E8E93 grey. |
| Jump to latest | 34px default button | 32px, `CornerRadius="16"`, clear glass (`TintOpacity="0.30"`). |
| Transfer banner | Full-width row | A glass capsule under the header, with a `brand` progress fill on an `inset-fill` track and byte counts in Cascadia Mono. |
| Host indicator window | `#D93025` / `#E58C1A` strip, radius 18 inside a rectangular borderless window, white text in both states | A neutral HUD: `Strip` fills the window in `TokenHudSurfaceBrush` (`CornerRadius="0"`), and the window asks DWM for rounded corners (`DWMWA_WINDOW_CORNER_PREFERENCE` = `DWMWCP_ROUND`). Text in `hud-ink`, elapsed in `hud-ink-secondary`, and the pulse dot in `hud-signal-view` or `hud-signal-control`. **Stop Sharing** is solid `danger`, and **Stop Control** is `hud-button`. Controlled adds a `hud-signal-control` outline through `DWMWA_BORDER_COLOR`. **Solid, no acrylic.** |
| Consent window | `LayerFillColorDefaultBrush` | Make the root transparent and set the window's `SystemBackdrop` to `DesktopAcrylicBackdrop`, so the whole window is real glass over the desktop. The warning note becomes `warning-wash` with a `warning-ink` icon instead of `#33C04B1A` / `#E8A33D`. **Decline keeps the accent style and stays the default. Allow stays plain.** |
| Content dialogs | Default | One helper sets each dialog's `Background` to `GlassThickAcrylicBrush` and `CornerRadius` to 26 (the template binds both), plus the `OverlayCornerRadius` resource for the inner grid. |
| Settings | Cards with radius 6 | Grouped `glass-thick` panels with radius 20 and `divider` between rows. Toggles already use `AppAccentBrush` for the on-track, which stays. |
| Avatars | 8 colours | Replace them with the eight `avatar-*` values. None of the current eight gives white initials 4.5:1, and four of them (#FFCC00, #5AC8FA, #50C878, #FF9500) are under 2.3:1. |

Top-level windows keep the operating system's own corner (Windows 11 rounds them at 8px). `radius-sheet` applies to sheets and dialogs drawn inside a window.

### 4. Where the values live

`Theme.cs` keeps owning everything painted from code-behind (bubbles, ticks, avatars), and `Initialize(isDark)` swaps them. Everything painted from XAML reads `ThemeResource`s. Colours come from the generated `Styles/GlassTokens.xaml` (`Token<Name>Brush`), and the hand-written `Styles/Glass.xaml` holds the surface and button styles. `scripts/design/gen_tokens.py` generates `GlassTokens.xaml`, `GlassTokens.cs` and `GlassTokens.swift` from `tokens.json`, so a token change never has to be copied by hand.

## macOS (SwiftUI)

On macOS 26 the sidebar list, toolbars, menus and sheets already render as Liquid Glass. The app deploys to macOS 13, so every change below goes behind `if #available(macOS 26, *)` and keeps today's material as the fallback.

Touch-ups, most visible first:

- **RemoteConsentView** paints an opaque `Color(white: 0.13 / 0.98)` behind the whole panel, which blocks the system glass. Use `.glassEffect(.regular, in: .rect(cornerRadius: 26))`, with `.thickMaterial` before 26. Its orange note becomes `warning-wash` plus `warning-ink`.
- **ComposerView** sits on `.bar` with a `.quaternary` field. Wrap it in a `GlassEffectContainer`: the pill takes `.glassEffect(in: .capsule)` and the send button becomes a `brand` orb (`.buttonStyle(.glassProminent)` tinted `brand`, glyph in `on-brand`). Letting the thread scroll under the composer and header (`.safeAreaInset(edge:)`) is a separate, later step. `ChatView` computes the distance to the bottom as `contentBottom − viewportHeight`, and a bottom inset makes that wrong by exactly the inset's height. `docs/LIQUID_GLASS_PLAN.md` gives the correction and how to verify it.
- **Chat header**: replace `.background(.bar)` and the `Divider()` under it with `.glassEffect(in: .capsule)`.
- **Jump to latest**: replace the `.regularMaterial` circle with `.glassEffect(.clear, in: .circle)`.
- **Bubbles**: `Theme.incomingBubble` and `Theme.outgoingBubble` become 86% and 90% translucent. Read `@Environment(\.accessibilityReduceTransparency)` and use the `-fallback` value when it is on.
- **RemoteHostIndicatorView**: becomes the neutral HUD. A `hud-surface` capsule with a `hud-rim` stroke (`hud-signal-control` when controlled), the pulse dot in the state colour, `hud-ink` text, a `danger` Stop Sharing button and a `hud-button` Stop Control button, pinned to `.colorScheme(.dark)`. It stays solid: no `glassEffect`.
- **BubbleStatusView**: `tick-read` per theme instead of `Color(red: 0.31, green: 0.62, blue: 0.97)`.
- **Unread badge**: `on-brand` text instead of white.
- **Relay badge**: drop the `.opacity(0.75)`. It takes `ink-secondary` below 4.5:1.
- **Theme.avatarColor**: port the FNV-1a function and the eight `avatar-*` colours. `abs(name.hashValue)` is reseeded every launch, so today a contact's colour changes on every restart and never matches Windows.
