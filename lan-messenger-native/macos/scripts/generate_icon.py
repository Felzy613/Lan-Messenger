#!/usr/bin/env python3
"""
Generate LAN Messenger app icon in all required sizes.
Requires: pip install Pillow

Output: lan-messenger-native/macos/LanMessenger/Assets.xcassets/AppIcon.appiconset/
Run from repo root or from lan-messenger-native/macos/:
    python3 scripts/generate_icon.py
"""
import math
import os
import json
from pathlib import Path
from PIL import Image, ImageDraw

# ── Sizes required for a macOS app ──────────────────────────────────────────
SIZES = [16, 32, 64, 128, 256, 512, 1024]

# ── Colours matching Theme.swift ────────────────────────────────────────────
BG      = (37, 211, 102)        # Theme.accent  (#25D366)
BUBBLE  = (255, 255, 255, 255)  # white

def draw_icon(size: int) -> Image.Image:
    """Render a rounded-rect background with a speech-bubble glyph."""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    # Rounded-rect background
    r = size // 6
    draw.rounded_rectangle([0, 0, size - 1, size - 1], radius=r, fill=BG)

    # Speech bubble body — centred, ~55 % of icon width
    m   = size * 0.18          # margin
    bx0 = m
    by0 = m
    bx1 = size - m
    by1 = size * 0.68
    br  = (bx1 - bx0) * 0.18
    draw.rounded_rectangle([bx0, by0, bx1, by1], radius=br, fill=BUBBLE)

    # Tail — small triangle bottom-left of bubble
    cx = bx0 + (bx1 - bx0) * 0.22
    tail = [
        (bx0 + 2,   by1 - 1),
        (cx,         by1 - 1),
        (bx0 + 2,   by1 + size * 0.15),
    ]
    draw.polygon(tail, fill=BUBBLE)

    # Three text lines inside bubble
    line_w = (bx1 - bx0) * 0.55
    line_h = max(2, size * 0.045)
    line_x = bx0 + (bx1 - bx0) * 0.15
    gap    = (by1 - by0 - 3 * line_h) / 4
    for i in range(3):
        ly = by0 + gap + i * (line_h + gap)
        # Last line is shorter
        w = line_w if i < 2 else line_w * 0.6
        draw.rounded_rectangle(
            [line_x, ly, line_x + w, ly + line_h],
            radius=line_h // 2,
            fill=(*BG, 160),
        )

    return img


def main():
    script_dir = Path(__file__).parent
    # Works whether run from repo root or from lan-messenger-native/macos/
    candidates = [
        script_dir.parent / "LanMessenger" / "Assets.xcassets" / "AppIcon.appiconset",
        script_dir / ".." / "LanMessenger" / "Assets.xcassets" / "AppIcon.appiconset",
    ]
    out_dir = next((p for p in candidates if p.parent.parent.exists()), candidates[0])
    out_dir = out_dir.resolve()
    out_dir.mkdir(parents=True, exist_ok=True)

    files = []
    for size in SIZES:
        for scale in ([1, 2] if size < 512 else [1]):
            px = size * scale
            filename = f"icon_{size}x{size}@{scale}x.png" if scale > 1 else f"icon_{size}x{size}.png"
            img = draw_icon(px)
            img.save(out_dir / filename, "PNG")
            print(f"  wrote {filename} ({px}×{px})")
            files.append({
                "filename": filename,
                "idiom":    "mac",
                "scale":    f"{scale}x",
                "size":     f"{size}x{size}",
            })

    # Write Contents.json
    contents = {"images": files, "info": {"author": "generate_icon.py", "version": 1}}
    (out_dir / "Contents.json").write_text(json.dumps(contents, indent=2) + "\n")
    print(f"\nAppIcon written to:\n  {out_dir}")


if __name__ == "__main__":
    main()
