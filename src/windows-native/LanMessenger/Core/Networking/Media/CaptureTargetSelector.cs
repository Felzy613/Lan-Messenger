namespace LanMessenger.Core.Networking.Media;

// Which GPU to capture from, and which encoder to hand the frames to.
//
// This is deliberately pure. The probe proved Desktop Duplication works on one
// machine — an Intel UHD 730 with a single attached output — and that machine
// cannot tell us anything about an Optimus laptop, an AMD box, a Windows N SKU
// with no H.264 MFTs at all, or two monitors hanging off two different adapters.
// Buying five GPUs is not an option; describing five topologies is.
//
// So every decision lives here as a function of data, and the only thing real
// hardware has to prove is that the D3D and Media Foundation plumbing works once
// the right objects have been chosen. The topologies below are all asserted in
// CaptureTargetSelectorTests, including the three-adapter shape the Dell turned
// out to have, which nothing in the plan predicted.

/// One display attached to an adapter, as DXGI reports it.
public readonly record struct DisplayOutputInfo(
    int Index, string DeviceName, int Width, int Height, bool IsAttached);

/// One DXGI adapter and the outputs it owns.
public readonly record struct GpuAdapterInfo(
    int Index,
    string Description,
    uint VendorId,
    long DedicatedVideoMemory,
    IReadOnlyList<DisplayOutputInfo> Outputs)
{
    /// Microsoft Basic Render Driver — the WARP software adapter. It is present
    /// on essentially every machine, owns no outputs, and duplicating it is
    /// meaningless. It is identified by vendor id rather than by description,
    /// because the description is localised.
    public const uint MicrosoftVendorId = 0x1414;

    public bool IsSoftwareAdapter => VendorId == MicrosoftVendorId;

    public bool HasAttachedOutput
    {
        get
        {
            foreach (var output in Outputs) { if (output.IsAttached) return true; }
            return false;
        }
    }
}

/// The adapter/output pair a capture session should be built on.
public readonly record struct CaptureTarget(
    GpuAdapterInfo Adapter, DisplayOutputInfo Output);

public enum CaptureTargetFailure
{
    None,
    /// No adapters at all. A machine with no DXGI is not one we can capture.
    NoAdapters,
    /// Adapters exist but none owns an attached display — a headless server, or
    /// an RDP session that has disconnected its virtual display.
    NoAttachedOutput,
    /// The caller named a display that is not present any more. A monitor
    /// unplugged mid-session lands here.
    RequestedDisplayGone,
}

public readonly record struct CaptureTargetResult(
    CaptureTarget? Target, CaptureTargetFailure Failure)
{
    public bool Success => Target is not null;
    public static CaptureTargetResult Ok(CaptureTarget target) => new(target, CaptureTargetFailure.None);
    public static CaptureTargetResult Fail(CaptureTargetFailure why) => new(null, why);
}

public static class CaptureTargetSelector
{
    /// Chooses the adapter and output to capture.
    ///
    /// The rule that matters: **the device must be created on the adapter that
    /// owns the output**, never on "adapter 0". On an Optimus laptop adapter 0
    /// is the discrete GPU, the panel hangs off the integrated one, and
    /// duplicating the wrong adapter fails with DXGI_ERROR_UNSUPPORTED. The Dell
    /// makes the same point differently: it enumerates the same UHD 730 twice
    /// and only the first copy owns the display.
    ///
    /// <param name="preferredDeviceName">A `\\.\DISPLAYn` the user picked, or
    /// null for "whatever is sensible". A name that is no longer present is an
    /// explicit failure rather than a silent fallback — a viewer being switched
    /// to a different monitor without being told is worse than an error.</param>
    public static CaptureTargetResult Select(
        IReadOnlyList<GpuAdapterInfo> adapters, string? preferredDeviceName = null)
    {
        if (adapters is null || adapters.Count == 0)
        {
            return CaptureTargetResult.Fail(CaptureTargetFailure.NoAdapters);
        }

        if (!string.IsNullOrEmpty(preferredDeviceName))
        {
            foreach (var adapter in adapters)
            {
                foreach (var output in adapter.Outputs)
                {
                    if (output.IsAttached && output.DeviceName == preferredDeviceName)
                    {
                        return CaptureTargetResult.Ok(new CaptureTarget(adapter, output));
                    }
                }
            }
            return CaptureTargetResult.Fail(CaptureTargetFailure.RequestedDisplayGone);
        }

        // Hardware adapters first. The Basic Render Driver owns no outputs in
        // practice, but preferring real silicon means we never depend on that.
        var best = FirstAttached(adapters, softwareAllowed: false);
        best ??= FirstAttached(adapters, softwareAllowed: true);

        return best is { } target
            ? CaptureTargetResult.Ok(target)
            : CaptureTargetResult.Fail(CaptureTargetFailure.NoAttachedOutput);
    }

