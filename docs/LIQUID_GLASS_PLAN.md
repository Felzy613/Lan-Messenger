# Liquid Glass UI: implementation plan

This plan takes both apps to the **LAN Messenger Glass** design system in
`design/liquid-glass/`. It is written for an agent to execute top to bottom.
Every milestone lists the files it touches, the steps, what "done" means, and how
to prove it.

| | |
| --- | --- |
| Design source | `design/liquid-glass/` (README, `implementation.md`, `tokens.json`, `components/*/README.md`). The published copy is https://claude.ai/artifact/6zeqPGoUvV8Q6PjNDNgpBB |
| Scope | Visual only. No protocol, storage, networking or behaviour change. |
| Order | Groundwork (G) → Windows (W1–W10) → macOS (M1–M6) → Wrap-up (X) |
| PRs | Three: **G** alone, then **W**, then **M**. Each is reviewable on its own. |
| Status | Tick the boxes in this file in the same commit that finishes the step. |

---

## Read before starting

1. `design/liquid-glass/README.md`: principles, colour roles, type, shape, states.
2. `design/liquid-glass/implementation.md`: the platform mapping this plan executes.
3. The component README for each milestone (named in the milestone).
4. `CLAUDE.md` → *Do not accidentally regress*. These rules sit directly in the path of this work:
   - **Scroll pinning** on both platforms: the latched `pinnedToBottom` / `_pinnedToBottom`, the ~0.6s settle (`pinToBottom`, `ScrollToBottomSettled`), and "only a reading taken at rest may unpin".
   - **SwiftUI preference-in-background trap.** `ChatView`'s scroll sentinels must stay real siblings inside the `ScrollView`.
   - **WinUI drag and drop.** No `await` and no throw in `DragEnter`/`DragOver`. Drop handlers stay registered on the children (`WireDropTargets`) with `handledEventsToo`.
   - **`Style` cannot set `Resources`** (`WMC0095`). Per-control resource overrides are per-instance `*.Resources` blocks, or a full `ControlTemplate` in a style.
   - **`MicaKind` lives in `Microsoft.UI.Composition.SystemBackdrops`**, not `Microsoft.UI.Xaml.Media`.
   - **`DllImport` names must be real exported entry points** (or set `EntryPoint`). Nothing inside a WinUI event handler may throw.
   - **Remote-desktop consent: Decline is the default.** Enter, Escape, closing and the countdown all decline, and Allow has no shortcut. The warning colour appears only for an unexpected key.
   - **Image caches**: `BitmapCreateOptions.IgnoreImageCache` before `UriSource` on Windows, and `ThumbnailCache` keyed on path plus modification date plus size on macOS.
5. Memory notes worth reading: `shared-working-tree-concurrent-sessions`, `windows-build-check-on-macos`, `windows-remote-build-access`, `driving-the-dell-headlessly`, `macos-ui-visual-verification`, `windows-winui-accent-backdrop-gotchas`.

## Ground rules

- **Work in your own git worktree.** Other sessions switch branches in the shared checkout (it has happened twice). Create the branch and worktree once:
  ```bash
  git fetch origin
  git worktree add ../lm-glass -b feat/liquid-glass-ui origin/main
  ```
  If `design/liquid-glass/` is not on `main` yet, base the branch on `design/liquid-glass-system` instead.
- **No colour, radius or size literal outside the generated token files.** If a value is missing, add it to `tokens.json`, regenerate, and republish the design system. Do not hard-code it.
- **Every change must survive the three accessibility paths**: transparency off (Windows *Transparency effects* off or battery saver; macOS *Reduce transparency*), dark mode, and Windows High Contrast.
- **Copy does not change** except where a step says so.
- **Versions.** The pre-commit hook bumps a patch version on every commit that touches a platform tree. That is expected: do not hand-edit `version/*.json`. The release version is set at release time, as in `chore: 2.0.0`.
- **Proof before "done".** For C#: compile on the Mac with the shim (fast loop), then a real MSBuild and MSTest run on the Dell before each Windows milestone is ticked. For Swift: `swift build && swift test`, plus the ImageRenderer harness for anything visual.

## Out of scope, owned elsewhere

- **Avatar palette and FNV-1a hash.** Being done in the separate session "Fix macOS avatar colours reshuffling each launch". If that has merged when you reach W1/M1, skip the avatar lines below. If not, do them there, using its spec: eight `avatar-*` colours, FNV-1a over UTF-16 code units, `index = (hash % 2147483647) % 8`, and a shared test vector in both suites.
- **Remote-desktop audit rows on Windows.** Windows never writes them. `RemoteDesktopController.AppendAudit` is declared and never assigned, so no `__REMOTE__:` record reaches Windows history and `ChatPage.MapEntry` has no case for one. The RemoteAuditRow component is therefore macOS-only for now. Fixing the gap is a feature, not a restyle. File it separately.
- `RemoteViewerWindow` (both platforms), `MediaPreviewWindow` / `MediaPreviewSheet`, the region-screenshot overlay: unchanged. They show media on black by design.

---

## G: Groundwork (both platforms, PR 1)

### G1. Token generator
- [ ] Add `scripts/design/gen_tokens.py` (Python 3, standard library only). It reads `design/liquid-glass/tokens.json` and writes three files, each starting with `// GENERATED by scripts/design/gen_tokens.py from design/liquid-glass/tokens.json. Do not edit.` (use `<!-- … -->` in XAML):
  1. `src/macos/LanMessenger/UI/GlassTokens.swift`
  2. `src/windows-native/LanMessenger/UI/GlassTokens.cs`
  3. `src/windows-native/LanMessenger/Styles/GlassTokens.xaml`
- [ ] **Colour parsing.** `#rrggbb` becomes alpha FF. `rgba(r, g, b, a)` becomes alpha `round(a × 255)`. `{name}` resolves to that token's value *for the same theme*; follow chains, and error on a cycle or a missing name. A plain string value applies to both themes.
- [ ] **Naming.** Token `accent-ink-out` becomes Swift `accentInkOut` and C#/XAML `AccentInkOut`. Radius, spacing and size tokens drop their family prefix inside a nested namespace (`radius-bubble` becomes `GlassTokens.Radius.bubble` / `GlassTokens.Radius.Bubble`).
- [ ] **Swift output:**
  ```swift
  import SwiftUI
  import AppKit

  enum GlassTokens {
      // Dynamic colours: resolve per NSAppearance, so no colorScheme needs threading through.
      static let brand = Color(glassLight: 0xFF25D366, dark: 0xFF25D366)
      // …one per colour token…
      enum Radius { static let bubble: CGFloat = 16 /* … */ }
      enum Space  { static let s12: CGFloat = 12 /* space-12 → s12 */ }
      enum Size   { static let avatarRow: CGFloat = 44 /* … */ }
      enum Blur   { static let regular: CGFloat = 24 /* … */ }
  }

  extension Color {
      init(glassLight light: UInt32, dark: UInt32) {
          self.init(nsColor: NSColor(name: nil) { appearance in
              let isDark = appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
              return NSColor(argb: isDark ? dark : light)
          })
      }
  }
  // plus a fileprivate NSColor(argb:) initialiser
  ```
