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
| WS0 | Spikes: MF encoder, `ICodecAPI`, Desktop Duplication | **Done**, every stage answered 2026-09-17 | — |
| WS1 | Protocol spec | **Done** | no |
| WS2 | Handshake crypto | **Done**, both platforms | no |
| WS3 | Media transport | **Done**, both platforms | no |
| WS4a | macOS capture + encode | **Done and verified on real hardware** 2026-09-17 | grant now given |
| WS4b | Windows capture + encode | **Done and verified on hardware** — Desktop Duplication + Quick Sync async MFT, measured end to end 2026-09-18 | no |
| WS5 | Decode + present, both platforms | **Both done and verified on hardware** — Windows self-view measured 2026-09-18 | no |
| WS6 | Cross-platform conformance | **Done** — both fixtures committed, both suites assert the other platform | no |
| WS7 | Input capture + injection | **Done on both platforms and proven between the two machines** 2026-09-20 — Mac → Dell drives the mouse and keyboard; Dell → Mac reaches the injector and is gated only by the macOS Accessibility TCC grant | done |
| WS8 | Consent, indicator, kill switch, invite exchange | **Done on both platforms and proven between the two machines** 2026-09-20 — invite, consent, accept, attach, capture, the second control prompt and the grant, in both directions | done |
| WS9 | Settings, logging, diagnostics | **Done** — log channel, stats contents, and the settings toggle on both platforms | no |
| WS10 | Latency tuning | **Done and measured between the two machines** 2026-09-21 — **26–31ms glass to glass at 29–30fps**, against a clock offset of 32.57 days that `ping`/`pong` now measures. The two-frame rule is enforced or accounted for at all five places | done |
| WS11 | Packaging, docs, CI | **`dpiAwareness` done**, and the app builds with it; Vortice refs and the SDK decision wait on WS4b | no |

Test counts on this branch, both suites green:

- Windows GPU/encoder selection covers **nine machine topologies**, only one of
  which we own
- macOS **513 passing**, 11 skipped (all generators or hardware-gated: the
  H.264 fixture emitter, the control-vector emitter, the UI renderers, and the
  live capture check)
- Windows **400 passing**, run on real hardware 2026-09-21 from a freshly built
  binary, with the app project compiling — which is what validates the XAML

Of those, the remote-desktop tests are:

