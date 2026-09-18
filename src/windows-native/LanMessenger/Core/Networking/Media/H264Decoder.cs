using LanMessenger.Core.Services;
using SharpGen.Runtime;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace LanMessenger.Core.Networking.Media;

// The Windows receive half: Annex-B off the wire to NV12 pictures.
//
// Ported from spikes/windows-mf-probe, which is working code against real
// hardware rather than a design — it decoded a 60-frame VideoToolbox stream on
// the Dell. Four things in that path each cost an iteration to find, and every
// one of them fails by emitting nothing rather than by reporting an error:
//
//  1. **Set the output type before the first ProcessOutput.** Without one the
//     decoder answers every call with MF_E_TRANSFORM_TYPE_NOT_SET (0xC00D6D60)
//     and emits nothing, forever — including the MF_E_TRANSFORM_STREAM_CHANGE
//     you were hoping to discover the real format from. Set a placeholder NV12
//     type up front and let the stream change correct it once the SPS is parsed.
//  2. **Input samples need timestamps.** Without one the decoder has no timeline
//     and buffers every access unit without ever emitting one.
//  3. **MF_E_NOTACCEPTING (0xC00D36B5) is normal flow control**, not an error.
//     It means output is waiting to be collected; drain and retry the same
//     sample.
//  4. **Split access units on slices**, not on AUDs or parameter sets — which is
//     H264Bitstream.SplitAccessUnits' job, and the reason the macOS fixture is
//     committed: VideoToolbox emits no delimiters at all.

/// One decoded picture. The buffer is NV12 and is only valid until the next
/// call — the presenter copies what it needs.
public sealed class DecodedVideoFrame
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// Carried through from the media frame header, for the latency figure.
    public required ulong CaptureUs { get; init; }
    /// NV12: a full-size luma plane followed by interleaved half-size chroma.
    public required byte[] Nv12 { get; init; }
    /// Bytes per row of the luma plane. Media Foundation pads rows, so this is
    /// not the same as Width and using Width instead skews the picture into a
    /// diagonal smear.
    public required int Stride { get; init; }
}

public sealed class H264DecoderException(string message) : Exception(message);

public sealed class H264Decoder : IDisposable
{
    private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    private const int MF_E_TRANSFORM_STREAM_CHANGE   = unchecked((int)0xC00D6D61);
    private const int MF_E_NOTACCEPTING              = unchecked((int)0xC00D36B5);
    private const int MF_E_TRANSFORM_TYPE_NOT_SET    = unchecked((int)0xC00D6D60);

    private static readonly Guid MF_MT_MAJOR_TYPE     = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE        = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MF_MT_FRAME_SIZE     = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MFMediaType_Video    = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_H264   = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_NV12   = new("3231564e-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFT_CATEGORY_VIDEO_DECODER = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");

    // Written out literally rather than taken from Vortice's constants, exactly
    // as the WS0 spike does — it removes any dependence on how the binding
    // happens to name or expose them, which is what these cost an iteration on.
    private const uint MFT_ENUM_FLAG_SYNCMFT       = 0x00000001;
    private const uint MFT_ENUM_FLAG_HARDWARE      = 0x00000004;
    private const uint MFT_ENUM_FLAG_SORTANDFILTER = 0x00000040;

    private IMFTransform? _decoder;
    private long _sampleIndex;
    private bool _needsKeyframe = true;
    private bool _keyframeRequested;
    private int _width;
    private int _height;

    /// Raised when the stream cannot progress without an IDR. The session turns
    /// this into a keyframe_request. Debounced: once per stall, not per frame.
    public Action<string>? OnKeyframeNeeded { get; set; }
    /// Raised the first time the picture size is known and on every change.
    public Action<int, int>? OnDimensionsChanged { get; set; }

    public int Width => _width;
    public int Height => _height;

    public H264Decoder()
    {
        MediaFactory.MFStartup(false).CheckError();
        _decoder = CreateDecoder();
    }

    private IMFTransform CreateDecoder()
    {
        // Hardware first, software second. A machine with no H.264 decoder at
        // all is a Windows N or KN SKU, and there is nothing to fall back to.
        var transform = EnumerateDecoder(hardware: true) ?? EnumerateDecoder(hardware: false)
            ?? throw new H264DecoderException(
                "no H.264 decoder MFT — a Windows N/KN SKU without the media feature pack");

        using (var inType = MediaFactory.MFCreateMediaType())
        {
            inType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            inType.Set(MF_MT_SUBTYPE, MFVideoFormat_H264);
            inType.Set(MF_MT_INTERLACE_MODE, 2u);   // MFVideoInterlace_Progressive
            transform.SetInputType(0, inType, 0);
        }

        // The placeholder. Not optional — see note 1 in the header.
        if (!NegotiateOutput(transform))
        {
            transform.Dispose();
            throw new H264DecoderException("the decoder offered no NV12 output type");
        }

        transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        return transform;
    }

