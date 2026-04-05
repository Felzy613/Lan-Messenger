# Remote Update Server

Host this folder on any static web server.

The app looks for:

- `lan-messenger-update.json`

You can either point the app directly at that JSON file, or point it at the
folder URL and the app will append the manifest filename automatically.

Example manifest URL:

- `https://example.com/lan-messenger-update.json`

Example folder URL:

- `https://example.com/updates/`

Required manifest fields:

- `version`
- `downloads.windows`
- `downloads.macos`

Example hosting options:

- GitHub Pages
- Cloudflare Pages
- Netlify
- Any normal web server or CDN