| Suite | macOS | Windows |
|---|---|---|
| `RemoteSessionCryptoTests` | 32 | 34 |
| `MediaFrameTests` | 26 | 26 |
| `H264BitstreamTests` | — (folded into the codec suites) | 21 |
| `SampleBufferVideoPresenterTests` | 3 | — |
| `ScreenCaptureSourceTests` | 18 | — (`CaptureTargetSelectorTests`, 14) |
| `VideoPipelineEndToEndTests` | 5 | — |
| `ProtocolCapabilityTests` | 10 | 10 |
| `PeerKeyTrustTests` | 9 | — (folded into the policy suite) |
| `RemoteDesktopPolicyTests` | 23 | 26 |
| `RemoteConsentTests` | 22 | — (folded into `RemoteSessionShapeTests`, 23) |
| `RemoteHostIndicatorTests` | 13 | — |
| `RemoteSessionStopTests` | 12 | — (folded into `RemoteSessionShapeTests`) |
| `RemoteAuditTests` | 16 | — (folded into `RemoteSessionShapeTests`) |
| `RemoteDesktopSessionTests` | 10 | — |
| `RemoteInviteCoordinatorTests` | 16 | 18 |
| `RemoteInputRecordTests` | 14 | 17 |
| `RemoteInputGeometryTests` | 8 | 11 |
| `HidKeyMapTests` | 9 | 12 |
| `MediaControlMessageTests` | 11 | 11 |
| `RemoteDesktopQueueTests` | 4 | 4 |
| `RemoteLatencyClockTests` | — (the macOS presenter reports its own) | 9 |
| `RemoteClockSyncTests` | 9 | 9 |
| `VideoFrameBudgetTests` | 9 | 9 |

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
| **Desktop Duplication acquires real frames** | WS0 stage 3 at the Dell's physical keyboard, 2026-09-17: adapter 0 → `\\.\DISPLAY22` 1920x1080, `DuplicateOutput: OK`, `AcquireNextFrame: OK accumulated=1` |
| **Desktop Duplication and the Quick Sync encoder both start in the real app** | self-view smoke test at the Dell, 2026-09-17: `capture_started \\.\DISPLAY22 1920x1080 adapter=Intel(R) UHD Graphics 730`, then `encoder_started IntelAr Quick Sync Video H.264 Encoder MFT 1920x1080@30 kind=HardwareAsync` |
| **An async MFT will not be driven synchronously** | the same run, before the fix: 34,731 `ProcessOutput 0x8000FFFF` (E_UNEXPECTED) in fifty seconds and no frames. Unlocking an async MFT is not the same as driving one — see the `H264Encoder` header and CLAUDE.md |
| **The Windows capture-encode-decode-present loop runs end to end** | self-view at the Dell, 2026-09-18: 1920x1080, **fps 25-27, capture-to-composited 44-53ms**, zero drops, zero errors, memory flat at ~410MB over minutes |
| Quick Sync encoders exist and `ICodecAPI` is reachable on them | same run: two hardware MFTs, both async, both unlocking and answering `QueryInterface` |
| **`SCStream` captures the real screen, and it encodes** | `ScreenCaptureLiveTests` with the grant, 2026-09-17: **32 frames at 1920x1080**, delivered size matching the configured size, capture clock advancing; then **30 frames encoded, 1 keyframe, 505,315 bytes** through the real VideoToolbox encoder |
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

### Proven between the two machines, 2026-09-20

A full session was run in both directions between this Mac and the Dell, over a
real socket and the `media_attach` upgrade:

| | Mac → Dell | Dell → Mac |
|---|---|---|
| invite, consent, accept, attach | yes | yes |
| video | yes, ~26fps | yes, ~29fps |
| second control prompt and grant | yes | yes |
| input arriving at the host's injector | yes, and moving the pointer | yes, refused by TCC — see below |

Four defects were found by doing it, each of which is invisible in one-machine
testing and each now has a rule in `CLAUDE.md`:

1. **A macOS host never routed a single inbound frame.** `onFrame` was wired
   inside `start()`'s `presentsLocally` block, so only a viewer set it. Video is
   one-way and `MediaSession` eats the keepalives itself, so both ends looked
   healthy while the host ignored every `control_request`, input record and
   keyframe request it was sent.
2. **`viewer_stats` latency was meaningless across machines** — `capture_us` is
   the host's clock. It reported 32.5 days.
3. **The audit trail was written from the host's chair in both roles**, so a
   viewer recorded "You stopped sharing your screen." about a session where it
   shared nothing.
4. **The Windows audit record serialized its own derived properties**, so the
   two platforms wrote different bytes for the same record.

### Not yet proven

