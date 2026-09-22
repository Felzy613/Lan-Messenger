using LanMessenger.Core.Networking.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Tasks;

namespace LanMessenger.Tests;

/// <summary>
/// The protocol's two-frame rule, which PROTOCOL.md states once and five
/// independent places have to keep. Mirror of VideoFrameBudgetTests.swift.
/// </summary>
[TestClass]
public class VideoFrameBudgetTests
{
    [TestMethod]
    public void TheThirdFrameIsRefused()
    {
        // One being worked on, one waiting. Fixed by the protocol, not tuned.
        var budget = new VideoFrameBudget();
        Assert.IsTrue(budget.TryAcquire());
        Assert.IsTrue(budget.TryAcquire());
        Assert.IsFalse(budget.TryAcquire());
        Assert.AreEqual(2, budget.InFlight);
        Assert.AreEqual(1, budget.Refused);
    }

    [TestMethod]
    public void ARefusalIsCountedRatherThanSwallowed()
    {
        // A budget that is constantly full is a real signal about the encoder.
        // Dropping silently is how that signal is lost — and "it feels laggy"
        // becomes the only available measurement again.
        var budget = new VideoFrameBudget();
        budget.TryAcquire(); budget.TryAcquire();
        for (int i = 0; i < 5; i++) Assert.IsFalse(budget.TryAcquire());
        Assert.AreEqual(5, budget.Refused);
        StringAssert.Contains(budget.Summary(), "refused=5");
    }

    [TestMethod]
    public void AReleasedSlotComesBack()
    {
        var budget = new VideoFrameBudget();
        budget.TryAcquire(); budget.TryAcquire();
        budget.Release();
        Assert.IsTrue(budget.TryAcquire());
        Assert.AreEqual(2, budget.InFlight);
    }

    [TestMethod]
    public void ReleasingMoreThanWasTakenCannotWidenTheBudget()
    {
        // An encoder that emits two outputs for one input would otherwise drive
        // the count negative, and the budget would stop bounding anything at
        // all — a slow drift back into unbounded latency with nothing to see.
        var budget = new VideoFrameBudget();
        budget.TryAcquire();
        budget.Release();
        budget.Release();
        budget.Release();
        Assert.AreEqual(0, budget.InFlight);

        Assert.IsTrue(budget.TryAcquire());
        Assert.IsTrue(budget.TryAcquire());
        Assert.IsFalse(budget.TryAcquire(), "the cap held after an over-release");
    }

    [TestMethod]
    public void ResetForgetsWhatWillNeverComeBack()
    {
        // Flush, drain-complete and teardown. A slot leaked across one of those
        // shrinks the budget for the rest of the session, and after two the
        // encoder accepts nothing with no error anywhere to explain it.
        var budget = new VideoFrameBudget();
        budget.TryAcquire(); budget.TryAcquire();
        budget.Reset();
        Assert.AreEqual(0, budget.InFlight);
        Assert.IsTrue(budget.TryAcquire());
    }

    [TestMethod]
    public void TheCapacityIsTheProtocolsNumber()
    {
        // Not a tuning knob. PROTOCOL.md fixes it, and both platforms say 2.
        Assert.AreEqual(2, VideoFrameBudget.ProtocolCapacity);
        Assert.AreEqual(2, new VideoFrameBudget().Capacity);
    }

    [TestMethod]
    public void ACodecPipelineGetsARunawayGuardRatherThanTheQueueRule()
    {
        // The mistake this encodes: two is right for a queue and wrong for a
        // hardware transform. Quick Sync issues one METransformNeedInput per
        // pipeline slot and emits nothing until enough are filled — capped at
        // two it produced no video at all, and not one encoder_stats line to
        // say why. A pipeline's depth is fixed latency, not growth.
        Assert.IsTrue(VideoFrameBudget.PipelineCapacity > VideoFrameBudget.ProtocolCapacity);

        var pipeline = new VideoFrameBudget(VideoFrameBudget.PipelineCapacity);
        for (int i = 0; i < VideoFrameBudget.PipelineCapacity; i++)
            Assert.IsTrue(pipeline.TryAcquire(), "a pipeline must not be starved");
        Assert.IsFalse(pipeline.TryAcquire(), "but a runaway is still caught");
    }

    [TestMethod]
    public void ThePeakIsReportedBecauseNothingElseCanSeeIt()
    {
        // The codec's own depth is latency no other measurement reaches, and
        // exposing it is the point of the encoder's budget now that it is not
        // pacing anything.
        var budget = new VideoFrameBudget(8);
        for (int i = 0; i < 5; i++) budget.TryAcquire();
        for (int i = 0; i < 5; i++) budget.Release();

        Assert.AreEqual(0, budget.InFlight);
        Assert.AreEqual(5, budget.HighWater);
        StringAssert.Contains(budget.Summary(), "peak=5");
    }

    [TestMethod]
    public void ConcurrentAcquiresNeverExceedTheCap()
    {
        // The capture thread submits and the MFT's pump thread releases, on
        // different threads. A cap that only holds single-threaded is not a cap.
        var budget = new VideoFrameBudget();
        int highWater = 0;
        object peak = new();

        Parallel.For(0, 8, _ =>
        {
            for (int i = 0; i < 500; i++)
            {
                if (!budget.TryAcquire()) continue;
                lock (peak) highWater = System.Math.Max(highWater, budget.InFlight);
                budget.Release();
            }
        });

        Assert.IsTrue(highWater <= VideoFrameBudget.ProtocolCapacity, $"peaked at {highWater}");
        Assert.AreEqual(0, budget.InFlight);
    }
}
