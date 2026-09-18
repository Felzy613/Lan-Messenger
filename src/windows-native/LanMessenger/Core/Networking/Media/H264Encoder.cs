using LanMessenger.Core.Services;
using SharpGen.Runtime;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace LanMessenger.Core.Networking.Media;

// The Windows host's encoder: NV12 in, Annex-B out.
//
// Mirror of H264Encoder.swift in purpose, and nothing like it in shape, because
// Media Foundation and VideoToolbox disagree about almost everything. Two of
// those disagreements are worth stating before the code:
//
//  * **This encoder already emits Annex-B**, with in-band SPS/PPS before every
//    IDR. That is what the wire format specifies, so a Windows host sends what
//    its encoder produces unchanged. The macOS side is the one that converts.
//  * **Output type before input type.** The MF H.264 encoder requires it in that
//    order, and setting them the other way round fails with an error that has no
//    obvious relationship to the cause. The decoder is the opposite — input
//    first — which is not symmetry anybody would guess.
//
// The async trap is the expensive one, and it is the reason this file has two
// drive loops instead of one.
//
// A hardware MFT refuses ProcessInput with MF_E_TRANSFORM_ASYNC_LOCKED
// (0xC00D6D77) until MF_TRANSFORM_ASYNC_UNLOCK is set on its attributes — and
// **unlocking is necessary but not sufficient.** Unlocking does not make an
// async MFT behave synchronously; it grants permission to drive it the async
// way, which inverts who is in charge:
//
//   sync MFT    we call ProcessInput, then pull with ProcessOutput until it
//               says MF_E_TRANSFORM_NEED_MORE_INPUT.
//   async MFT   it tells us. METransformNeedInput means we may call
//               ProcessInput exactly once; METransformHaveOutput means we may
//               call ProcessOutput exactly once. Both events arrive on the
//               MFT's IMFMediaEventGenerator. Calling either method at any
//               other time returns E_UNEXPECTED (0x8000FFFF).
//
// That last sentence was learned the hard way. This code unlocked the MFT and
// then drove it synchronously anyway, which on the test machine's
// "IntelAr Quick Sync Video H.264 Encoder MFT" produced 34,731 identical
// E_UNEXPECTED lines in fifty seconds and not one frame — the viewer sat on
// "waiting for first frame" forever, because nothing in the failure path said
// anything louder than a log line. Hence both the event pump below and the
// consecutive-failure fuse in NoteEncoderFailure.
//
// SoftwareSync and HardwareSync encoders still take the synchronous path, so
// the fallback on a machine with no Quick Sync is untouched.

public sealed class H264EncoderException(string message) : Exception(message);

/// One encoded access unit, already in the wire format.
public sealed class EncodedVideoFrame
{
    public required byte[] AnnexB { get; init; }
    public required bool IsKeyframe { get; init; }
    public required ulong CaptureUs { get; init; }
}

public sealed class H264Encoder : IDisposable
{
    private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    private const int MF_E_TRANSFORM_STREAM_CHANGE   = unchecked((int)0xC00D6D61);
    private const int MF_E_NOTACCEPTING              = unchecked((int)0xC00D36B5);
    private const int MF_E_TRANSFORM_ASYNC_LOCKED    = unchecked((int)0xC00D6D77);
    private const int MF_E_SHUTDOWN                  = unchecked((int)0xC00D3E85);

    /// How many consecutive ProcessInput/ProcessOutput failures the encoder
    /// tolerates before declaring itself dead.
    ///
    /// Any positive number would have been an improvement on the previous
    /// behaviour, which was to log and keep trying forever. Thirty is about one
    /// second of frames: long enough to ride out a transient, short enough that
    /// the user hears about it while they are still looking at the window.
    private const int MaxConsecutiveFailures = 30;

    private static readonly Guid MF_MT_MAJOR_TYPE         = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE            = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_INTERLACE_MODE     = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MF_MT_FRAME_SIZE         = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_FRAME_RATE         = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid MF_MT_AVG_BITRATE        = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid MF_MT_MPEG2_PROFILE      = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    private static readonly Guid MF_MT_VIDEO_NOMINAL_RANGE = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    private static readonly Guid MF_MT_VIDEO_PRIMARIES    = new("dbfbe4d7-0740-4ee0-8192-850ab0e21935");
    private static readonly Guid MF_MT_TRANSFER_FUNCTION  = new("5fb0fce9-be5c-4935-a811-ec838f8eed93");
    private static readonly Guid MF_MT_YUV_MATRIX         = new("3e23d450-2c75-4d25-a00e-b91670d12327");

