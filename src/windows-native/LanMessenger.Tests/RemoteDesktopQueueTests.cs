using LanMessenger.Core.Crypto;
using LanMessenger.Core.Networking.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;

namespace LanMessenger.Tests;

// The media transport's queue-starvation cover. Mirror of the macOS
// RemoteDesktopQueueTests.swift, with the same test names so a failure here
// names a macOS test that already passes.
//
// The bug being guarded has been found twice on the macOS side, both times in
// DiscoveryService and both times invisible to code review, because starvation
// is a queue-occupancy property rather than anything visible at a call site:
// first a blocking receive loop sharing a serial queue with the beacon timer,
// then a health-summary timer created on the very queue that receive loop never
// releases — so it never fired once in production.
//
// Windows avoids that trap by construction, because System.Threading.Timer fires
// on the thread pool rather than a caller-chosen serial queue, which is exactly
// why the Windows DiscoveryService never had the macOS bug. These tests exist
// anyway: a future refactor onto a shared scheduler or a single dedicated thread
// would reintroduce it, and this is what would say so.
[TestClass]
public class RemoteDesktopQueueTests
{
    private const string SessionId = "9f2c4a6e8b0d1f3a5c7e9b1d3f5a7c9e";

    private static MediaSession MakeSession(IMediaLink link)
    {
        var keyParams = new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport };
        using var aStatic = new Key(KeyAgreementAlgorithm.X25519, keyParams);
        using var bStatic = new Key(KeyAgreementAlgorithm.X25519, keyParams);
        using var aEph    = new Key(KeyAgreementAlgorithm.X25519, keyParams);
        using var bEph    = new Key(KeyAgreementAlgorithm.X25519, keyParams);

        static string Pub(Key k) => Convert.ToBase64String(k.PublicKey.Export(KeyBlobFormat.RawPublicKey));

        var keys = RemoteSessionCrypto.DeriveKeys(
            RemoteSessionRole.Initiator, aEph, aStatic, Pub(bEph), Pub(bStatic), SessionId,
            new RemoteHandshakeParams(new Dictionary<string, RemoteParamValue>
            {
                ["protocol"] = RemoteParamValue.Int(1),
            }));

        return new MediaSession(SessionId, Pub(bStatic), link.PeerIP,
                                RemoteSessionRole.Initiator, keys, link);
    }

    // The core regression. Against a session whose timers shared the read thread
    // this sees zero ticks and times out: the read loop parks in ReadExact and
    // never yields.
    [TestMethod]
    [Timeout(30_000)]
    public void KeepaliveKeepsFiringWhileTheReadLoopIsParked()
    {
        var link = new BlockingMediaLink();
        var session = MakeSession(link);
        session.KeepaliveInterval = TimeSpan.FromMilliseconds(200);
        session.WatchdogInterval = TimeSpan.FromSeconds(60);   // keep the watchdog out of this test
        session.WatchdogTimeout = TimeSpan.FromSeconds(600);

        using var ticks = new CountdownEvent(3);
        session.OnKeepaliveTick = () => { if (!ticks.IsSet) ticks.Signal(); };

        session.Start();
        try
        {
            Assert.IsTrue(ticks.Wait(TimeSpan.FromSeconds(20)),
                "the keepalive timer must keep firing while the read loop is parked");
        }
        finally { session.Stop(); link.Release(); }
    }

    // The watchdog is what stops a crashed viewer leaving a host's screen
    // captured indefinitely, so it must survive the same parked read loop.
    [TestMethod]
    [Timeout(30_000)]
    public void WatchdogKeepsFiringWhileTheReadLoopIsParked()
    {
        var link = new BlockingMediaLink();
        var session = MakeSession(link);
        session.KeepaliveInterval = TimeSpan.FromSeconds(60);
        session.WatchdogInterval = TimeSpan.FromMilliseconds(200);
        session.WatchdogTimeout = TimeSpan.FromSeconds(600);   // observe ticks without tearing down

        using var ticks = new CountdownEvent(3);
        session.OnWatchdogTick = () => { if (!ticks.IsSet) ticks.Signal(); };

        session.Start();
        try
        {
            Assert.IsTrue(ticks.Wait(TimeSpan.FromSeconds(20)),
                "the watchdog timer must keep firing while the read loop is parked");
        }
        finally { session.Stop(); link.Release(); }
    }

    // Guards against a future regression that gives every session one shared
    // read thread: the second session's timers must not be blocked by the first
    // session's parked reader.
    [TestMethod]
    [Timeout(30_000)]
    public void ConcurrentSessionsBothKeepTicking()
    {
        var firstLink = new BlockingMediaLink();
        var secondLink = new BlockingMediaLink();
        var first = MakeSession(firstLink);
        var second = MakeSession(secondLink);
        foreach (var s in new[] { first, second })
        {
            s.KeepaliveInterval = TimeSpan.FromMilliseconds(200);
            s.WatchdogInterval = TimeSpan.FromSeconds(60);
            s.WatchdogTimeout = TimeSpan.FromSeconds(600);
        }

        using var firstTicks = new CountdownEvent(2);
        using var secondTicks = new CountdownEvent(2);
        first.OnKeepaliveTick = () => { if (!firstTicks.IsSet) firstTicks.Signal(); };
        second.OnKeepaliveTick = () => { if (!secondTicks.IsSet) secondTicks.Signal(); };

        first.Start(); second.Start();
        try
        {
            Assert.IsTrue(firstTicks.Wait(TimeSpan.FromSeconds(20)), "first session stopped ticking");
            Assert.IsTrue(secondTicks.Wait(TimeSpan.FromSeconds(20)), "second session stopped ticking");
        }
        finally
        {
            first.Stop(); second.Stop();
            firstLink.Release(); secondLink.Release();
        }
    }

    // The watchdog must actually tear the session down, not merely tick.
    [TestMethod]
    [Timeout(30_000)]
    public void WatchdogClosesAnIdleSession()
    {
        var link = new BlockingMediaLink();
        var session = MakeSession(link);
        session.KeepaliveInterval = TimeSpan.FromSeconds(60);
        session.WatchdogInterval = TimeSpan.FromMilliseconds(200);
        session.WatchdogTimeout = TimeSpan.FromMilliseconds(300);

        using var closed = new ManualResetEventSlim(false);
        session.OnClosed = _ => closed.Set();

        session.Start();
        try
        {
            Assert.IsTrue(closed.Wait(TimeSpan.FromSeconds(20)),
                "the watchdog must close an idle session");
            Assert.IsTrue(link.IsClosed, "the watchdog must close the link, not just log");
        }
        finally { link.Release(); }
    }
}

