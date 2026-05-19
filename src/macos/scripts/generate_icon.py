#!/usr/bin/env python3
"""
Generate the LAN Messenger macOS app icon in every slot Apple's AppIcon catalog
expects, plus a standalone AppIcon.icns suitable for the DMG volume icon and
any other place macOS asks for a raw .icns file.

Source logo : <repo-root>/Images/Logo.png
AppIcon out : src/macos/LanMessenger/Assets.xcassets/AppIcon.appiconset/
.icns out   : src/macos/LanMessenger/Assets.xcassets/AppIcon.appiconset/AppIcon.icns
              (best-effort — only emitted when iconutil is available, i.e. on macOS)

Apple's macOS AppIcon catalog has exactly five base sizes (16, 32, 128, 256, 512)
and each one has a 1x and 2x scale. There is NO 64x64 slot — files claiming that
slot are silently dropped by Asset Catalog, which is why broken builds end up
with a generic icon at certain Finder sizes.

Run from anywhere inside the repo:
    python3 src/macos/scripts/generate_icon.py
"""
from __future__ import annotations

import json
import shutil
import subprocess
import sys
from pathlib import Path

try:
    from PIL import Image
except ImportError:
    sys.stderr.write("error: Pillow not installed (pip install Pillow)\n")
    sys.exit(2)

# (slot_size, scale) -> filename. These are the only slots Apple's AppIcon
# catalog accepts for the "mac" idiom. Pixel size is slot_size * scale.
SLOTS: list[tuple[int, int]] = [
    (16, 1), (16, 2),
    (32, 1), (32, 2),
    (128, 1), (128, 2),
    (256, 1), (256, 2),
    (512, 1), (512, 2),
]


def find_repo_root(start: Path) -> Path:
    for p in [start, *start.parents]:
        if (p / ".git").exists():
            return p
    raise FileNotFoundError("Could not locate repo root (no .git directory found)")


def slot_filename(size: int, scale: int) -> str:
    return f"icon_{size}x{size}.png" if scale == 1 else f"icon_{size}x{size}@{scale}x.png"


def write_appiconset(src_img: Image.Image, appiconset_dir: Path) -> list[Path]:
    """Resize the master logo into every Apple-supported AppIcon slot.

    Returns the list of files that should be kept in the directory after
    cleanup — anything else will be deleted to prevent stale artwork from
    previous (broken) icon generators leaking into the catalog.
    """
    appiconset_dir.mkdir(parents=True, exist_ok=True)

    images_meta: list[dict] = []
    keep: list[Path] = []

    for size, scale in SLOTS:
        px = size * scale
        filename = slot_filename(size, scale)
        out_path = appiconset_dir / filename
        resized = src_img.resize((px, px), Image.LANCZOS)
        resized.save(out_path, "PNG", optimize=True)
        keep.append(out_path)
        images_meta.append({
            "filename": filename,
            "idiom":    "mac",
            "scale":    f"{scale}x",
            "size":     f"{size}x{size}",
        })
        print(f"  wrote {filename} ({px}x{px})")

    contents = {
        "images": images_meta,
        "info":   {"author": "generate_icon.py", "version": 1},
    }
    contents_path = appiconset_dir / "Contents.json"
    contents_path.write_text(json.dumps(contents, indent=2) + "\n")
    keep.append(contents_path)

    # Prune leftovers — old generators emitted icon_64x64*.png and other
    # non-standard slots that confuse Asset Catalog. Keep .icns if present;
    # it's regenerated separately below.
    for existing in appiconset_dir.iterdir():
        if existing in keep:
            continue
        if existing.suffix == ".icns":
            continue
        print(f"  removed stale {existing.name}")
        existing.unlink()

    return keep


def write_iconutil_icns(src_img: Image.Image, icns_path: Path) -> bool:
    """Build a standalone AppIcon.icns using iconutil (macOS only).

    iconutil expects the iconset directory to follow Apple's strict naming:
        icon_16x16.png, icon_16x16@2x.png, icon_32x32.png, ...
    We build it in a temp directory and ask iconutil to compile.

    Returns True if .icns was written, False if iconutil is unavailable
    (typical when running this script on Linux / in CI preflight).
    """
    iconutil = shutil.which("iconutil")
    if not iconutil:
        print("  iconutil not found — skipping AppIcon.icns "
              "(this is expected off-macOS; CI will build the icns on the mac runner)")
        return False

    tmp = icns_path.parent / "AppIcon.iconset.tmp"
    if tmp.exists():
        shutil.rmtree(tmp)
    tmp.mkdir(parents=True)

    try:
        for size, scale in SLOTS:
            px = size * scale
            resized = src_img.resize((px, px), Image.LANCZOS)
            resized.save(tmp / slot_filename(size, scale), "PNG", optimize=True)

        result = subprocess.run(
            [iconutil, "-c", "icns", "-o", str(icns_path), str(tmp)],
            check=False, capture_output=True, text=True,
        )
        if result.returncode != 0:
            sys.stderr.write(f"iconutil failed: {result.stderr.strip()}\n")
            return False
        print(f"  wrote {icns_path.name} ({icns_path.stat().st_size} bytes)")
        return True
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def main() -> int:
    script_dir = Path(__file__).resolve().parent
    repo_root  = find_repo_root(script_dir)
    logo_path  = repo_root / "Images" / "Logo.png"

    if not logo_path.exists():
        sys.stderr.write(f"error: logo not found at {logo_path}\n")
        return 2

    src = Image.open(logo_path).convert("RGBA")
    print(f"source: {logo_path} ({src.width}x{src.height})")

    appiconset_dir = (
        script_dir.parent
        / "LanMessenger"
        / "Assets.xcassets"
        / "AppIcon.appiconset"
    )
    write_appiconset(src, appiconset_dir)
    write_iconutil_icns(src, appiconset_dir / "AppIcon.icns")

    print(f"\nAppIcon written to:\n  {appiconset_dir}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
