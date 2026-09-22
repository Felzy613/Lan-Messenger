# Design

`liquid-glass/` is the source of the **LAN Messenger Glass** design system, which is
published as a Design System artifact at
https://claude.ai/artifact/6zeqPGoUvV8Q6PjNDNgpBB (private to its owner until shared).

| File | What it is |
| --- | --- |
| `liquid-glass/README.md` | The brand book: principles, colour, type, shape, motion, states, iconography, accessibility, and the platform drift it resolves. |
| `liquid-glass/implementation.md` | How to build it: WinUI 3 first (acrylic recipe, ChatPage restructure, per-component table), then the macOS 26 touch-ups. |
| `liquid-glass/tokens.json` | Every token, with light and dark values and a usage note. The numbers in `Theme.swift`, `Theme.cs` and `App.xaml` should match these. |
| `liquid-glass/components/<Name>/README.md` | Guidelines per component. |
| `liquid-glass/components/<Name>/preview.html` | The reference rendering. It needs the artifact's generated `tokens.css`, so view it on the artifact page. |
| `liquid-glass/components/bundle.css` | The reference CSS the previews use. The native apps do not load it. |

The logo files named in `liquid-glass/assets/Logos/README.md` are the repository's own
`Images/Logo.png` and the macOS `AppIcon.appiconset/icon_512x512.png`.

When a token changes, update `tokens.json` here, then the artifact, then both
platforms' theme code.
