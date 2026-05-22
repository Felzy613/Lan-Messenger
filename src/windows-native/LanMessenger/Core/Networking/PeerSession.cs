using LanMessenger.Core.Protocol;
using LanMessenger.Core.Services;
using System.Net.Sockets;

namespace LanMessenger.Core.Networking;

// One persistent TCP connection to a single peer.
// Reconnects automatically with exponential back-off: 500 ms → 2 s → 5 s.
// Received validated packets are delivered via OnPacket on the thread pool.
// Outgoing frames are queued and sent serially.
public sealed class PeerSession : IDisposable
{
    public string PeerIP   { get; }
    public int    PeerPort { get; }
    public string OwnPublicKeyB64 { get; set; } = "";

    public event Action<ValidatedPacket>? OnPacket;
    public event Action<PeerSession>?     OnDisconnect;

    private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _sendQueue = new();
    private TcpClient?  _client;
    private NetworkStream? _stream;
    private bool _connected;
    private bool _stopped;
    private SemaphoreSlim _sendSem = new(0, int.MaxValue);

    private static readonly TimeSpan[] _backoff = [
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
    ];

    public PeerSession(string ip, int port)
    {
        PeerIP   = ip;
        PeerPort = port;
    }

    public void Start() => Task.Run(ConnectLoop);

    public void Stop()
    {
        _stopped = true;
        _sendSem.Release(); // unblock sender
        Teardown();
    }

    public void Dispose() => Stop();

    // Enqueue a frame for sending. Thread-safe.
    public void Send(byte[] frame)
    {
        _sendQueue.Enqueue(frame);
        _sendSem.Release();
    }

    // MARK: - Connection lifecycle

    private async Task ConnectLoop()
    {
        int backoffIdx = 0;
        while (!_stopped)
        {
            Teardown();
            var attemptStartedAt = DateTime.UtcNow;
            LanLogger.Peer("connect", peer: PeerIP);
            try
            {
                _client = new TcpClient();
                await _client.ConnectAsync(PeerIP, PeerPort).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                _stream    = _client.GetStream();
                _connected = true;
                backoffIdx = 0;
                LanLogger.Peer("connected", peer: PeerIP,
                    durationMs: (int)(DateTime.UtcNow - attemptStartedAt).TotalMilliseconds);

                // Run sender and receiver concurrently; either finishing ends the session
                var recvTask = ReceiveLoop(_stream);
                var sendTask = SendLoop(_stream);
                await Task.WhenAny(recvTask, sendTask).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LanLogger.Peer("connect_fail", peer: PeerIP,
                    durationMs: (int)(DateTime.UtcNow - attemptStartedAt).TotalMilliseconds,
                    reason: $"{ex.GetType().Name}: {ex.Message}");
            }

            _connected = false;
            LanLogger.Peer("disconnect", peer: PeerIP,
                reason: _stopped ? "stopped" : "stream closed");
            OnDisconnect?.Invoke(this);
            Teardown();

            if (_stopped) break;
            var delay = _backoff[Math.Min(backoffIdx++, _backoff.Length - 1)];
            await Task.Delay(delay).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoop(NetworkStream stream)
    {
        try
        {
            while (!_stopped)
            {
                byte[]? frameData = await FrameCodec.ReadFrameAsync(stream).ConfigureAwait(false);
                if (frameData is null) break;

                var pkt = PacketValidator.Validate(frameData, PeerIP, OwnPublicKeyB64);
                if (pkt is not null) OnPacket?.Invoke(pkt);
            }
        }
        catch { }
    }

    private async Task SendLoop(NetworkStream stream)
    {
        try
        {
            while (!_stopped)
            {
                await _sendSem.WaitAsync().ConfigureAwait(false);
                if (_stopped) break;
                while (_sendQueue.TryDequeue(out var frame))
                    await stream.WriteAsync(frame).ConfigureAwait(false);
            }
        }
        catch { }
    }

    private void Teardown()
    {
        _connected = false;
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _stream = null;
        _client = null;
    }
}