- [ ] **C# output.** A `public static class GlassTokens` in `LanMessenger.UI`, holding `public static readonly Windows.UI.Color BrandLight, BrandDark, …` for every colour token, `public static Color Pick(Color light, Color dark) => Theme.IsDark ? dark : light;`, nested `Radius`, `Space`, `Size` and `Acrylic` classes of `double` constants, and one `Color` per theme for each fallback.
- [ ] **XAML output.** A `ResourceDictionary` with `ThemeDictionaries` for `Light`, `Dark` and `HighContrast`:
  - one `SolidColorBrush x:Key="Token<Name>Brush"` per colour token (`glass-hover` becomes `TokenGlassHoverBrush`), plus a `Color x:Key="Token<Name>Color"`
  - three acrylic brushes per theme. `GlassClearAcrylicBrush` uses TintColor = RGB of `glass-clear`, TintOpacity = `acrylic-clear-tint`, TintLuminosityOpacity = `acrylic-clear-luminosity`, and FallbackColor = `glass-regular-fallback`. `GlassRegularAcrylicBrush` follows the same pattern. `GlassThickAcrylicBrush` uses FallbackColor = `glass-thick-fallback`.
  - `GlassSheenBrush`: a vertical `LinearGradientBrush` from `glass-sheen` at 0 to its transparent twin at 0.55.
  - `GlassRimBrush`: `glass-rim` at 0, then `glass-edge` at 0.35 and at 1.
  - `x:Double` resources for the radius, size and space tokens, and `CornerRadius` resources `TokenRadius<Name>`.
- [ ] **HighContrast** maps by role, without acrylic or gradients:

  | Tokens | HighContrast resource |
  | --- | --- |
  | wallpaper, sidebar-ground, glass-*, bubble-*, *-fallback, inset-fill, scrim | `SystemColorWindowColor` |
  | ink, ink-secondary, meta-out, glass-edge, divider, glass-rim | `SystemColorWindowTextColor` |
  | brand*, accent-*, focus-ring, tick-read, presence-online, warning*, danger* | `SystemColorHighlightColor` |
  | on-brand, on-warning, ink-inverse | `SystemColorHighlightTextColor` |
  | presence-offline, glass-hover, glass-pressed, glass-sheen, avatar-* | `SystemColorGrayTextColor` |
  | wallpaper-glow* | `Transparent` |

- [ ] `--check` mode regenerates into memory and exits 1, with a diff, if any output differs from disk.
- [ ] Add a step to `.github/workflows/pr-checks.yml` in **both** jobs, before tests: `python3 scripts/design/gen_tokens.py --check` (`python` on the Windows runner).

**Done when:** generation is deterministic (running it twice gives no diff), `--check` passes, and the Swift and C# files compile.

### G2. Token tests
- [ ] `src/macos/LanMessengerTests/GlassTokensTests.swift` and `src/windows-native/LanMessenger.Tests/GlassTokensTests.cs`, with the same cases on both sides:
  - Spot values: `brand` = #25D366 in both themes; `bubbleOut` dark = #E0005C4B; `Radius.bubble` = 16; `Size.bubbleMax` = 420.
  - **Contrast guards.** Implement WCAG relative luminance. A surface is the glass token composited over `wallpaper` and over `wallpaper-glow`-on-`wallpaper`, whichever is worse. Assert per theme:

    | Foreground | Surface | Minimum |
    | --- | --- | --- |
    | ink | bubble-in, bubble-out, glass-regular, glass-thick | 7.0 |
    | ink-secondary | bubble-in, glass-regular, glass-thick, sidebar-ground | 4.5 |
    | meta-out | bubble-out | 4.5 |
    | accent-ink | bubble-in, glass-regular, glass-thick | 4.5 |
    | accent-ink-out | bubble-out | 4.5 |
    | on-brand | brand | 4.5 |
    | on-warning | warning | 4.5 |
    | ink-inverse | danger, every avatar-* | 4.5 |
    | tick-read | bubble-out | 3.0 |
    | focus-ring | wallpaper, sidebar-ground | 3.0 |

  These guard future token edits. They fail loudly if someone "tunes" a colour below the floor.

**Done when:** both suites pass: `swift test` on the Mac, and the Mac shim plus the Dell MSTest run.

---

## W: Windows (PR 2)

Inner loop after every step: the shim compile from `windows-build-check-on-macos` (it compiles every `*.xaml.cs` via generated stubs). Real proof at the end of each milestone: copy the tree to the Dell (`COPYFILE_DISABLE=1 tar … --exclude='._*'`), restore and build as **separate** MSBuild calls, and run MSTest against the **newest** DLL, printing its timestamp.