    private static IMFTransform? EnumerateDecoder(bool hardware)
    {
        uint flags = hardware
            ? MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER
            : MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER;

        // Input type, not output: we are looking for something that *accepts*
        // H.264. The encoder search is the mirror image and passes it as the
        // output argument instead.
        var input = new RegisterTypeInfo
        {
            GuidMajorType = MFMediaType_Video,
            GuidSubtype = MFVideoFormat_H264,
        };

        try
        {
            // MFTEnumEx hands back a CoTaskMem array of IMFActivate*, which has
            // to be walked and freed by hand — Vortice does not wrap that.
            MediaFactory.MFTEnumEx(MFT_CATEGORY_VIDEO_DECODER, flags, input, null,
                                   out IntPtr array, out uint count);
            if (array == IntPtr.Zero || count == 0) return null;

            try
            {
                IntPtr first = Marshal.ReadIntPtr(array);
                if (first == IntPtr.Zero) return null;
                // Everything past the first still holds a reference.
                for (uint i = 1; i < count; i++)
                {
                    IntPtr p = Marshal.ReadIntPtr(array, (int)(i * (uint)IntPtr.Size));
                    if (p != IntPtr.Zero) Marshal.Release(p);
                }
                using var activate = new IMFActivate(first);
                return activate.ActivateObject<IMFTransform>();
            }
            finally { Marshal.FreeCoTaskMem(array); }
        }
        catch (Exception ex)
        {
            LanLogger.Remote("error", reason: $"decoder enumeration failed: {ex.Message}");
            return null;
        }
    }

    /// Picks the decoder's NV12 output type and applies it, recording the frame
    /// size it agreed to. Called once at startup with a placeholder size, and
    /// again on every MF_E_TRANSFORM_STREAM_CHANGE once the real SPS is parsed.
    private bool NegotiateOutput(IMFTransform transform)
    {
        for (int i = 0; ; i++)
        {
            IMFMediaType candidate;
            try { candidate = transform.GetOutputAvailableType(0, i); }
            catch { return false; }

            bool isNv12;
            try { isNv12 = candidate.GetGUID(MF_MT_SUBTYPE) == MFVideoFormat_NV12; }
            catch { candidate.Dispose(); continue; }

            if (!isNv12) { candidate.Dispose(); continue; }

            transform.SetOutputType(0, candidate, 0);
            try
            {
                ulong packed = candidate.GetUInt64(MF_MT_FRAME_SIZE);
                int width = (int)(packed >> 32), height = (int)(packed & 0xFFFFFFFF);
                if (width != _width || height != _height)
                {
                    _width = width;
                    _height = height;
                    LanLogger.Remote("decoder_format", reason: $"{width}x{height}");
                    OnDimensionsChanged?.Invoke(width, height);
                }
            }
            catch { /* a type without a frame size is still usable; the stream
                       change will bring one. */ }

            candidate.Dispose();
            return true;
        }
    }

    /// Decodes one reassembled video frame, emitting every picture that falls
    /// out. Normally one, but the decoder may hold frames and release several.
    public void Decode(ReadOnlySpan<byte> annexB, ulong captureUs, Action<DecodedVideoFrame> onFrame)
    {
        var decoder = _decoder ?? throw new ObjectDisposedException(nameof(H264Decoder));

        foreach (var accessUnit in H264Bitstream.SplitAccessUnits(annexB))
        {
            // A P-frame handed to a cold decoder is not a recoverable glitch; it
            // is garbage propagating until the next IDR.
            if (_needsKeyframe)
            {
                if (!H264Bitstream.AnnexBContainsKeyframe(accessUnit))
                {
                    RequestKeyframe("awaiting_idr");
                    continue;
                }
                _needsKeyframe = false;
                _keyframeRequested = false;
            }

            FeedSample(decoder, accessUnit, captureUs, onFrame);
        }
    }

