namespace LanMessenger.Core.Networking.Media;

// Display for the viewer half of a session.
//
// The interface exists so the session can run without a window — a test, or a
// headless conformance run, substitutes a recorder — and so the eventual
// swap-chain presenter is a swap rather than a rewrite.
//
// **The WriteableBitmap implementation is deliberately the boring one, and it is
// deliberately first.** The plan is explicit about why: it is about fifty lines,
// it always works, and it costs 30-60% of a core at 1080p30. A separate Win32
// window with CreateSwapChainForHwnd is faster and skips WinUI COM interop
// entirely — but writing it first would mean the whole pipeline is blocked on
// presentation, and a pipeline that cannot be seen cannot be debugged.
// ISwapChainPanelNative is also not projected to C#, and its WinUI 3 IID differs
// from the UWP one, which is a baffling failure to walk into before there is
// anything to look at.
//
// The NV12 to BGRA conversion here is the cost. It is per-pixel CPU work on the
// display path, which is exactly what the swap-chain presenter would remove by
// handing NV12 straight to the GPU.

/// One decoded picture. NV12, and only valid until the next call — the presenter
/// copies what it needs.
///
/// Lives here rather than with the decoder on purpose: it is a plain value with
/// no Media Foundation in it, so the presentation arithmetic stays testable on a
/// machine that has no Media Foundation at all.
public sealed class DecodedVideoFrame
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// Carried through from the media frame header, for the latency figure.
    public required ulong CaptureUs { get; init; }
    /// NV12: a full-size luma plane followed by interleaved half-size chroma.
    public required byte[] Nv12 { get; init; }
    /// Bytes per row of the luma plane. Media Foundation pads rows, so this is
    /// not the same as Width, and using Width instead skews the picture into a
    /// diagonal smear.
    public required int Stride { get; init; }

    /// Rows in the luma plane as the decoder laid it out, which is not Height.
    ///
    /// H.264 codes in 16-pixel macroblocks, so a 1080-line picture lives in a
    /// 1088-line surface, and the chroma plane begins after all 1088 rows.
    /// Height is what to *display*; this is where the buffer actually divides.
    /// Confusing the two puts the chroma read eight rows out of place — the
    /// picture stays perfectly sharp and every colour in it is wrong.
    public required int SurfaceHeight { get; init; }
}

public interface IVideoPresenter
{
    /// Called with a decoded picture. Implementations may drop it; they must ask
    /// for a keyframe when they do.
    void Present(DecodedVideoFrame frame);
    /// Drops anything queued. Callers must follow with a keyframe request.
    void Flush();
    /// Clears the surface entirely, for session end.
    void Clear();
    /// Fires when the presenter needs an IDR to make progress. Debounced.
    Action<string>? OnKeyframeNeeded { get; set; }
}

/// Converts NV12 to BGRA. Separated from the presenter so the arithmetic is
/// testable without a window — it is the one part of the display path that can
/// be wrong in a way that still produces a picture.
public static class Nv12Converter
{
    /// Writes BGRA into `destination`, which must be width * height * 4 bytes.
    ///
    /// `stride` is the luma row pitch and is usually larger than the width:
    /// Media Foundation pads rows for alignment. Treating stride as width is the
    /// classic failure here and it does not look like a stride bug — it looks
    /// like a corrupt stream, because each row lands progressively further left
    /// and the picture shears into a diagonal smear.
    /// `surfaceHeight` is the row count the planes were laid out with, which is
    /// at least `height` and usually more — see DecodedVideoFrame.SurfaceHeight.
    /// It decides where chroma begins; `height` decides how much is drawn.
    public static void ToBgra(ReadOnlySpan<byte> nv12, int stride, int width, int height,
                              Span<byte> destination, int surfaceHeight = 0)
    {
        if (stride < width) stride = width;
        if (surfaceHeight < height) surfaceHeight = height;
        int chromaOffset = stride * surfaceHeight;

        for (int y = 0; y < height; y++)
        {
            int lumaRow = y * stride;
            int chromaRow = chromaOffset + (y / 2) * stride;
            int outRow = y * width * 4;

            for (int x = 0; x < width; x++)
            {
                int lumaIndex = lumaRow + x;
                // Chroma is 2x2 subsampled and interleaved: Cb then Cr.
                int chromaIndex = chromaRow + (x & ~1);
                if (lumaIndex >= nv12.Length || chromaIndex + 1 >= nv12.Length) return;

                // BT.709, limited range — the inverse of what the capture side
                // encodes. Getting the matrix right but the range wrong is the
                // washed-out-blacks bug seen from the other end.
                int c = nv12[lumaIndex] - 16;
                int d = nv12[chromaIndex] - 128;
                int e = nv12[chromaIndex + 1] - 128;

                int r = (298 * c + 459 * e + 128) >> 8;
                int g = (298 * c - 55 * d - 136 * e + 128) >> 8;
                int b = (298 * c + 541 * d + 128) >> 8;

                int o = outRow + x * 4;
                if (o + 3 >= destination.Length) return;
                destination[o + 0] = Clamp(b);
                destination[o + 1] = Clamp(g);
                destination[o + 2] = Clamp(r);
                destination[o + 3] = 255;
            }
        }
    }

    private static byte Clamp(int value) =>
        value < 0 ? (byte)0 : value > 255 ? (byte)255 : (byte)value;

    /// The BGRA buffer size a frame of this size needs.
    public static int BgraLength(int width, int height) => width * height * 4;
}
