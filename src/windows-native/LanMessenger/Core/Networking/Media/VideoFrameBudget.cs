namespace LanMessenger.Core.Networking.Media;

/// <summary>
/// The protocol's "never buffer more than two video frames anywhere", as an
/// object rather than a comment. Mirror of <c>VideoFrameBudget.swift</c>.
/// </summary>
/// <remarks>
/// <para>
/// PROTOCOL.md states it once and it has to hold at <b>five</b> independent
/// places: the capture queue, the encoder, the socket writer, TCP itself, and
/// the decoder/display. Four of them enforce it with machinery that already
/// exists — Desktop Duplication holds exactly one frame between acquire and
/// release, <see cref="MediaWriteScheduler"/> keeps one in-progress plus one
/// queued, <c>SendBufferSize</c> is capped so the kernel cannot hold a second of
/// video, and the presenter drops rather than queues. The encoder had nothing:
/// a submit went in whenever the codec said it had room, and an asynchronous
/// hardware MFT says it has room for as many frames as its pipeline is deep. On
/// the Quick Sync encoder here that is several, and every one of them is latency.
/// </para>
/// <para>
/// Every frame held anywhere is latency the viewer can never pay back, because
/// this is live screen content — there is no value in a frame that arrives late,
/// only in the next one. So the budget refuses rather than queues, and the
/// refusal is counted: a budget that is constantly full is a real signal about
/// the encoder, and silently dropping would hide it.
/// </para>
/// <para>
/// <b>A codec's own pipeline is not a queue.</b> This was very nearly shipped
/// with the encoder capped at two, which is correct for a queue and wrong for a
/// hardware transform: Quick Sync issues a <c>METransformNeedInput</c> for every
/// slot in its pipeline and does not emit its first frame until enough of them
/// are filled. Capped at two it produced <b>no output at all</b> — no video, and
/// not one <c>encoder_stats</c> line to say why. A pipeline's depth is fixed
/// latency, not growth, so the encoder gets a <i>runaway</i> guard at
/// <see cref="PipelineCapacity"/> and reports its high-water mark; the places
/// that really do queue keep the protocol's two.
/// </para>
/// </remarks>
public sealed class VideoFrameBudget
{
    /// <summary>
    /// Fixed by the protocol, not tunable. One being worked on, one waiting.
    /// This is the number for anything that <i>queues</i>.
    /// </summary>
    public const int ProtocolCapacity = 2;

    /// <summary>
    /// For a codec pipeline, which is deep by design. High enough not to starve
    /// a hardware transform, low enough that a runaway is caught long before it
    /// becomes seconds of latency.
    /// </summary>
    public const int PipelineCapacity = 8;

    public int Capacity { get; }

    public VideoFrameBudget(int capacity = ProtocolCapacity) => Capacity = capacity;

    private readonly object _gate = new();
    private int _inFlight;
    private int _refused;
    private int _highWater;

    public int InFlight { get { lock (_gate) return _inFlight; } }
    public int Refused { get { lock (_gate) return _refused; } }

    /// <summary>
    /// The deepest this ever got. The encoder's is its pipeline depth, which is
    /// latency nothing else can see — and the number WS10 exists to expose.
    /// </summary>
    public int HighWater { get { lock (_gate) return _highWater; } }

    /// <summary>
    /// Takes a slot, or refuses. A refusal means drop this frame — never wait
    /// for one, because the capture thread is the thread that would be waiting
    /// and the screen does not stop changing while it does.
    /// </summary>
    public bool TryAcquire()
    {
        lock (_gate)
        {
            if (_inFlight >= Capacity) { _refused++; return false; }
            _inFlight++;
            if (_inFlight > _highWater) _highWater = _inFlight;
            return true;
        }
    }

    /// <summary>
    /// Gives a slot back, when a frame comes out or its submission failed.
    /// </summary>
    /// <remarks>
    /// Clamped at zero rather than trusted: an encoder that emits two outputs
    /// for one input would otherwise drive the count negative and the budget
    /// would stop bounding anything at all — the failure would be a slow drift
    /// back into unbounded latency, with nothing to see.
    /// </remarks>
    public void Release()
    {
        lock (_gate) { if (_inFlight > 0) _inFlight--; }
    }

    /// <summary>
    /// Forgets everything in flight. For flush, restart and teardown, where the
    /// frames that were outstanding are never coming back and a leaked slot
    /// would shrink the budget for the rest of the session.
    /// </summary>
    public void Reset()
    {
        lock (_gate) _inFlight = 0;
    }

    /// <summary>For the stats line. Refusals are not reset with the count.</summary>
    public string Summary() =>
        $"in_flight={InFlight}/{Capacity} peak={HighWater} refused={Refused}";
}
