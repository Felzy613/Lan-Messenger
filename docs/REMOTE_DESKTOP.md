# Remote Desktop

Status and handoff document for the remote-desktop feature. It answers three
questions: what exists, what is proven rather than merely written, and exactly
what the next person has to do.

The wire format is specified in [PROTOCOL.md → Remote Desktop](../PROTOCOL.md#remote-desktop).
That document is normative and this one is not — where they disagree, the
protocol spec wins and this file is stale.

- **Branch:** `feat/remote-desktop-transport`
- **Last updated:** 2026-09-14
- **Shipped?** No. Nothing here is on `main` and no release contains it.

---

## Scope

Decided up front, and not revisited since:

| Axis | Decision |
|---|---|
| Capability | **Full remote control**, not view-only |
| Quality | **Smooth 30 fps H.264**, not JPEG tiles |
| Transport | Industry-standard, AnyDesk/TeamViewer-shaped |
| Platforms | **Both, in the same pass** — the wire format is the deliverable |

Two consequences are worth restating because they shape everything:

**This is the largest feature the app has taken on.** It adds a real-time media
pipeline, a second cryptographic handshake, OS-level input injection and a
consent model to a codebase whose heaviest previous path was a 64 KiB file-chunk
loop.

**The Windows half cannot be written blind.** DXGI Desktop Duplication, Media
Foundation, `SendInput` and WinUI presentation all need real Windows hardware.
See [The development environment](#the-development-environment) — that access
now exists, and every remaining Windows workstream assumes it.

### Out of scope, deliberately

Audio, clipboard sync, host screen blanking, host input blocking, file drag-drop
into a session, pen/touch, multi-viewer sessions, and the Windows secure desktop
(UAC prompt, Ctrl+Alt+Del, lock screen — a user-mode app cannot capture or drive
it; that needs a SYSTEM service). `SendInput` also cannot reach elevated windows
from an `asInvoker` process. The last two are **detected and explained** via the
`host_state` control message rather than failing silently.

Clipboard sync is the most likely fast-follow: it is expected in any remote
desktop, and it needs its own channel, size cap and loop guard.

---

## Status at a glance

| WS | Scope | State | Needs hardware |
|---|---|---|---|
| WS0 | Spikes: MF encoder, `ICodecAPI`, Desktop Duplication | **Done**, one stage unanswered | Windows console session |
| WS1 | Protocol spec | **Done** | no |
| WS2 | Handshake crypto | **Done**, both platforms | no |
| WS3 | Media transport | **Done**, both platforms | no |
| WS4a | macOS capture + encode | **Written, end to end**; capture unverified | Screen Recording grant |
| WS4b | Windows capture + encode | **Not started** | yes |
| WS5 | Decode + present, both platforms | **macOS done**; Windows not started | Windows: yes |
| WS6 | Cross-platform conformance | **Converter done; both directions proven** | macOS fixture pending |
| WS7 | Input capture + injection | **Not started** | yes, both |
| WS8 | Session lifecycle + consent UI | **Policy + consent prompt done** (macOS); indicator, hotkey, watchdog outstanding | no (UI work) |
| WS9 | Settings, logging, diagnostics | **Log channel done**, settings not started | no |
| WS10 | Latency tuning | **Not started** | yes |
| WS11 | Packaging, docs, CI | **Docs done; `dpiAwareness` outstanding** | no |

Test counts on this branch, both suites green:

- macOS **362 passing**, 2 skipped (both generators, skipped by design: the
  H.264 fixture emitter and the consent-prompt renderer)
- Windows **210 passing**, run on real hardware

Of those, the remote-desktop tests are:

| Suite | macOS | Windows |
|---|---|---|
| `RemoteSessionCryptoTests` | 32 | 34 |
| `MediaFrameTests` | 26 | 26 |
| `H264BitstreamTests` | 15 | 15 |
| `H264EncoderTests` | 12 | — (no encoder yet) |
| `H264DecoderTests` | 14 | — (no decoder yet) |
| `SampleBufferVideoPresenterTests` | 3 | — |
| `ScreenCaptureSourceTests` | 18 | — (no capture yet) |
| `VideoPipelineEndToEndTests` | 5 | — |
| `ProtocolCapabilityTests` | 10 | 9 |
| `PeerKeyTrustTests` | 9 | — (folded into the policy suite) |
| `RemoteDesktopPolicyTests` | 23 | 27 |
| `RemoteConsentTests` | 19 | — |
| `RemoteDesktopQueueTests` | 4 | 4 |

---

## What is built

### WS1 — Protocol

`PROTOCOL.md` carries a full `## Remote Desktop` section: media framing, the
handshake, five new JSON packet types, the sub-channel table, the control and
input sub-channel contents, session lifecycle, and the consent rules **as
protocol requirements rather than UI suggestions**.

The five packet types are implemented and validated on both platforms —
`remote_invite`, `remote_accept`, `remote_decline`, `remote_end`, `media_attach`
in `Core/Protocol/PacketTypes.*` and `PacketValidator.*`.

The optional `caps` discovery field is **implemented on both platforms**, with
`remote-desktop-v1` advertised by every beacon, reply and goodbye. See
[WS8](#ws8--session-lifecycle-and-consent).

### WS2 — Handshake crypto

`Core/Crypto/RemoteSessionCrypto.{swift,cs}`. A Noise-KK-shaped triple DH over
the long-term X25519 identity keys the app already pins per contact, because
there is no signing key to work with:

```
es = X25519(eph_initiator, static_responder)    authenticates the responder
se = X25519(static_initiator, eph_responder)    authenticates the initiator
ee = X25519(eph_initiator, eph_responder)       forward secrecy

keys = HKDF-SHA256(ikm = es||se||ee, salt = session_id, info = transcript, 72 B)
     → key_i2r(32) || key_r2i(32) || salt_i2r(4) || salt_r2i(4)
```

Nonces are counters — `direction_salt(4) || sequence(8)` — never random, and the
sequence is enforced strictly increasing on receive.

Both platforms assert the shared `remote_handshake_vector.json`, present in both
test directories. That fixture is the thing that makes "Swift and C# derive the
same bytes" a test rather than a hope.

### WS3 — Media transport

`Core/Networking/Media/`, both platforms. Layered so that only the socket
adapter is untestable:

| Layer | Files | Testable |
|---|---|---|
| Pure logic | `MediaFrame`, `MediaFrameCodec`, `MediaWriteScheduler`, `MediaReassembler`, `MediaSequenceGate` | fully |
| Loops | `MediaFrameReader`, `MediaFrameWriter` — all I/O through the `MediaLink` seam | via in-memory doubles |
| Socket | `SocketMediaLink` | only on real sockets |

The transport reuses **TCP 54232** via a connection upgrade: a validated
`media_attach` detaches the socket from the JSON read loop and hands it to the
media subsystem. No new port, no firewall rule, no installer change.

`RemoteSessionRegistry` holds the ~10 s accept window, one in-flight session per
peer, and single-shot `session_id` lookup, with an injected clock so the window
is testable without sleeping.

`MediaSession` owns three execution contexts — read, write and timers — that
**never share**. `RemoteDesktopQueueTests` on both platforms guards that.

### WS6 — Bitstream conversion

`Core/Networking/Media/H264Bitstream.{swift,cs}`. Pure byte manipulation with no
CoreMedia or Media Foundation types, which is what makes it testable against a
real encoder artefact rather than against its own output.

`windows_h264_sample.h264` (129,547 bytes, 60 frames, 126 NAL units) lives in
both test directories. It is genuine Microsoft H264 Encoder MFT output and
**cannot be regenerated without the Dell** — do not delete it.

### WS4a — macOS encoder

`Core/Networking/Media/H264Encoder.swift`. A real `VTCompressionSession`:
High profile, `AllowFrameReordering=false`, `MaxFrameDelayCount=0`, BT.709 tags,
low-latency rate control, parameter sets re-read on every keyframe.

`H264EncoderTests` drives an actual hardware-backed session and asserts on the
bitstream that comes out — not on a mock. That is deliberate: almost every way
this component can be wrong (a silently rejected property, parameter sets read
from the wrong index, a truncated non-contiguous block buffer, an inverted
keyframe flag) produces plausible-looking objects and an unplayable stream.

### WS5a — macOS decode and present

`Core/Networking/Media/H264Decoder.swift` and `VideoPresenter.swift`.

The decoder turns wire Annex-B into `CMSampleBuffer`s: it splits access units on
slices, rebuilds the `CMVideoFormatDescription` from the in-band parameter sets
whenever they change, converts to AVCC with the parameter sets and delimiters
stripped, and attaches `DisplayImmediately` and an accurate `NotSync` flag. It
refuses to emit anything until it has seen an IDR, and raises a debounced
`onNeedsKeyframe` when it is stalled — which is what becomes a `keyframe_request`
on the control channel.

There is deliberately **no `VTDecompressionSession` in the production path**.
`AVSampleBufferDisplayLayer` decodes what it is handed on the hardware path and
owns presentation timing, so a session of our own would be a second decoder
producing pixel buffers nobody looks at. The tests bring one as an *oracle*
instead — see [What is proven](#what-is-proven-and-by-what).

`SampleBufferVideoPresenter` is the layer implementation of the `VideoPresenter`
protocol, and exists almost entirely for `requiresFlushToResumeDecoding`. Two
mechanisms clear it, and both are needed: the flag is checked before every
enqueue, **and** `NSApplication.didBecomeActiveNotification` is hooked, because
polling on enqueue cannot help when no frames are arriving — and a remote screen
is silent whenever nothing on it moves. Every flush asks for a keyframe, once,
and the request re-arms only when a keyframe has actually been enqueued.

### WS4a — macOS capture

`Core/Networking/Media/ScreenCaptureSource.swift`. An `SCStream` built for a
session that runs for hours, which is a different problem from `ScreenshotService`'s
one-frame grab and differs from it in nearly every decision — the two files mark
the differences where they occur.

Three `SCStream` properties shape it:

- **It is change-driven, not 30 fps.** `minimumFrameInterval` is a ceiling. A
  still screen delivers *no frames at all*, indefinitely, and that is correct.
  Nothing downstream may read silence as failure.
- **It stops silently.** Display reconfiguration, a resolution change, a GPU
  reset and a revoked TCC grant all arrive as `didStopWithError` and then
  nothing. Restart is mandatory, with backoff, re-enumerating shareable content
  because the display may be gone. `NSApplication.didChangeScreenParametersNotification`
  is watched too: a resolution change does not always stop the stream, and a
  stream that keeps running scales the new desktop into the old frame size.
- **Frames are not all pictures.** Idle, blank and suspended frames are marked in
  the sample attachments and still delivered, carrying whatever was in the pool.

Geometry, the `SCStreamConfiguration` mapping, the frame-status gate, the capture
clock and the restart backoff are all pure and tested. **That `SCStream` delivers
a frame at all is not**, and cannot be here — see [Verification](#verification).

### The pipeline

`VideoSendPipeline.swift` and `VideoReceivePipeline.swift` join what had never
been joined. The send side owns the encoder, converts AVCC to Annex-B with the
parameter sets in-band at every IDR, and turns a viewer's `keyframe_request`
into `kVTEncodeFrameOptionKey_ForceKeyFrame` on the next captured frame —
*latched*, because VideoToolbox has no "send an IDR now" call and a still screen
may not produce a frame for some time. The receive side decodes and hands
sample buffers on, deliberately **not** presenting: `VideoPresenter` is
main-actor work and the pipeline runs on the session read queue.

The one coupling that survives that split is `reset(reason:)` — the other half of
a presenter flush. After a flush nothing decodes until an IDR, and a decoder that
does not know it was flushed keeps handing over P-frames that are silently
discarded.

### WS9 — partial

`remote` is present in **both** `LogChannel` enums, so a remote-desktop session
reaches the bug-report bundle. That was done early on purpose: CLAUDE.md records
that the export bundle is derived from the enum, so an unlisted channel silently
never reaches a bug report.

---

## What is proven, and by what

There is a difference between code that exists and behaviour that has been
observed. This section is only the second kind.

| Claim | Evidence |
|---|---|
| Swift and C# derive identical handshake keys | `remote_handshake_vector.json` asserted by both suites |
| Swift and C# produce identical media frames | `media_frame_vector.json` asserted by both suites |
| The Windows half builds and passes with real tooling | 210/210 on the Dell, 2026-09-14 |
| Windows has a hardware H.264 encoder, and `ICodecAPI` is reachable | WS0 probe, stage 2 |
| Hardware MFTs are async and need `MF_TRANSFORM_ASYNC_UNLOCK` | WS0 probe — they refuse `ProcessInput` with `MF_E_TRANSFORM_ASYNC_LOCKED` until unlocked |
| The software MFT produces valid H.264 | WS0 probe — 60 frames, 126 NAL units, 129,547 bytes |
| Our Annex-B converter handles real Windows encoder output | `H264BitstreamTests` against `windows_h264_sample.h264` |
| **A macOS-encoded stream decodes on Windows** | WS0 probe `--decode=`: 60 frames in, **60 frames out at 320×240** |
| **A Windows-encoded stream decodes on macOS** | `H264DecoderTests`: `windows_h264_sample.h264` → 60 samples → **60 pictures at 1280×720** out of a real `VTDecompressionSession` |
| **A frame travels the whole video path** | `VideoPipelineEndToEndTests`: pixel buffers → encoder → Annex-B → scheduler → framing → AES-GCM → paired link → unseal → sequence gate → reassembly → decoder → `VTDecompressionSession`, with two real `MediaSession`s and independently derived directional keys |
| A 1080p keyframe fragments and reassembles | same suite — asserted to exceed one 16 KiB fragment, then decoded |
| `capture_us` survives the whole journey | same suite — submitted and received timestamps compared element by element |
| A viewer's keyframe request becomes an IDR | same suite — the recovery loop, closed |

Those last two rows are the plan's second-biggest risk, and it is now closed in
**both** directions. Note what the macOS one asserts against: not our own
encoder, and not a mock, but a real Microsoft H264 Encoder MFT artefact decoded
by a real VideoToolbox session. Nothing of ours is on the answering side of that
test, which is the only reason it is worth anything — parameter sets left in the
sample data, a format description built back to front, or access units split on
the wrong NAL type all produce well-formed objects that simply never become a
picture.

### Not yet proven

- Desktop Duplication acquiring a frame at all. The probe enumerates adapters
  but no outputs over SSH, because an SSH logon session has no attached desktop.
- macOS → macOS presentation. The decoder's output has been decoded, but nothing
  has been on screen yet: `SampleBufferVideoPresenter` is tested against a layer
  with no window behind it, which catches a rejected sample but not a blank one.
- **`SCStream` delivering a frame at all.** Everything downstream of capture is
  now proven end to end, but the capture source has never produced a picture:
  this machine's TCC grant is declined for the process that runs the tests, so
  `start()` can only be shown to refuse correctly.
- Anything over a real socket, or between two machines. The end-to-end test runs
  two sessions in one process over a paired in-memory link, so it proves framing,
  sealing, sequencing and reassembly — but not `SocketMediaLink`, not the
  `media_attach` upgrade, and nothing about a real network.

---

## The development environment

### This Mac

Normal workflow. `cd src/macos && swift build && swift test`.

macOS signing is now stable — `project.yml` carries
`DEVELOPMENT_TEAM: J97QDPV6YC` and a fixed bundle id `com.dave.lanmessenger`.
This was a WS0 blocker and is resolved: TCC grants keyed to the code signature
(Screen Recording for capture, Accessibility for injection) will now survive a
rebuild instead of being silently revoked.

To reset a grant while testing:

```bash
tccutil reset ScreenCapture com.dave.lanmessenger
tccutil reset Accessibility com.dave.lanmessenger
```

### The Windows machine

A Dell, Windows 11 Home 26200, Intel i5-12400 / UHD Graphics 730, reachable over
SSH on the LAN. Two helpers exist outside the repo:

- `~/.ssh/config` → host alias `lanmsg-win`
- `~/.local/bin/winrun` → runs a PowerShell command over that SSH connection

**The SSH session is elevated.** `sshd` runs as SYSTEM and the key is installed
in `C:\ProgramData\ssh\administrators_authorized_keys`, so anything run through
it has Administrator rights. The port-22 firewall rule is scoped **Private
profile only** and the machine is not port-forwarded to the internet. Treat that
access accordingly.

The toolchain pieces the WinUI build needs, discovered the hard way, are
recorded in `~/.claude/.../memory/windows-remote-build-access.md`.

### What an SSH session cannot do

An SSH logon has no attached desktop. That makes three things impossible
remotely, and each needs someone at the physical keyboard:

1. **Desktop Duplication** — `IDXGIOutput` enumeration returns nothing.
2. **`SendInput`** — there is no input desktop to inject into.
3. **Launching the WinUI app** to look at it.

---

## Remaining work

Ordered. Each workstream states its goal, the files it creates, the traps that
are already known, and what "done" means.

### WS4b — Windows capture and encode

**Goal:** a Windows host produces an Annex-B H.264 stream from its screen.

**Files to create**, mirroring the macOS layout:

```
src/windows-native/LanMessenger/Core/Networking/Media/
  DesktopDuplicator.cs     DXGI Desktop Duplication capture
  ColorConverter.cs        BGRA -> NV12 via the D3D11 video processor
  H264Encoder.cs           Media Foundation encoder, mirror of H264Encoder.swift
  CodecApi.cs              hand-rolled ICodecAPI COM interface
```

**Start from `spikes/windows-mf-probe/Program.cs`.** Its encode stage is working
code against this exact hardware; port it rather than writing from scratch.

**Interop decision, already settled.** `Vortice.MediaFoundation` 3.6.2 covers
`MFTEnumEx`, `IMFTransform`, `IMFMediaEventGenerator`, `IMFDXGIDeviceManager`,
`IMFDXGIBuffer`, `IMFActivate`, `IMFAttributes`, `IMFSample`, `IMFMediaBuffer`
and `IMFMediaType` — **everything except `ICodecAPI`**. So this is Vortice plus
one hand-rolled COM interface, not a migration to CsWin32. Verified by
reflection over the NuGet package from the Mac.

**Capture sequence:**

1. Enumerate adapters via `CreateDXGIFactory1`, then each adapter's outputs, and
   create the D3D11 device **on the adapter that owns the target output**.
   "Adapter 0" fails with `DXGI_ERROR_UNSUPPORTED` on every Optimus laptop.
2. `ID3D10Multithread::SetMultithreadProtected(TRUE)` on the device. Media
   Foundation calls in from its own threads; without this you get sporadic
   driver crashes with no useful diagnostic.
3. `IDXGIOutput1::DuplicateOutput`, then `AcquireNextFrame` in a loop.
4. `DXGI_ERROR_WAIT_TIMEOUT` means **nothing changed** — not an error, and not
   a reason to emit a frame. `DXGI_ERROR_ACCESS_LOST` needs full output
   re-enumeration, not just re-acquire. `DXGI_ERROR_ACCESS_DENIED` follows a
   desktop switch and persists; loop with backoff.
5. **Desktop Duplication excludes the cursor from the desktop image** and hands
   you the shape separately via `GetFramePointerShape`. This is the opposite of
   `SCStream`, where `showsCursor = true` bakes it in. v1 composites the pointer
   onto the frame so both platforms look the same to the viewer.
6. BGRA → NV12 through `ID3D11VideoDevice` / `ID3D11VideoContext`, setting the
   stream and output colour spaces **explicitly** to BT.709, limited range.
   Unsignalled full range is how you get washed-out blacks on the far side.

**Encoder sequence:**

1. `MFTEnumEx` for `MFT_CATEGORY_VIDEO_ENCODER` / `MFVideoFormat_H264`, hardware
   first, `MFT_ENUM_FLAG_SYNCMFT` as the fallback. **Build the software path
   too** — Windows N/KN SKUs have no H.264 MFTs at all, and it gives you a
   working reference while debugging the hardware path.
2. Set `MF_TRANSFORM_ASYNC_UNLOCK = 1` on the MFT's attributes. Unlocking alone
   is not enough: the hardware MFTs then require the
   `METransformNeedInput` / `METransformHaveOutput` event pump.
3. `MFCreateDXGIDeviceManager` → `ResetDevice(device)` →
   `ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, ptr)`.
4. Output media type: `MF_MT_MPEG2_PROFILE` = High, progressive, frame size,
   frame rate, average bitrate, and the colour attributes
   (`MF_MT_VIDEO_NOMINAL_RANGE` = `MFNominalRange_16_235`,
   `MF_MT_VIDEO_PRIMARIES` / `MF_MT_TRANSFER_FUNCTION` / `MF_MT_YUV_MATRIX` all
   BT.709) to match the macOS encoder.
5. `ICodecAPI` for `AVEncCommonRateControlMode`, `AVEncCommonMeanBitRate`,
   `AVLowLatencyMode`, `AVEncVideoMaxNumRefFrame = 1`. Force an IDR for a
   viewer's `keyframe_request` with `AVEncVideoForceKeyFrame`.

**Output packaging:** this encoder already emits Annex-B with in-band
`AUD, SPS, PPS, IDR` before every IDR and 4-byte start codes, so the Windows
host needs **no conversion** before the wire. Keep handling 3-byte codes on
receive anyway — the Quick Sync MFT has not been sampled.

**Done when:** the probe's capture stage acquires real frames from an
interactive console session, and a Windows-captured stream plays in VLC.

**Needs hardware:** yes, at the physical keyboard.

### WS5 — Decode and present

**Goal:** a viewer on either platform shows the other platform's stream.

**macOS is done.** `H264Decoder.swift` and `VideoPresenter.swift` exist,
`H264DecoderTests` asserts 60 pictures out of the committed Windows fixture, and
the notes that were in this section are now comments in those two files. Five
things it settled, for whoever ports them:

1. `H264Bitstream.annexBToAVCC` drops SPS/PPS/AUD by default, which is what you
   want — feeding parameter sets in the sample data *as well as* the format
   description is a decode error on VideoToolbox, not a harmless duplicate.
2. The format description is rebuilt when the parameter sets **change**, not on
   every keyframe. Rebuilding every time throws away the decoder's warm state
   several times a minute; never rebuilding decodes a resolution change against
   a description of the old picture.
3. `CMBlockBufferCreateWithMemoryBlock` does not copy. The block is `malloc`'d
   and handed to `kCFAllocatorMalloc` so CoreMedia frees it exactly once.
4. **`requiresFlushToResumeDecoding` is the one that bites.** Two mechanisms
   clear it and both are needed: check before every enqueue, *and* hook
   `NSApplication.didBecomeActiveNotification` — polling on enqueue cannot help
   while the stream is silent, and a remote screen is silent whenever nothing on
   it moves. Each flush requests an IDR, once.
5. Deployment target is macOS 13, so `sampleBufferRenderer` is behind
   `if #available(macOS 14, *)` with `enqueue(_:)` as the fallback.

**Windows** — `Core/Networking/Media/H264Decoder.cs` plus `IVideoPresenter`.
The decoder recipe is **already proven** in `spikes/windows-mf-probe`; port it.
Four requirements, each of which cost an iteration to find:

1. **Set the output type before the first `ProcessOutput`.** Without one the
   decoder answers every call with `MF_E_TRANSFORM_TYPE_NOT_SET` (0xC00D6D60)
   and emits nothing, forever. Set a placeholder NV12 type up front; the decoder
   issues a stream change with the real size once it has parsed the SPS.
2. **Input samples need timestamps**, or it buffers every access unit and never
   emits one.
3. **`MF_E_NOTACCEPTING` (0xC00D36B5) is normal flow control**, not an error —
   drain and retry the same sample.
4. **Split access units on slices, not on AUD or SPS.** VideoToolbox emits no
   access unit delimiters and parameter sets only at IDRs, so an AUD/SPS-based
   split collapses a 60-frame stream into 2 units. Every slice (NAL type 1 or 5)
   is one picture; any SPS/PPS/SEI ahead of it belongs to it.

**Presentation — build the boring one first.** `IVideoPresenter` with two
implementations:

1. `Image` + `WriteableBitmap`, ~50 lines, always works, costs 30–60% of a core
   at 1080p30. **Write this first** so the pipeline is never blocked on
   presentation.
2. A **separate plain Win32 window** with `CreateSwapChainForHwnd`. Preferred
   over `SwapChainPanel`: a viewer is naturally its own window, and this skips
   WinUI COM interop, IID archaeology and composition-scale maths entirely.
   `ISwapChainPanelNative` is not projected to C# and its **WinUI 3 IID differs
   from the UWP one** — using the UWP IID is a baffling and common failure.

**Done when:** ~~`windows_h264_sample.h264` decodes to 60 frames on macOS,
asserted in `H264DecoderTests`~~ — done, 60/60 at 1280×720 — and a viewer window
shows live frames on both platforms.

### WS6 — Conformance, remainder

The converter and **both** decode directions are done: macOS → Windows on real
hardware via the WS0 probe, Windows → macOS in `H264DecoderTests` against the
committed fixture. What remains:

- A macOS fixture committed alongside the Windows one, so the *Windows* suite can
  assert its direction without hardware too — the macOS side already can. It is
  currently regenerated on demand:

```bash
cd src/macos
LANMSG_EMIT_H264_FIXTURE=/tmp/macos_sample.h264 \
  swift test --filter testEmitMacOSFixtureForCrossPlatformDecode
```

- **Check blacks and whites, not just "there is a picture".** The colour-range
  bug is invisible until you look for it.

### WS7 — Input

**Goal:** keyboard and mouse from the viewer reach the host correctly.

**Mapping tables first.** There is no stock API on either platform for
HID ↔ platform keycode. Source both tables from Chromium's
`ui/events/keycodes/dom/dom_code_data.inc`, which carries HID usage, Windows
scancode and macOS keycode in one BSD-licensed table. That turns two days into
two hours. Roughly 110 entries each. Add a third small HID → human-name table
for the key-echo diagnostic.

**Ship the key-echo diagnostic on day one** of this workstream — the host
displays what it received and what it injected. It makes the layout-testing day
far less painful.

**macOS traps**, in rough order of how much time they cost:

- `localEventsSuppressionInterval` defaults to **0.25 s**, which makes the
  host user's own mouse feel broken. Set it to 0 and pair with
  `CGEventSourceSetLocalEventsFilterDuringSuppressionState(.permitAllEvents, …)`.
- Modifiers need **both** real key-down/up events for the modifier keycodes
  **and** cumulative `event.flags` on every subsequent event. It is redundant
  and it is what works — different apps read one or the other. **Lift all
  modifiers** on session end, focus loss or error; a stuck Cmd is a support
  ticket.
- Injected key-downs **do not auto-repeat**. The viewer forwards its own OS
  repeats.
- Double-click needs `.mouseEventClickState` 1 then 2. Drags need
  `.leftMouseDragged`, not `.mouseMoved`.
- CGEvent coordinates are global display space in **points, top-left origin** —
  use `CGDisplayBounds`, not `NSScreen.frame`.
- Do not forward CapsLock; forward effective shift instead.

**Windows traps:**

- **Extended keys.** Right Ctrl, AltGr, Insert/Delete/Home/End/PgUp/PgDn, all
  four arrows, Numpad Enter and `/`, and the Win keys need
  `KEYEVENTF_EXTENDEDKEY` with the **low byte only** (Right Ctrl `E0 1D` →
  `wScan = 0x1D` plus the flag). Omit it and Right Ctrl becomes Left Ctrl, and
  the arrows become numpad digits under NumLock.
- `INPUT.cbSize` must be **40 bytes on x64**, with `LayoutKind.Explicit`. Wrong
  size and `SendInput` returns 0 with no exception. Assert the return value
  equals the input count on every call.
- Absolute coordinates:
  `dx = (px - SM_XVIRTUALSCREEN) * 65535 / (SM_CXVIRTUALSCREEN - 1)`.
  Dividing by `CX` makes the rightmost column unreachable.
- Every `DllImport` name must be a real export. CLAUDE.md carries a scar here
  (`SetForegroundWindowInternal` crashed the screenshot overlay). `SendInput`,
  `GetCursorPos`, `SetProcessDpiAwarenessContext` and `OpenInputDesktop` are all
  real.

**Neither viewer can capture every key.** Cmd+Tab, Cmd+Space, Ctrl+Alt+Del and
Win+L are eaten by the viewer's own OS. Every remote desktop converges on the
same answer: a **"Send special keys" menu** plus a documented list of
un-forwardable combinations.

**Done when:** the HID tables have unit tests (on Windows, cross-check entries
against `MapVirtualKeyEx(vk, MAPVK_VK_TO_VSC_EX, layout)`), and a manual pass
across US/UK/German/French layouts is clean.

**Needs hardware:** yes, both platforms, at the keyboard.

### WS8 — Session lifecycle and consent

**Goal:** the feature becomes usable, and safe.

This is the difference between a feature and a liability, and it is far cheaper
to design in than to retrofit. The consent rules in PROTOCOL.md are **protocol
requirements** — a client that does not enforce them is not compatible.

**Config and capability:**

- `remoteDesktopMode` is **done**, both platforms. **It is two modes, `off` and
  `on`, not the three originally sketched** — `contactsOnly` and `ask` were
  indistinguishable under the consent rules, since only saved contacts may
  invite at all. Decided 2026-09-15.

  One switch governs the whole feature in **both** directions: a host that will
  not be viewed also does not offer to view, because a switch that only
  half-applies is one users misread. It is stored as a *string*, not a bool, for
  two reasons — a later mode (view-only, say) becomes a new case rather than a
  config migration, and an unrecognised value can **fail closed**. A bool has no
  way to express "I don't know", and the safe direction for this setting is the
  one that does nothing. Failing closed must not mean failing loudly, either:
  the Windows side serializes the raw string precisely because
  `System.Text.Json` would throw on an unknown enum value and take the user's
  contacts with it.

  No unattended-access mode in v1 — that is what turns a chat app into a RAT.
- **`RemoteDesktopPolicy`** is **done**, both platforms: one pure function for
  the inbound gate, one for whether the menu item is offered. Three decisions in
  it are deliberate and easy to get wrong in the friendlier direction:

  **A stranger gets silence, a contact gets an answer.** An invite from an
  unpinned key is dropped without reply, not declined — a decline confirms the
  address runs the app and has the feature, and a stranger who can provoke any
  response can use it to probe. A saved contact already knows all of that, and
  leaving them hanging is the exact failure `caps` exists to prevent.

  **Trust is checked before the mode.** Switching the feature on can never widen
  *who* may reach the host, only what happens for contacts who already could.
  Asserted across every mode.

  **A changed key is ignored, not prompted** — it is not a saved contact — but
  it is logged at `error` rather than as routine, because an unfamiliar key at a
  familiar address is the shape of the attack pinning defends against, and
  "nothing happened" is a poor account of it in a bug report.

- The optional **`caps` discovery field** is **done**, both platforms.
  `remote-desktop-v1` rides every beacon, reply and goodbye;
  `PeerInfo.supportsRemoteDesktop` is what the menu item will be gated on.
  Tolerance is the part that needed care and is now normative in PROTOCOL.md: a
  malformed `caps` is treated as absent rather than dropping the datagram,
  because System.Text.Json throws where Swift's decoder would not, and the peer
  that vanishes is always the one on the *other* platform.

**Consent UI:**

- **Two-stage.** Accepting an invite grants *viewing*. Control is a **separate
  prompt**. Both are built in this pass; this is the consent model, not a scope
  reduction. **Done on macOS.** `RemoteGrantState` is the ladder —
  `none → viewing → control`, with no path to `.control` except from `.viewing`,
  so there is no code path that arms input for a session nobody agreed to watch.
  `end()` is terminal: reconnect means a new `session_id` and new keys, so it
  means a new state rather than this one quietly resuming.

  `RemoteConsentView` + `RemoteConsentPresenter` are the dialog. Four decisions
  in it are deliberate rather than styling: the peer is **named in the
  headline**; the **fingerprint is always shown**, monospaced, because one that
  appears only when something is wrong is one nobody has ever seen before and
  cannot compare against anything; there is **no reassuring badge on the safe
  path**, since a green tick on every prompt trains people to look for the tick
  instead of the words; and **every accidental way out lands on "no"** — Return
  declines, Escape declines, the close button declines, and the countdown
  declines with the `timeout` token PROTOCOL.md already reserves.

  The prompt is an `NSPanel` + `NSHostingView` rather than `openWindow`, and its
  countdown timer runs in `.common` run-loop mode: a plain scheduled timer stops
  while a menu is open or a window is being dragged, and a countdown that
  silently pauses is worse than none, because the caller's own expiry still
  arrives on time.
- The dialog shows the peer's pinned name, IP and **identity key fingerprint**,
  distinguishing "matches your saved contact" from "new key". A display name
  alone is trivially spoofable; the pinned key is not.
  **`PeerKeyTrust` is built** (macOS) and answers exactly that question:
  `pinned`, `changedAtKnownAddress`, or `unknown`. The awkwardness it works
  around is that contacts are keyed *by* public key, so a peer whose key changes
  looks like a brand-new contact rather than a changed one — the signal is
  positional, a saved contact having last lived at this address under a
  different key. Still to mirror on Windows.
- **Persistent always-on-top host indicator** naming the viewer and the grant
  level, with a Stop control.
- A **host-reserved kill hotkey that is never forwarded**, so a host being
  actively controlled can always stop the session.
- **Host watchdog** — no input, stats or keepalive for N seconds tears the
  session down and releases capture. Not optional: a crashed viewer must never
  leave a screen captured indefinitely. The `MediaSession` timer context already
  exists for exactly this, and `RemoteDesktopQueueTests` guards it against
  starvation.
- Auto-stop on screen lock, user switch, sleep, network loss and app quit.
- An audit entry in chat history for every session start, stop and control grant.

**Window patterns**, so nobody rediscovers them:

- macOS viewer: follow the **`NSPanel` + `NSHostingView`** pattern in
  `MediaBubbleView.swift`, **not** `openWindow` — `WindowController.openWindow`
  is a single slot taking only a String id and cannot carry a session payload.
- Windows viewer: the separate-window pattern from `MediaPreviewWindow.xaml.cs`.

Also needed here: the `video_config` control message (dimensions before the
first frame), resolution-change and monitor-hot-plug handling, and reconnect
(~30 s warm window, **fresh handshake** — the UI may present it as one
continuous session, the crypto must not).

### WS9 — Settings and diagnostics, remainder

`remote` is already in both `LogChannel` enums. What remains is the
`remoteDesktopMode` setting in the UI (the 3-step `SettingsPage` recipe on
Windows, the `@State` mirror + `save()` pattern on macOS) and the stats channel
contents: RTT, decoded fps, dropped frames, decode queue depth, and end-to-end
latency from `capture_us`.

### WS10 — Latency tuning

A named phase, not a hope. Budget a week.

Every stage defaults to buffering, and "never more than two frames in flight"
has to be enforced at **five** places, not one: the capture queue, the encoder,
the socket writer, TCP itself, and the decoder/display. `MediaWriteScheduler`
already implements the writer's share of this — it drops the **queued** video
frame, never the in-progress one, because abandoning a frame whose first
fragment is already on the wire desyncs the peer's reassembler permanently.

Build a glass-to-glass measurement mode off `capture_us`. It is mandatory from
the first commit precisely so this phase measures rather than estimates.

### WS11 — Packaging and CI, remainder

- **`<dpiAwareness>PerMonitorV2</dpiAwareness>` in `app.manifest`.** Verified
  still missing. Without per-monitor awareness the virtual-screen metrics are
  DPI-virtualised and clicks land wrong on mixed-DPI setups. Verify at runtime
  with `GetThreadDpiAwarenessContext`. *This also means the existing
  `ScreenshotService.cs` `GetSystemMetrics(SM_CXSCREEN)` call is probably
  already wrong on high-DPI displays — worth a separate fix.*
- New NuGet references for Vortice.
- `Package.swift`'s `sources:` is an explicit allow-list. `Core/Networking/Media`
  is covered because SPM recurses into `Core/Networking`, which is listed — but
  **a new top-level directory such as `Core/RemoteDesktop` must be added there**
  or it builds in Xcode and silently fails under `swift build` and CI.
- Decide whether to move off Windows App SDK 1.5.240627000 (June 2024). If
  presentation interop misbehaves, upgrading is the first move — but it touches
  the self-contained story and the `IncludePriFileInPublishOutput` workaround.
- Version bump via the pre-commit hook with `BUMP=minor`.

---

## Landmines

Things that cost real time to find. Most are also in CLAUDE.md as
non-regression rules; they are collected here because they are one feature's
worth of hard-won knowledge.

**Framing**

- The AEAD associated data is the **22 header bytes exactly as received**.
  Reserved flag bits 3–7 must be ignored for interpretation but preserved
  byte-for-byte — a normalised re-encode differs by one byte from any peer that
  sets one and fails every tag check. Both `MediaFrameHeader` types keep
  `rawFlags` beside the masked view for this.
- A frame's `length` is `18 + sealedPayloadCount`, **tag included**, and it lives
  inside the AAD. Computing it from the plaintext is off by exactly 16 and every
  frame fails on the peer with no other symptom. `encodeFrame` / `EncodeFrame`
  is the only sanctioned constructor.
- The media cap is 4 MiB and is deliberately independent of `FrameCodec`'s
  50 MiB JSON cap.

**Threading**

- A media session's timers must never share a queue or thread with its read
  loop. The read loop never returns, so a keepalive or watchdog scheduled onto
  it is never dequeued. This exact bug has been found twice in `DiscoveryService`
  already. A starved watchdog is the worst case here: a crashed viewer would
  leave the host's screen captured indefinitely with nothing saying so.

**Crypto**

- Both sides deciding they are the initiator derives swapped keys, and the only
  symptom is "decryption randomly fails" once video is already flowing. The
  initiator is **always** the peer that sent `remote_invite`, and the role is
  bound into the transcript.
- A dropped media socket terminates the crypto session. Reconnect means a new
  `session_id`, new ephemerals, new keys. Reusing keys with a reset counter is
  catastrophic GCM nonce reuse.
- Canonical JSON had to be hand-rolled on both platforms: Foundation escapes
  `/`, and `System.Text.Json` escapes `/` and non-ASCII. One byte of difference
  and the transcripts diverge.

**H.264**

- A 4-byte start code **contains** a 3-byte one at offset+1. A scanner that
  advances one byte instead of consuming the whole code reports exactly twice as
  many NAL units as exist. The tell is suspiciously equal 3-byte and 4-byte
  counts.
- Do not hard-code the NAL length prefix size. Read it from the format
  description. VideoToolbox emits 4 in practice, which is exactly why
  hard-coding it survives testing and fails later. The one exception is the
  AVCC the *receive* path authors for itself, where the same value goes into the
  format description and the length prefixes and they agree by construction.
- Parameter sets belong in the format description **or** the sample data, never
  both. VideoToolbox treats the duplicate as a decode error, and the error is a
  picture that never appears.
- `AVSampleBufferDisplayLayer.requiresFlushToResumeDecoding` silently swallows
  everything enqueued until `flush()` is called. It is set by occlusion and focus
  loss, so it fires in the first minute of the first real session, and it needs
  clearing from two directions — before an enqueue, and on app activation, since
  a silent stream never reaches the first.
- `CMBlockBufferGetDataPointer` returns the **first contiguous range**, not the
  whole buffer. Use `CMBlockBufferCopyDataBytes` into a flat buffer.
- Iterate the parameter set count; do not assume index 0 = SPS, 1 = PPS. Re-read
  them on every keyframe.
- Keyframe test is `!(attachments[kCMSampleAttachmentKey_NotSync] ?? false)` —
  note the inversion.
- `VTCompressionSessionEncodeFrameWithOutputHandler` is renamed by
  `VideoToolbox.apinotes` onto the base name with a trailing `outputHandler:`.
  The block API is the right one; wiring a C callback and passing
  `outputCallback: nil` compiles and does nothing.
- `kVTVideoEncoderSpecification_EnableLowLatencyRateControl` is a
  **creation-time** key passed to `VTCompressionSessionCreate`, not a property
  set afterwards. Setting it the wrong way silently does nothing.
- Tolerate `kVTPropertyNotSupportedErr` on every `VTSessionSetProperty` —
  hardware support varies across Apple Silicon generations.

**Capture**

- `SCStream` is **change-driven, not 30 fps**; `minimumFrameInterval` is a
  ceiling. A static screen produces no frames, so a viewer must never time out
  purely because no video arrived. The same is true of Desktop Duplication's
  `DXGI_ERROR_WAIT_TIMEOUT`. This is why the stats channel and keepalive exist.
- `SCStream` stops silently on display reconfiguration. `didStopWithError` →
  restart + IDR is mandatory.
- For a long-lived stream, use `stopCapture`'s completion-handler form **with a
  timeout** — unlike the one-shot screenshot path, you genuinely need to know
  teardown finished before starting the next stream.

**Windows**

- `ICodecAPI` is the one thing Vortice does not project.
- Async MFTs refuse `ProcessInput` with `MF_E_TRANSFORM_ASYNC_LOCKED`
  (0xC00D6D77) until unlocked — the plan predicted a bare `E_FAIL`, and it was
  wrong: the HRESULT names the cause directly.
- Windows N/KN SKUs have no H.264 MFTs at all. `MFTEnumEx` returns zero and the
  video path is dead. Detect at invite time and say so.
- Do not use Microsoft RDP to test any of this. RDP creates a virtual session
  with its own display driver: Desktop Duplication describes the RDP display
  rather than the real one, and some GPU drivers disable their hardware encoder
  in RDP sessions entirely. You would be measuring RDP. TeamViewer keeps the
  console session.

---

## Verification

**Per workstream** — see each section above for its own "done when".

**The macOS capture smoke test**, which is the one thing `swift test` cannot do.
Screen Recording is a TCC grant keyed to the code signature, and the process that
runs the test bundle does not have it — so `ScreenCaptureSourceTests` asserts
that `start()` refuses cleanly and skips itself on a machine that *is* granted.
Verifying capture needs the signed app, a human, and a look at the log:

```bash
tccutil reset ScreenCapture com.dave.lanmessenger   # to re-test the prompt
cd src/macos && ./scripts/build_app.sh
```

Then drive a capture and confirm the `remote` log channel shows `capture_started`
with the expected `WxH@fps`, followed by frames. **Move a window while watching**:
a still screen produces no frames at all, so "no frames" on a static desktop is
the correct result and proves nothing either way.

**End-to-end, needs two real machines:**

1. Mac ↔ Mac: invite, accept, view. Confirm 30 fps on a moving window, and that
   a static screen does not time out.
2. Escalate to control. Verify modifiers do not stick; Right Ctrl and AltGr are
   distinct from their left counterparts; arrows work with NumLock on;
   double-click works; drag works; scroll direction is correct with and without
   natural scrolling.
3. Mac ↔ Windows **both directions**. This is where codec interop and the
   colour-range bug surface. Check blacks and whites.
4. Kill paths: host hotkey while being controlled; viewer force-quit (watchdog
   releases capture); network drop and reconnect (new `session_id`); screen lock.
5. Windows-specific: focus an elevated window → banner, not silent failure;
   trigger a UAC prompt → secure-desktop banner, not a frozen frame; mixed-DPI
   multi-monitor click accuracy.
6. Latency: glass-to-glass measurement mode, wired and over Wi-Fi.
7. Consent: a non-contact cannot invite; `off` is the default on a fresh install;
   the fingerprint warning fires on a changed key.

**Before finishing:**

```bash
cd src/macos && swift build && swift test
```

```powershell
cd src\windows-native
msbuild /t:Restore /p:Configuration=Release /p:Platform=x64 LanMessenger.sln
msbuild LanMessenger.Tests\LanMessenger.Tests.csproj /p:Configuration=Release /p:Platform=x64
$testDll = Get-ChildItem LanMessenger.Tests\bin -Filter LanMessenger.Tests.dll -Recurse | Select-Object -First 1
dotnet vstest $testDll.FullName --logger:"console;verbosity=normal"
```

Plus `git diff --check`; confirm any new top-level Swift source directory is in
`Package.swift`'s `sources:` list; confirm `remote` is still in both
`LogChannel` enums and reaches the log-export bundle; and check **both** copies
of every test fixture.

---

## Immediate next steps

For whoever picks this up:

1. **Grant Screen Recording to the built app and confirm capture delivers.**
   This is the only unverified link in the macOS chain, and it needs a human:
   the TCC grant is declined for the process that runs `swift test`, so the
   refusal path is all that can be asserted here. Build and launch the signed
   app, grant it, and check that `capture_started` is followed by frames —
   signing is stable, so the grant will stick across rebuilds.

   ```bash
   cd src/macos && ./scripts/build_app.sh && swift run
   ```

2. **WS8, session lifecycle and consent.** Everything below it now works and
   none of it is reachable: there is no invite, no accept, no viewer window and
   no host indicator. It is also the workstream that decides whether this feature
   is safe, and retrofitting consent is much harder than building it in.
3. **At the Dell's physical keyboard**, run the probe to answer Desktop
   Duplication:

   ```powershell
   cd C:\Users\Davef\lanmsg\spikes\windows-mf-probe
   dotnet run -c Release -- --frames=2
   ```
