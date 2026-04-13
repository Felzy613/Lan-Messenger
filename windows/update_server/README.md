# Update Server

`update_server/` is now a deployable static update feed for LAN Messenger.

What lives here:

- `lan-messenger-update.json`: the manifest the app reads
- `index.html`: a simple landing page for humans
- `build_update_server.py`: copies installers into `downloads/` and rewrites the manifest/page
- `downloads/`: generated installer files to upload with the manifest

## Build The Feed

From the repo root:

```bash
python3 update_server/build_update_server.py --version 1.5.0
```

Default input files:

- Windows: `windows/dist-installer/LanMessengerSetup.exe`
- macOS: `macos/releases/LAN-Messenger-Installer.dmg`

Generated output:

- `update_server/lan-messenger-update.json`
- `update_server/index.html`
- `update_server/downloads/LanMessengerSetup-<version>.exe`
- `update_server/downloads/LAN-Messenger-Installer-<version>.dmg`

## Host It

Upload the full `update_server/` folder to any static host:

- GitHub Pages
- Cloudflare Pages
- Netlify
- S3 / CloudFront
- Any normal web server

In the app Settings, use either:

- the direct manifest URL, such as `https://example.com/updates/lan-messenger-update.json`
- or the folder URL, such as `https://example.com/updates/`

The app will append `lan-messenger-update.json` automatically for folder URLs.

## Manifest Format

Required fields:

- `version`
- `downloads.windows`
- `downloads.macos`

The download URLs can be absolute or relative. Relative URLs are resolved against the manifest URL, so the generated feed works cleanly on static hosting.