### W1. Resources and Theme.cs
Files: `App.xaml`, new `Styles/Glass.xaml`, `Styles/GlassTokens.xaml` (generated), `UI/Theme.cs`.
- [ ] `App.xaml`: merge, in this order, `XamlControlsResources`, `ms-appx:///Styles/GlassTokens.xaml`, then `ms-appx:///Styles/Glass.xaml`. Point the existing keys at tokens so current call sites keep working until they are migrated: `AppChatBackgroundBrush` → wallpaper, `AppReplyAccentBrush` → brand, `AppReplyAccentTextBrush` → accent-ink, `AppOnlineDotStrokeBrush` → presence-ring, `AppAccentBrush*` → brand, brand-hover and brand-pressed, `AppIconHoverBrush` → glass-hover, `AppIconPressedBrush` → glass-pressed.
- [ ] `Styles/Glass.xaml` (hand-written) holds:
  - **`GlassSurfaceStyle`** (`TargetType="ContentControl"`). A template of four layers in a `Grid`: a fill `Border` (`Background="{TemplateBinding Background}"`, `CornerRadius="{TemplateBinding CornerRadius}"`); a sheen `Border` (`GlassSheenBrush`, same radius, `IsHitTestVisible="False"`); a `ContentPresenter` with `Margin="{TemplateBinding Padding}"`; and a rim `Border` (`BorderBrush="{ThemeResource GlassRimBrush}"`, `BorderThickness="1"`, same radius, `IsHitTestVisible="False"`). The setters default `Background` to `GlassRegularAcrylicBrush`, `HorizontalContentAlignment` to Stretch, and `IsTabStop` to False.
  - **`GlassIconButtonStyle`** (`TargetType="Button"`, full `ControlTemplate`): 32×32, `CornerRadius="16"`, a transparent `Border` whose background the `VisualStateManager` sets to `TokenGlassHoverBrush` in `PointerOver` and `TokenGlassPressedBrush` in `Pressed`. In `Disabled` the foreground is `TokenInkSecondaryBrush`. The focus visual is `TokenFocusRingBrush`, 2px. Doing this in a template replaces the six-line `Button.Resources` block repeated on every icon button today.
  - **`GlassPrimaryButtonStyle`**: `brand` fill with a sheen, `on-brand` text, 32 tall, `CornerRadius="16"`, `brand-hover` and `brand-pressed` states, 50% opacity when disabled.
  - **`GlassButtonStyle`**: a glass-regular fill with rim, `ink` text, the same metrics.
  - **`GlassDangerButtonStyle`**: `danger` fill with a sheen, `ink-inverse` text, the same metrics. Only for actions that remove data, and always behind a confirmation.
  - **`GlassPillButtonStyle`**: the in-bubble Open and Show pills. `inset-fill` fill, `caption-strong` text, padding 8,4, fully rounded. An `Accent` variant uses `accent-wash` fill with `accent-ink` text.
- [ ] `UI/Theme.cs`: keep the public surface and re-source every colour from `GlassTokens`.
  - `IncomingBubbleBrush` and `OutgoingBubbleBrush` become the translucent bubble tokens.
  - `CheckBlueBrush` becomes `tick-read`, now theme-dependent.
  - `CheckGreyBrush` stays for incoming bubbles, and a new `MetaOutBrush` is used inside outgoing ones.
  - Add `MetaInBrush` (ink-secondary), `AccentInkBrush`, `AccentInkOutBrush`, `OnBrandBrush` and `DangerInkBrush`.
  - `OnlineDotBrush` and `OfflineDotBrush` take the theme-dependent presence tokens.
  - `Initialize(isDark)` recreates **every** theme-dependent brush. Today it skips the ticks and the dots.
  - When `UISettings.AdvancedEffectsEnabled` is false, the bubble brushes use the `-fallback` tokens. Subscribe to `UISettings.AdvancedEffectsEnabledChanged` and re-run `Initialize`, marshalled to the UI thread.

**Done when:** the app builds and looks unchanged apart from colours now coming from tokens, and every brand-coloured control still shows the brand green.

### W2. Window shell and sidebar panel
Files: `MainWindow.xaml(.cs)`, `UI/Sidebar/SidebarControl.xaml`.
- [ ] `ApplyBackdrop()`: `new MicaBackdrop { Kind = MicaKind.BaseAlt }`, with `using Microsoft.UI.Composition.SystemBackdrops`. Keep the `DesktopAcrylicBackdrop` fallback.
- [ ] Sidebar column: the existing column-0 `Grid` becomes a `ContentControl Style="{StaticResource GlassSurfaceStyle}"`:
  - `Margin="8,8,0,8"`, `CornerRadius="20"`, `Padding="8"`
  - `Background="{ThemeResource TokenGlassRegularBrush}"`, the translucent **solid** brush, not acrylic: it sits on Mica, which is already the blur.
  - Remove its `BorderBrush` / `BorderThickness`, and widen the column to `296` so the list keeps its width.
  - The title row: `Text="Chats"` in 20/26 semibold, and the three buttons use `GlassIconButtonStyle` inside a flat glass capsule (`CornerRadius="20"`, `Padding="3"`, `Background` = `TokenGlassClearBrush`, rim only).
- [ ] `SidebarControl.xaml` root `Grid`: `Background="Transparent"`.
- [ ] `NoConversationState`: the EmptyState recipe. A 64px `glass-clear` disc holding the E8BD glyph in ink-secondary, "LAN Messenger" in 20/26 semibold, and the sentence in 12/16 ink-secondary, at most 240 wide. Keep the existing copy.
- [ ] The sidebar empty state gets the same recipe, with its existing copy.

**Done when:** in light, dark and transparency-off, the sidebar reads as a rounded glass panel floating on Mica with an 8px gap all round, and nothing is clipped at 960×700 (the startup size).

### W3. ChatPage: the thread runs under the chrome
Files: `UI/Chat/ChatPage.xaml(.cs)`. This is the structural change. Do it before any restyling so the scroll behaviour is proven on its own.
- [ ] Replace the five-row `Grid` with a single cell holding these children, in z-order:
  1. `ThreadBackground` (`Grid`): `Background="{ThemeResource TokenWallpaperBrush}"` plus two `Ellipse`s with a `RadialGradientBrush` each. The first uses `wallpaper-glow` to transparent, 60%×55% of the pane, anchored top-left. The second uses `wallpaper-glow-alt`, anchored bottom-right. Both `IsHitTestVisible="False"`.
  2. `MessagesList`: fills the cell, with `Background="Transparent"` (not the wallpaper brush, or it hides the glows).
  3. `TopChrome` (`StackPanel`, `VerticalAlignment="Top"`, `Margin="8"`, `Spacing="6"`): the header, then `TransferBanner`.
  4. `JumpToLatestBtn`: bottom-right, `Margin="0,0,20,0"`, positioned 12px above the composer (set its bottom margin from the composer height, see below).
  5. `Composer`: `VerticalAlignment="Bottom"`, `Margin="8"`.
  6. `DropOverlay`: spans the cell, unchanged apart from W4's styling.
