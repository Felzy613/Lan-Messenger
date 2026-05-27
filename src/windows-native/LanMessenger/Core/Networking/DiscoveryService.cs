using LanMessenger.Core.Protocol;
using LanMessenger.Core.Services;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace LanMessenger.Core.Networking;

// UDP discovery on port 54231.
//
// Architecture: one shared receive socket bound to INADDR_ANY:54231, with the
// 239.255.42.99 multicast group joined explicitly per interface (so multi-homed
// boxes — VPN + Wi-Fi + Hyper-V/WSL — actually receive multicast on the real
// LAN adapter, not just the OS default-route interface). For sending, one
// dedicated socket per interface is bound to that interface's local IP, with
// IP_MULTICAST_IF set so multicast and limited-broadcast (255.255.255.255)
// beacons leave the box via every interface, not just the lowest-metric one.
//
// On network change (adapter add/remove, Wi-Fi reconnect, VPN toggle, sleep/
// resume), the NetworkInterfaceMonitor fires Changed and this service tears
// down stale sockets and rebuilds the per-interface set. This is the only
// reliable way to keep discovery alive across the kinds of transitions that
// previously poisoned the single shared socket.
//
// Wire-protocol invariants from PROTOCOL.md are preserved exactly: port 54231,
// multicast group 239.255.42.99, TTL 1, 1.5 s beacon interval, JSON shape.
public sealed class DiscoveryService : IDisposable
{
    public delegate void PeerDiscoveredHandler(DiscoveryPacket packet, string fromIP);
    public event PeerDiscoveredHandler? PeerDiscovered;

    // Set before Start()
    public Func<DiscoveryPacket>?  BuildPayload  { get; set; }
    public Func<IEnumerable<string>>? ExtraTargets { get; set; }
    public string OwnPublicKeyB64 { get; set; } = "";

    private const int      DiscoveryPort  = 54231;
    private const string   MulticastGroup = "239.255.42.99";
    private static readonly IPAddress MulticastAddr = IPAddress.Parse(MulticastGroup);
    private const double   IntervalMs     = 1500;

    // Windows-specific: turn off SIO_UDP_CONNRESET so an ICMP "port unreachable"
    // from a peer that went offline doesn't poison the socket with WSAECONNRESET
    // (10054) on the next operation. Documented here:
    // https://learn.microsoft.com/en-us/windows/win32/winsock/wsaioctl-2
    private const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
    private static readonly byte[] DisableConnReset = [0, 0, 0, 0];

    // Each beacon is emitted to three targets (subnet-bcast, multicast, limited-bcast) per
    // interface, so the shared receive socket sees 2–3 copies per peer per cycle. Suppress
    // duplicates within a window shorter than the 1.5 s beacon interval.
    private readonly ConcurrentDictionary<string, long> _lastSeen = new();
    private const long DedupWindowMs = 1200;

    private readonly NetworkInterfaceMonitor _monitor;
    private readonly Dictionary<string, Socket> _sendSockets = [];   // keyed by interface LocalIP
    private Socket?      _recvSocket;
    private Timer?       _beaconTimer;
    private CancellationTokenSource _cts = new();
    private readonly object _socketLock = new();
    private bool _running;

    public DiscoveryService(NetworkInterfaceMonitor monitor)
    {
        _monitor = monitor;
    }

    public HashSet<string> OwnIPs => _monitor.LocalIPs;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _cts = new CancellationTokenSource();

        RebuildSockets(reason: "start");
        _monitor.Changed += OnInterfacesChanged;

        _beaconTimer = new Timer(_ => SendBeacon(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(IntervalMs));
        Task.Run(() => ReceiveLoop(_cts.Token));

        LanLogger.Info("Discovery",
            $"started port={DiscoveryPort} group={MulticastGroup} interval={IntervalMs}ms " +
            $"interfaces={_monitor.Adapters.Count}");
    }

