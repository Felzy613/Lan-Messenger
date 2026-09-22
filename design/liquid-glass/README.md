LAN Messenger Glass keeps WhatsApp's conversation grammar: the green, the beige and night wallpaper, tailed bubbles, double ticks, avatars with a presence dot. It builds all of it from one material, liquid glass. Chrome floats over the thread as clear or frosted glass, messages sit in tinted-glass bubbles, and security signals stay solid.

macOS 26 draws system materials as Liquid Glass already, so the Mac app is the reference and needs only the touch-ups listed under *Implementation*. The Windows app gets the same result from Mica, in-app `AcrylicBrush`, a rim border and `ThemeShadow`, and most of the work is there.

## Principles

1. **The thread is the ground.** `wallpaper` runs edge to edge under the whole chat pane. The header, composer, jump button and transfer banner float over it, and the thread scrolls underneath them. Never stack chrome above and below the thread as opaque rows. Pad the list by the height of the chrome instead.
2. **One material, three thicknesses.** Pick by what sits on it. Use `glass-clear` for icons only, `glass-regular` for chrome that carries a line of text, and `glass-thick` for anything read at length (sheets, menus, settings).
3. **Glass is four layers, always all four:** a tint fill (`glass-*`), a backdrop blur plus `saturate-glass`, a sheen (`glass-sheen` fading to transparent by 55% of the height), and a rim (`shadow-rim` plus a 1px `glass-edge`). Without the rim it reads as fog. Without the blur it reads as tinted plastic.
4. **Green is a fill, not a font.** `brand` (#25d366) fills the send orb, the unread badge, the progress bar and switched-on toggles, with `on-brand` on top. Green text and icons use `accent-ink` (`accent-ink-out` inside an outgoing bubble). White on `brand` is 2:1, so never use it.
5. **Signals don't go translucent.** The screen-sharing indicator and destructive fills are solid tint with a glass rim. Something that says "your screen is being watched" must never let the desktop show through and wash it out.
6. **Transparency is optional, legibility isn't.** Every glass and bubble token has a `-fallback` sibling for Reduce Transparency, battery saver and remote sessions. Text contrast is measured against the worst case: glass composited over `wallpaper-glow`.

## Content

Write short, literal sentences, second person, naming the peer by their display name. Use sentence case for text and Title Case for buttons and menu items, as both platforms do. No emoji or exclamation marks in the chrome. Copy the app already uses:

- "This message was deleted"
- "Drop to send to Priya Raman"
- "Priya is viewing your screen" / "Priya is controlling your Mac" with **Stop Control** and **Stop Sharing**
- "Declines automatically in 24s"
- "Tap “New message” to chat with one of your saved contacts, or add a new contact."
- Context menus: **Reply**, **Copy**, **Edit**, **Delete for Me**, **Delete for Everyone**, **Show in Finder**

Timestamps come from the system formatter: the time for today, then "Yesterday", then the abbreviated weekday within a week, then a short date. Inside bubbles show only the time. Byte counts, key fingerprints and IP addresses are set in `code` (monospaced).

## Colour

**Grounds.** The thread sits on `wallpaper`, with `wallpaper-glow` (top-left) and `wallpaper-glow-alt` (bottom-right) painted as two soft radial lights. The glows are what give the glass something to refract. `sidebar-ground` is the sidebar's resting colour and its Mica tint.

**Glass.** Use `glass-clear`, `glass-regular` and `glass-thick` with `blur-clear`, `blur-regular` and `blur-thick` respectively. `glass-rim` and `glass-sheen` make the specular edge. `glass-edge` keeps two panes of glass apart. `glass-hover` and `glass-pressed` are the interaction layers laid over any glass control. Use `divider` only between rows inside one pane of glass, never between two panes.

**Bubbles.** `bubble-in` is WhatsApp white or #202c33 and `bubble-out` is #dcf8c6 or #005c4b, both as 86–90% glass. Bubbles do not need a backdrop blur on Windows, because only the wallpaper is behind them (see *Implementation*).

**Ink.** `ink` for names and bodies. `ink-secondary` for previews, statuses, captions and placeholders. Inside an outgoing bubble, time, ticks and "edited" use `meta-out`, because `ink-secondary` fails on the green. `ink-inverse` is for avatar initials and `danger` fills.

**Signals.** `tick-read` is the blue double tick. `presence-online` and `presence-offline` are the dots, ringed in `presence-ring` wherever they overlap an avatar. `danger` means being controlled, destructive or failed (`danger-ink` for its text and icons). `warning` means being viewed or needing caution, with `on-warning` text (dark, not white) and `warning-ink` or `warning-wash` for notes.

**Avatars.** There are eight fills, from `avatar-blue` to `avatar-slate`. Pick one by FNV-1a of the display name modulo 8, identically on both platforms:

```
hash = 2166136261
for each UTF-16 code unit c in name: hash = (hash XOR c) * 16777619   (uint32, wrapping)
index = (hash mod 2147483647) mod 8
```

Initials: the first letter of the first two words, or the first two letters of a single word, uppercased.

## Type

Use the platform's own face: SF Pro on macOS and Segoe UI Variable on Windows (`sans` and `display`). Mono is SF Mono or Cascadia Mono. No font files ship; both faces come with the operating system.

- `message` 14/20 for message bodies and the composer, and `name` 14/19 semibold for contact names.
- `preview` 13/18 for row previews, `file-name` 13/17 medium for documents.
- `label` 12/16 for banner previews and explanations, and `label-strong` 12/16 semibold for headlines inside chrome.
- `caption` 11/14 for row times and header status. `caption-strong` 11/14 semibold for pill buttons and reply senders. `badge` 11/14 bold for tabular counts.
- `meta` 10/13 for time and "edited" in bubbles. `micro` 9/11 is only for the "via relay" badge.
- `title-sheet` 15/20 semibold for sheet titles, `title-pane` 20/26 semibold for pane and empty-state titles.

Use tabular figures wherever digits update in place: times, counts, bytes, elapsed time.

## Shape and space

- Every bubble is `radius-bubble` (16). The first bubble of a run gets one `radius-tail` (4) corner: bottom-leading when incoming, bottom-trailing when outgoing. That is the WhatsApp tail, drawn as a corner rather than a triangle.
- Radii are concentric: an inner shape takes the outer radius minus the padding between them. The reply chip is `radius-chip` (16 − 10), media is `radius-media` (16 − 4), and a sheet's inner cards follow `radius-sheet`.
- Floating chrome is a capsule (`radius-pill`): header, composer, host indicator, badges, pill buttons, icon buttons. Panels that hold lists are `radius-panel` (20). The selected row is a `radius-row` (12) lozenge of `glass-thick`.
- The thread gutter is `space-12`. Bubble padding is 7/10 (`space-10` horizontal). Bubbles keep at least 60px from the far side so a run always reads as one speaker. Floating chrome sits `space-8` from the pane edges, and the list is padded by the chrome's height plus `space-16` so the first and last messages clear it at rest.
- Sizes: `size-avatar-row` 44, `size-avatar-header` 36, `size-row` 72, `size-header` 56, `size-icon-button` 32, `size-send` 36, `size-bubble-max` 420.

## Depth and motion

Glass lifts through its rim and blur, not through darkness. `shadow-bubble` is WhatsApp's own hairline drop. `shadow-float` is for floating chrome and `shadow-sheet` for sheets and menus. Each is combined with `shadow-rim`.

Motion is small and purposeful:

- Typing dots swell from 0.82 to 1.18 and brighten from 0.38 to 1 over 0.6s ease-in-out, each dot 0.2s behind the one before it.
- The header crossfades between the status line and the typing dots in 0.2s. The typing bubble fades in over 0.18s.
- The jump button fades in 0.15s.
- The host indicator's dot breathes on a 1.6s cycle. Slow is deliberate: a fast blink reads as an error.
- Reply and edit banners grow out of the composer glass. On macOS 26, put them in the same `GlassEffectContainer` so the two panes merge.

With Reduce Motion on, the dots hold still at 0.8 opacity and nothing morphs.

## States

- **Hover:** a `glass-hover` layer over the control.
- **Pressed:** a `glass-pressed` layer.
- **Focus:** a 2px `focus-ring` offset 2px. It measures 3.6:1 on `wallpaper` and 4.4:1 on `sidebar-ground` in light, 8.8:1 in dark.
- **Disabled:** glyphs in `ink-secondary` with no fill. The remote-desktop button stays visible when disabled, with the reason in its tooltip.
- **Selected row:** a `glass-thick` lozenge with the rim. It replaces the platform's accent bar.

## Logo

The app's mark lives under Logos: two blue-to-cyan speech bubbles over the LAN MESSENGER wordmark on near-black. It keeps its own palette. Place it on `ink` or its own dark square, never on glass, and never recoloured green.

## Iconography

Use each platform's own symbol set: SF Symbols on macOS and Segoe Fluent Icons on Windows. They are line icons at a 16px body size, with 11px inside bubbles. The icons in these previews are stand-ins drawn to the same weight. Do not ship them.

| Action | SF Symbol | Segoe Fluent |
| --- | --- | --- |
| Attach | `paperclip` | `E723` |
| Screenshot | `camera.viewfinder` | `E722` |
| Send | `arrow.up` in the send orb | `E74A` |
| Save edit | `checkmark` in the send orb | `E73E` |
| Remote desktop | `macwindow.on.rectangle` | `E7F4` |
| Jump to latest | `chevron.down` | `E70D` |
| New message | `square.and.pencil` | `E932` |
| Contacts | `person.2` | `E77B` |
| Settings | `gear` | `E713` |
| Security note | `exclamationmark.triangle` | `E7BA` |

## Accessibility

- Every text pair clears 4.5:1 on its ground in both themes, and every meaningful mark (ticks, presence dots, focus ring) clears 3:1. Grounds are measured as glass composited over `wallpaper-glow`.
- Presence and typing never rely on colour alone. The header says "Online" or "Offline", typing indicators carry "Priya is typing" as their accessible label, and the read state is shown by the double tick's shape as well as its blue.
- Icon buttons are 32px. The row's options button draws at 22px but keeps a 32px hit area.

## Intentional additions

Things this system adds to what the code had, and why:

- **The glass material** (`glass-*`, `blur-*`, `acrylic-*`, rim and sheen) and the wallpaper glows. This is the requested direction.
- **The send orb.** One 36px `brand` disc with an `on-brand` arrow replaces a 28pt symbol on macOS and a 38px disc on Windows.
- **Floating header and composer capsules,** so the thread scrolls under glass.
- **The selected-row lozenge.**
- **`radius-panel`, `radius-row`, `radius-sheet`,** for the larger, concentric corners glass needs.
- **ChatScreen,** a composed reference screen and the Windows target.

## Drift this system resolves

The two apps had drifted apart. These values are now one:

| | macOS | Windows | System |
| --- | --- | --- | --- |
| Bubble max width | 420 | 520 | `size-bubble-max` 420 |
| Header avatar | 36 | 40 | `size-avatar-header` 36 |
| Jump button | 30 | 34 | `size-icon-button` 32 |
| Send | 28pt symbol | 38 disc | `size-send` 36 orb |
| Reply chip radius | 6 | 4 | `radius-chip` 6 |
| Controlled indicator | #d93030 | #d93025 | `danger` #d93030 |
| Viewed-indicator text | white on orange (2.4:1) | n/a | `on-warning` (7:1) |
| Read tick | #4f9ef7 (2.6:1 on light out) | #4f9ef7 | `tick-read` per theme |
| Avatar palette | 7 colours, `String.hashValue` (reseeded every launch) | 8 colours, FNV-1a | 8 `avatar-*`, FNV-1a on both |
| Unread badge text | white on green (2:1) | white on green | `on-brand` (9.4:1) |