- [ ] **Padding sync.** `MessagesList.Padding = new Thickness(12, TopChrome.ActualHeight + 16, 12, Composer.ActualHeight + 16)`. Run it from `TopChrome.SizeChanged` and `Composer.SizeChanged`. ListView padding lives on the `ItemsPresenter` **inside** the `ScrollViewer` (verified in the WinAppSDK 1.5 template), so it scrolls with the content and `ScrollableHeight` already includes it. `ScrollToBottom()` therefore still lands with the last bubble clear of the composer, and needs no change.
- [ ] Growing the bottom padding raises the scroll content's `SizeChanged`, and `OnScrollContentSizeChanged` then re-pins if the reader was pinned. That is the wanted behaviour when the composer grows (a reply banner opens, or the draft wraps). Do **not** add a second path that scrolls on composer resize.
- [ ] `WireDropTargets()` still registers `MessagesList` and `Composer`. The composer now overlays the list, so dragging over it must still show the overlay and deliver the drop. Test this explicitly.
- [ ] Shadows (optional within this milestone): the header, composer and jump button get `Translation="0,0,16"` and a `ThemeShadow`. In the constructor, `shadow.Receivers.Add(ThreadBackground)`. Without a receiver, a non-popup `ThemeShadow` draws nothing.

**Done when**, verified on the Dell in both themes:
- (a) Opening a conversation lands on the newest message, fully visible above the composer.
- (b) An arriving message while pinned follows. While scrolled up, it does not, and the jump button appears.
- (c) An image bubble that decodes late still ends fully visible.
- (d) Opening the reply banner while pinned keeps the last message visible.
- (e) Scrolling up shows message content passing under the header and composer.
- (f) File drag and drop works over the thread and over the composer.

### W4. Header, transfer banner, jump button, drop overlay
Files: `ChatPage.xaml(.cs)`, `UI/Chat/FileTransferBannerControl.xaml`. Read `ChatHeader`, `FileTransferBanner`, `IconButton` and `DropOverlay` in the design system.
- [ ] Header: a `GlassSurfaceStyle` control with `Background` = `GlassRegularAcrylicBrush` (**acrylic**: content moves under it), `Height="56"`, `CornerRadius="28"`, `Padding="10,0"`.
  - `HeaderAvatar` 36×36.
  - The name at 14/19 semibold, then the 8px presence dot (`Theme.OnlineDotBrush` / `OfflineDotBrush`).
  - `HeaderSubtext` at 11/14 `TokenInkSecondaryBrush`.
  - `RemoteDesktopBtn` uses `GlassIconButtonStyle`, keeps its `x:Name` (UIA automation uses it), and stays disabled with its reason when unavailable. Never hide it.
  - Delete the unused `HeaderOnlineDot` ellipse and the header `BorderThickness`.
- [ ] `FileTransferBannerControl`: a glass capsule (acrylic regular, `CornerRadius="24"`, `Padding="10,8,16,8"`).
  - A 32px `accent-wash` disc with the transfer glyph in accent-ink.
  - The label at 12/16 semibold.
  - The `ProgressBar` re-templated or resourced to a 4px `brand` fill on an `inset-fill` track (`ProgressBarForeground`, `ProgressBarBackground` and `ProgressBarTrackHeight` as per-instance resources).
  - The byte count in Cascadia Mono 11, ink-secondary.
- [ ] `JumpToLatestBtn`: `GlassIconButtonStyle` plus a clear-acrylic background, 32×32, `CornerRadius="16"`, the E70D glyph at 12. Its visibility logic stays as it is.
- [ ] `DropOverlay`: an `accent-wash` fill and a 2px dashed `accent-ink` border (`BorderBrush` plus a `Rectangle` with `StrokeDashArray="4,3"`), `CornerRadius="16"`, `Margin="10"`. The caption sits in a thick-glass capsule with the E723 glyph: "Drop to send to {name}" (unchanged copy).

**Done when** these match `components/ChatHeader`, `FileTransferBanner` and `IconButton` in both themes, and UIA can still invoke `RemoteDesktopBtn`.

### W5. Composer
Files: `UI/Chat/ComposerControl.xaml(.cs)`, `ChatPage.xaml(.cs)`. Read `Composer` in the design system.
- [ ] Root: `Grid` with `ColumnSpacing="8"` and `Background="Transparent"`. Column 0 is the **pill** (`GlassSurfaceStyle`, `GlassRegularAcrylicBrush`, `CornerRadius="22"`, `Padding="4"`). Column 1 is the send orb.
- [ ] Inside the pill, a `StackPanel`:
  1. `BannerHost` (`Border`, collapsed by default, `inset-fill`, `CornerRadius="18"`, `Margin="2,2,2,4"`, `Padding="10,6,8,6"`): a 3px `brand` bar; `BannerTitle` at 11/14 semibold accent-ink; `BannerPreview` at 12/16 ink-secondary, one line with ellipsis; `BannerCancelBtn` (`GlassIconButtonStyle` at 24×24, glyph E711).
  2. A row holding `AttachBtn`, `ScreenshotBtn` (both `GlassIconButtonStyle`, keeping their names, handlers, tooltips and the screenshot `ProgressRing`) and `InputBox`.
- [ ] `InputBox`: remove the old field `Border`. The TextBox sits directly in the pill with `Background="Transparent"` and `BorderThickness="0"`, keeps its existing `TextBox.Resources` overrides and every event handler, `MinHeight="32"`, `MaxHeight="138"`, 14/20, placeholder "Message".
- [ ] `SendBtn`: 36×36, `CornerRadius="18"`, `Style="{StaticResource GlassPrimaryButtonStyle}"`, `Margin="0,0,0,4"`, `VerticalAlignment="Bottom"`. **Set `SendIcon.Foreground` to `TokenOnBrandBrush`.** It is hard-coded `White` today, which is why the `ButtonForeground*` resources never applied. Remove those resources. Disabled: glass-regular fill with an ink-secondary glyph. `IsEditing` keeps swapping E74A and E73E and the tooltip.
- [ ] New API on `ComposerControl`: `public void ShowBanner(string title, string preview)`, `public void HideBanner()`, and `public event Action? BannerCancelRequested`.
- [ ] `ChatPage`: delete the `ReplyBanner` element and its named children (`ReplyBanner`, `ReplyBannerWho`, `ReplyBannerPreview`, `CancelReplyBtn`). `SetReplyTarget` and `SetEditTarget` call `Composer.ShowBanner(...)` and `Composer.HideBanner()` with **exactly the same strings and conditions** as today. `CancelReplyBtn_Click`'s logic moves to a `Composer.BannerCancelRequested` handler.

