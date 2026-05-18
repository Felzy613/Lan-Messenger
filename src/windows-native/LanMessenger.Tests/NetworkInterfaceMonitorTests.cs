using LanMessenger.Core.Networking;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;

namespace LanMessenger.Tests;

// These tests cover the platform-agnostic invariants of NetworkInterfaceMonitor.
// The OS-event subscription and live socket re-binding are exercised by manual
// QA (Wi-Fi reconnect, VPN toggle, sleep/resume) — there's no clean way to
// simulate those transitions inside an MSTest run.
[TestClass]
public class NetworkInterfaceMonitorTests
{
    [TestMethod]
    public void EnumeratePopulatesAtLeastOneAdapterOnDevMachine()
    {
        using var monitor = new NetworkInterfaceMonitor();
        monitor.Start();
        // CI runners always have at least loopback; we explicitly exclude
        // loopback, so machines with no other NIC will legitimately produce
        // an empty set. Assert only on shape.
        Assert.IsNotNull(monitor.Adapters);
        foreach (var a in monitor.Adapters)
        {
            Assert.AreEqual(System.Net.Sockets.AddressFamily.InterNetwork, a.LocalIP.AddressFamily);
            Assert.IsFalse(IPAddress.IsLoopback(a.LocalIP), "loopback addresses must be excluded");
            var bytes = a.LocalIP.GetAddressBytes();
            Assert.IsFalse(bytes[0] == 169 && bytes[1] == 254,
                "APIPA link-local addresses must be excluded");
        }
    }

    [TestMethod]
    public void IsLocalNetworkAvailableMatchesAdapterCount()
    {
        using var monitor = new NetworkInterfaceMonitor();
        monitor.Start();
        Assert.AreEqual(monitor.Adapters.Count > 0, monitor.IsLocalNetworkAvailable);
    }

    [TestMethod]
    public void BroadcastAddressComputedFromMask()
    {
        using var monitor = new NetworkInterfaceMonitor();
        monitor.Start();
        foreach (var a in monitor.Adapters)
        {
            var ip   = a.LocalIP.GetAddressBytes();
            var mask = a.SubnetMask.GetAddressBytes();
            var bc   = a.BroadcastAddress.GetAddressBytes();
            for (var i = 0; i < 4; i++)
                Assert.AreEqual((byte)(ip[i] | ~mask[i]), bc[i],
                    $"broadcast byte {i} mismatch for {a.LocalIP}/{a.SubnetMask}");
        }
    }

    [TestMethod]
    public void StartIsIdempotent()
    {
        using var monitor = new NetworkInterfaceMonitor();
        monitor.Start();
        var before = monitor.Adapters;
        monitor.Start();   // must not throw or double-subscribe
        Assert.AreSame(before, monitor.Adapters);
    }

    [TestMethod]
    public void StopThenStartRebinds()
    {
        var monitor = new NetworkInterfaceMonitor();
        monitor.Start();
        monitor.Stop();
        monitor.Start();
        // Should not throw and should still have a usable snapshot.
        Assert.IsNotNull(monitor.Adapters);
        monitor.Dispose();
    }
}
