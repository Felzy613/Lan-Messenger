using LanMessenger.Core.Crypto;
using LanMessenger.Core.Protocol;
using LanMessenger.Core.Services;
using Microsoft.UI.Dispatching;
using System.Net;
using System.Net.Sockets;

namespace LanMessenger.Core.Networking;

// Owns all PeerSessions and the DiscoveryService.
// Routes validated packets to callbacks (MessagingService / FileTransferService).
// Manages the TCP listener for inbound connections from peers.
// Callbacks are dispatched to the UI thread via the stored DispatcherQueue.
public sealed class NetworkCoordinator : IDisposable
{
    public event Action<ValidatedPacket>?   PacketReceived;
    public event Action<DiscoveryPacket, string>? PeerDiscovered;

    public readonly DiscoveryService Discovery = new();
    private readonly Dictionary<string, PeerSession> _sessions = [];
    private TcpListener? _listener;
    private CancellationTokenSource _cts = new();
    private bool _running;
    private DispatcherQueue? _dispatcherQueue;

    private const int TcpPort = 54232;

    private string OwnPublicKeyB64 => KeyManager.Shared.PublicKeyB64;

    public void Start(string username, HashSet<string> localIPs, DispatcherQueue dispatcherQueue)
    {
        if (_running) return;
        _running = true;
        _cts = new CancellationTokenSource();
        _dispatcherQueue = dispatcherQueue;

        // Configure discovery
        Discovery.OwnPublicKeyB64 = OwnPublicKeyB64;
        Discovery.OwnIPs          = localIPs;
        Discovery.BuildPayload    = () => new DiscoveryPacket
        {
            Type         = "discovery",
            Username     = username,
            Port         = TcpPort,
            PublicKeyB64 = OwnPublicKeyB64,
            Ips          = [.. localIPs],
        };
        Discovery.ExtraTargets = () => _sessions.Keys;
        Discovery.PeerDiscovered += (pkt, ip) =>
            _dispatcherQueue?.TryEnqueue(() => PeerDiscovered?.Invoke(pkt, ip));
        Discovery.Start();

        StartTcpListener();
        LanLogger.Info("Net", $"coordinator started user='{username}' tcp={TcpPort} udp=54231 ips=[{string.Join(",", localIPs)}]");
    }

    public void Stop()
    {
        _running = false;
        _cts.Cancel();
        Discovery.Stop();
        lock (_sessions)
        {
            foreach (var s in _sessions.Values) s.Stop();
            _sessions.Clear();
        }
        _listener?.Stop();
    }

    public void Dispose() => Stop();

    // Send a pre-encoded frame to a peer. Uses persistent session if available, else fire-and-forget TCP.
    public void Send(byte[] frame, string toIP, int port = TcpPort)
    {
        PeerSession? session;
        lock (_sessions) _sessions.TryGetValue(toIP, out session);

        if (session is not null)
        {
            session.Send(frame);
        }
        else
        {
            Task.Run(async () =>
            {
                try
                {
                    using var tcp = new TcpClient();
                    await tcp.ConnectAsync(toIP, port).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    await tcp.GetStream().WriteAsync(frame).ConfigureAwait(false);
                }
                catch { }
            });
        }
    }

    public void Send(IEnumerable<byte[]> frames, string toIP, int port = TcpPort)
    {
        foreach (var f in frames) Send(f, toIP, port);
    }

    // Ensure a persistent session exists for this peer.
    public void EnsureSession(string ip, int port)
    {
        lock (_sessions)
        {
            if (_sessions.ContainsKey(ip)) return;
            var session = new PeerSession(ip, port) { OwnPublicKeyB64 = OwnPublicKeyB64 };
            session.OnPacket += pkt =>
                _dispatcherQueue?.TryEnqueue(() => PacketReceived?.Invoke(pkt));
            session.OnDisconnect += s => { lock (_sessions) _sessions.Remove(s.PeerIP); };
            _sessions[ip] = session;
            session.Start();
        }
    }

    // MARK: - TCP Listener (inbound connections)

    private void StartTcpListener()
    {
        _listener = new TcpListener(IPAddress.Any, TcpPort);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start(backlog: 16); // throws SocketException if port is already bound
        Task.Run(() => AcceptLoop(_cts.Token));
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _running)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                var fromIP = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
                _ = Task.Run(() => HandleInbound(client, fromIP, ct));
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private async Task HandleInbound(TcpClient client, string fromIP, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                // Short receive timeout so half-open / abandoned connections are
                // released promptly — important on Windows where the system
                // default would otherwise keep the accepted socket alive for
                // minutes if the peer crashed mid-frame.
                client.ReceiveTimeout = 30_000;
                var stream = client.GetStream();
                while (!ct.IsCancellationRequested)
                {
                    byte[]? frameData = await FrameCodec.ReadFrameAsync(stream, ct).ConfigureAwait(false);
                    if (frameData is null) break;

                    var pkt = PacketValidator.Validate(frameData, fromIP, OwnPublicKeyB64);
                    if (pkt is not null)
                        _dispatcherQueue?.TryEnqueue(() => PacketReceived?.Invoke(pkt));
                    else
                        LanLogger.Warn("Net", $"dropped invalid frame from {fromIP} bytes={frameData.Length}");
                }
            }
            catch (Exception ex) { LanLogger.Warn("Net", $"inbound from {fromIP} ended: {ex.GetType().Name} {ex.Message}"); }
        }
    }
}
