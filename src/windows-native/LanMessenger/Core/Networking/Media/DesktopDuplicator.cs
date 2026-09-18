using LanMessenger.Core.Services;
using SharpGen.Runtime;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LanMessenger.Core.Networking.Media;

// Windows screen capture, via DXGI Desktop Duplication.
//
// The counterpart of ScreenCaptureSource.swift, and it shares that file's most
// important property: **it is change-driven, not 30 fps**. DXGI_ERROR_WAIT_TIMEOUT
// means nothing on screen changed. It is not an error, it is not a dropped
// frame, and it is not a reason to emit anything. A screen nobody is touching
// produces no frames indefinitely, and every layer above has to treat that as
// normal — which is why the keepalive and stats sub-channels exist.
//
// Three DXGI failures need distinct handling and look alike if you do not know
// them apart:
//
//   WAIT_TIMEOUT   (0x887A0027) nothing changed. Normal. Try again.
//   ACCESS_LOST    (0x887A0026) desktop switch or mode change. Full
//                               re-enumeration, not just a re-acquire.
//   ACCESS_DENIED  (0x887A002B) the secure desktop is up — a UAC prompt or the
//                               lock screen. Persists. Back off and retry; this
//                               is also what host_state reports to the viewer.
//   UNSUPPORTED    (0x887A0004) the device was created on the wrong adapter for
//                               this output. CaptureTargetSelector exists to
//                               make this unreachable.
//
// The pointer is the other asymmetry with macOS. ScreenCaptureKit composites the
// cursor into the frame when asked; Desktop Duplication excludes it and hands
// the shape over separately — and only when it *changes*, which the WS0 probe
// showed as pointerShapeBytes=0 on the first acquire. A loop that reads the
// shape per frame draws nothing on most frames, so the last one has to be cached.

public sealed class DesktopCaptureException(string message) : Exception(message);

/// One captured frame, already converted to the NV12 the encoder wants.
public sealed class CapturedFrame
{
    public required byte[] Nv12 { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Stride { get; init; }
    public required ulong CaptureUs { get; init; }
}

public sealed class DesktopDuplicator : IDisposable
{
    private const int DXGI_ERROR_WAIT_TIMEOUT  = unchecked((int)0x887A0027);
    private const int DXGI_ERROR_ACCESS_LOST   = unchecked((int)0x887A0026);
    private const int DXGI_ERROR_ACCESS_DENIED = unchecked((int)0x887A002B);
    private const int DXGI_ERROR_UNSUPPORTED   = unchecked((int)0x887A0004);

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _staging;
    private ColorConverter? _converter;

    private readonly string? _preferredDisplay;
    private CaptureTarget? _target;
    private int _width, _height;

    /// True while the secure desktop is up. Surfaced to the viewer as
    /// host_state rather than left as a frozen picture with no explanation.
    public bool SecureDesktopActive { get; private set; }

    public int Width => _width;
    public int Height => _height;
    public string DisplayName => _target?.Output.DeviceName ?? "";

    public DesktopDuplicator(string? preferredDisplay = null)
    {
        _preferredDisplay = preferredDisplay;
        Start();
    }

    /// Builds the device and duplication. Also the recovery path: ACCESS_LOST
    /// requires full re-enumeration because the output itself may be gone.
    private void Start()
    {
        Release();

        var adapters = EnumerateAdapters();
        var result = CaptureTargetSelector.Select(adapters, _preferredDisplay);
        if (!result.Success)
        {
            throw new DesktopCaptureException($"nothing to capture: {result.Failure}");
        }

        var target = result.Target!.Value;
        _target = target;

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        if (factory.EnumAdapters1((uint)target.Adapter.Index, out IDXGIAdapter1 adapter).Failure)
        {
            throw new DesktopCaptureException($"adapter {target.Adapter.Index} vanished");
        }

        using (adapter)
        {
            // The device MUST be created on the adapter that owns this output.
            // Creating on adapter 0 regardless is what fails with
            // DXGI_ERROR_UNSUPPORTED on every hybrid-graphics laptop.
            var hr = D3D11.D3D11CreateDevice(
                adapter, DriverType.Unknown,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
                out ID3D11Device? device);

            if (hr.Failure || device is null)
            {
                throw new DesktopCaptureException($"D3D11CreateDevice 0x{hr.Code:X8}");
            }
            _device = device;
            _context = device.ImmediateContext;

            // Media Foundation calls in from its own threads. Without this the
            // driver crashes sporadically, with no useful diagnostic.
            using (var multithread = device.QueryInterfaceOrNull<ID3D11Multithread>())
            {
                multithread?.SetMultithreadProtected(true);
            }

            IDXGIOutput? output = null;
            for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput candidate).Success; o++)
            {
                if (candidate.Description.DeviceName == target.Output.DeviceName)
                {
                    output = candidate;
                    break;
                }
                candidate.Dispose();
            }
            if (output is null) throw new DesktopCaptureException("the output vanished");

            using (output)
            using (var output1 = output.QueryInterface<IDXGIOutput1>())
            {
                _duplication = output1.DuplicateOutput(device);
            }

            var bounds = target.Output;
            _width = bounds.Width;
            _height = bounds.Height;
        }

