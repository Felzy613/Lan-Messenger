using System.Net.Sockets;

namespace LanMessenger.Core.Networking.Media;

// THE testability seam. Three methods, and they are the entire socket surface of
// the media transport. Mirror of the macOS MediaLink.swift.
//
// Everything above this interface — framing, mux, demux, sequence enforcement,
// the session state machine — is exercised in tests against an in-memory double.
// Nothing below it needs a test, because SocketMediaLink contains no logic worth
// testing. That split is what makes this half reviewable from a Mac at all.
//
// Deliberately has no "read with timeout" method. The inherited socket read
// timeout is CLEARED at detach, so a read can only end in bytes, clean close, or
// error — never in "some bytes consumed, unknown how many", which is the state
// that makes mid-frame resynchronisation necessary. Liveness is the session
// watchdog's job, on its own timer, not the reader's.

public enum MediaLinkRead { Ok, Closed, Failed }

public interface IMediaLink
{
    /// <summary>Blocking. Fills exactly <paramref name="count"/> bytes or reports why it could not.</summary>
    MediaLinkRead ReadExact(byte[] buffer, int count);
    /// <summary>Blocking. Writes every byte, or returns false.</summary>
    bool WriteAll(ReadOnlySpan<byte> bytes);
    /// <summary>Idempotent. Must interrupt a reader parked in ReadExact.</summary>
    void ShutdownAndClose();
    bool IsClosed { get; }
    string PeerIP { get; }
}

/// <summary>
/// The only untestable code in the media transport: a thin adapter over a
/// TcpClient that has already been accepted and validated.
/// </summary>
public sealed class SocketMediaLink : IMediaLink
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly object _gate = new();
    private bool _closed;

    public string PeerIP { get; }

    public bool IsClosed { get { lock (_gate) return _closed; } }

    /// <summary>Takes ownership of an already-accepted TcpClient.</summary>
    /// <remarks>
    /// Three socket settings matter here and none is applied by the listener:
    ///
    /// * ReceiveTimeout is CLEARED. Inbound JSON connections run under a 60 s
    ///   per-frame idle cancellation, which is right for request/response and
    ///   catastrophic for a media session: a legitimately idle stream — a
    ///   completely static screen produces no frames at all — would be torn down
    ///   mid-session. The media reader has no timeout at all; the watchdog owns
    ///   liveness.
    /// * NoDelay is SET. Without it Nagle plus delayed ACK parks a 20-byte input
    ///   event for tens of milliseconds, which is exactly the latency the 16 KiB
    ///   fragmentation exists to avoid.
    /// * SendBufferSize is CAPPED at 128 KiB. If only the fragmentation is
    ///   implemented and this is not, the feature does not meet its latency goal:
    ///   an input frame handed to the socket still sits behind everything already
    ///   absorbed into an auto-tuned kernel buffer. 128 KiB stays above the
    ///   bandwidth-delay product of every realistic LAN path (1 Gbit x 1 ms is
    ///   about 125 KB), so throughput is untouched.
    /// </remarks>
    public SocketMediaLink(TcpClient client, string peerIP)
    {
        _client = client;
        PeerIP = peerIP;
        _client.NoDelay = true;
        _client.ReceiveTimeout = 0;
        _client.SendTimeout = 0;
        _client.SendBufferSize = 128 * 1024;
        _stream = client.GetStream();
    }

    public MediaLinkRead ReadExact(byte[] buffer, int count)
    {
        if (count <= 0) return MediaLinkRead.Ok;
        int total = 0;
        try
        {
            while (total < count)
            {
                int n = _stream.Read(buffer, total, count - total);
                if (n == 0) return MediaLinkRead.Closed;
                total += n;
            }
            return MediaLinkRead.Ok;
        }
        catch (IOException) { return IsClosed ? MediaLinkRead.Closed : MediaLinkRead.Failed; }
        catch (ObjectDisposedException) { return MediaLinkRead.Closed; }
        catch (SocketException) { return MediaLinkRead.Failed; }
    }

    public bool WriteAll(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return true;
        try
        {
            _stream.Write(bytes);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Shutdown before close so a reader parked in Read on another thread returns
    /// immediately rather than waiting for the handle to be reclaimed.
    /// </summary>
    public void ShutdownAndClose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
        }
        try { _client.Client.Shutdown(SocketShutdown.Both); } catch { }
        try { _stream.Dispose(); } catch { }
        try { _client.Dispose(); } catch { }
    }
}
