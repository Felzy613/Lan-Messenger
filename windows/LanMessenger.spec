# -*- mode: python ; coding: utf-8 -*-

from PyInstaller.utils.hooks import collect_submodules

hiddenimports = [
    "pystray",
    "pystray._base",
    "pystray._info",
    "pystray._util",
    "pystray._win32",
]

try:
    hiddenimports += [
        "plyer",
        "plyer.platforms",
        "plyer.platforms.win",
        "plyer.platforms.win.notification",
    ]
except Exception:
    pass

try:
    hiddenimports += collect_submodules("tkinterdnd2")
except Exception:
    pass


a = Analysis(
    ["main.py"],
    pathex=[],
    binaries=[],
    datas=[],
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="LanMessenger",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    console=False,
    disable_windowed_traceback=False,
    icon="assets/LanMessenger.ico",
)

coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=True,
    upx_exclude=[],
    name="LanMessenger",
)
