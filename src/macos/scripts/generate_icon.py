#!/usr/bin/env python3
"""
Generate LAN Messenger app icon in all required sizes from the project logo.
Requires: pip install Pillow

Source logo: <repo-root>/Images/Logo.png (1254×1254 RGB)
Output:      src/macos/LanMessenger/Assets.xcassets/AppIcon.appiconset/

Run from anywhere inside the repo:
    python3 src/macos/scripts/generate_icon.py
"""
import json
from pathlib import Path
from PIL import Image

SIZES = [16, 32, 64, 128, 256, 512, 1024]


def find_repo_root(start: Path) -> Path:
    for p in [start, *start.parents]:
        if (p / ".git").exists():
            return p
    raise FileNotFoundError("Could not locate repo root (no .git directory found)")


def main():
    script_dir = Path(__file__).resolve().parent
    repo_root  = find_repo_root(script_dir)
    logo_path  = repo_root / "Images" / "Logo.png"

    if not logo_path.exists():
        raise FileNotFoundError(f"Logo not found at {logo_path}")

    src = Image.open(logo_path).convert("RGBA")

    out_dir = (
        script_dir.parent
        / "LanMessenger"
        / "Assets.xcassets"
        / "AppIcon.appiconset"
    )
    out_dir.mkdir(parents=True, exist_ok=True)

    files = []
    for size in SIZES:
        for scale in ([1, 2] if size < 512 else [1]):
            px       = size * scale
            filename = f"icon_{size}x{size}@{scale}x.png" if scale > 1 else f"icon_{size}x{size}.png"
            img      = src.resize((px, px), Image.LANCZOS)
            img.save(out_dir / filename, "PNG")
            print(f"  wrote {filename} ({px}×{px})")
            files.append({
                "filename": filename,
                "idiom":    "mac",
                "scale":    f"{scale}x",
                "size":     f"{size}x{size}",
            })

    contents = {"images": files, "info": {"author": "generate_icon.py", "version": 1}}
    (out_dir / "Contents.json").write_text(json.dumps(contents, indent=2) + "\n")
    print(f"\nAppIcon written to:\n  {out_dir}")


if __name__ == "__main__":
    main()