    public void Stop()
    {
        _running = false;
        _cts.Cancel();
        _monitor.Changed -= OnInterfacesChanged;
        _beaconTimer?.Dispose(); _beaconTimer = null;
        TeardownSockets();
        LanLogger.Info("Discovery", "stopped");
    }

    public void Dispose() => Stop();

    // Send a single UDP datagram (used by NetworkCoordinator for one-off unicast
    // discovery replies). Goes out the send socket on the interface that owns
    // the most-specific route to toIP; falls back to the first available send
    // socket if no interface match (e.g. cross-subnet unicast).
    public void SendUdp(byte[] data, string toIP, int port)
    {
        if (!IPAddress.TryParse(toIP, out var dest))
        {
            LanLogger.Warn("Discovery", $"SendUdp: bad target IP '{toIP}'");
            return;
        }

        Socket? socket = PickSocketForTarget(dest);
        if (socket is null)
        {
            LanLogger.Warn("Discovery", $"SendUdp: no send socket available for {toIP}");
            return;
        }

        try { socket.SendTo(data, new IPEndPoint(dest, port)); }
        catch (Exception ex)
        {
            LanLogger.Warn("Discovery", $"SendUdp to {toIP}:{port} failed: {ex.GetType().Name} {ex.Message}");
        }
    }

    private Socket? PickSocketForTarget(IPAddress dest)
    {
        lock (_socketLock)
        {
            if (_sendSockets.Count == 0) return null;

            // Prefer the socket on the same subnet as the target — that guarantees
            // the packet exits via the correct interface for routed unicast.
            var destBytes = dest.GetAddressBytes();
            foreach (var adapter in _monitor.Adapters)
            {
                if (!SameSubnet(destBytes, adapter.LocalIP.GetAddressBytes(), adapter.SubnetMask.GetAddressBytes()))
                    continue;
                if (_sendSockets.TryGetValue(adapter.LocalIP.ToString(), out var s)) return s;
            }
            // Multicast and limited-broadcast targets reach any socket — return the first.
            return _sendSockets.Values.FirstOrDefault();
        }
    }

    private static bool SameSubnet(byte[] a, byte[] b, byte[] mask)
    {
        if (a.Length != 4 || b.Length != 4 || mask.Length != 4) return false;
        for (var i = 0; i < 4; i++)
            if ((a[i] & mask[i]) != (b[i] & mask[i])) return false;
        return true;
    }

    // MARK: - Socket lifecycle

    private void OnInterfacesChanged() => RebuildSockets(reason: "iface-change");

    private void RebuildSockets(string reason)
    {
        if (!_running && reason != "start") return;
        lock (_socketLock)
        {
            TeardownSocketsLocked();
            SetupReceiveSocketLocked();
            SetupSendSocketsLocked();
        }
        LanLogger.Info("Discovery",
            $"sockets rebuilt ({reason}): send={_sendSockets.Count} recv={(_recvSocket is null ? 0 : 1)}");
    }

    private void TeardownSockets()
    {
        lock (_socketLock) TeardownSocketsLocked();
    }

    private void TeardownSocketsLocked()
    {
        foreach (var s in _sendSockets.Values)
        {
            try { s.Close(); } catch { /* best-effort */ }
        }
        _sendSockets.Clear();
        try { _recvSocket?.Close(); } catch { }
        _recvSocket = null;
    }

    private void SetupReceiveSocketLocked()
    {
        try
        {
            var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            try { sock.IOControl(SIO_UDP_CONNRESET, DisableConnReset, null); }
            catch (Exception ex) { LanLogger.Warn("Discovery", $"recv: disable WSAECONNRESET failed: {ex.Message}"); }

            // Bind to INADDR_ANY:54231 so unicast, broadcast, and multicast
            // traffic from any interface all land here. Multicast still needs
            // explicit per-interface joins below.
            sock.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            _recvSocket = sock;

            JoinMulticastOnAllInterfacesLocked();
        }
        catch (Exception ex)
        {
            LanLogger.Error("Discovery", $"recv socket setup failed: {ex.GetType().Name} {ex.Message}");
            _recvSocket = null;
        }
    }