    private static readonly Guid MFMediaType_Video  = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");

    private static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    private static readonly Guid MF_TRANSFORM_ASYNC         = new("f81da2c7-b103-4d8d-9fb3-3f3085fc51d3");
    private static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK  = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    private static readonly Guid MFT_FRIENDLY_NAME_Attribute = new("314ffbae-5b41-4c95-9c19-4e7d586face3");

    private const uint MFT_ENUM_FLAG_SYNCMFT       = 0x00000001;
    private const uint MFT_ENUM_FLAG_HARDWARE      = 0x00000004;
    private const uint MFT_ENUM_FLAG_SORTANDFILTER = 0x00000040;

    private const uint MFVideoInterlace_Progressive = 2;
    private const uint eAVEncH264VProfile_High = 100;
    private const uint MFNominalRange_16_235 = 2;
    private const uint MFVideoPrimaries_BT709 = 3;
    private const uint MFVideoTransFunc_709 = 5;
    private const uint MFVideoTransferMatrix_BT709 = 1;

    private readonly int _width;
    private readonly int _height;
    private readonly int _frameRate;
    private int _bitrate;
    private IMFTransform? _encoder;
    private ICodecAPI? _codecApi;
    private long _sampleIndex;
    private ulong _firstCaptureUs;
    private long _lastSampleTime = -1;
    private int _forceKeyframe;

    // ---- async drive state; all null on the synchronous path ----------------

    private IMFMediaEventGenerator? _events;
    private Thread? _pump;
    private volatile bool _pumping;

    /// One permit per METransformNeedInput the MFT has issued and we have not
    /// yet spent. This is the whole async contract in one field: a frame may
    /// only go in when the encoder has asked for one.
    private SemaphoreSlim? _inputCredits;

    /// Capture timestamps, in submission order, waiting for their encoded frame.
    ///
    /// An async MFT hands output back on its own thread some time after Encode
    /// returned, so the captureUs that was on the stack at submission is gone by
    /// then. It has to travel alongside. Output order follows input order here
    /// because the encoder is configured for low latency with one reference
    /// frame, so there are no B-frames to reorder anything.
    private readonly ConcurrentQueue<ulong> _pendingCaptureUs = new();

    private readonly ManualResetEventSlim _drainComplete = new(false);

    private int _consecutiveFailures;

    // Counters, and a timer that prints them. Not debug scaffolding to be
    // removed later: a pipeline that fails by going *quiet* cannot be diagnosed
    // from errors, because there are none. The first async-pump build produced
    // no errors and no picture, and there was nothing in the log that
    // distinguished "capture is not delivering" from "the encoder never asked
    // for input" from "output is never collected".
    private long _encodeCalls;
    private long _needInput;
    private long _submitted;
    private long _droppedForBackpressure;
    private long _haveOutput;
    private long _emitted;
    private Timer? _stats;
    private string _lastStats = "";
    private int _statsQuietTicks;

    /// Raised once, when the encoder has failed enough times in a row to be
    /// considered dead. The session ends rather than showing a picture that is
    /// never going to arrive.
    public Action<string>? OnFatalError { get; set; }

    public EncoderKind Kind { get; private set; } = EncoderKind.None;
    public string EncoderName { get; private set; } = "";

    /// Emitted on the calling thread, once per access unit.
    public Action<EncodedVideoFrame>? OnEncodedFrame { get; set; }

    public H264Encoder(int width, int height, int bitrate = 10_000_000, int frameRate = 30)
    {
        _width = width;
        _height = height;
        _frameRate = frameRate;

        MediaFactory.MFStartup(false).CheckError();

        var mfts = EnumerateEncoders();
        var choice = EncoderSelector.Select(mfts.Select(m => m.Info).ToList());
        if (!choice.IsUsable)
        {
            foreach (var m in mfts) m.Activate.Dispose();
            throw new H264EncoderException(
                "no H.264 encoder MFT — a Windows N/KN SKU without the media feature pack");
        }

        Kind = choice.Kind;
        EncoderName = choice.Mft?.Name ?? "";

        var selected = mfts.First(m => m.Info.Name == EncoderName);
        foreach (var m in mfts) { if (!ReferenceEquals(m, selected)) m.Activate.Dispose(); }

        using (selected.Activate)
        {
            _encoder = selected.Activate.ActivateObject<IMFTransform>();
        }

        Configure(bitrate);
        LanLogger.Remote("encoder_started",
            reason: $"{EncoderName} {_width}x{_height}@{_frameRate} kind={Kind}");
    }

