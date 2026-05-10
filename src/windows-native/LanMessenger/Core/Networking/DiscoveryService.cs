using LanMessenger.Core.Protocol;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace LanMessenger.Core.Networking;

// Owns the UDP discovery socket on port 54231.
// Sends a beacon every 1.5 s to: broadcast, multicast, per-subnet, contacts/peers.
// On receiving a "discovery" packet from a remote host, replies with "discovery_reply".
// All callbacks fire on a background thread — caller marshals to UI thread as needed.
public sealed class DiscoveryService : IDisposable
{
    public delegate void PeerDiscoveredHandler(DiscoveryPacket packet, string fromIP);
    public event PeerDiscoveredHandler? PeerDiscovered;

    // Set before calling Start()
    public Func<DiscoveryPacket>?  BuildPayload   { get; set; }
    public Func<IEnumerable<string>>? ExtraTargets { get; set; }
    public string        OwnPublicKeyB64 { get; set; } = "";
    public HashSet<string> OwnIPs        { get; set; } = [];

    private const int    DiscoveryPort   = 54231;
    private const string MulticastGroup  = "239.255.42.99";
    private const double IntervalMs      = 1500;

    private UdpClient?  _sendSocket;
    private UdpClient?  _recvSocket;
    private Timer?      _beaconTimer;
    private CancellationTokenSource _cts = new();
    private bool _running;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _cts = new CancellationTokenSource();
        SetupSendSocket();
        SetupReceiveSocket();
        _beaconTimer = new Timer(_ => SendBeacon(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(IntervalMs));
        Task.Run(() => ReceiveLoop(_cts.Token));
    }

    public void Stop()
    {
        _running = false;
        _cts.Cancel();
        _beaconTimer?.Dispose(); _beaconTimer = null;
        _sendSocket?.Dispose();  _sendSocket  = null;
        _recvSocket?.Dispose();  _recvSocket  = null;
    }

    public void Dispose() => Stop();

    // Send a single UDP datagram (used by NetworkCoordinator for discovery replies).
    public void SendUdp(byte[] data, string toIP, int port)
    {
        try { _sendSocket?.Send(data, data.Length, toIP, port); } catch { }
    }

    // MARK: - Send socket (broadcast + multicast TTL=1)

    private void SetupSendSocket()
    {
        _sendSocket = new UdpClient(AddressFamily.InterNetwork);
        _sendSocket.EnableBroadcast = true;
        _sendSocket.MulticastLoopback = false;
        _sendSocket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
    }

    private void SendBeacon()
    {
        if (BuildPayload is null) return;
        try
        {
            var payload = BuildPayload();
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(payload);

            var targets = new HashSet<string>(BroadcastTargets());
            foreach (var ip in ExtraTargets?.Invoke() ?? []) targets.Add(ip);

            foreach (var target in targets)
                SendUdp(data, target, DiscoveryPort);
        }
        catch { }
    }

    private IEnumerable<string> BroadcastTargets()
    {
        yield return "255.255.255.255";
        yield return MulticastGroup;
        foreach (var ip in OwnIPs)
        {
            var parts = ip.Split('.');
            if (parts.Length == 4)
                yield return $"{parts[0]}.{parts[1]}.{parts[2]}.255";
        }
    }

    // MARK: - Receive socket

    private void SetupReceiveSocket()
    {
        _recvSocket = new UdpClient();
        _recvSocket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _recvSocket.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        try { _recvSocket.JoinMulticastGroup(IPAddress.Parse(MulticastGroup)); } catch { }
        _recvSocket.Client.ReceiveTimeout = 1000; // 1 s so loop checks cancellation
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _running)
        {
            try
            {
                if (_recvSocket is null) break;
                var result = await _recvSocket.ReceiveAsync(ct).ConfigureAwait(false);
                var fromIP = result.RemoteEndPoint.Address.ToString();
                HandleReceived(result.Buffer, fromIP);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { }
            catch (ObjectDisposedException) { break; }
            catch { }
        }
    }

    private void HandleReceived(byte[] data, string fromIP)
    {
        var pkt = PacketValidator.ValidateDiscovery(data, fromIP, OwnPublicKeyB64, OwnIPs);
        if (pkt is null) return;

        // Reply to "discovery" packets
        if (pkt.Type == "discovery" && BuildPayload is not null)
        {
            var reply = BuildPayload();
            reply = new DiscoveryPacket
            {
                Type         = "discovery_reply",
                Username     = reply.Username,
                Port         = reply.Port,
                PublicKeyB64 = reply.PublicKeyB64,
                Ips          = reply.Ips,
            };
            SendUdp(JsonSerializer.SerializeToUtf8Bytes(reply), fromIP, DiscoveryPort);
        }

        PeerDiscovered?.Invoke(pkt, fromIP);
    }
}