    private void JoinMulticastOnAllInterfacesLocked()
    {
        if (_recvSocket is null) return;

        var joined = 0;
        foreach (var adapter in _monitor.Adapters)
        {
            try
            {
                var opt = new MulticastOption(MulticastAddr, adapter.LocalIP);
                _recvSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, opt);
                joined++;
            }
            catch (Exception ex)
            {
                // Some interfaces (some VPN tunnels, Hyper-V virtual switches) refuse
                // multicast joins. Log and continue — the per-subnet broadcast still
                // reaches peers on those segments.
                LanLogger.Warn("Discovery",
                    $"multicast join failed on {adapter.LocalIP} ({adapter.Description}): {ex.Message}");
            }
        }
        LanLogger.Info("Discovery", $"multicast joined on {joined}/{_monitor.Adapters.Count} interfaces");
    }

    private void SetupSendSocketsLocked()
    {
        foreach (var adapter in _monitor.Adapters)
        {
            try
            {
                var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                sock.EnableBroadcast = true;
                sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);

                // Force multicast output to use THIS adapter regardless of routing
                // table. Without this, Windows picks the lowest-metric interface
                // (often a virtual adapter) and peers on the real LAN never see
                // the beacon.
                var ifaceBytes = adapter.LocalIP.GetAddressBytes();
                sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, ifaceBytes);

                // Bind to the adapter's IP so limited-broadcast (255.255.255.255)
                // and unicast packets also exit through this interface.
                sock.Bind(new IPEndPoint(adapter.LocalIP, 0));

                try { sock.IOControl(SIO_UDP_CONNRESET, DisableConnReset, null); } catch { }

                _sendSockets[adapter.LocalIP.ToString()] = sock;
                LanLogger.Info("Discovery",
                    $"send socket bound on {adapter.LocalIP} subnet={adapter.SubnetMask} bcast={adapter.BroadcastAddress} ({adapter.Description})");
            }
            catch (Exception ex)
            {
                LanLogger.Warn("Discovery",
                    $"send socket setup failed for {adapter.LocalIP} ({adapter.Description}): {ex.GetType().Name} {ex.Message}");
            }
        }
    }

    // MARK: - Beacon

    public void SendBeacon()
    {
        if (BuildPayload is null) return;
        if (!_running) return;

        byte[] data;
        try
        {
            var payload = BuildPayload();
            data = JsonSerializer.SerializeToUtf8Bytes(payload);
        }
        catch (Exception ex)
        {
            LanLogger.Warn("Discovery", $"beacon serialize failed: {ex.Message}");
            return;
        }

        var extras = ExtraTargets?.Invoke()?.ToList() ?? [];

        lock (_socketLock)
        {
            if (_sendSockets.Count == 0)
            {
                // No interfaces yet — common during boot or after net loss.
                return;
            }

            foreach (var (localIP, sock) in _sendSockets)
            {
                // Each interface's send socket emits the beacon to three targets:
                //   1. Its own directed-subnet broadcast (x.x.x.255) — most reliable
                //      across NICs/routers that block 255.255.255.255.
                //   2. The multicast group 239.255.42.99 — IP_MULTICAST_IF makes
                //      this exit through this specific interface.
                //   3. The limited broadcast 255.255.255.255 — Windows + most
                //      routers respect the bind address and route this out the
                //      bound interface.
                var adapter = _monitor.Adapters.FirstOrDefault(a => a.LocalIP.ToString() == localIP);
                if (adapter is not null)
                    TrySend(sock, data, adapter.BroadcastAddress, DiscoveryPort, "subnet-bcast");
                TrySend(sock, data, MulticastAddr,       DiscoveryPort, "multicast");
                TrySend(sock, data, IPAddress.Broadcast, DiscoveryPort, "limited-bcast");

                // Unicast hints (last-known IPs of known peers / contacts) help
                // cross-subnet reach where multicast/broadcast can't bridge.
                foreach (var target in extras)
                {
                    if (!IPAddress.TryParse(target, out var ip)) continue;
                    TrySend(sock, data, ip, DiscoveryPort, "unicast");
                }
            }
        }
    }

    private static void TrySend(Socket sock, byte[] data, IPAddress dest, int port, string label)
    {
        try { sock.SendTo(data, new IPEndPoint(dest, port)); }
        catch (Exception ex)
        {
            // Don't spam — broadcast/multicast failures are noisy on locked-down
            // networks. Demote to debug-tier (Info) to leave a trail without
            // burying the actually-useful log lines.
            LanLogger.Info("Discovery", $"send {label} to {dest}:{port} via {sock.LocalEndPoint} failed: {ex.Message}");
        }
    }

    // MARK: - Receive loop

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (!ct.IsCancellationRequested && _running)
        {
            Socket? sock;
            lock (_socketLock) sock = _recvSocket;
            if (sock is null)
            {
                // Sockets being rebuilt — wait briefly and retry.
                try { await Task.Delay(200, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                continue;
            }

            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int n;
            string fromIP;
            try
            {
                var result = await sock.ReceiveFromAsync(buffer, SocketFlags.None, remote, ct).ConfigureAwait(false);
                n = result.ReceivedBytes;
                fromIP = ((IPEndPoint)result.RemoteEndPoint).Address.ToString();
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { continue; }     // socket rebuilt — loop will pick up the new one
            catch (SocketException ex)
            {
                // WSAECONNRESET is suppressed via SIO_UDP_CONNRESET, but other
                // transient errors (network unreachable, interrupted) shouldn't
                // kill the receive loop. Log once and continue.
                LanLogger.Info("Discovery", $"recv socket error: {ex.SocketErrorCode} {ex.Message}");
                continue;
            }
            catch (Exception ex)
            {
                LanLogger.Warn("Discovery", $"recv loop unexpected: {ex.GetType().Name} {ex.Message}");
                continue;
            }

            if (n <= 0) continue;
            var data = new byte[n];
            Buffer.BlockCopy(buffer, 0, data, 0, n);
            HandleReceived(data, fromIP);
        }
    }

    private void HandleReceived(byte[] data, string fromIP)
    {
        var pkt = PacketValidator.ValidateDiscovery(data, fromIP, OwnPublicKeyB64, OwnIPs);
        if (pkt is null) return;

        // Suppress duplicate copies of the same beacon (we send to three targets per
        // interface so the receive socket sees each peer's beacon 2-3 times per cycle).
        var dedupKey = $"{pkt.PublicKeyB64}:{pkt.Type}";
        var now = Environment.TickCount64;
        if (_lastSeen.TryGetValue(dedupKey, out var last) && now - last < DedupWindowMs)
            return;
        _lastSeen[dedupKey] = now;

        // Reply to "discovery" packets (not to "discovery_reply" — that would
        // create an infinite ping-pong).
        if (pkt.Type == "discovery" && BuildPayload is not null)
        {
            try
            {
                var src = BuildPayload();
                var reply = new DiscoveryPacket
                {
                    Type         = "discovery_reply",
                    Username     = src.Username,
                    Port         = src.Port,
                    PublicKeyB64 = src.PublicKeyB64,
                    Ips          = src.Ips,
                    RelayIdHash  = src.RelayIdHash,
                };
                SendUdp(JsonSerializer.SerializeToUtf8Bytes(reply), fromIP, DiscoveryPort);
            }
            catch (Exception ex)
            {
                LanLogger.Warn("Discovery", $"reply to {fromIP} failed: {ex.Message}");
            }
        }

        LanLogger.Info("Discovery", $"rx {pkt.Type} from {fromIP} user='{pkt.Username}' port={pkt.Port}");
        try { PeerDiscovered?.Invoke(pkt, fromIP); }
        catch (Exception ex)
        {
            LanLogger.Warn("Discovery", $"PeerDiscovered handler threw: {ex.GetType().Name} {ex.Message}");
        }
    }
}