    private void FeedSample(IMFTransform decoder, byte[] accessUnit, ulong captureUs,
                            Action<DecodedVideoFrame> onFrame)
    {
        using var buffer = MediaFactory.MFCreateMemoryBuffer(accessUnit.Length);
        unsafe
        {
            buffer.Lock(out IntPtr data, out _, out _);
            accessUnit.AsSpan().CopyTo(new Span<byte>((void*)data, accessUnit.Length));
            buffer.Unlock();
            buffer.CurrentLength = accessUnit.Length;
        }

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        // Without a timestamp the decoder buffers every access unit forever.
        // 100ns units, the Media Foundation clock.
        sample.SampleTime = _sampleIndex * 10_000_000L / 30;
        sample.SampleDuration = 10_000_000L / 30;
        _sampleIndex++;

        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                decoder.ProcessInput(0, sample, 0);
                break;
            }
            catch (SharpGenException ex) when (ex.ResultCode.Code == MF_E_NOTACCEPTING)
            {
                // Normal flow control: the decoder wants its output collected
                // before it will take more input.
                Drain(decoder, captureUs, onFrame);
            }
            catch (SharpGenException ex)
            {
                LanLogger.Remote("error", reason: $"ProcessInput 0x{ex.ResultCode.Code:X8}");
                Reset("process_input_failed");
                return;
            }
        }

        Drain(decoder, captureUs, onFrame);
    }

    private void Drain(IMFTransform decoder, ulong captureUs, Action<DecodedVideoFrame> onFrame)
    {
        while (true)
        {
            var info = decoder.GetOutputStreamInfo(0);
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

            var hr = decoder.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);

            if (hr.Code == MF_E_TRANSFORM_NEED_MORE_INPUT)
            {
                output.Sample?.Dispose();
                return;
            }
            if (hr.Code == MF_E_TRANSFORM_STREAM_CHANGE)
            {
                output.Sample?.Dispose();
                // The decoder has parsed the SPS and knows the real size now, so
                // the placeholder set at startup is replaced here.
                if (!NegotiateOutput(decoder)) return;
                continue;
            }
            if (hr.Code == MF_E_TRANSFORM_TYPE_NOT_SET)
            {
                // Should be impossible — the type is set at construction — but
                // silence is this decoder's failure mode, so say so rather than
                // spin.
                output.Sample?.Dispose();
                LanLogger.Remote("error", reason: "output type not set; decoder will emit nothing");
                return;
            }
            if (hr.Failure)
            {
                output.Sample?.Dispose();
                LanLogger.Remote("error", reason: $"ProcessOutput 0x{hr.Code:X8}");
                Reset("process_output_failed");
                return;
            }
            if (output.Sample is null) return;

            using (output.Sample)
            {
                var frame = CopyOut(output.Sample, captureUs);
                if (frame is not null) onFrame(frame);
            }
        }
    }

    /// Copies one NV12 picture out of the sample.
    ///
    /// The stride is read from the buffer rather than assumed equal to the
    /// width. Media Foundation pads rows for alignment, and a presenter that
    /// treats stride as width draws the picture as a diagonal smear — which
    /// looks like a corrupt stream rather than a layout bug.
    private DecodedVideoFrame? CopyOut(IMFSample sample, ulong captureUs)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        int stride = _width;
        try
        {
            using var buffer2d = buffer.QueryInterface<IMF2DBuffer>();
            buffer2d.Lock2D(out IntPtr scan0, out stride);
            try
            {
                if (stride <= 0) stride = _width;
                int length = stride * _height * 3 / 2;
                var managed = new byte[length];
                Marshal.Copy(scan0, managed, 0, length);
                return new DecodedVideoFrame
                {
                    Width = _width, Height = _height, CaptureUs = captureUs,
                    Nv12 = managed, Stride = stride,
                };
            }
            finally { buffer2d.Unlock2D(); }
        }
        catch (SharpGenException)
        {
            // Not every buffer exposes IMF2DBuffer. Fall back to the flat one,
            // where the stride is the width by definition.
            buffer.Lock(out IntPtr data, out _, out int current);
            try
            {
                if (current <= 0) return null;
                var managed = new byte[current];
                Marshal.Copy(data, managed, 0, current);
                return new DecodedVideoFrame
                {
                    Width = _width, Height = _height, CaptureUs = captureUs,
                    Nv12 = managed, Stride = _width,
                };
            }
            finally { buffer.Unlock(); }
        }
    }

    /// Everything after a flush or an error is undecodable until an IDR, so this
    /// also asks for one.
    public void Reset(string reason)
    {
        _needsKeyframe = true;
        _keyframeRequested = false;
        RequestKeyframe(reason);
    }

    private void RequestKeyframe(string reason)
    {
        if (_keyframeRequested) return;
        _keyframeRequested = true;
        LanLogger.Remote("decoder_needs_keyframe", reason: reason);
        OnKeyframeNeeded?.Invoke(reason);
    }

    public void Dispose()
    {
        if (_decoder is null) return;
        try
        {
            _decoder.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            _decoder.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
        }
        catch { /* tearing down a decoder that has already faulted is fine */ }
        _decoder.Dispose();
        _decoder = null;
    }
}
