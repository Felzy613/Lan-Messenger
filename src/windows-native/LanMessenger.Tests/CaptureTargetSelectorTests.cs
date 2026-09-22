using LanMessenger.Core.Networking.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanMessenger.Tests;

// Which GPU to capture from, tested against machines we do not own.
//
// The WS0 probe proved Desktop Duplication works on one Intel UHD 730 with one
// attached display. That machine cannot tell us anything about an Optimus
// laptop, an AMD box, a Windows N SKU with no H.264 MFTs, or two monitors on two
// adapters — and buying five GPUs is not an option. Describing five topologies
// is.
//
// So the decisions are pure and the topologies are data. Real hardware only has
// to prove the D3D and Media Foundation plumbing works once the right objects
// have been chosen.
[TestClass]
public class CaptureTargetSelectorTests
{
    private static DisplayOutputInfo Display(int i, string name, bool attached = true) =>
        new(i, name, 1920, 1080, attached);

    private static GpuAdapterInfo Gpu(int index, string name, uint vendor,
                                      params DisplayOutputInfo[] outputs) =>
        new(index, name, vendor, 2L * 1024 * 1024 * 1024, outputs);

    private const uint Intel = 0x8086;
    private const uint Nvidia = 0x10DE;
    private const uint Amd = 0x1002;
    private const uint Microsoft = 0x1414;

    private static GpuAdapterInfo BasicRenderDriver =>
        new(99, "Microsoft Basic Render Driver", Microsoft, 0, []);

    // ---- The machine we actually have --------------------------------------

    [TestMethod]
    public void TheDellsThreeAdapterShapeResolvesToTheOneThatOwnsTheDisplay()
    {
        // Observed 2026-09-17, and not predicted by the plan: this machine
        // enumerates the same UHD 730 twice, and only the first copy owns the
        // display. Taking "the first adapter with outputs" is right; taking
        // "adapter 0 and hoping" happens to work here and would not elsewhere.
        var adapters = new List<GpuAdapterInfo>
        {
            Gpu(0, "Intel(R) UHD Graphics 730", Intel, Display(0, @"\\.\DISPLAY22")),
            Gpu(1, "Intel(R) UHD Graphics 730", Intel),
            BasicRenderDriver,
        };

        var result = CaptureTargetSelector.Select(adapters);
        Assert.IsTrue(result.Success);
        Assert.AreEqual(0, result.Target!.Value.Adapter.Index);
        Assert.AreEqual(@"\\.\DISPLAY22", result.Target.Value.Output.DeviceName);
    }

    // ---- Machines we do not have -------------------------------------------

    [TestMethod]
    public void AnOptimusLaptopCapturesTheIntegratedGpuNotTheDiscreteOne()
    {
        // The case the plan warns about and the most common laptop in the world.
        // The discrete GPU enumerates first and owns nothing; the panel hangs off
        // the integrated one. Duplicating adapter 0 here fails with
        // DXGI_ERROR_UNSUPPORTED, which is not a diagnosable error message.
        var adapters = new List<GpuAdapterInfo>
        {
            Gpu(0, "NVIDIA GeForce RTX 4060 Laptop GPU", Nvidia),
            Gpu(1, "Intel(R) Iris(R) Xe Graphics", Intel, Display(0, @"\\.\DISPLAY1")),
            BasicRenderDriver,
        };

        var result = CaptureTargetSelector.Select(adapters);
        Assert.IsTrue(result.Success);
        Assert.AreEqual(Intel, result.Target!.Value.Adapter.VendorId,
                        "captured the discrete GPU, which owns no display");
    }

    [TestMethod]
    public void TwoMonitorsOnTwoAdaptersEachResolveToTheirOwnAdapter()
    {
        // A desktop with the monitor plugged into the motherboard and a second
        // into the graphics card. Each output must be captured on its owner.
        var adapters = new List<GpuAdapterInfo>
        {
            Gpu(0, "AMD Radeon RX 7600", Amd, Display(0, @"\\.\DISPLAY2")),
            Gpu(1, "AMD Radeon(TM) Graphics", Amd, Display(0, @"\\.\DISPLAY1")),
        };

        var first = CaptureTargetSelector.Select(adapters, @"\\.\DISPLAY1");
        Assert.AreEqual(1, first.Target!.Value.Adapter.Index);

        var second = CaptureTargetSelector.Select(adapters, @"\\.\DISPLAY2");
        Assert.AreEqual(0, second.Target!.Value.Adapter.Index);
    }

    [TestMethod]
    public void TheSoftwareAdapterIsNeverChosenWhileRealSiliconExists()
    {
        // The Basic Render Driver is on essentially every machine. If it ever
        // reported an attached output, duplicating it would be meaningless.
        var adapters = new List<GpuAdapterInfo>
        {
            new(0, "Microsoft Basic Render Driver", Microsoft, 0, [Display(0, @"\\.\DISPLAYX")]),
            Gpu(1, "Intel(R) UHD Graphics", Intel, Display(0, @"\\.\DISPLAY1")),
        };

        var result = CaptureTargetSelector.Select(adapters);
        Assert.AreEqual(Intel, result.Target!.Value.Adapter.VendorId);
    }

    [TestMethod]
    public void AHeadlessMachineFailsClearlyRatherThanPickingNothing()
    {
        // A server, or an RDP session whose virtual display has gone. The
        // failure has to be nameable so the invite can be declined with a reason
        // instead of timing out.
        var adapters = new List<GpuAdapterInfo> { Gpu(0, "Intel(R) UHD Graphics", Intel), BasicRenderDriver };

        var result = CaptureTargetSelector.Select(adapters);
        Assert.IsFalse(result.Success);
        Assert.AreEqual(CaptureTargetFailure.NoAttachedOutput, result.Failure);
    }