**Done when:**
- reply, edit, cancel with ✕, cancel with Escape, send, save edit and multi-line growth all behave as before
- the pill grows upward and the thread re-pins (W3's rule)
- attach, screenshot, paste and drop still reach `SendFile`

### W6. Bubbles and thread content
Files: `UI/Chat/MessageBubbleControl.xaml(.cs)`, `UI/TypingIndicatorControl.xaml.cs`. Read `MessageBubble`, `StatusTicks`, `FileBubble`, `MediaBubble` and `ReplyChip`.
- [ ] `Bubble`: `MaxWidth="420"`, `Padding="10,7,10,7"`, `BorderThickness="1"`, `BorderBrush="{ThemeResource GlassRimBrush}"`. The corner radii stay as they are (the tail logic is already right). **No `AcrylicBrush` per bubble.**
- [ ] Keep 60px clear on the far side: `BubbleColumn.Margin` becomes `0,0,60,0` when incoming and `60,0,0,0` when outgoing, set in `Refresh()`.
- [ ] Meta row: replace every `Opacity` dimming (`EditedText` 0.45, `TimestampText` 0.55, `RelayBadge` 0.6) with brushes: `Theme.MetaInBrush` when incoming, `Theme.MetaOutBrush` when outgoing. Opacity on text is what took these below 4.5:1.
- [ ] Status ticks: keep the text glyphs and change only their brushes. Sent and delivered use the meta colour for the bubble; read uses `Theme.CheckBlueBrush` (now `tick-read`); failed uses `Theme.DangerInkBrush`.
- [ ] Reply chip: background `TokenInsetFillBrush`, `CornerRadius="6"`. `ReplySender` uses `AccentInk` or `AccentInkOut` by side. `ReplyPreview` loses `Opacity="0.75"` and takes the meta brush.
- [ ] File actions: `OpenFileBtn` takes `GlassPillButtonStyle` (accent) and `ShowInExplorerBtn` takes `GlassPillButtonStyle`. Labels are unchanged ("Open", "Show in folder").
- [ ] Media: `ImageTile` and `VideoTile` get `CornerRadius="12"`. `VideoTile`'s `#22000000` becomes `TokenInsetFillBrush` and its play square becomes the `scrim` token. `FileMissingText` loses its opacity and takes the meta brush.
- [ ] Deleted: italic 13 in the meta brush. `Theme.MutedTextBrush` becomes ink-secondary.
- [ ] `TypingIndicatorControl.UseBubbleShell()`: incoming bubble brush, `CornerRadius(16,16,16,4)` (the tail: it is always the newest incoming row), and the rim border. The dots use `TokenInkSecondaryBrush`. The row variant (see W7) uses `AccentInk`.

**Done when** `components/MessageBubble`, `StatusTicks`, `FileBubble`, `MediaBubble` and `ReplyChip` match in both themes, and with transparency off the bubbles fall back to opaque WhatsApp colours.

### W7. Conversation rows
Files: `UI/Sidebar/ConversationRowControl.xaml(.cs)`, `UI/Sidebar/SidebarControl.xaml`. Read `ConversationRow`, `Avatar` and `Sidebar`.
- [ ] `ConversationList.Resources` (per instance, **not** a style setter):
  - `ListViewItemSelectionIndicatorVisualEnabled` = False
  - `ListViewItemBackgroundSelected`, `…SelectedPointerOver` and `…SelectedPressed` → `TokenGlassThickBrush`
  - `ListViewItemBackgroundPointerOver` → `TokenGlassHoverBrush`
  - `ListViewItemCornerRadius` = 12
  - Item container `Padding` stays `8,4`.
- [ ] Row: `Height="72"`.
  - `OnlineDot` fill from `Theme.OnlineDotBrush` / `OfflineDotBrush`, stroke `TokenPresenceRingBrush`.
  - `TimestampText` in ink-secondary. When unread, accent-ink and semibold (set in code-behind where `UnreadBadge` is toggled).
  - `PreviewText` at 13/18 ink-secondary, two lines.
  - `UnreadBadge`: `brand`, `CornerRadius="10"`, `MinWidth="20"`, `Padding="6,0"`. `UnreadCount` uses **`Foreground` `TokenOnBrandBrush`** and **Bold**, not white.
- [ ] `TypingDots` in a row: wrap it in an `accent-wash` capsule (`CornerRadius="10"`, `Padding="8,5"`), with 6px dots at 4px spacing in accent-ink.
- [ ] `OptionsBtn`: `GlassIconButtonStyle`. The glyph draws at 14 and the hit area is 32.
- [ ] `ArchivedSection` stays **last**. Restyle it as a 52px row: a 32px `accent-wash` disc holding E7B8 in accent-ink, "Archived" at 14/19 semibold, the subtitle at 11/14 ink-secondary, and the chevron in ink-secondary.

**Done when** `components/ConversationRow` and `Sidebar` match: selected, unread, typing, offline, archived.

### W8. Dialogs and Settings
Files: `MainWindow.xaml.cs`, `UI/Sidebar/ContactsDialog.cs`, `UI/Sidebar/ContactEditorDialog.cs` (holds `NewMessageDialog`), `UI/Chat/ScreenshotDialogs.cs`, `UI/Chat/ChatPage.xaml.cs`, `UI/Chat/MessageBubbleControl.xaml.cs`, `UI/Sidebar/ConversationRowControl.xaml.cs`, `UI/Settings/SettingsPage.xaml`, new `UI/GlassDialog.cs`. Read `SettingsGroup`, `Button`, `Toggle` and `TextField`.
- [ ] Add one helper, `GlassDialog.Apply(ContentDialog d)`, in `UI/GlassDialog.cs`. It sets `d.Background` to the `GlassThickAcrylicBrush` resource and `d.CornerRadius = new CornerRadius(26)`. The template's `BackgroundElement` template-binds both. It also sets `d.Resources["OverlayCornerRadius"] = new CornerRadius(26)`, because the inner `DialogSpace` grid reads that resource. The signature is `Apply(ContentDialog d, bool destructive = false)`: it sets `d.PrimaryButtonStyle` to `GlassPrimaryButtonStyle`, or to `GlassDangerButtonStyle` when `destructive` is true. Pass `destructive: true` for "Delete conversation?" and leave its `DefaultButton = Close` alone.
- [ ] Call it from **every** construction site. There are nine on `main`:

  | Site | Dialog |
  | --- | --- |
  | `MainWindow.xaml.cs` `ShowSettingsPage` | Settings |
  | `MainWindow.xaml.cs` `ShowMigrationDialog` | Import existing account |
  | `ChatPage.xaml.cs` `ShowErrorAsync` | Error |
  | `MessageBubbleControl.xaml.cs` `ShowErrorAsync` | File errors ("File not found", "Could not open file") |
  | `ConversationRowControl.xaml.cs` (~line 96) | Delete conversation |
  | `ContactsDialog` (class) | Contacts |
  | `NewMessageDialog` in `ContactEditorDialog.cs` (class) | New message |
  | `ScreenshotWindowPickerDialog`, `ScreenshotPreviewDialog` in `ScreenshotDialogs.cs` (classes) | Screenshot picker and preview |

  For the subclassed dialogs, call it in the constructor. Re-run `git grep -n "new ContentDialog\|new Microsoft.UI.Xaml.Controls.ContentDialog\|: ContentDialog" -- src/windows-native` and confirm every hit is covered.
- [ ] The template's `CommandSpace` template-binds the same `Background`, so the button strip draws acrylic over acrylic. If it reads denser than the body on the Dell, switch the helper to the opaque `TokenGlassThickFallbackBrush`: a dialog sits over a smoke layer anyway, and re-templating `ContentDialog` is out of scope.
- [ ] `SettingsPage.xaml`: each section (`Display name`, `Received files folder`, `Screenshots folder`, `Background`, `Logging`, `Remote Desktop`, `Cloud Relay`, `Updates`, `About`) becomes:
  - a header `TextBlock` at 12/16 semibold ink-secondary, **outside** the panel
  - a `ContentControl Style="{StaticResource GlassSurfaceStyle}"` with `Background` = `TokenGlassThickBrush` (translucent solid: it sits in a dialog that is already acrylic), `CornerRadius="20"`, `Padding="14,8"`
  - rows at least 44 tall, separated by 1px `TokenDividerBrush` lines
  - caption paragraphs at 12/16 ink-secondary

  Keep every control, name, handler and string. `InstallNowBtn` takes `GlassPrimaryButtonStyle`. `UpdatePanel`'s `CornerRadius="6"` becomes 20. The two `ToggleSwitch.Resources` blocks now reference the brand brushes by key, with the same per-instance technique.

**Done when** all nine dialogs open as thick-glass sheets with 26px corners in both themes, every setting still saves, and the destructive confirmations still default to their safe button.

### W9. Remote-desktop windows
Files: `UI/RemoteDesktop/RemoteConsentWindow.xaml(.cs)`, `UI/RemoteDesktop/RemoteHostIndicatorWindow.xaml(.cs)`. Read `RemoteConsentSheet` and `RemoteHostIndicator`.
- [ ] Consent:
  - Root `Grid` `Background="Transparent"`, and in the constructor `SystemBackdrop = new DesktopAcrylicBackdrop();`.
  - Title at 15/20 semibold, explanation at 12/16 ink-secondary.
  - The address and key rows move into an `inset-fill` card (`CornerRadius="8"`, `Padding="10"`), with the key still in a monospaced font (`Cascadia Mono, Consolas`), selectable and never trimmed.
  - `WarningPanel` becomes `TokenWarningWashBrush` with its icon in `TokenWarningInkBrush`, **still shown only when `WarningFor(trust)` returns text**.
  - **`DeclineButton` keeps the accent (now `GlassPrimaryButtonStyle`) and default behaviour. `AcceptButton` takes `GlassButtonStyle` and gets no keyboard accelerator.** The countdown text is unchanged.
- [ ] Indicator:
  - `Strip` fills the window with `CornerRadius="0"`. After the window is created, and again after every `Reposition()`, call `DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE = 33, ref DWMWCP_ROUND = 2, sizeof(int))`. Declare it as `[DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);`, where `DwmSetWindowAttribute` is the real export name. Wrap the call in try/catch and log it: nothing here may throw.
  - The colours become `GlassTokens.Danger` when controlled and `GlassTokens.Warning` when viewed, replacing `0xE58C1A`. Text, dot and button foregrounds become `on-warning` #1F1300 when viewed and `ink-inverse` when controlled, switched in `ApplyGrant`.
  - Add a 1px top highlight: a 1px `Border` along the top edge in #80FFFFFF, `IsHitTestVisible="False"`.
  - The buttons keep their `#38FFFFFF` capsule style. **No acrylic here.**

**Done when:**
- a pinned-key invite shows no warning and an unknown device shows it
- Enter declines, and Escape and closing decline
- the indicator reads amber with dark text when viewing and red with white text when controlled, with rounded corners, top-centre, on every monitor layout

### W10. Windows verification pass
- [ ] Full Dell build (separate restore and build) and MSTest, with the DLL timestamp printed and the test totals up by the G2 tests.
- [ ] Screenshots on the Dell for review. Run a capture script **in the user's session** through a `/IT` scheduled task (see `driving-the-dell-headlessly`): `System.Drawing` `CopyFromScreen` on the app window's rectangle, saved to a file, then `scp` it back. Capture the main window in light, dark, transparency off, and High Contrast (Aquatic), plus the consent window and the indicator in both states.
- [ ] Put the screenshots next to the matching design-system previews and fix any mismatch before ticking the milestone.
- [ ] Performance check: scroll a 200-message thread with images. `viewer_stats`-style numbers are not available here, so use Task Manager's GPU and CPU while scrolling, before and after. Acrylic on three surfaces should cost nothing noticeable. If it does, the first thing to drop is `ThemeShadow`.

---

## M: macOS (PR 3)

macOS 26 already renders the sidebar, toolbar, menus, sheets and popovers as Liquid Glass. This PR adjusts the app's own painted surfaces. Everything that calls a macOS 26 API is gated with `if #available(macOS 26, *)` and keeps today's look as the fallback (the deployment target is 13).

Inner loop: `cd src/macos && swift build && swift test`. Visual proof: the ImageRenderer harness from `macos-ui-visual-verification`, rendered once per `colorScheme`. It renders pre-26 materials faithfully; glass effects must be checked in the running app on macOS 26.

### M1. Tokens into Theme.swift, plus a glass helper
Files: `UI/Theme.swift`, new `UI/Glass.swift`, `UI/GlassTokens.swift` (generated in G1).
- [ ] `Theme` keeps its API and re-sources values from `GlassTokens`:
  - `accent` becomes `GlassTokens.brand`.
  - `incomingBubble(_:)` and `outgoingBubble(_:)` return the translucent tokens. Add a `reduceTransparency: Bool` parameter with a default so call sites compile, and pass it from `@Environment(\.accessibilityReduceTransparency)` in the bubble views, where true selects the `-fallback` token.
  - `chatBackground` becomes wallpaper and `sidebarBackground` becomes sidebar-ground.
  - Add `accentInk`, `accentInkOut`, `metaOut`, `inkSecondary`, `tickRead`, `onBrand`, `insetFill`, `accentWash`, `presenceOnline` and `presenceOffline`.
- [ ] `UI/Glass.swift`:
  ```swift
  enum GlassThickness { case clear, regular, thick }

  extension View {
      /// One call site for every glass surface. macOS 26 gets real Liquid Glass;
      /// earlier systems get the matching material; Reduce Transparency gets the
      /// opaque fallback token.
      @ViewBuilder
      func glassSurface<S: Shape>(_ thickness: GlassThickness = .regular,
                                  in shape: S,
                                  tint: Color? = nil) -> some View {
          modifier(GlassSurfaceModifier(thickness: thickness, shape: shape, tint: tint))
      }
  }
  // GlassSurfaceModifier reads accessibilityReduceTransparency.
  // Reduce transparency: .background(fallback, in: shape).
  // macOS 26: .glassEffect(thickness == .clear ? .clear : .regular.tint(tint), in: shape).
  // Otherwise: .background(.ultraThinMaterial / .regularMaterial / .thickMaterial, in: shape)
  //   plus .overlay(shape.strokeBorder(GlassTokens.glassEdge)).
  ```

**Done when** it builds and tests pass, and the ImageRenderer output of `MessageBubbleView` and `ConversationRowView` shows only colour changes.

### M2. Bubbles and thread content
Files: `Chat/MessageBubbleView.swift`, `Chat/MediaBubbleView.swift`, `Chat/ReplyChipView.swift`, `Chat/BubbleStatusView.swift`, `TypingIndicatorView.swift`, `RemoteDesktop/RemoteAuditRowView.swift`.
- [ ] Bubble fills use the translucent tokens, honouring Reduce Transparency. Add the rim: `.overlay(UnevenRoundedRectangle(…same radii…).strokeBorder(LinearGradient(colors: [GlassTokens.glassRim, GlassTokens.glassEdge], startPoint: .top, endPoint: .center), lineWidth: 1))`. Keep `shadow-bubble` as `.shadow(color: .black.opacity(0.13), radius: 0.75, y: 1)` from the tokens.
- [ ] Meta text: `.foregroundStyle(.secondary)` inside bubbles becomes `Theme.inkSecondary` when incoming and `Theme.metaOut` when outgoing. `editedMarker` uses the same, not `.tertiary`. `relayBadge` drops `.opacity(0.75)`.
- [ ] `BubbleStatusView`: read uses `Theme.tickRead`, sent and delivered use the meta colour of the bubble it sits in (pass `incoming: Bool` or an explicit colour), and failed uses `danger-ink`.
- [ ] Reply chip: `insetFill`, radius 6; the sender in `accentInk` or `accentInkOut` by side; the preview in the meta colour.
- [ ] File bubble: the doc icon in `accentInk` or `accentInkOut`. The Open pill is `accentWash` with `accentInk` text; the Show pill is `insetFill` with the meta colour.
- [ ] `MediaBubbleView`: `bubbleBackground` radius 14 becomes 16 (`radius-bubble`), and the media clip stays 12.
- [ ] `TypingBubbleView`: the translucent bubble plus the rim, with dots in `inkSecondary`.
- [ ] `RemoteAuditRowView`: wrap the existing content in a centred capsule, `.glassSurface(.regular, in: Capsule())` without a float shadow. Text in `inkSecondary`, the summary's actor in `ink` semibold. Remove the `.opacity(0.6)` / `.opacity(0.75)` dimming. The wording logic (`viewing`) is untouched.

**Done when** the ImageRenderer renders of each view, in light and dark, match `components/MessageBubble`, `StatusTicks`, `FileBubble`, `MediaBubble`, `ReplyChip`, `TypingIndicator` and `RemoteAuditRow`.

### M3. Chat chrome touch-ups
Files: `Chat/ChatView.swift`, `Chat/ComposerView.swift`, `Chat/FileTransferBannerView.swift`, `Sidebar/ConversationRowView.swift`.
- [ ] Header: on macOS 26, replace `.background(.bar)` with `.glassSurface(.regular, in: Capsule())`, add `.padding(8)` around it, and remove the `Divider()` below it. Before 26, keep `.bar` and the divider exactly as they are. Avatar 36, online dot 8, status text `inkSecondary`. The header stays a row above the thread in this milestone (the thread does not scroll under it yet; see M5).
- [ ] Composer on macOS 26: `GlassEffectContainer(spacing: 8) { HStack(alignment: .bottom, spacing: 8) { pill; sendOrb } }`.
  - The **pill** is a `VStack` of the reply/edit banner plus the attach, screenshot and text row, with `.glassEffect(.regular, in: .rect(cornerRadius: 22))`.
  - The banner moves out of `ChatView` (`replyBanner(for:)` / `editBanner(for:)`) into the pill, **with the same strings and cancel actions**. Pass `replyTarget`/`editTarget` (already bindings on `ComposerView`) and a cancel closure. Delete the two banner rows from `ChatView`.
  - The text field loses `.background(.quaternary, …)`.
  - The **send orb**: a 36pt `Circle` in `Theme.accent` with `arrow.up` or `checkmark` at 15 semibold in `Theme.onBrand`, `.buttonStyle(.plain)`. Disabled uses `.glassEffect(.regular, in: .circle)` with an `inkSecondary` glyph.
  - Before 26, keep today's composer but still take the orb and the banner move, so the layout matches.
- [ ] Jump to latest: on 26, `.glassEffect(.clear, in: .circle)`; before 26, keep the `.regularMaterial` circle. Size 32, glyph in `accentInk`.
- [ ] `FileTransferBannerView`: a capsule `.glassSurface(.regular, …)` with `.padding(.horizontal, 8)`; the icon in an `accentWash` disc; a `brand` progress tint on `insetFill`; bytes monospaced in `inkSecondary`.
- [ ] Drop overlay: `accentWash` fill, a 2px dashed `accentInk` border, and a `.glassSurface(.thick, in: Capsule())` label.
- [ ] `ConversationRowView`:
  - online dot `presenceOnline` / `presenceOffline` with a 2pt `presence-ring` stroke
  - time in `inkSecondary`, becoming `accentInk` semibold when `unreadCount > 0`
  - the unread badge text in `Theme.onBrand` and bold
  - typing capsule dots in `accentInk` on `accentWash`

**Done when**, on a macOS 26 machine running the app: the header is a floating capsule, the composer and orb merge as glass, and reply, edit and cancel work as before. On a pre-26 renderer the layout matches with materials. The scroll pin checks from W3 (a), (b), (c) and (d) pass on macOS.

### M4. The windows that need touch-ups
Files: `RemoteDesktop/RemoteConsentView.swift`, `RemoteDesktop/RemoteConsentPresenter.swift`, `RemoteDesktop/RemoteHostIndicatorView.swift`, `App/LanMessengerApp.swift` (`ContentView.emptyState`).
- [ ] Consent:
  - Delete `.background(scheme == .dark ? Color(white: 0.13) : Color(white: 0.98))`. That opaque fill is what blocks the system glass.
  - On macOS 26 the view takes `.glassSurface(.regular, in: .rect(cornerRadius: 26))`.
  - In `ConsentPanel`, set `isOpaque = false`, `backgroundColor = .clear`, `titlebarAppearsTransparent = true`, and add `.fullSizeContentView` to the style mask, so the glass reaches the panel's edges.
  - Before 26, `.thickMaterial`.
  - Key card: `insetFill`, radius 8. Warning row: `warningWash` with a `warningInk` icon, shown only when `request.warning != nil` (unchanged). The permissions `systemNotice` stays plain `insetFill`.
  - **Decline keeps `.keyboardShortcut(.defaultAction)` and the allow button gets none.**
- [ ] Indicator: stays solid (signal glass). When viewing, `.foregroundStyle` becomes `GlassTokens.onWarning` instead of `.white`; controlled keeps white. Tint colours become `GlassTokens.danger` / `GlassTokens.warning`. Replace the `strokeBorder(.white.opacity(0.22))` overlay with a top-weighted rim gradient (`#FFFFFF` at 50% at the top to transparent at mid-height).
- [ ] `ContentView.emptyState`: the EmptyState recipe, with a 64pt `.glassSurface(.clear, in: Circle())` disc. **Keep its copy.**
- [ ] Settings, Contacts, New Message and Archived are system sheets and forms, already glass on 26. No change.

**Done when** the consent panel shows glass edge to edge on macOS 26 and a thick material on 13–15 with no opaque slab, and the indicator's viewing text is dark and legible.

### M5. Optional: the thread runs under the chrome on macOS
Do this last, and only if M3 shipped cleanly. It is the riskiest change in the plan, because `ChatView`'s scroll geometry assumes the viewport is the visible area.
- [ ] Move the header into `.safeAreaInset(edge: .top)` and the composer, banner and transfer banner into `.safeAreaInset(edge: .bottom)` on the `ScrollView`. Add `.scrollEdgeEffectStyle(.soft, for: [.top, .bottom])` on macOS 26.
- [ ] **Correct the geometry.** Today the code computes the distance still to scroll as `max(0, contentBottom − viewportHeight)`. With a bottom inset, the last pixel of content rests at `viewportHeight − bottomInset`, so that distance under-reads by exactly the inset's height. The jump button appears late, and the at-bottom slack effectively grows by the inset. Read the bottom inset in the same background `GeometryReader` that writes `viewportHeight` (`geo.safeAreaInsets.bottom`, via `onAppear`/`onChange`, **not** through a preference). Replace every `viewportHeight` in distance maths with `viewportHeight − bottomInset`. Both content edges still travel in the one `ScrollGeometryKey` preference. Do not split them.
- [ ] Confirm `proxy.scrollTo(threadEndID, anchor: .bottom)` lands above the inset. If it lands under the composer, add a zero-height spacer of the inset's height after the thread-end view and scroll to that instead.
- [ ] **Verify with the scroll-position harness** (real `NSWindow` + `NSHostingView`, reading the `NSScrollView` clip bounds on `asyncAfter` ticks), run on both the old and new code: (a) opening lands at distance 0; (b) appending while pinned follows; (c) growing a row by 200pt after 300ms while pinned ends at distance 0; (d) scrolling up 150pt then appending does not move the view, and the jump button shows; (e) opening a reply banner while pinned ends at distance 0.

**Done when** all five harness cases give the same results as before the change.

### M6. macOS verification pass
- [ ] `swift build && swift test` green, including G2.
- [ ] ImageRenderer renders of every touched view in both schemes, reviewed against the design-system previews.
- [ ] The running app on macOS 26 (packaged with `scripts/macos/package.sh` so notifications and TCC behave): open a conversation, reply, edit, send a file, receive a message while scrolled up, toggle Reduce Transparency, and toggle dark mode. The shell cannot take screenshots here, so ask the user for them.

---

## X: Wrap-up (last commit of each PR)

- [ ] `CLAUDE.md` → *Do not accidentally regress*, add:
  - "Do not put an `AcrylicBrush` on message bubbles. Acrylic is only for chrome that has app content moving under it (header, composer, transfer banner, jump button). Bubbles and the sidebar panel use translucent solid brushes."
  - "Do not make the remote-desktop Allow button primary or give it a shortcut. Decline stays the default on both platforms, and the warning colour appears only for an unexpected key."
  - "Do not add a colour, radius or size literal to UI code. Add a token to `design/liquid-glass/tokens.json` and run `scripts/design/gen_tokens.py`. CI's `--check` fails on a stale generated file."
  - (After W3) "Do not give `MessagesList` a background brush. The wallpaper and its glows are painted by `ThreadBackground` behind it, and the list padding, not a margin, keeps the first and last messages clear of the floating chrome."
- [ ] `docs/ARCHITECTURE.md` UI section: the token pipeline, and the chrome-over-thread layout.
- [ ] `docs/FILE_MAP.md`: `scripts/design/gen_tokens.py`, `GlassTokens.*`, `Styles/Glass.xaml`, `UI/Glass.swift`, `UI/GlassDialog.cs`.
- [ ] `design/README.md`: how a token change flows from `tokens.json` to the generator, the apps and the artifact.
- [ ] Tick every box above. Set this file's status table to done.

## Commit map

| PR | Commits (one per milestone) |
| --- | --- |
| 1: groundwork | G1 generator and generated files · G2 token tests · X docs for G |
| 2: Windows | W1 · W2 · W3 · W4 · W5 · W6 · W7 · W8 · W9 · W10 fixes · X docs |
| 3: macOS | M1 · M2 · M3 · M4 · (M5) · M6 fixes · X docs |