    private static CaptureTarget? FirstAttached(
        IReadOnlyList<GpuAdapterInfo> adapters, bool softwareAllowed)
    {
        foreach (var adapter in adapters)
        {
            if (!softwareAllowed && adapter.IsSoftwareAdapter) continue;
            foreach (var output in adapter.Outputs)
            {
                if (output.IsAttached) return new CaptureTarget(adapter, output);
            }
        }
        return null;
    }

    /// Every capturable display, for the `display_list` control message.
    /// Software adapters are excluded: offering a viewer a display that cannot
    /// be duplicated is offering them a failure.
    public static List<DisplayOutputInfo> EnumerateCapturable(
        IReadOnlyList<GpuAdapterInfo> adapters)
    {
        var displays = new List<DisplayOutputInfo>();
        if (adapters is null) return displays;
        foreach (var adapter in adapters)
        {
            if (adapter.IsSoftwareAdapter) continue;
            foreach (var output in adapter.Outputs)
            {
                if (output.IsAttached) displays.Add(output);
            }
        }
        return displays;
    }
}

// ---------------------------------------------------------------------------

/// One H.264 encoder MFT as MFTEnumEx reports it.
public readonly record struct EncoderMftInfo(
    string Name, bool IsHardware, bool IsAsync, bool SupportsCodecApi);

public enum EncoderKind
{
    /// No H.264 encoder at all. Windows N and KN SKUs ship without the media
    /// feature pack, so MFTEnumEx returns nothing and the video path is dead.
    /// Detected at invite time and declined with the `no_encoder` token rather
    /// than failing halfway through a handshake.
    None,
    HardwareAsync,
    HardwareSync,
    SoftwareSync,
}

public readonly record struct EncoderChoice(EncoderKind Kind, EncoderMftInfo? Mft)
{
    public bool IsUsable => Kind != EncoderKind.None;

    /// Whether the async METransformNeedInput / METransformHaveOutput pump is
    /// required. Hardware MFTs refuse ProcessInput with
    /// MF_E_TRANSFORM_ASYNC_LOCKED until unlocked, and then need the event pump.
    public bool NeedsAsyncPump => Kind == EncoderKind.HardwareAsync;
}

public static class EncoderSelector
{
    /// Picks an encoder, preferring hardware but never requiring it.
    ///
    /// The software fallback is not a nicety. Windows N and KN SKUs have no
    /// H.264 MFTs at all; some machines have hardware MFTs that refuse to
    /// initialise in an RDP session; and a software encoder is a working
    /// reference while debugging a hardware path that is emitting nothing.
    public static EncoderChoice Select(IReadOnlyList<EncoderMftInfo> mfts)
    {
        if (mfts is null || mfts.Count == 0) return new EncoderChoice(EncoderKind.None, null);

        foreach (var mft in mfts)
        {
            if (mft.IsHardware && mft.IsAsync) return new EncoderChoice(EncoderKind.HardwareAsync, mft);
        }
        foreach (var mft in mfts)
        {
            if (mft.IsHardware) return new EncoderChoice(EncoderKind.HardwareSync, mft);
        }
        return new EncoderChoice(EncoderKind.SoftwareSync, mfts[0]);
    }

    /// Forces the software path. Used by the diagnostic report and as the
    /// recovery when a hardware MFT enumerates but then fails to produce a
    /// frame — which is a real failure mode on some drivers in RDP sessions.
    public static EncoderChoice SelectSoftware(IReadOnlyList<EncoderMftInfo> mfts)
    {
        if (mfts is null) return new EncoderChoice(EncoderKind.None, null);
        foreach (var mft in mfts)
        {
            if (!mft.IsHardware) return new EncoderChoice(EncoderKind.SoftwareSync, mft);
        }
        return new EncoderChoice(EncoderKind.None, null);
    }
}