// ---- Test doubles ---------------------------------------------------------

/// <summary>
/// A link fed from memory. Feed appends readable bytes; Written is whatever the
/// code under test wrote.
/// </summary>
public sealed class LoopbackMediaLink : IMediaLink
{
    private readonly object _gate = new();
    private readonly List<byte> _inbox = [];
    private readonly List<byte> _outbox = [];
    private bool _finished;
    private bool _closed;

    public string PeerIP { get; }
    public LoopbackMediaLink(string peerIP = "10.0.0.9") => PeerIP = peerIP;

    public bool IsClosed { get { lock (_gate) return _closed; } }
    public byte[] Written { get { lock (_gate) return [.. _outbox]; } }

    public void Feed(byte[] bytes)
    {
        lock (_gate) { _inbox.AddRange(bytes); Monitor.PulseAll(_gate); }
    }

    /// <summary>Subsequent reads that cannot be satisfied return Closed.</summary>
    public void Finish() { lock (_gate) { _finished = true; Monitor.PulseAll(_gate); } }

    public MediaLinkRead ReadExact(byte[] buffer, int count)
    {
        if (count <= 0) return MediaLinkRead.Ok;
        lock (_gate)
        {
            while (_inbox.Count < count && !_finished && !_closed) Monitor.Wait(_gate);
            if (_closed || _inbox.Count < count) return MediaLinkRead.Closed;
            _inbox.CopyTo(0, buffer, 0, count);
            _inbox.RemoveRange(0, count);
            return MediaLinkRead.Ok;
        }
    }

    public bool WriteAll(ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            if (_closed) return false;
            _outbox.AddRange(bytes.ToArray());
            return true;
        }
    }

    public void ShutdownAndClose() { lock (_gate) { _closed = true; Monitor.PulseAll(_gate); } }
}

/// <summary>
/// A link whose reads block until released. Models a peer that has connected and
/// then gone quiet — which is the normal state of a media session whose host
/// screen is static, and the state in which a shared queue starves everything.
/// </summary>
public sealed class BlockingMediaLink : IMediaLink
{
    private readonly object _gate = new();
    private bool _released;
    private bool _closed;

    public string PeerIP => "10.0.0.10";
    public bool IsClosed { get { lock (_gate) return _closed; } }

    public MediaLinkRead ReadExact(byte[] buffer, int count)
    {
        lock (_gate)
        {
            while (!_released && !_closed) Monitor.Wait(_gate);
            return MediaLinkRead.Closed;
        }
    }

    public bool WriteAll(ReadOnlySpan<byte> bytes) { lock (_gate) return !_closed; }

    public void Release() { lock (_gate) { _released = true; Monitor.PulseAll(_gate); } }

    public void ShutdownAndClose() { lock (_gate) { _closed = true; Monitor.PulseAll(_gate); } }
}
