using LanMessenger.Core.Crypto;
using LanMessenger.Core.Protocol;
using LanMessenger.Core.Services;
using Microsoft.UI.Dispatching;
using System.Net;
using System.Net.Sockets;

namespace LanMessenger.Core.Networking;

// Owns all PeerSessions, the DiscoveryService, and the NetworkInterfaceMonitor.
// Routes validated packets to callbacks (MessagingService / FileTransferService).
// Manages the TCP listener for inbound connections from peers.
// Callbacks are dispatched to the UI thread via the stored DispatcherQueue.
public sealed class NetworkCoordinator : IDisposable
{
    public event Action<ValidatedPacket>?         PacketReceived;
    public event Action<DiscoveryPacket, string>? PeerDiscovered;
    public event Action?                          NetworkAvailabilityChanged;

    public readonly NetworkInterfaceMonitor Network  = new();
    public readonly DiscoveryService        Discovery;
    private readonly Dictionary<string, PeerSession> _sessions = [];
    private TcpListener? _listener;
    private CancellationTokenSource _cts = new();
    private bool _running;
    private DispatcherQueue? _dispatcherQueue;

    private const int TcpPort = 54232;

    public bool IsLocalNetworkAvailable => Network.IsLocalNetworkAvailable;

    private string OwnPublicKeyB64 => KeyManager.Shared.PublicKeyB64;

    public NetworkCoordinator()
    {
        Discovery = new DiscoveryService(Network);
    }

    public void Start(DispatcherQueue dispatcherQueue)
    {
        if (_running) return;
        _running = true;
        _cts = new CancellationTokenSource();
        _dispatcherQueue = dispatcherQueue;

        // Monitor must be running BEFORE Discovery so the first beacon sees a
        // populated interface set.
        Network.Start();
        Network.Changed += () =>
            _dispatcherQueue?.TryEnqueue(() => NetworkAvailabilityChanged?.Invoke());

        // Configure discovery — read the username fresh on every beacon so
        // changes the user makes in Settings propagate without needing to
        // restart the network stack.
        Discovery.OwnPublicKeyB64 = OwnPublicKeyB64;
        Discovery.BuildPayload    = () => new DiscoveryPacket
        {
            Type         = "discovery",
            Username     = LanMessenger.Core.Persistence.ConfigStore.Shared.Config.Username,
            Port         = TcpPort,
            PublicKeyB64 = OwnPublicKeyB64,
            Ips          = [.. Network.LocalIPs],
        };
        Discovery.ExtraTargets = () => _sessions.Keys;
        Discovery.PeerDiscovered += (pkt, ip) =>
            _dispatcherQueue?.TryEnqueue(() => PeerDiscovered?.Invoke(pkt, ip));
        Discovery.Start();

        StartTcpListener();

        LanLogger.Info("Net",
            $"coordinator started user='{LanMessenger.Core.Persistence.ConfigStore.Shared.Config.Username}' tcp={TcpPort} udp=54231 " +
            $"interfaces={Network.Adapters.Count} available={Network.IsLocalNetworkAvailable}");
    }

    public void Stop()
    {
        _running = false;
        _cts.Cancel();
        Discovery.Stop();
        Network.Stop();
        lock (_sessions)
        {
            foreach (var s in _sessions.Values) s.Stop();
            _sessions.Clear();
        }
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    public void Dispose() => Stop();

    // Send a pre-encoded frame to a peer. Uses persistent session if available,
    // else fire-and-forget TCP.
    public void Send(byte[] frame, string toIP, int port = TcpPort)
    {
        PeerSession? session;
        lock (_sessions) _sessions.TryGetValue(toIP, out session);

        if (session is not null)
        {
            session.Send(frame);
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(toIP, port).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await tcp.GetStream().WriteAsync(frame).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LanLogger.Info("Net", $"one-shot send to {toIP}:{port} failed: {ex.GetType().Name} {ex.Message}");
            }
        });
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
        LanLogger.Info("Net", $"TCP listener bound on 0.0.0.0:{TcpPort}");
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
            catch (Exception ex)
            {
                LanLogger.Info("Net", $"accept failed: {ex.GetType().Name} {ex.Message}");
            }
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
