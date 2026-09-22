# Remote Desktop — Working Notes

**The status document is [docs/REMOTE_DESKTOP.md](../docs/REMOTE_DESKTOP.md).**
Read that first; it carries the workstream table, the remaining plan, and the
landmine list. This file holds only what does not belong in product
documentation: how the work is being done, and what has already been tried.

Branch: `feat/remote-desktop-transport`. Nothing is on `main`.

## Shape of the work

The feature was planned as twelve workstreams (WS0–WS11) and they are being
taken roughly in order, with the risky ones pulled forward. As of 2026-09-14:
protocol, handshake crypto, media transport, bitstream conversion and the macOS
encoder are done; capture, decode, presentation, input and consent are not.

Two risks drove the ordering, and both have been attacked early:

1. **Media Foundation plumbing from C#** — answered by `spikes/windows-mf-probe`
   on real hardware rather than by reading documentation.
2. **Cross-platform H.264 interop** — answered by testing the converter against
   a real Windows encoder artefact, then by actually decoding a macOS-encoded
   stream on Windows.

## Things already resolved, so nobody re-opens them

- **Should we vendor RustDesk instead?** Asked and answered: no. The decision was
  to keep building it inside LAN Messenger.
- **Does Vortice expose `ICodecAPI`?** No — and that was the plan's biggest
  flagged unknown. It exposes everything else that is needed. Determined from the
  Mac by reflecting over the NuGet package, before any Windows access existed.
  The answer is Vortice plus one hand-rolled COM interface, **not** a migration
  to CsWin32.
- **New port for media?** No. Connection upgrade on the existing TCP 54232.
- **macOS code signing.** `DEVELOPMENT_TEAM` is now set in `project.yml`, so TCC
  grants survive rebuilds. This was a WS0 blocker and is closed.

## Working methods that paid off

- **Test against the other platform's real output, never your own.** A converter
  tested against its own output proves only self-consistency. Both H.264 test
  suites run against `windows_h264_sample.h264`.
- **Instrument instead of guessing.** The Media Foundation decoder produced zero
  frames for three iterations of plausible-sounding fixes. Surfacing the
  swallowed `ProcessOutput` HRESULT turned it into a named error and a five
  minute fix.
- **Suspiciously round numbers are a bug, not a result.** A NAL scan reporting
  "126 four-byte and 126 three-byte start codes" was double-counting: the equal
  counts were the tell.
- **Verify an artefact's contents before shipping it anywhere.** A tarball built
  while files were still being written was missing two of them; checking the file
  list caught it.

## Environment

A Dell (Windows 11 Home, i5-12400 / UHD 730) is reachable over SSH on the LAN,
and that is what finally let the Windows half build and pass its tests with real
tooling for the first time — 210/210. **The SSH session is elevated**, since
sshd runs as SYSTEM with the key in `administrators_authorized_keys`; the port-22
rule is Private-profile only and the machine is not exposed to the internet.

An SSH logon has no attached desktop, so Desktop Duplication, `SendInput`, and
launching the WinUI app all need someone at the physical keyboard. Do not
substitute Microsoft RDP — it creates a virtual session with its own display
driver, so you would be measuring RDP rather than the machine.
