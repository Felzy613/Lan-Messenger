# Design

`liquid-glass/` is the source of the **LAN Messenger Glass** design system, which is
published as a Design System artifact at
https://claude.ai/artifact/6zeqPGoUvV8Q6PjNDNgpBB (private to its owner until shared).

| File | What it is |
| --- | --- |
| `liquid-glass/README.md` | The brand book: principles, colour, type, shape, motion, states, iconography, accessibility, and the platform drift it resolves. |
| `liquid-glass/implementation.md` | How to build it: WinUI 3 first (acrylic recipe, ChatPage restructure, per-component table), then the macOS 26 touch-ups. |
| `liquid-glass/tokens.json` | Every token, with light and dark values and a usage note. The apps read it through the files `scripts/design/gen_tokens.py` generates from it. |
| `liquid-glass/components/<Name>/README.md` | Guidelines per component. |
| `liquid-glass/components/<Name>/preview.html` | The reference rendering. It needs the artifact's generated `tokens.css`, so view it on the artifact page. |
| `liquid-glass/components/bundle.css` | The reference CSS the previews use. The native apps do not load it. |

The logo files named in `liquid-glass/assets/Logos/README.md` are the repository's own
`Images/Logo.png` and the macOS `AppIcon.appiconset/icon_512x512.png`.

When a token changes:

1. Edit `liquid-glass/tokens.json`. It is the only place a colour, radius or size
   is written by hand.
2. Run `python3 scripts/design/gen_tokens.py`. It rewrites
   `src/macos/LanMessenger/UI/GlassTokens.swift`,
   `src/windows-native/LanMessenger/UI/GlassTokens.cs` and
   `src/windows-native/LanMessenger/Styles/GlassTokens.xaml`. A new colour token
   also needs a High Contrast role in the generator's `HC_RULES`; it refuses to
   run without one.
3. Run both test suites. `GlassTokensTests` fails if the change takes any text
   below its contrast floor.
4. Commit `tokens.json` and the three generated files together. PR checks run
   `gen_tokens.py --check` and fail on a generated file that does not match.
5. Republish the design-system artifact so its `tokens.css` and previews match.

The plan for implementing the system in both apps, milestone by milestone, is
[`docs/LIQUID_GLASS_PLAN.md`](../docs/LIQUID_GLASS_PLAN.md).
