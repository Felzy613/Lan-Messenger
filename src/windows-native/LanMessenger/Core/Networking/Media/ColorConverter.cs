using LanMessenger.Core.Services;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LanMessenger.Core.Networking.Media;

// BGRA off the desktop, NV12 into the encoder.
//
// Desktop Duplication hands back a BGRA texture; the H.264 encoder wants NV12.
// Somebody has to convert, and where that happens decides whether this is a
// remote desktop or a slideshow: on the GPU through the D3D11 video processor it
// is effectively free, and on the CPU a 1080p frame is about four megabytes of
// per-pixel arithmetic thirty times a second.
//
// **The colour space must be stated, not assumed.** This is the bug the plan
// singles out and it is invisible unless you look for it: an unsignalled
// full-range conversion produces washed-out blacks and clipped whites on the far
// side, and nothing in the pipeline reports anything wrong. The encoder is
// configured for 16-235 BT.709 to match the macOS side, so the conversion has to
// produce exactly that — hence both colour spaces are set explicitly rather than
// left to the driver's default.
//
// The CPU fallback exists because `VideoProcessorBlt` is not available
// everywhere — the Basic Render Driver has no video processor at all, and some
// virtualised GPUs decline. A slow session beats a black one.

public sealed class ColorConverter : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly int _width;
    private readonly int _height;

    private ID3D11VideoDevice? _videoDevice;
    private ID3D11VideoContext? _videoContext;
    private ID3D11VideoProcessor? _processor;
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11Texture2D? _nv12Texture;
    private ID3D11Texture2D? _staging;
    private bool _gpuPathUnavailable;

    public ColorConverter(ID3D11Device device, ID3D11DeviceContext context, int width, int height)
    {
        _device = device;
        _context = context;
        _width = width;
        _height = height;
        TryInitialiseGpuPath();
    }

    private void TryInitialiseGpuPath()
    {
        try
        {
            _videoDevice = _device.QueryInterfaceOrNull<ID3D11VideoDevice>();
            _videoContext = _context.QueryInterfaceOrNull<ID3D11VideoContext>();
            if (_videoDevice is null || _videoContext is null)
            {
                _gpuPathUnavailable = true;
                LanLogger.Remote("encoder_property_unsupported",
                    reason: "no D3D11 video processor; converting on the CPU");
                return;
            }

            var description = new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputWidth = (uint)_width,
                InputHeight = (uint)_height,
                OutputWidth = (uint)_width,
                OutputHeight = (uint)_height,
                Usage = VideoUsage.PlaybackNormal,
            };
            _enumerator = _videoDevice.CreateVideoProcessorEnumerator(description);
            _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

            // The part that matters. Both sides stated explicitly: the desktop
            // arrives full-range RGB, and the encoder was configured for
            // 16-235 BT.709, so the conversion has to be told to produce that.
            // Leaving either to the default is how blacks come out washed out.
            _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0,
                new VideoProcessorColorSpace
                {
                    Usage = 0,          // playback
                    RGB_Range = 0,      // full-range RGB in
                    YCbCr_Matrix = 1,   // BT.709
                    YCbCr_xvYCC = 0,
                    Nominal_Range = 2,  // 16-235 out
                });
            _videoContext.VideoProcessorSetOutputColorSpace(_processor,
                new VideoProcessorColorSpace
                {
                    Usage = 0,
                    RGB_Range = 0,
                    YCbCr_Matrix = 1,
                    YCbCr_xvYCC = 0,
                    Nominal_Range = 2,
                });

            _nv12Texture = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
            });

            _staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
        }
        catch (Exception ex)
        {
            _gpuPathUnavailable = true;
            LanLogger.Remote("encoder_property_unsupported",
                reason: $"video processor unavailable ({ex.Message}); converting on the CPU");
        }
    }

    /// Converts one BGRA desktop texture to NV12 bytes.
    ///
    /// `stride` comes back as the luma row pitch, which the encoder needs: the
    /// mapped rows are padded for alignment and are not the same as the width.
    public byte[]? Convert(ID3D11Texture2D bgra, out int stride)
    {
        stride = _width;
        if (_gpuPathUnavailable) return ConvertOnCpu(bgra, out stride);

        try
        {
            using var inputView = _videoDevice!.CreateVideoProcessorInputView(
                bgra, _enumerator!, new VideoProcessorInputViewDescription
                {
                    ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
                });

            using var outputView = _videoDevice.CreateVideoProcessorOutputView(
                _nv12Texture!, _enumerator!, new VideoProcessorOutputViewDescription
                {
                    ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                });

            var stream = new VideoProcessorStream
            {
                Enable = true,
                OutputIndex = 0,
                InputFrameOrField = 0,
                PastFrames = 0,
                FutureFrames = 0,
                InputSurface = inputView,
            };

            // (processor, outputView, outputFrame, streamCount, streams)
            _videoContext!.VideoProcessorBlt(_processor!, outputView, 0, 1, [stream]);
            _context.CopyResource(_staging!, _nv12Texture!);

            return ReadStaging(out stride);
        }
        catch (Exception ex)
        {
            // One failure is enough to stop trying. A per-frame exception on a
            // 30 fps path costs more than the conversion it is failing at.
            _gpuPathUnavailable = true;
            LanLogger.Remote("error", reason: $"VideoProcessorBlt failed ({ex.Message}); CPU fallback");
            return ConvertOnCpu(bgra, out stride);
        }
    }

    /// One buffer, grown on demand and reused for every frame.
    ///
    /// Allocating a fresh NV12 array per frame is roughly 80MB a second at 1080p
    /// — all of it Large Object Heap, which is swept only on a gen2 collection.
    /// The process reached 32GB of private commit in ten minutes and starved the
    /// machine badly enough to crash unrelated builds.
    ///
    /// Safe because CapturedFrame.Nv12 is consumed by the encoder inside the
    /// same capture-loop iteration that produced it.
    private byte[] _frameBuffer = [];

    private byte[] Rent(int length)
    {
        if (_frameBuffer.Length < length) _frameBuffer = new byte[length];
        return _frameBuffer;
    }

    private byte[]? ReadStaging(out int stride)
    {
        stride = _width;
        var mapped = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            stride = (int)mapped.RowPitch;
            // NV12: a full-height luma plane then a half-height interleaved
            // chroma plane, both at the same row pitch.
            int length = stride * _height * 3 / 2;
            var managed = Rent(length);
            Marshal.Copy(mapped.DataPointer, managed, 0, length);
            return managed;
        }
        finally { _context.Unmap(_staging!, 0); }
    }

    /// The fallback. Correct, and slow enough that it should only ever run on a
    /// machine with no video processor at all.
    private byte[]? ConvertOnCpu(ID3D11Texture2D bgra, out int stride)
    {
        stride = _width;
        try
        {
            _staging ??= _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_width, Height = (uint)_height, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging, BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
            });

            _context.CopyResource(_staging, bgra);
            var mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                return BgraToNv12(mapped.DataPointer, (int)mapped.RowPitch, _width, _height);
            }
            finally { _context.Unmap(_staging, 0); }
        }
        catch (Exception ex)
        {
            LanLogger.Remote("error", reason: $"CPU colour conversion failed: {ex.Message}");
            return null;
        }
    }

    /// BT.709, limited range — the same numbers the GPU path is configured for,
    /// so a machine that falls back does not also change colour.
    private byte[] BgraToNv12(IntPtr source, int sourceStride, int width, int height)
    {
        var output = Rent(width * height * 3 / 2);
        int chromaOffset = width * height;

        unsafe
        {
            byte* src = (byte*)source;
            for (int y = 0; y < height; y++)
            {
                byte* row = src + (long)y * sourceStride;
                for (int x = 0; x < width; x++)
                {
                    byte b = row[x * 4 + 0], g = row[x * 4 + 1], r = row[x * 4 + 2];

                    // Y' = 16 + (0.2126R + 0.7152G + 0.0722B) * 219/255
                    int luma = (int)(16.0 + (0.1826 * r + 0.6142 * g + 0.0620 * b));
                    output[y * width + x] = (byte)Math.Clamp(luma, 16, 235);

                    // Chroma is subsampled 2x2: sample the top-left of each block.
                    if ((y & 1) == 0 && (x & 1) == 0)
                    {
                        int cb = (int)(128.0 + (-0.1006 * r - 0.3386 * g + 0.4392 * b));
                        int cr = (int)(128.0 + (0.4392 * r - 0.3989 * g - 0.0403 * b));
                        int index = chromaOffset + (y / 2) * width + x;
                        output[index] = (byte)Math.Clamp(cb, 16, 240);
                        output[index + 1] = (byte)Math.Clamp(cr, 16, 240);
                    }
                }
            }
        }
        return output;
    }

    public void Dispose()
    {
        _staging?.Dispose(); _staging = null;
        _nv12Texture?.Dispose(); _nv12Texture = null;
        _processor?.Dispose(); _processor = null;
        _enumerator?.Dispose(); _enumerator = null;
        _videoContext?.Dispose(); _videoContext = null;
        _videoDevice?.Dispose(); _videoDevice = null;
    }
}