    [TestMethod]
    public void NoAdaptersAtAllIsItsOwnFailure()
    {
        Assert.AreEqual(CaptureTargetFailure.NoAdapters,
                        CaptureTargetSelector.Select([]).Failure);
        Assert.AreEqual(CaptureTargetFailure.NoAdapters,
                        CaptureTargetSelector.Select(null!).Failure);
    }

    [TestMethod]
    public void ADetachedOutputIsNotCapturable()
    {
        // A monitor that is present but asleep or disabled.
        var adapters = new List<GpuAdapterInfo>
        {
            Gpu(0, "Intel(R) UHD Graphics", Intel, Display(0, @"\\.\DISPLAY1", attached: false)),
        };
        Assert.AreEqual(CaptureTargetFailure.NoAttachedOutput,
                        CaptureTargetSelector.Select(adapters).Failure);
    }

    [TestMethod]
    public void AMonitorUnpluggedMidSessionIsAnErrorNotASilentSwitch()
    {
        // Falling back to a different display would show the viewer somebody's
        // other screen without telling them, which is worse than an error.
        var adapters = new List<GpuAdapterInfo>
        {
            Gpu(0, "Intel(R) UHD Graphics", Intel, Display(0, @"\\.\DISPLAY1")),
        };

        var result = CaptureTargetSelector.Select(adapters, @"\\.\DISPLAY9");
        Assert.IsFalse(result.Success);
        Assert.AreEqual(CaptureTargetFailure.RequestedDisplayGone, result.Failure);
    }

    [TestMethod]
    public void TheDisplayListOffersOnlyWhatCanActuallyBeCaptured()
    {
        var adapters = new List<GpuAdapterInfo>
        {
            Gpu(0, "NVIDIA GeForce RTX 4060", Nvidia, Display(0, @"\\.\DISPLAY2")),
            Gpu(1, "Intel(R) Iris(R) Xe Graphics", Intel,
                Display(0, @"\\.\DISPLAY1"), Display(1, @"\\.\DISPLAY3", attached: false)),
            BasicRenderDriver,
        };

        var displays = CaptureTargetSelector.EnumerateCapturable(adapters);
        CollectionAssert.AreEquivalent(
            new List<string> { @"\\.\DISPLAY2", @"\\.\DISPLAY1" },
            displays.ConvertAll(d => d.DeviceName),
            "offering a display that cannot be duplicated is offering a failure");
    }

    // ---- Encoders ----------------------------------------------------------

    [TestMethod]
    public void AWindowsNSkuHasNoEncoderAndMustSaySo()
    {
        // N and KN SKUs ship without the media feature pack: MFTEnumEx returns
        // nothing and the video path is dead. Detected at invite time and
        // declined with `no_encoder` rather than failing mid-handshake.
        var choice = EncoderSelector.Select([]);
        Assert.AreEqual(EncoderKind.None, choice.Kind);
        Assert.IsFalse(choice.IsUsable);
    }

    [TestMethod]
    public void HardwareIsPreferredAndNeedsTheAsyncPump()
    {
        // Both Quick Sync MFTs on the Dell are async, and async MFTs refuse
        // ProcessInput with MF_E_TRANSFORM_ASYNC_LOCKED until unlocked.
        var choice = EncoderSelector.Select(
        [
            new EncoderMftInfo("H264 Encoder MFT", IsHardware: false, IsAsync: false, SupportsCodecApi: true),
            new EncoderMftInfo("Intel® Quick Sync Video H.264 Encoder MFT", true, true, true),
        ]);

        Assert.AreEqual(EncoderKind.HardwareAsync, choice.Kind);
        Assert.IsTrue(choice.NeedsAsyncPump);
    }

    [TestMethod]
    public void ASoftwareOnlyMachineStillWorks()
    {
        // Not a nicety. It is the path on a machine with no hardware encoder,
        // and a working reference while debugging a hardware path that emits
        // nothing.
        var choice = EncoderSelector.Select(
            [new EncoderMftInfo("H264 Encoder MFT", false, false, true)]);

        Assert.AreEqual(EncoderKind.SoftwareSync, choice.Kind);
        Assert.IsTrue(choice.IsUsable);
        Assert.IsFalse(choice.NeedsAsyncPump);
    }

    [TestMethod]
    public void TheSoftwareFallbackCanBeForced()
    {
        // Some drivers enumerate a hardware MFT that then fails to produce a
        // frame in an RDP session. Falling back has to be possible at runtime,
        // not only at enumeration.
        var mfts = new List<EncoderMftInfo>
        {
            new("Intel® Quick Sync Video H.264 Encoder MFT", true, true, true),
            new("H264 Encoder MFT", false, false, true),
        };

        Assert.AreEqual(EncoderKind.HardwareAsync, EncoderSelector.Select(mfts).Kind);
        Assert.AreEqual(EncoderKind.SoftwareSync, EncoderSelector.SelectSoftware(mfts).Kind);
        Assert.AreEqual(EncoderKind.None,
            EncoderSelector.SelectSoftware(
                [new EncoderMftInfo("Quick Sync", true, true, true)]).Kind,
            "a hardware-only machine has no software path to fall back to");
    }

    [TestMethod]
    public void ASynchronousHardwareEncoderNeedsNoPump()
    {
        // Not every hardware MFT is async — some AMD and older Intel drivers
        // expose a sync one, and running the event pump against it hangs.
        var choice = EncoderSelector.Select(
            [new EncoderMftInfo("AMD H.264 Hardware MFT Encoder", true, false, true)]);

        Assert.AreEqual(EncoderKind.HardwareSync, choice.Kind);
        Assert.IsFalse(choice.NeedsAsyncPump);
    }
}