- Dell → Mac **injection actually moving the Mac's pointer**. The records arrive
  and reach `RemoteInputInjector`, which logs
  `no Accessibility grant — CGEvent.post will do nothing silently` and stops
  there. That is the designed behaviour, not a defect: injection needs the
  **Accessibility** TCC grant, which is separate from Screen Recording. Note
  that every rebuild of an *ad-hoc-signed* bundle is a new code identity and
  loses both grants; `scripts/macos/create-dev-identity.sh` plus the dev-signing
  branch in `package.sh` fix that, so grant once and rebuild freely — see
  [DEVELOPMENT.md → TCC grants](DEVELOPMENT.md#tcc-grants). The control consent
  prompt says so when the grant is missing.
- macOS → macOS presentation. The decoder's output has been decoded, but nothing
  has been on screen in that configuration: `SampleBufferVideoPresenter` is
  tested against a layer with no window behind it, which catches a rejected
  sample but not a blank one.

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

Reference detail per workstream: the goal, the files it creates, the traps that
are already known, and what "done" means. For the order to actually do them in,
see [The plan to finish](#the-plan-to-finish) at the end.

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

**One machine is not every machine.** The probe proved Desktop Duplication on an
Intel UHD 730 with one attached display. That says nothing about an Optimus
laptop, an AMD box, a Windows N SKU with no H.264 MFTs, two monitors on two
adapters, or an RDP session. Buying five GPUs is not an option; describing five
topologies is.

So **every selection decision is pure and lives in `CaptureTargetSelector.cs`**,
and real hardware only has to prove the D3D and Media Foundation plumbing works
once the right objects have been chosen. `CaptureTargetSelectorTests` asserts
nine topologies, one of which we own: the Dell's own three-adapter shape; an
Optimus laptop where adapter 0 is the discrete GPU and owns nothing; two monitors
on two adapters; a Basic Render Driver that must never be chosen; a headless
machine; a detached output; a monitor unplugged mid-session; and a Windows N SKU
with no encoder at all.

**Confirmed working on this hardware, 2026-09-17.** The probe, run at the
physical keyboard, reported `DuplicateOutput: OK` and
`AcquireNextFrame: OK accumulated=1` on adapter 0's `\\.\DISPLAY22` at
1920x1080. Three details from that run shape the code:

- **The adapter/output warning below is live, not theoretical.** This machine
  enumerates three adapters: adapter 0 (UHD 730) owns the only output, adapter 1
  is the *same GPU again* and owns none, and adapter 2 is the Microsoft Basic
  Render Driver. Walking adapters and taking the first is wrong twice over here.
- **`pointerShapeBytes=0` on the first acquire.** The pointer shape is delivered
  only when it *changes*, so a capture loop must cache the last one — a v1 that
  composites the cursor and reads the shape per frame draws nothing most frames.
- **Both Quick Sync MFTs answer `ICodecAPI`**, which settles the interop plan:
  Vortice plus one hand-rolled COM interface, as expected.

**Capture sequence:**

1. Enumerate adapters via `CreateDXGIFactory1`, then each adapter's outputs, and
   create the D3D11 device **on the adapter that owns the target output**.
   "Adapter 0" fails with `DXGI_ERROR_UNSUPPORTED` on every Optimus laptop, and
   on this machine adapters 1 and 2 have no outputs at all.
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

**Done.** Both decode directions were already proven; what remained was a macOS
fixture so the Windows suite could assert its direction without a Mac, and that
is now committed as `macos_h264_sample.h264` in both test directories.

Its value is in how *unlike* the Windows fixture it is. 64 NAL units against 126;
**zero access unit delimiters** against one per frame; parameter sets only at the
two IDRs. It is direct evidence for the rule that an AUD-based access-unit split
collapses a stream — there are no AUDs in it to split on. Both suites now pin
both layouts.

Regenerate with:

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
  level, with a Stop control. **Done on macOS.** A borderless, non-activating
  `NSPanel` at `.statusBar` level with `.canJoinAllSpaces`, `.stationary` and
  `.fullScreenAuxiliary`, so it survives a space switch, Mission Control and a
  host working in full-screen Xcode — and clicking Stop does not pull the app
  forward and interrupt them.

  **It is deliberately immovable**, and that is a security property rather than
  a layout choice. A draggable indicator is a hole once control has been
  granted: a viewer holding the mouse could drag the host's own warning off the
  edge of the screen and carry on working unobserved, and nothing can tell an
  injected drag from a real one — that is the entire point of input injection.
  So placement is a pure function of the screen's visible frame, re-asserted on
  every `didChangeScreenParameters`, with no stored offset to corrupt. The cost
  is a host who cannot shift it off something it covers: a small visible
  annoyance instead of an invisible one.

  Amber for viewing, red for control, with a slowly pulsing dot — a static dot
  becomes furniture within a minute. Elapsed time is shown because it is the
  quiet part that catches the session somebody forgot they left open, and it
  ticks in `.common` run-loop mode so it does not freeze while a menu is open
  and make a live session look stopped. "Stop Control" appears only when there
  is control to take back, and leaves the session running — usually what a host
  actually wants.

  Nothing excludes the panel from `SCStream`, so the viewer sees it too. That is
  the right way round: it costs a strip of transmitted pixels and lets a host
  confirm the viewer is seeing what they think.
- A **host-reserved kill hotkey that is never forwarded**, so a host being
  actively controlled can always stop the session. **Done on macOS: `⌃⌥⌘⎋`.**

  The indicator's Stop button is the obvious exit and the weakest one — once
  control is granted the host's mouse is contested, so pressing a button is a
  race with the person you are trying to stop. The shortcut is not a race.

  Registered through Carbon's `RegisterEventHotKey`, **not** an `NSEvent` global
  monitor, because the monitor route needs the Accessibility grant and the whole
  point of this shortcut is to work when other things have gone wrong —
  including a grant that was never given or has been revoked. Carbon needs no
  permission and fires whatever app is frontmost, which is the requirement: a
  host being actively controlled is by definition not looking at our window.

  The neighbouring combination is deliberate. `⌘⌥⎋` is Force Quit, so a host
  groping for the escape hatch under stress lands on either this — a clean stop,
  with a `remote_end` and an audit line — or on Force Quit, which kills the app
  and therefore the session. Both exits work.

  `RemoteKillSwitch.reserved` is the list WS7's viewer-side capture must consult
  and never put on the wire, so a viewer can escape their own session and a
  host's kill switch can never be triggered remotely by the peer it exists to
  stop. It has a test already, so the rule is in place before the code that has
  to obey it.
- **Host watchdog** — already implemented in `MediaSession` (WS3) and guarded
  against queue starvation by `RemoteDesktopQueueTests`. WS8's remaining share
  was reacting to it, which `RemoteStopReason.watchdog` now does.
- Auto-stop on screen lock, user switch, sleep, network loss and app quit.
  **Done on macOS**, in `RemoteSessionGuard`. Screen lock is the awkward one:
  it is published only on the *distributed* notification centre under
  `com.apple.screenIsLocked`, with no public constant and no AppKit equivalent
  — and without it a locked Mac keeps streaming a lock screen, and then whatever
  is behind it when the host comes back and types their password.

  The guard disarms as it fires. A lid closing produces sleep *and* a screen
  lock, and the session must stop once with the first cause rather than twice,
  since the second callback would arrive after teardown had already run.
- An audit entry in chat history for every session start, stop and control
  grant. **Done on macOS.** Stored the way attachments are — an ordinary
  `MessageEntry` whose `text` carries a `__REMOTE__:` marker and a JSON body —
  so the history format is unchanged and there is nothing to migrate.

  The cost of that trick is that **every call site inspecting message text needs
  to know the prefix**, and there are three: the sidebar's last-message preview,
  the editability guard, and the chat row builder. A fourth that forgets renders
  raw JSON at somebody. `RemoteAuditTests` pins the conventions apart.

**Window patterns**, so nobody rediscovers them:

- macOS viewer: follow the **`NSPanel` + `NSHostingView`** pattern in
  `MediaBubbleView.swift`, **not** `openWindow` — `WindowController.openWindow`
  is a single slot taking only a String id and cannot carry a session payload.
- Windows viewer: the separate-window pattern from `MediaPreviewWindow.xaml.cs`.

The **control sub-channel is done on both platforms**: `MediaControlMessage`
and `MediaControlCodec` implement every `t` in the table, including
`video_config`, and `media_control_vector.json` pins all thirteen to exactly the
same bytes on each side. Two independently hand-written encoders agree because
both sort keys and both disable their JSON library's default escaping —
`System.Text.Json` escapes `/` and non-ASCII where Foundation does not, and one
byte of difference would mean sessions that work Mac-to-Mac and Windows-to-
Windows and fail across.

One bug worth recording came out of the tolerance tests. Swift's
`JSONSerialization.data(withJSONObject:)` raises an **ObjC exception**, not a
Swift error, when handed a non-container top-level value — so `try?` does not
catch it, and a peer sending `"displays": ["nonsense"]` would have taken the
session's read loop down. Tolerant-looking code that calls it without a type
check first is a remote crash, not a lenient parser.

What remains here: wiring the messages to the session (resolution-change and
monitor-hot-plug handling), and reconnect (~30 s warm window, **fresh
handshake** — the UI may present it as one continuous session, the crypto must
not).

### WS9 — Settings and diagnostics, remainder

`remote` is already in both `LogChannel` enums, and the **stats channel contents
are defined and encoded** on both platforms — `RemoteSessionStats` carries RTT,
decoded fps, dropped frames, decode queue depth and end-to-end latency from
`capture_us`, and is in the shared vector. Latency is deliberately allowed to go
negative: when the two machines' clocks disagree the figure is nonsense, and a
nonsense number visible is better than a plausible one invented.

The `remoteDesktopMode` toggle is now in both settings screens. Its explanatory
text is deliberately specific rather than reassuring: it names what a contact can
ask for, that viewing and control are separate asks, that consent is per session,
that there is no unattended access, and that non-contacts are ignored without
being told anything. A setting this consequential should read as a decision
rather than a feature. When it is on, the macOS copy also names the kill shortcut
by its symbol, since a shortcut nobody has read about is one nobody uses in a
panic.

### WS10 — Latency tuning

**Done 2026-09-21, and measured rather than estimated.**

The measurement had to come first, because the one that existed was wrong. A
viewer subtracting the host's `capture_us` from its own clock was reading the
gap between two boot times, and reported an average latency of 32.5 days while
visibly keeping up at 29fps.

`ping` and `pong` had been in PROTOCOL.md since WS1, described as "keepalive and
round-trip measurement", and nothing had ever sent one. They now carry each
side's own clock, and `RemoteClockSync` turns a round trip into an offset by the
usual four-timestamp argument, keeping the **lowest-RTT** sample because that is
the one whose error bound is tightest. `viewer_stats` says `clock=synced`,
`clock=shared` (self-view) or `clock=rel` so a reading always states what it is.

Measured on the two machines, Dell hosting and Mac viewing over the LAN:

| | |
|---|---|
| clock offset | 2,813,816,251 ms — **32.57 days**, which is what the old figure was reporting |
| round trip | 5 ms |
| **glass to glass** | **26–31 ms average**, 96–113 ms max |
| frame rate | 29–30 fps, no presenter drops, no flushes |
| encoder depth | `in_flight=1/8 peak=2 refused=0` |
| capture | `attempts=2550 delivered=1262 age_ms=0` — paced by declining conversion, frames as fresh as the compositor makes them |

The two-frame rule now has an enforcement at each of the five places, and the
one that had none — the encoder — turned out to need a different number:

| place | what bounds it |
|---|---|
| capture | SCK's `queueDepth`; Desktop Duplication's one-frame acquire/release |
| encoder | `VideoFrameBudget`, at **pipeline** capacity, not the protocol's two |
| socket writer | `MediaWriteScheduler`: one in progress, one queued |
| TCP | `TCP_NODELAY`, and `SO_SNDBUF` capped at 128 KiB |
| decode / display | drop rather than queue, and request a keyframe when dropping |

**The encoder is the interesting one.** A codec pipeline is not a queue: an
asynchronous hardware MFT issues a `METransformNeedInput` for every slot it has
and holds several frames by design, and that depth is fixed latency rather than
growth. At a capacity of 8 the Quick Sync encoder's measured depth is **2** and
it refuses nothing, so a cap of 2 would sit exactly on the limit and start
refusing on any jitter. What the budget provides there is a runaway guard and,
more usefully, the only view anything has of the codec's own contribution to
latency.

> A first pass capped it at two and the session produced no video and no
> `encoder_stats` line, which was written up here as starvation. It was not —
> the process was dying about a second in, before the two-second stats timer,
> from an unrelated GC'd window procedure (see below). **"No frames and no
> stats" reads exactly like a stalled encoder and is equally well explained by a
> dead process**; the `.NET Runtime` event log said so in one line.

### Three crashes the two-machine runs found, after WS10's own work

None of them are latency, and two of them presented *as* latency problems.

- **A window procedure rooted for one session too few.** A Win32 window class
  registered by a process stays registered until it exits, so the second
  session's window ran on the class the first `RemoteSessionGuard` registered —
  still pointing at that guard's instance delegate, by then collected. The CLR
  ends the process through `Environment.FailFast`, so there is no exception, no
  handler and nothing in the crash log; it fired inside `CreateWindowExW`,
  because the procedure runs for `WM_NCCREATE` before that call returns. It
  looked like an encoder producing nothing, because the process died before the
  stats timer. The delegate is now `static readonly`, and the owning guard is
  found from the HWND.
- **`ThreadLocal<T>.Values` at teardown.** Pressing Stop Sharing on a second
  session threw `ArrayTypeMismatchException` from inside
  `List<T>.AddWithResize`: slots from the previous session's exited capture
  thread are recycled between `ThreadLocal` instances of different `T`.
  `H264Encoder` now tracks the `ICodecAPI` RCWs it hands out in its own
  lock-protected list.
- **A clean close recorded as a fault.** The peer pressing Stop closes the
  socket without an error; a network that went away closes it with one. Both
  were reported the same way, so a session the *other side* ended deliberately
  went into the history as "the network connection was lost" — one line below a
  log entry that already said `peer ended`.

### Still open

- The macOS **host** side of the synced measurement has not been run: the Mac
  lost its Screen Recording grant when the app moved to `~/Applications`, and
  restoring it needs the user's password. The arithmetic is shared and unit
  tested on both sides, and the Windows viewer path is covered by
  `RemoteLatencyClockTests`.

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

**The macOS capture smoke test** now has a test to run, rather than only a
procedure to follow. `ScreenCaptureLiveTests` is skipped by default and starts a
real `SCStream` when enabled:

```bash
# System Settings -> Privacy & Security -> Screen Recording: enable your
# terminal, then QUIT AND REOPEN it. The grant does not reach a running process.
cd src/macos
LANMSG_LIVE_CAPTURE=1 swift test --filter ScreenCaptureLiveTests
```

Move the mouse while it runs. The second test pushes the captured frames through
the real encoder, which is the first time anything here encodes an actual screen.

**The older procedure**, for checking it inside the signed app:
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

## The plan to finish

Five phases, ordered so each one is verifiable before the next begins, and so
the platform with the slowest feedback loop comes *after* the design is proven
rather than alongside it.

### The gap the original plan had

There is no workstream for **wiring it together**. Every piece works in
isolation — capture, encode, transport, decode, present, consent, indicator,
kill switch, audit — and none of them are connected to anything. There is no
menu item, no invite, no session object. That is why nothing is usable today
despite eleven of twelve workstreams being largely complete, and it is the first
thing to fix.

---

### Phase 1 — Make it work on one Mac — **DONE 2026-09-17, confirmed in the app**

Self view runs in the packaged, signed build: a window showing the screen, the
host indicator counting up, and the kill shortcut ending it. Confirmed by a human
on 1.21.x, which is the only kind of confirmation that counts for this phase.

`RemoteDesktopSession` is the object the plan forgot, and it now exists:
`RemoteDesktopSession.swift`, `RemoteViewerWindow.swift`, the `AppModel`
wiring and a **Remote Desktop** menu with *Start Self View*, gated on the same
setting and policy a real session is judged by.

Verified live on this Mac: **30 frames** from `SCStream` through the encoder,
the Annex-B conversion, the decoder and into the display layer, with each
decoded sample's format description checked against the capture dimensions —
because "frames arrived" alone would also be true of a pipeline producing
green rubbish at the wrong size. Teardown asserted too: capture released,
layer gone, grant ended, audit trail reading start → end with a duration.

What follows is the original scope, kept for the record.

**No new platform code.** Everything this needs already exists and is tested;
the work is the orchestration that was never scoped.

- `RemoteDesktopSession` — the object that ties consent → capture → encode →
  transport on the host, and transport → decode → present on the viewer. Owns
  the `RemoteGrantState`, arms the `RemoteSessionGuard`, raises the indicator,
  writes the audit entries, and honours `remote_end`.
- A viewer window: `NSPanel` + `NSHostingView` hosting
  `SampleBufferVideoPresenter.layer`, per the `MediaBubbleView` pattern.
- The entry point — a menu item on a conversation, enabled by
  `RemoteDesktopPolicy.availability`, greyed with a reason otherwise.
- **A self-view mode**: the app views its own screen, in-process, no network.

That last item is the point. It makes the whole pipeline runnable and visible on
a single machine, with no second Mac and no peer, which is the fastest way to
find integration bugs while the codebase is still half its final size.

**Done when:** you can start a self-view session from the UI, see your own
screen in a window, watch the indicator count up, and stop it with `⌃⌥⌘⎋`.

**Verified by:** a human on this Mac. No hardware anyone has to find.

---

#### Two bugs that only running it could find

Neither was a test failure, and neither would have been caught by more tests.

**The entry point was invisible.** It was a `CommandMenu`, and `hideFromDock`
defaults to true — which makes this an `.accessory` process, and an accessory app
has no menu bar at all. The button belongs on the contact strip, beside the
peer's name, which is where somebody reaches for it.

**"Turn on Remote Desktop in Settings" sent people to the wrong app.** macOS
ships a System Settings pane called exactly *Remote Desktop* — the Apple Remote
Desktop privacy permission — which is unrelated and permanently empty for this
app. Somebody read the message, went there, found nothing, and reasonably
concluded the feature was broken. Every such message now names our own window,
and the settings screen says the macOS pane is not the one.

**And the `caps` field earned itself.** With two peers on older builds on the
same LAN, the button greyed out with *"Ari's version does not support remote
desktop"* — observed on the wire as `caps=ABSENT` against this Mac's
`caps=["remote-desktop-v1"]`. Without that field the invite would have been sent,
`PacketValidator` would have dropped it silently as an unknown type, and the
initiator would have waited forever with no error anywhere.

### Phase 2 — Two Macs

The first time anything crosses a socket.

- Real `remote_invite` / `remote_accept` / `remote_decline` over TCP 54232.
- The `media_attach` upgrade exercised for real rather than against a paired
  in-memory link.
- `video_config` before the first frame; resolution change and monitor hot-plug.
- Reconnect: ~30 s warm window, **fresh handshake** — the UI may present it as
  one continuous session, the crypto must not.

**Done when:** one Mac views another's screen over the LAN, survives a display
being unplugged, and recovers from the network dropping.

**Verified by:** two Macs. If only one is available, a VM on the same host
exercises everything except real-network timing.

---

### Phase 3 — Windows, as a mirror

Only now, and deliberately: porting a design that has been proven end to end is
a different job from inventing two unproven halves in parallel.

- **WS4b** — `DesktopDuplicator`, `ColorConverter`, `H264Encoder`, `CodecApi`.
  Port the spike's encode stage rather than writing it fresh.
  `CaptureTargetSelector` already decides *which* GPU and encoder; this is the
  plumbing underneath it.
- **WS5 Windows** — `H264Decoder` plus `IVideoPresenter`. Build the
  `WriteableBitmap` presenter first so the pipeline is never blocked on
  presentation; the swap-chain window is an optimisation, not a prerequisite.
- **WS8 Windows** — consent prompt, host indicator, kill switch, audit trail,
  mirroring the macOS versions.

**Done when:** Mac ↔ Windows works in both directions. **Check blacks and
whites, not just "there is a picture"** — the colour-range bug is invisible
unless you look for it.

**Verified by:** the Dell, at the keyboard. Batch this work: every check needs a
human there, so the loop is slow and should be run few times rather than often.

---

### Phase 4 — Input

Last on purpose. It is the riskiest surface, needs the most manual testing, and
is useless until there is a session to inject into.

- **Take Chromium's `dom_code_data.inc`** — HID usage, Windows scancode and
  macOS keycode in one BSD-licensed table. Hand-building this is where two days
  of bugs live.
- Ship the **key-echo diagnostic on day one**: the host shows what it received
  and what it injected. It makes the layout-testing day survivable.
- `CGEvent` injector on macOS, `SendInput` on Windows. Both are ~300 lines and
  both are hand-written by everybody, Chrome Remote Desktop included — the
  cross-platform input libraries target automation ("type hello"), not faithful
  replay of a remote user's raw events.
- Wire the **second consent prompt** — `control_request` → `control_grant` —
  and make the input sub-channel inert until it arrives.

**Done when:** a manual pass across US/UK/German/French layouts is clean, no
modifier sticks, Right Ctrl and AltGr are distinct from their left counterparts,
and the arrows work with NumLock on.

---

### Phase 5 — Ship it

- **WS10, kept small.** Measure RTT and loss, step the bitrate, drop stale
  frames. This is a LAN: sub-millisecond RTT, gigabit, near-zero loss. Do **not**
  reimplement a congestion-control stack — that is what WebRTC exists for and it
  is not what this network needs.
- Populate the stats channel from the numbers the pipeline already counts.
- **WS11 remainder** — Vortice package references, and the Windows App SDK
  1.5 decision (only if presentation interop misbehaves; it touches the
  self-contained story and the `IncludePriFileInPublishOutput` workaround).
- **A diagnostic report users can run and send back.** We own one Intel GPU. An
  Optimus laptop, an AMD box or a Windows N SKU will behave differently, and the
  only way to learn how is to ask. `CaptureTargetSelector` already decides
  correctly for nine topologies on paper; this is how the paper gets checked.

---

### What this deliberately does not do

**It does not switch to WebRTC.** The question was never asked in the original
plan and should have been, so for the record: WebRTC would have replaced WS2,
WS3, WS6 and WS10 — and nothing else. Capture, encode, decode, input and consent
are written either way; libwebrtc's own `DesktopCapturer` is a wrapper over the
same DXGI and ScreenCaptureKit calls we make.

Against that, the C# binding story is effectively dead —
`Microsoft.MixedReality.WebRTC` is archived, and `SIPSorcery` is pure-managed
with no hardware H.264 — so adopting it means building and binding libwebrtc for
both Swift and C#, then shipping it in both installers.

And most of what its transport buys you is for the internet: NAT traversal,
loss recovery, bandwidth estimation across a hostile path. On a LAN those
problems are largely absent. The transport we have is built, tested, and
byte-identical across platforms.

The one place the argument genuinely favours WebRTC is congestion control, which
is why Phase 5 keeps WS10 deliberately small rather than attempting a real one.

---

## Immediate next step

**Phase 1.** Everything it needs exists and is tested, it needs no hardware
anybody has to find, and it ends with something you can look at.