        _converter = new ColorConverter(_device!, _context!, _width, _height);
        LanLogger.Remote("capture_started",
            reason: $"{DisplayName} {_width}x{_height} adapter={target.Adapter.Description}");
    }

    private static List<GpuAdapterInfo> EnumerateAdapters()
    {
        var adapters = new List<GpuAdapterInfo>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
        {
            using (adapter)
            {
                var desc = adapter.Description1;
                var outputs = new List<DisplayOutputInfo>();
                for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
                {
                    using (output)
                    {
                        var od = output.Description;
                        outputs.Add(new DisplayOutputInfo(
                            (int)o, od.DeviceName,
                            od.DesktopCoordinates.Right - od.DesktopCoordinates.Left,
                            od.DesktopCoordinates.Bottom - od.DesktopCoordinates.Top,
                            od.AttachedToDesktop));
                    }
                }
                adapters.Add(new GpuAdapterInfo((int)a, desc.Description?.Trim() ?? "",
                                                (uint)desc.VendorId,
                                                (long)desc.DedicatedVideoMemory, outputs));
            }
        }
        return adapters;
    }

    /// Everything a capturable display list needs, for the display_list control
    /// message.
    public static List<RemoteDisplayInfo> EnumerateDisplays()
    {
        var capturable = CaptureTargetSelector.EnumerateCapturable(EnumerateAdapters());
        var displays = new List<RemoteDisplayInfo>();
        foreach (var d in capturable)
        {
            displays.Add(new RemoteDisplayInfo
            {
                DisplayId = (uint)d.Index, Width = d.Width, Height = d.Height,
                IsPrimary = displays.Count == 0, Name = d.DeviceName,
            });
        }
        return displays;
    }

    /// Tries to acquire one frame.
    ///
    /// Returns null when nothing changed, which is the ordinary case and must
    /// not be treated as failure. `timeoutMs` bounds the wait so the caller's
    /// loop stays responsive to a stop request.
    /// Desktop frames the compositor produced since we last asked. Anything
    /// above 1 is screen updates we are not sending — the cost of pacing.
    public uint AccumulatedFrames { get; private set; }

    /// How old the last frame already was when we collected it, in
    /// microseconds. Pure waiting: it happened before we touched the frame.
    public ulong FrameAgeUs { get; private set; }

    /// How long the last frame spent in colour conversion, in microseconds.
    /// Read by the capture loop for its stats line: this is the single most
    /// expensive step per frame and the first place to look when the loop
    /// cannot make its slot.
    public ulong ConvertUs { get; private set; }

    private static ulong NowUs() =>
        (ulong)(Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1_000_000L));

    public CapturedFrame? TryCapture(int timeoutMs = 100)
    {
        var duplication = _duplication;
        if (duplication is null) return null;

        IDXGIResource? resource = null;
        try
        {
            var hr = duplication.AcquireNextFrame((uint)timeoutMs, out var info, out resource);

            if (hr.Code == DXGI_ERROR_WAIT_TIMEOUT)
            {
                // Nothing changed. Not an error, not a dropped frame.
                return null;
            }
            if (hr.Code == DXGI_ERROR_ACCESS_LOST)
            {
                LanLogger.Remote("capture_stopped", reason: "ACCESS_LOST — re-enumerating");
                Start();
                return null;
            }
            if (hr.Code == DXGI_ERROR_ACCESS_DENIED)
            {
                // The secure desktop: a UAC prompt or the lock screen. A
                // user-mode process cannot capture it, and saying so is the
                // whole point of host_state.
                if (!SecureDesktopActive)
                {
                    SecureDesktopActive = true;
                    LanLogger.Remote("host_state", reason: "secure_desktop");
                }
                return null;
            }
            if (hr.Code == DXGI_ERROR_UNSUPPORTED)
            {
                throw new DesktopCaptureException(
                    "DXGI_ERROR_UNSUPPORTED — the device is on the wrong adapter for this output");
            }
            if (hr.Failure || resource is null) return null;

            SecureDesktopActive = false;

            // The compositor's own timestamp for this desktop frame, not the
            // moment we got round to collecting it.
            //
            // AcquireNextFrame returns the newest frame, which may already be
            // most of an interval old — we only ask 30 times a second, and the
            // desktop composes faster than that. Stamping "now" hides that age,
            // and it is real latency: the user is comparing against when the
            // screen actually changed, which is what LastPresentTime records.
            //
            // It is a QPC value, the same clock and units Stopwatch uses here,
            // so no conversion beyond the divide. Zero means nothing was
            // presented — a mouse-only update — and then "now" is the honest
            // answer rather than 1601.
            ulong captureUs = info.LastPresentTime > 0
                ? (ulong)(info.LastPresentTime / (Stopwatch.Frequency / 1_000_000L))
                : NowUs();
            AccumulatedFrames = info.AccumulatedFrames;
            FrameAgeUs = NowUs() - captureUs;

            using var texture = resource.QueryInterface<ID3D11Texture2D>();
            var nv12 = _converter!.Convert(texture, out int stride);
            if (nv12 is null) return null;
            ConvertUs = NowUs() - captureUs;

            return new CapturedFrame
            {
                Nv12 = nv12, Width = _width, Height = _height, Stride = stride,
                // The host clock in microseconds, carried verbatim into the
                // media frame header. Taken at acquire rather than at delivery:
                // the difference is the time the frame spent being converted,
                // which is exactly the latency the measurement exists to catch.
                CaptureUs = captureUs,
            };
        }
        catch (SharpGenException ex)
        {
            LanLogger.Remote("error", reason: $"AcquireNextFrame 0x{ex.ResultCode.Code:X8}");
            return null;
        }
        finally
        {
            resource?.Dispose();
            try { duplication.ReleaseFrame(); } catch { /* already released */ }
        }
    }

    private void Release()
    {
        _converter?.Dispose(); _converter = null;
        _staging?.Dispose(); _staging = null;
        _duplication?.Dispose(); _duplication = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
    }

    public void Dispose() => Release();
}
