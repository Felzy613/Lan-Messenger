using LanMessenger.Core.Services;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LanMessenger.Core.Networking;

// One usable IPv4 interface on this machine. Snapshot value — never mutated
// after construction; the monitor publishes a fresh set on every change.
public sealed record NetworkAdapterSnapshot(
    string    InterfaceId,
    string    Description,
    IPAddress LocalIP,
    IPAddress SubnetMask,
    IPAddress BroadcastAddress);

// Tracks the set of IPv4 interfaces that are eligible for LAN discovery.
//
// "Eligible" means:
//   - OperationalStatus is Up
//   - Has at least one non-loopback IPv4 unicast address
//   - The address is not in the APIPA link-local range (169.254/16) — those
//     indicate "no real network" on Windows when DHCP fails, and shipping
//     beacons there is pointless
//
// The monitor publishes a snapshot of the current adapter set and fires
// Changed whenever the set differs from the previous snapshot. It listens to
// both NetworkChange.NetworkAddressChanged (fires when an IP comes/goes) and
// NetworkChange.NetworkAvailabilityChanged (fires when the system flips
// between "any network" and "no network"), and also polls every 5 s as a
// safety net — those events are documented as best-effort on Windows and
// occasionally miss adapter transitions (especially Wi-Fi roam, VPN bring-up,
// and Hyper-V/WSL adapter creation).
//
// IsLocalNetworkAvailable is true whenever the snapshot is non-empty. This is
// what the UI should treat as "the app has a usable local network", not
// "internet is reachable" — LAN messaging doesn't need internet.
public sealed class NetworkInterfaceMonitor : IDisposable
{
    public event Action? Changed;

    public IReadOnlyList<NetworkAdapterSnapshot> Adapters { get; private set; } = [];
    public bool IsLocalNetworkAvailable => Adapters.Count > 0;

    public HashSet<string> LocalIPs =>
        [.. Adapters.Select(a => a.LocalIP.ToString())];

    private System.Threading.Timer? _pollTimer;
    private readonly object _lock = new();
    private bool _started;

    public void Start()
    {
        lock (_lock)
        {
            if (_started) return;
            _started = true;
        }

        NetworkChange.NetworkAddressChanged      += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;

        // Initial snapshot synchronously so callers see populated state immediately.
        Refresh(reason: "initial");

        // 5 s safety-net poll — the OS events are documented as best-effort
        // and occasionally miss transitions on Wi-Fi roams or VPN bring-up.
        _pollTimer = new System.Threading.Timer(_ => Refresh(reason: "poll"), null,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_started) return;
            _started = false;
        }

        NetworkChange.NetworkAddressChanged      -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    public void Dispose() => Stop();

    private void OnNetworkChanged(object? sender, EventArgs e)      => Refresh(reason: "addr-changed");
    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
        => Refresh(reason: $"avail-changed={e.IsAvailable}");

    private void Refresh(string reason)
    {
        IReadOnlyList<NetworkAdapterSnapshot> fresh;
        try { fresh = Enumerate(); }
        catch (Exception ex)
        {
            LanLogger.Warn("NetMonitor", $"enumerate failed ({reason}): {ex.GetType().Name} {ex.Message}");
            return;
        }

        var prev = Adapters;
        if (AdapterSetsEqual(prev, fresh)) return;

        Adapters = fresh;
        LanLogger.Info("NetMonitor",
            $"interfaces changed ({reason}): " +
            $"was={prev.Count} now={fresh.Count} " +
            $"ips=[{string.Join(",", fresh.Select(a => a.LocalIP))}] " +
            $"available={IsLocalNetworkAvailable}");
        try { Changed?.Invoke(); }
        catch (Exception ex)
        {
            LanLogger.Warn("NetMonitor", $"Changed handler threw: {ex.GetType().Name} {ex.Message}");
        }
    }

    // MARK: - Enumeration

    private static IReadOnlyList<NetworkAdapterSnapshot> Enumerate()
    {
        var list = new List<NetworkAdapterSnapshot>();
        foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (iface.OperationalStatus != OperationalStatus.Up) continue;
            if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            // Tunnel and PPP can be legitimate (Wireguard, modem) — keep them.
            // Filtering is intentionally permissive: it's safer to send a beacon to a
            // dead VPN adapter than to silently exclude a working LAN adapter.

            var props = iface.GetIPProperties();
            foreach (var uni in props.UnicastAddresses)
            {
                if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                var ip = uni.Address;
                if (IPAddress.IsLoopback(ip)) continue;

                var bytes = ip.GetAddressBytes();
                // 169.254/16 (APIPA) — "DHCP failed", no real network. Skip so we
                // don't pollute beacons with addresses no peer can reach.
                if (bytes[0] == 169 && bytes[1] == 254) continue;
                // 0.0.0.0 — unbound interface, skip.
                if (bytes is [0, 0, 0, 0]) continue;

                IPAddress mask;
                try { mask = uni.IPv4Mask ?? IPAddress.Parse("255.255.255.0"); }
                catch { mask = IPAddress.Parse("255.255.255.0"); }

                // Pre-compute the IPv4 directed-broadcast address (host bits all 1).
                // Used as one of the beacon targets so peers on the same subnet
                // receive even when limited-broadcast (255.255.255.255) is dropped
                // by some routers/switches.
                var broadcast = ComputeBroadcast(ip, mask);

                list.Add(new NetworkAdapterSnapshot(
                    InterfaceId:      iface.Id,
                    Description:      iface.Description,
                    LocalIP:          ip,
                    SubnetMask:       mask,
                    BroadcastAddress: broadcast));
            }
        }
        return list;
    }

    private static IPAddress ComputeBroadcast(IPAddress ip, IPAddress mask)
    {
        var ipBytes   = ip.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        if (ipBytes.Length != 4 || maskBytes.Length != 4) return IPAddress.Broadcast;
        var bcast = new byte[4];
        for (var i = 0; i < 4; i++) bcast[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
        return new IPAddress(bcast);
    }

    private static bool AdapterSetsEqual(
        IReadOnlyList<NetworkAdapterSnapshot> a,
        IReadOnlyList<NetworkAdapterSnapshot> b)
    {
        if (a.Count != b.Count) return false;
        var aSet = a.Select(x => $"{x.InterfaceId}|{x.LocalIP}|{x.SubnetMask}").ToHashSet();
        return b.All(x => aSet.Contains($"{x.InterfaceId}|{x.LocalIP}|{x.SubnetMask}"));
    }
}