    private sealed record Candidate(IMFActivate Activate, EncoderMftInfo Info);

    /// Enumerates hardware and software encoders together, so `EncoderSelector`
    /// — which is pure and tested against nine machine topologies — makes the
    /// choice rather than this code making it twice.
    private static List<Candidate> EnumerateEncoders()
    {
        var found = new List<Candidate>();
        Collect(MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER, hardware: true, found);
        Collect(MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER, hardware: false, found);
        return found;

        static void Collect(uint flags, bool hardware, List<Candidate> into)
        {
            var output = new RegisterTypeInfo
            {
                GuidMajorType = MFMediaType_Video,
                GuidSubtype = MFVideoFormat_H264,
            };
            try
            {
                // Output type for an encoder: we want something that *produces*
                // H.264. The decoder search passes it as the input argument.
                MediaFactory.MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, flags, null, output,
                                       out IntPtr array, out uint count);
                if (array == IntPtr.Zero) return;
                try
                {
                    for (uint i = 0; i < count; i++)
                    {
                        IntPtr p = Marshal.ReadIntPtr(array, (int)(i * (uint)IntPtr.Size));
                        if (p == IntPtr.Zero) continue;
                        var activate = new IMFActivate(p);
                        into.Add(new Candidate(activate, new EncoderMftInfo(
                            FriendlyName(activate), hardware, IsAsync(activate, hardware), true)));
                    }
                }
                finally { Marshal.FreeCoTaskMem(array); }
            }
            catch (Exception ex)
            {
                LanLogger.Remote("error", reason: $"encoder enumeration failed: {ex.Message}");
            }
        }
    }

    private static string FriendlyName(IMFActivate activate)
    {
        try { return activate.GetString(MFT_FRIENDLY_NAME_Attribute) ?? "unnamed"; }
        catch { return "unnamed"; }
    }

    /// Whether this MFT is asynchronous, which decides which drive loop it gets.
    ///
    /// The previous version set MF_TRANSFORM_ASYNC_UNLOCK and returned true if
    /// that did not throw — but `IMFAttributes::SetUINT32` succeeds for *any*
    /// GUID, so it answered "async" for every transform ever enumerated,
    /// software ones included. It happened to be right about the Quick Sync
    /// encoder, which is how it survived.
    ///
    /// The real answer is the MF_TRANSFORM_ASYNC attribute the registry puts on
    /// the activation object. Where a driver omits it, hardware is taken to mean
    /// async, which has been true of every hardware encoder since Windows 8.
    private static bool IsAsync(IMFActivate activate, bool hardware)
    {
        try
        {
            if (activate.GetUInt32(MF_TRANSFORM_ASYNC) == 1) return true;
            return false;
        }
        catch
        {
            return hardware;
        }
    }

    private void Configure(int bitrate)
    {
        var encoder = _encoder!;

        // Unlock before anything else. A hardware MFT refuses ProcessInput with
        // MF_E_TRANSFORM_ASYNC_LOCKED until this is set, and the HRESULT is the
        // only thing that names the cause — the plan predicted a bare E_FAIL and
        // was wrong in our favour.
        try
        {
            var attributes = encoder.Attributes;
            attributes?.Set(MF_TRANSFORM_ASYNC_UNLOCK, 1u);
        }
        catch (Exception ex)
        {
            LanLogger.Remote("encoder_property_failed", reason: $"async unlock: {ex.Message}");
        }

        // Output type FIRST. The MF H.264 encoder requires this order and fails
        // unhelpfully in the other one.
        _bitrate = bitrate;
        using (var outType = MediaFactory.MFCreateMediaType())
        {
            outType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            outType.Set(MF_MT_SUBTYPE, MFVideoFormat_H264);
            ApplyOutputAttributes(outType);
            encoder.SetOutputType(0, outType, 0);
        }

        using (var inType = MediaFactory.MFCreateMediaType())
        {
            inType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            inType.Set(MF_MT_SUBTYPE, MFVideoFormat_NV12);
            inType.Set(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
            inType.Set(MF_MT_FRAME_SIZE, Pack(_width, _height));
            inType.Set(MF_MT_FRAME_RATE, Pack(_frameRate, 1));
            inType.Set(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
            encoder.SetInputType(0, inType, 0);
        }

        // ICodecAPI is what makes this an encoder for remote control rather than
        // for video. Every setting is best-effort: support varies by vendor and
        // driver, and a missing property is a worse session rather than a broken
        // one.
        _codecApi = CodecApiExtensions.TryGetCodecApi(encoder.NativePointer);
        if (_codecApi is null)
        {
            LanLogger.Remote("encoder_property_unsupported", reason: "ICodecAPI not available");
        }
        else
        {
            _codecApi.TrySet(CodecApiProperty.LowLatencyMode, true, "AVLowLatencyMode");
            _codecApi.TrySet(CodecApiProperty.CommonRateControlMode, 0u, "RateControlMode=CBR");
            _codecApi.TrySet(CodecApiProperty.CommonMeanBitRate, (uint)bitrate, "MeanBitRate");
            _codecApi.TrySet(CodecApiProperty.VideoMaxNumRefFrame, 1u, "MaxNumRefFrame=1");
        }

        // Begin streaming is what makes an async MFT start issuing events, so
        // the generator has to be in hand before it. Events are queued rather
        // than dropped, so the pump thread can start either side of this — but
        // the QI cannot fail silently afterwards.
        if (Kind == EncoderKind.HardwareAsync) AttachEventGenerator(encoder);

        encoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        encoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

        StartEventPump();
    }

    /// Async MFTs only. A transform that accepted MF_TRANSFORM_ASYNC_UNLOCK but
    /// exposes no event generator cannot be driven either way, and saying so
    /// here is better than discovering it one E_UNEXPECTED at a time.
    private void AttachEventGenerator(IMFTransform encoder)
    {
        _events = encoder.QueryInterfaceOrNull<IMFMediaEventGenerator>();
        if (_events is null)
        {
            throw new H264EncoderException(
                $"{EncoderName} reports async but exposes no IMFMediaEventGenerator");
        }
        _inputCredits = new SemaphoreSlim(0);
    }

    private void StartEventPump()
    {
        StartStats();
        if (_events is null) return;
        _pumping = true;
        _pump = new Thread(PumpEvents)
        {
            IsBackground = true,
            Name = "h264-encoder-events",
        };
        _pump.Start();
        LanLogger.Remote("encoder_pump_started", reason: EncoderName);
    }

    /// Prints the counters every two seconds.
    ///
    /// Its own timer thread on purpose. The capture thread blocks in
    /// AcquireNextFrame and the pump thread blocks in GetEvent, so either would
    /// stop reporting at exactly the moment the report became interesting —
    /// the same trap that left the discovery health timer dead for years.
    private void StartStats()
    {
        _stats = new Timer(_ => LogStats(), null,
                           TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private void LogStats()
    {
        string line =
            $"calls={Interlocked.Read(ref _encodeCalls)} "
          + $"need_input={Interlocked.Read(ref _needInput)} "
          + $"submitted={Interlocked.Read(ref _submitted)} "
          + $"dropped={Interlocked.Read(ref _droppedForBackpressure)} "
          + $"have_output={Interlocked.Read(ref _haveOutput)} "
          + $"emitted={Interlocked.Read(ref _emitted)}";

        // Unchanged counters still get through every 30s, so a stalled session
        // is visibly stalled rather than merely absent from the log.
        if (line == _lastStats && ++_statsQuietTicks < 15) return;
        _statsQuietTicks = 0;
        _lastStats = line;
        LanLogger.Remote("encoder_stats", reason: line);
    }

    /// The async MFT's half of the conversation.
    ///
    /// Its own thread, and deliberately not the capture thread: the capture
    /// thread spends most of its life blocked in AcquireNextFrame waiting for
    /// the screen to change, and an encoder that could only deliver output while
    /// the screen was moving would stall on the last frame of every pause. It is
    /// the same rule as the discovery receive loop and the media session timers
    /// — a loop that blocks does not get to share.
    private void PumpEvents()
    {
        while (_pumping)
        {
            IMFMediaEvent ev;
            try
            {
                // 0 means block. The wake-up on teardown is a queued event.
                ev = _events!.GetEvent(0);
            }
            catch (SharpGenException ex)
            {
                // MF_E_SHUTDOWN during teardown is the ordinary way this ends.
                // Every other exit gets said out loud: a pump that returned
                // early looks exactly like one that is simply waiting, and the
                // difference is a session that will never show a picture.
                bool expected = !_pumping || ex.ResultCode.Code == MF_E_SHUTDOWN;
                LanLogger.Remote(expected ? "encoder_pump_stopped" : "error",
                    reason: $"GetEvent 0x{ex.ResultCode.Code:X8}");
                return;
            }
            catch (Exception ex)
            {
                LanLogger.Remote(_pumping ? "error" : "encoder_pump_stopped",
                                 reason: $"event pump: {ex.Message}");
                return;
            }

            using (ev)
            {
                if (!_pumping) return;
                switch (ev.EventType)
                {
                    case MediaEventTypes.TransformNeedInput:
                        // Permission to submit exactly one frame.
                        Interlocked.Increment(ref _needInput);
                        _inputCredits!.Release();
                        break;

                    case MediaEventTypes.TransformHaveOutput:
                        // Exactly one ProcessOutput per event. Looping here the
                        // way the synchronous path does would produce the same
                        // E_UNEXPECTED this whole pump exists to avoid.
                        Interlocked.Increment(ref _haveOutput);
                        var encoder = _encoder;
                        if (encoder is not null)
                        {
                            // Normally exactly one ProcessOutput per event. A
                            // stream change is the exception: it consumed the
                            // event without producing a frame, and the output
                            // it was announcing is still waiting behind the
                            // renegotiated type.
                            if (ReadOutput(encoder) == OutputStep.StreamChange) ReadOutput(encoder);
                        }
                        break;

                    case MediaEventTypes.TransformDrainComplete:
                        _drainComplete.Set();
                        break;
                }
            }
        }
    }

    /// Everything that has to be true of the output type, wherever it came from.
    ///
    /// Extracted because a stream change hands back a *bare* type from
    /// GetOutputAvailableType: setting that as-is silently discards the bitrate
    /// and the colour signalling. The colour tags are the dangerous loss — an
    /// unsignalled range is how blacks come out washed out and whites clipped
    /// at the far end, with neither side reporting anything wrong.
    private void ApplyOutputAttributes(IMFMediaType type)
    {
        type.Set(MF_MT_AVG_BITRATE, (uint)_bitrate);
        type.Set(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        type.Set(MF_MT_FRAME_SIZE, Pack(_width, _height));
        type.Set(MF_MT_FRAME_RATE, Pack(_frameRate, 1));
        type.Set(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
        type.Set(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_High);

        // The colour tags the macOS encoder also sets. They have to agree.
        type.Set(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235);
        type.Set(MF_MT_VIDEO_PRIMARIES, MFVideoPrimaries_BT709);
        type.Set(MF_MT_TRANSFER_FUNCTION, MFVideoTransFunc_709);
        type.Set(MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT709);
    }

    private static ulong Pack(int high, int low) =>
        ((ulong)(uint)high << 32) | (uint)low;

    /// Honours a viewer's keyframe_request. Latched rather than applied, because
    /// it rides the next frame submitted — and on a still screen, capture is
    /// silent, so there may not be one for a while.
    public void LatchKeyframe() => Interlocked.Exchange(ref _forceKeyframe, 1);

    /// Encodes one NV12 frame. `stride` is the luma row pitch, which is not
    /// necessarily the width.
    ///
    /// Called from the capture thread. On the async path the encoded frame does
    /// NOT come back before this returns — it arrives later, on the pump thread.
    public void Encode(ReadOnlySpan<byte> nv12, int stride, ulong captureUs)
    {
        var encoder = _encoder;
        if (encoder is null) return;
        Interlocked.Increment(ref _encodeCalls);

        if (Interlocked.Exchange(ref _forceKeyframe, 0) == 1)
        {
            _codecApi?.TrySet(CodecApiProperty.VideoForceKeyFrame, 1u, "ForceKeyFrame");
        }

        int size = nv12.Length;
        using var buffer = MediaFactory.MFCreateMemoryBuffer(size);
        unsafe
        {
            buffer.Lock(out IntPtr data, out _, out _);
            nv12.CopyTo(new Span<byte>((void*)data, size));
            buffer.Unlock();
            buffer.CurrentLength = size;
        }

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);

        // Without a timestamp the encoder has no timeline, and MF buffers
        // untimed samples forever. 100ns units.
        //
        // Derived from the capture clock rather than counted off a frame index.
        // A counter asserts that frames arrive at exactly _frameRate, and when
        // they do not — capture is change-driven, so they never do — the
        // encoder's idea of elapsed time drifts away from real time and its
        // rate control is steering by a clock that is wrong.
        if (_firstCaptureUs == 0) _firstCaptureUs = captureUs;
        long presentation = (long)(captureUs - _firstCaptureUs) * 10L;
        sample.SampleTime = Math.Max(presentation, _lastSampleTime + 1);
        _lastSampleTime = sample.SampleTime;
        sample.SampleDuration = 10_000_000L / _frameRate;
        _sampleIndex++;

        if (_inputCredits is not null) SubmitAsync(encoder, sample, captureUs);
        else SubmitSync(encoder, sample, captureUs);
    }

    /// An async MFT asks for input; it is not told. ProcessInput before a
    /// METransformNeedInput has arrived returns E_UNEXPECTED, so a credit has to
    /// be in hand before the sample goes anywhere near it.
    private void SubmitAsync(IMFTransform encoder, IMFSample sample, ulong captureUs)
    {
        // One frame period, no longer. Waiting for a busy encoder only queues a
        // picture that is already stale — and on a still screen the next capture
        // may be minutes away, so blocking here would wedge the capture thread
        // rather than merely delay it.
        if (!_inputCredits!.Wait(Math.Max(1, 1000 / _frameRate)))
        {
            Interlocked.Increment(ref _droppedForBackpressure);
            return;
        }

        try
        {
            // Bounded. If the MFT ever swallows a frame without producing one,
            // an unbounded FIFO would drift by exactly that much forever and
            // every latency figure after it would be wrong.
            while (_pendingCaptureUs.Count > 120) _pendingCaptureUs.TryDequeue(out _);

            _pendingCaptureUs.Enqueue(captureUs);
            encoder.ProcessInput(0, sample, 0);
            Interlocked.Increment(ref _submitted);
            Interlocked.Exchange(ref _consecutiveFailures, 0);
        }
        catch (SharpGenException ex)
        {
            // The credit was never spent, and the timestamp never used.
            _pendingCaptureUs.TryDequeue(out _);
            try { _inputCredits.Release(); } catch (ObjectDisposedException) { }
            NoteEncoderFailure("ProcessInput", ex.ResultCode);
        }
    }

    /// The classic loop, for SoftwareSync and HardwareSync transforms: push one
    /// in, then pull until it asks for more.
    private void SubmitSync(IMFTransform encoder, IMFSample sample, ulong captureUs)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                _pendingCaptureUs.Enqueue(captureUs);
                encoder.ProcessInput(0, sample, 0);
                Interlocked.Increment(ref _submitted);
                break;
            }
            catch (SharpGenException ex) when (ex.ResultCode.Code == MF_E_NOTACCEPTING)
            {
                // Normal flow control, not an error: it has output waiting.
                _pendingCaptureUs.TryDequeue(out _);
                Drain(encoder);
            }
            catch (SharpGenException ex) when (ex.ResultCode.Code == MF_E_TRANSFORM_ASYNC_LOCKED)
            {
                _pendingCaptureUs.TryDequeue(out _);
                LanLogger.Remote("error",
                    reason: "MF_E_TRANSFORM_ASYNC_LOCKED — MF_TRANSFORM_ASYNC_UNLOCK was not accepted");
                return;
            }
            catch (SharpGenException ex)
            {
                _pendingCaptureUs.TryDequeue(out _);
                NoteEncoderFailure("ProcessInput", ex.ResultCode);
                return;
            }
        }

        Drain(encoder);
    }

    /// Synchronous path only. An async MFT gets exactly one ReadOutput per
    /// METransformHaveOutput instead.
    private void Drain(IMFTransform encoder)
    {
        while (true)
        {
            switch (ReadOutput(encoder))
            {
                case OutputStep.Emitted:
                case OutputStep.StreamChange:
                    continue;
                default:
                    return;
            }
        }
    }

    private enum OutputStep { Emitted, NeedMoreInput, StreamChange, Failed }

    /// Exactly one ProcessOutput call, shared by both drive loops.
    private OutputStep ReadOutput(IMFTransform encoder)
    {
        var info = encoder.GetOutputStreamInfo(0);
        bool selfAllocating =
            (info.Flags & (int)(OutputStreamInfoFlags.OutputStreamProvidesSamples |
                                OutputStreamInfoFlags.OutputStreamCanProvideSamples)) != 0;

        var output = new OutputDataBuffer { StreamID = 0 };
        if (!selfAllocating && info.Size > 0)
        {
            var allocated = MediaFactory.MFCreateSample();
            allocated.AddBuffer(MediaFactory.MFCreateMemoryBuffer(info.Size));
            output.Sample = allocated;
        }

        Result hr;
        try
        {
            hr = encoder.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
        }
        catch (SharpGenException ex) when (LogFirstOutcomes(ex.ResultCode, info.Flags, info.Size, -1))
        {
            throw;   // unreachable: the filter always returns false
        }
        catch (SharpGenException ex)
        {
            output.Sample?.Dispose();
            NoteEncoderFailure("ProcessOutput", ex.ResultCode);
            return OutputStep.Failed;
        }

        LogFirstOutcomes(hr, info.Flags, info.Size, -1);

        if (hr.Code == MF_E_TRANSFORM_NEED_MORE_INPUT)
        {
            output.Sample?.Dispose();
            return OutputStep.NeedMoreInput;
        }
        if (hr.Code == MF_E_TRANSFORM_STREAM_CHANGE)
        {
            output.Sample?.Dispose();
            RenegotiateOutputType(encoder);
            return OutputStep.StreamChange;
        }
        if (hr.Failure)
        {
            output.Sample?.Dispose();
            NoteEncoderFailure("ProcessOutput", hr);
            return OutputStep.Failed;
        }

        Interlocked.Exchange(ref _consecutiveFailures, 0);
        if (output.Sample is null) return OutputStep.NeedMoreInput;

        using (output.Sample)
        {
            var annexB = CopyOut(output.Sample);
            LogFirstOutcomes(hr, info.Flags, info.Size, annexB?.Length ?? 0);
            if (annexB is null || annexB.Length == 0) return OutputStep.Emitted;

            // The submission-time capture timestamp, not whatever is current:
            // on the async path this runs on the pump thread, long after the
            // frame it describes was captured.
            if (!_pendingCaptureUs.TryDequeue(out ulong captureUs)) captureUs = 0;

            // This encoder emits Annex-B with in-band parameter sets before
            // every IDR, which is exactly the wire format — no conversion,
            // unlike the macOS side.
            Interlocked.Increment(ref _emitted);
            OnEncodedFrame?.Invoke(new EncodedVideoFrame
            {
                AnnexB = annexB,
                IsKeyframe = H264Bitstream.AnnexBContainsKeyframe(annexB),
                CaptureUs = captureUs,
            });
        }
        return OutputStep.Emitted;
    }

    /// Says what the first few ProcessOutput calls actually did.
    ///
    /// Written because "have_output=1 emitted=0" is not a diagnosable state:
    /// every branch out of ReadOutput that is not an outright failure returns
    /// quietly, so a transform that announces output and then hands back
    /// nothing looks identical to one that was never asked. Bounded to the
    /// first few calls — after that the counters carry the story.
    private int _outcomesLogged;

    private bool LogFirstOutcomes(Result hr, int flags, int size, int copied)
    {
        if (Interlocked.Increment(ref _outcomesLogged) <= 6)
        {
            LanLogger.Remote("encoder_output",
                reason: $"hr=0x{hr.Code:X8} stream_flags=0x{flags:X} stream_size={size} copied={copied}");
        }
        return false;   // never handles the exception; only observes it
    }

    /// MF_E_TRANSFORM_STREAM_CHANGE means the transform has withdrawn its
    /// output type and will do nothing further until a new one is set.
    ///
    /// Ignoring it deadlocks the async path in a way that reads as silence: the
    /// MFT is holding output it cannot give us, so it never asks for input
    /// again, and the counters freeze one frame in.
    private bool RenegotiateOutputType(IMFTransform encoder)
    {
        for (int i = 0; i < 8; i++)
        {
            try
            {
                var candidate = encoder.GetOutputAvailableType(0, i);
                if (candidate is null) break;
                using (candidate)
                {
                    // A bare type from the transform carries none of our
                    // configuration. Put it back before accepting it.
                    ApplyOutputAttributes(candidate);
                    encoder.SetOutputType(0, candidate, 0);
                }
                LanLogger.Remote("encoder_output_renegotiated", reason: $"type index {i}");
                return true;
            }
            catch (SharpGenException)
            {
                // That candidate was refused; try the next one.
            }
        }
        LanLogger.Remote("error", reason: "no output type accepted after stream change");
        return false;
    }

    /// Rate-limits the log, and gives up rather than spinning.
    ///
    /// The previous version logged every failure and carried on. Driving an
    /// async MFT synchronously made that 34,731 identical E_UNEXPECTED lines in
    /// fifty seconds, a pegged core, a log with nothing else left in it, and a
    /// viewer window that said "waiting for first frame" indefinitely. A fault
    /// that repeats without recovering is not a log line; it is the end of the
    /// session, and the user is entitled to be told.
    private void NoteEncoderFailure(string stage, Result hr)
    {
        int count = Interlocked.Increment(ref _consecutiveFailures);

        if (count == 1)
        {
            LanLogger.Remote("error", reason: $"encoder {stage} 0x{hr.Code:X8}");
        }
        else if (count == MaxConsecutiveFailures)
        {
            string detail = $"{stage} failed {count}x in a row, last 0x{hr.Code:X8}";
            LanLogger.Remote("encoder_failed", reason: detail);
            // Fired from whichever thread noticed; the handler is responsible
            // for getting itself onto the right one.
            try { OnFatalError?.Invoke(detail); }
            catch (Exception ex)
            {
                LanLogger.Remote("error", reason: $"encoder fatal handler: {ex.Message}");
            }
        }
    }

    private static byte[]? CopyOut(IMFSample sample)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out IntPtr data, out _, out int current);
        try
        {
            if (current <= 0) return null;
            var managed = new byte[current];
            Marshal.Copy(data, managed, 0, current);
            return managed;
        }
        finally { buffer.Unlock(); }
    }

    public void Dispose()
    {
        var encoder = _encoder;
        if (encoder is null) return;
        _encoder = null;          // stops Encode submitting into a closing MFT

        try
        {
            encoder.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            encoder.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);

            // An async MFT answers a drain with METransformDrainComplete, on the
            // pump thread. Waiting for it is not politeness — it is what makes
            // it safe to release a COM object the pump is sitting inside.
            if (_pump is not null) _drainComplete.Wait(TimeSpan.FromMilliseconds(500));

            encoder.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
        }
        catch { /* tearing down an encoder that already faulted is fine */ }

        try { _stats?.Dispose(); } catch { }
        _stats = null;
        _statsQuietTicks = int.MaxValue;   // force the closing line out
        LogStats();

        bool pumpStopped = StopEventPump();

        if (_codecApi is not null)
        {
            Marshal.ReleaseComObject(_codecApi);
            _codecApi = null;
        }

        if (pumpStopped)
        {
            _events?.Dispose();
            _events = null;
            encoder.Dispose();
        }
        else
        {
            // The pump did not come back, which means it is still inside
            // GetEvent on this object. Releasing it now would free memory the
            // other thread is about to touch, and the crash would land
            // somewhere unrelated minutes later. Leaking one encoder at
            // teardown is the cheaper of the two.
            LanLogger.Remote("error",
                reason: "encoder event pump did not stop; leaking the transform rather than "
                      + "releasing it underneath the thread");
        }

        // _inputCredits is deliberately not disposed: Encode may be sitting in
        // its Wait right now, and disposing a SemaphoreSlim under a waiter
        // throws in the waiter's thread. It holds no unmanaged handle unless
        // AvailableWaitHandle is touched, which nothing here does.
        _drainComplete.Dispose();
    }

    /// Returns false if the pump thread would not come back.
    private bool StopEventPump()
    {
        var pump = _pump;
        if (pump is null) return true;
        _pump = null;

        _pumping = false;

        // A thread cannot join itself. Callers are told to get off the pump
        // before tearing down, but a handler that ignores that should not take
        // the process with it.
        if (pump == Thread.CurrentThread) return false;

        // The pump is blocked in GetEvent. Posting an event is the only thing
        // that reliably returns it; the flag alone would never be re-read.
        try { _events?.QueueEvent((int)MediaEventTypes.TransformUnknown, Guid.Empty, Result.Ok, null); }
        catch { /* the generator may already be shut down, which also wakes it */ }

        return pump.Join(TimeSpan.FromSeconds(2));
    }
}
