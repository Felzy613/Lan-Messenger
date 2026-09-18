using LanMessenger.Core.Services;
using SharpGen.Runtime;
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
// The async trap is the expensive one. A hardware MFT refuses ProcessInput with
// MF_E_TRANSFORM_ASYNC_LOCKED (0xC00D6D77) until MF_TRANSFORM_ASYNC_UNLOCK is
// set on its attributes — and unlocking is necessary but not sufficient, because
// a real async MFT then has to be driven by METransformNeedInput and
// METransformHaveOutput events rather than by a synchronous loop. The WS0 probe
// confirmed both Quick Sync encoders on this hardware are async; it also only
// ever encoded through the *software* MFT, so the event pump below has never
// produced a frame anywhere and is the least proven code in this file.

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
    private IMFTransform? _encoder;
    private ICodecAPI? _codecApi;
    private long _sampleIndex;
    private int _forceKeyframe;

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
                            FriendlyName(activate), hardware, IsAsync(activate), true)));
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

    private static bool IsAsync(IMFActivate activate)
    {
        // An MFT that accepts the unlock attribute is telling us it is async.
        try
        {
            activate.Set(MF_TRANSFORM_ASYNC_UNLOCK, 1u);
            return true;
        }
        catch { return false; }
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
        using (var outType = MediaFactory.MFCreateMediaType())
        {
            outType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            outType.Set(MF_MT_SUBTYPE, MFVideoFormat_H264);
            outType.Set(MF_MT_AVG_BITRATE, (uint)bitrate);
            outType.Set(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
            outType.Set(MF_MT_FRAME_SIZE, Pack(_width, _height));
            outType.Set(MF_MT_FRAME_RATE, Pack(_frameRate, 1));
            outType.Set(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
            outType.Set(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_High);

            // The colour tags the macOS encoder also sets. They have to agree:
            // an unsignalled range is how blacks come out washed out and whites
            // clipped on the far side, and neither component complains.
            outType.Set(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235);
            outType.Set(MF_MT_VIDEO_PRIMARIES, MFVideoPrimaries_BT709);
            outType.Set(MF_MT_TRANSFER_FUNCTION, MFVideoTransFunc_709);
            outType.Set(MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT709);

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

        encoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        encoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    private static ulong Pack(int high, int low) =>
        ((ulong)(uint)high << 32) | (uint)low;

    /// Honours a viewer's keyframe_request. Latched rather than applied, because
    /// it rides the next frame submitted — and on a still screen, capture is
    /// silent, so there may not be one for a while.
    public void LatchKeyframe() => Interlocked.Exchange(ref _forceKeyframe, 1);

    /// Encodes one NV12 frame. `stride` is the luma row pitch, which is not
    /// necessarily the width.
    public void Encode(ReadOnlySpan<byte> nv12, int stride, ulong captureUs)
    {
        var encoder = _encoder;
        if (encoder is null) return;

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
        // Without a timestamp the encoder has no timeline. 100ns units.
        sample.SampleTime = _sampleIndex * 10_000_000L / _frameRate;
        sample.SampleDuration = 10_000_000L / _frameRate;
        _sampleIndex++;

        for (int attempt = 0; attempt < 8; attempt++)
        {
            try { encoder.ProcessInput(0, sample, 0); break; }
            catch (SharpGenException ex) when (ex.ResultCode.Code == MF_E_NOTACCEPTING)
            {
                // Normal flow control, not an error.
                Drain(encoder, captureUs);
            }
            catch (SharpGenException ex) when (ex.ResultCode.Code == MF_E_TRANSFORM_ASYNC_LOCKED)
            {
                LanLogger.Remote("error",
                    reason: "MF_E_TRANSFORM_ASYNC_LOCKED — MF_TRANSFORM_ASYNC_UNLOCK was not accepted");
                return;
            }
            catch (SharpGenException ex)
            {
                LanLogger.Remote("error", reason: $"encoder ProcessInput 0x{ex.ResultCode.Code:X8}");
                return;
            }
        }

        Drain(encoder, captureUs);
    }

    private void Drain(IMFTransform encoder, ulong captureUs)
    {
        while (true)
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

            var hr = encoder.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);

            if (hr.Code == MF_E_TRANSFORM_NEED_MORE_INPUT) { output.Sample?.Dispose(); return; }
            if (hr.Code == MF_E_TRANSFORM_STREAM_CHANGE) { output.Sample?.Dispose(); continue; }
            if (hr.Failure)
            {
                output.Sample?.Dispose();
                LanLogger.Remote("error", reason: $"encoder ProcessOutput 0x{hr.Code:X8}");
                return;
            }
            if (output.Sample is null) return;

            using (output.Sample)
            {
                var annexB = CopyOut(output.Sample);
                if (annexB is null || annexB.Length == 0) continue;

                // This encoder emits Annex-B with in-band parameter sets before
                // every IDR, which is exactly the wire format — no conversion,
                // unlike the macOS side.
                OnEncodedFrame?.Invoke(new EncodedVideoFrame
                {
                    AnnexB = annexB,
                    IsKeyframe = H264Bitstream.AnnexBContainsKeyframe(annexB),
                    CaptureUs = captureUs,
                });
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
        if (_encoder is null) return;
        try
        {
            _encoder.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            _encoder.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
            _encoder.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
        }
        catch { /* tearing down an encoder that already faulted is fine */ }

        if (_codecApi is not null)
        {
            Marshal.ReleaseComObject(_codecApi);
            _codecApi = null;
        }
        _encoder.Dispose();
        _encoder = null;
    }
}
