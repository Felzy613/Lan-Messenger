# Spikes

Throwaway diagnostics. **Not part of the shipping app** and not referenced by
`LanMessenger.sln`. Delete a spike once the question it answers is settled and
the answer is written down in the plan or in `docs/`.

## `windows-mf-probe`

WS0 of the remote-desktop work. Answers, on a real Windows machine, the four
things that cannot be answered from a Mac:

1. Which H.264 encoder MFTs exist (hardware / software sync / software async).
2. Whether `ICodecAPI` is reachable on them — this decides whether the
   low-latency and rate-control settings are usable at all.
3. Whether DXGI Desktop Duplication acquires on each adapter/output.
4. Whether NV12 frames actually encode to a playable Annex-B `.h264`.

### Already settled without Windows

`Vortice.MediaFoundation` 3.6.2 was inspected by reflection from the Mac:

| Needed | Present |
|---|---|
| `MediaFactory.MFTEnumEx`, `MFStartup` | yes |
| `IMFTransform` (`ProcessInput`/`Output`/`Message`/`Event`) | yes |
| `IMFMediaEventGenerator` (async MFT pattern) | yes |
| `IMFDXGIDeviceManager`, `IMFDXGIBuffer` | yes |
| `IMFActivate`, `IMFAttributes`, `IMFSample`, `IMFMediaBuffer`, `IMFMediaType` | yes |
| **`ICodecAPI`** | **no** |

So the plan's fallback position — abandon Vortice for CsWin32 — is not needed.
Use **Vortice for the MFT and D3D surface, plus a hand-rolled `ICodecAPI`**
(one COM interface, or a CsWin32-generated one if you prefer). That is the only
gap, and stage 2 of the probe confirms whether `QueryInterface` for it actually
succeeds on this machine's encoders.

### Running it

```powershell
cd spikes\windows-mf-probe
dotnet run
```

Options: `--frames=120 --width=1280 --height=720`.

It always exits 0 — it is a report, not a gate. Read the output; "all stages
ran" is not the same as "all stages are healthy".

Verify the encoded file with `ffplay out.h264`, or open it in VLC.

### Where to run it, and what each environment can prove

**A physical x64 PC with a real GPU** is the only environment that answers
everything. Reach it with TeamViewer or any tool that attaches to the *physical
console session*.

Do **not** use Microsoft RDP for this. RDP creates a new virtual session with
its own display driver: the physical GPU's output is not present, Desktop
Duplication describes the RDP virtual display rather than the real one, and some
GPU drivers disable their hardware encoder in RDP sessions entirely. You would
be measuring RDP, not the machine. TeamViewer keeps the console session, so the
probe sees the real adapter and the real encoder.

**A VMware Fusion VM on Apple Silicon** runs Windows 11 **ARM64** and has no
hardware video encoder. It can still do real work:

- build the WinUI app with real MSBuild, and run the real MSTest suite — neither
  has ever been done for the remote-desktop code, all of which was written and
  cross-compiled from a Mac;
- validate the **software** MFT encode path, which the plan wants built first
  anyway as the always-available fallback for Windows N/KN, VMs and old hardware;
- exercise Desktop Duplication against the virtual display adapter.

It cannot validate the hardware encoder path or produce meaningful latency
numbers. Expect stage 2 to report `HARDWARE 0 transform(s)`; that is the VM
being a VM, not a bug.

The app is x64-only, so on ARM64 Windows the build cross-compiles and the result
runs under emulation. The probe is AnyCPU so it runs natively and reports the
OS's own capability; add `-r win-x64` to probe the emulated path instead. It
prints both architectures at startup so the output is self-describing.

### While you are on a Windows machine

The highest-value thing there is not the probe — it is proving the Windows half
of the app actually builds and passes its tests with real tooling:

```powershell
cd src\windows-native
msbuild /t:Restore /p:Configuration=Release /p:Platform=x64 LanMessenger.sln
msbuild LanMessenger.Tests\LanMessenger.Tests.csproj /p:Configuration=Release /p:Platform=x64
$testDll = Get-ChildItem LanMessenger.Tests\bin -Filter LanMessenger.Tests.dll -Recurse | Select-Object -First 1
dotnet vstest $testDll.FullName --logger:"console;verbosity=normal"
```

One known result: `PacketValidatorTests.SanitizeFilenameStripsPath` **fails on
macOS and passes on Windows**, because `SanitizeFilename` uses
`Path.DirectorySeparatorChar`. On Windows it should pass. If it does not, that
is a real regression.
