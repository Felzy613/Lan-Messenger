using LanMessenger.Core.Networking.Media;
using LanMessenger.Core.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;

namespace LanMessenger.UI.RemoteDesktop;

// The window a Windows viewer watches in, and the WriteableBitmap presenter that
// fills it.
//
// A separate Window rather than a page, following MediaPreviewWindow: a viewer
// is naturally its own window, and a remote screen inside the chat layout would
// be both cramped and confusing.
//
// The threading rule is the one that bites. Decoded frames arrive on the session
// read thread; WriteableBitmap must be touched only on the UI thread. So every
// frame crosses a DispatcherQueue hop, and the presenter **drops** rather than
// queues when the UI thread is behind — a backlog of stale frames is latency a
// viewer can never pay off, and on live screen content the newest frame is the
// only one anybody wants.
//
// What crosses that hop is deliberately as little as possible: the NV12 to BGRA
// conversion happens on the decode thread, and the UI thread does only the
// bitmap write and the invalidate. The conversion is two million pixels of
// per-pixel work, and on the UI thread it decides the display rate for the
// whole app rather than just for this window.

public sealed partial class RemoteViewerWindow : Window, IVideoPresenter
{
    private readonly DispatcherQueue _dispatcher;
    private WriteableBitmap? _bitmap;
    private byte[]? _bgra;
    private int _width;
    private int _height;
    /// The size _bgra was allocated for. Written on the decode thread, read on
    /// the UI thread, and only ever while _framePending gates the two apart.
    private int _bgraWidth;
    private int _bgraHeight;

    private long _dropped;
    private long _presented;
    private long _latencySumMs;
    private long _latencySamples;
    private long _latencyMaxMs;
    private ulong _awaitingCompositionUs;
    private long _compositeSumMs;
    private long _compositeSamples;
    private long _compositeMaxMs;
    private readonly Stopwatch _statsClock = Stopwatch.StartNew();

    /// Set while a frame is already on its way to the UI thread. The next one
    /// is dropped rather than queued: stale frames are worse than missing ones.
    private int _framePending;
    private bool _keyframeRequested;
    private bool _closed;

    public Action<string>? OnKeyframeNeeded { get; set; }
    /// Raised when the user closes the window. Closing ends the session — a
    /// viewer window that is gone while a host's screen is still captured is
    /// exactly the state the watchdog exists to catch.
    public Action? OnClosed { get; set; }

    public RemoteViewerWindow(string peerName)
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Title = $"{peerName} — screen";

        // The compositor is the last link, and until now it was outside every
        // measurement. Writing the bitmap and calling Invalidate does not put a
        // pixel on the glass — it marks the surface dirty, and the render thread
        // uploads and composites it on some later vsync. Measuring only as far
        // as Invalidate reports the latency of the half of the pipeline we
        // wrote, which is not the half the user is looking at.
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnComposited;

        Closed += (_, _) =>
        {
            _closed = true;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnComposited;
            var handler = OnClosed;
            OnClosed = null;
            handler?.Invoke();
        };
    }

    // MARK: - IVideoPresenter

    public void Present(DecodedVideoFrame frame)
    {
        if (_closed || frame.Width <= 0 || frame.Height <= 0) return;

        // Drop rather than queue. This is live screen content: a frame that has
        // been waiting is already wrong, and the backlog only grows.
        //
        // Dropping here does NOT need a keyframe, and asking for one was a bug.
        // The decoder has already decoded this picture and holds it as the
        // reference for the next; throwing away our *copy* of it costs the
        // decoder nothing. Requesting an IDR every time the UI thread blinked
        // meant the slower the display got, the more full-size keyframes the
        // encoder was told to emit — the one response guaranteed to make it
        // slower still.
        if (Interlocked.Exchange(ref _framePending, 1) == 1)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        try
        {
            // Converted here, on the decode thread, not on the UI thread.
            //
            // This is per-pixel work across two million pixels. On the UI
            // thread it competes with the compositor and with every other
            // window the app owns, and it sets the ceiling on how fast frames
            // can be shown. Moving it off is the difference between the
            // display rate being ours and it being XAML's.
            if (_bgra is null || _bgraWidth != frame.Width || _bgraHeight != frame.Height)
            {
                _bgraWidth = frame.Width;
                _bgraHeight = frame.Height;
                _bgra = new byte[Nv12Converter.BgraLength(_bgraWidth, _bgraHeight)];
            }
            Nv12Converter.ToBgra(frame.Nv12, frame.Stride, _bgraWidth, _bgraHeight, _bgra,
                                 frame.SurfaceHeight);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _framePending, 0);
            LanLogger.Remote("error", reason: $"colour conversion failed: {ex.Message}");
            return;
        }

        // Only the value is carried across, never the frame: DecodedVideoFrame
        // is valid until the next decode call and the UI thread will read it
        // later than that.
        ulong captureUs = frame.CaptureUs;
        if (!_dispatcher.TryEnqueue(DispatcherQueuePriority.High, () => Render(captureUs)))
        {
            Interlocked.Exchange(ref _framePending, 0);
        }
    }

    private void Render(ulong captureUs)
    {
        try
        {
            if (_closed || _bgra is null) return;

            if (_bitmap is null || _width != _bgraWidth || _height != _bgraHeight)
            {
                _width = _bgraWidth;
                _height = _bgraHeight;
                _bitmap = new WriteableBitmap(_width, _height);
                VideoImage.Source = _bitmap;
                ResizeToAspect();
                LanLogger.Remote("viewer_format", reason: $"{_width}x{_height}");
            }

            using (var stream = _bitmap!.PixelBuffer.AsStream())
            {
                stream.Write(_bgra, 0, _bgra.Length);
            }
            _bitmap.Invalidate();
            // Closed out by the next CompositionTarget.Rendering, which is the
            // first moment this frame can actually be on screen.
            _awaitingCompositionUs = captureUs;

            if (WaitingText.Visibility == Visibility.Visible)
            {
                WaitingText.Visibility = Visibility.Collapsed;
            }
            _keyframeRequested = false;
            NotePresented(captureUs);
        }
        catch (Exception ex)
        {
            // Nothing that runs inside a WinUI callback may throw — CLAUDE.md
            // carries two separate scars from exactly that.
            LanLogger.Remote("error", reason: $"present failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _framePending, 0);
        }
    }

    /// Fires once per compositor frame. The first one after a bitmap write is
    /// the earliest that write can have reached the glass, so the gap between
    /// capture and here is the real end-to-end figure — everything the shorter
    /// measurement leaves off the end.
    private void OnComposited(object? sender, object e)
    {
        ulong captureUs = _awaitingCompositionUs;
        if (captureUs == 0) return;
        _awaitingCompositionUs = 0;

        long nowUs = Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1_000_000L);
        long ms = (nowUs - (long)captureUs) / 1000;
        if (ms < 0) return;
        _compositeSumMs += ms;
        _compositeSamples++;
        if (ms > _compositeMaxMs) _compositeMaxMs = ms;
    }

    /// End-to-end latency, measured rather than reasoned about.
    ///
    /// capture_us has ridden along with the frame since Desktop Duplication
    /// handed it over, through encode, decode and conversion, so the subtraction
    /// here is the whole pipeline and nothing else. Without it "it feels laggy"
    /// and "it feels smooth" are the only two available measurements.
    private void NotePresented(ulong captureUs)
    {
        _presented++;
        long nowUs = Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1_000_000L);
        long latencyMs = ((long)nowUs - (long)captureUs) / 1000;
        if (latencyMs >= 0)
        {
            _latencySumMs += latencyMs;
            _latencySamples++;
            if (latencyMs > _latencyMaxMs) _latencyMaxMs = latencyMs;
        }

        if (_statsClock.ElapsedMilliseconds < 2000) return;

        double seconds = _statsClock.ElapsedMilliseconds / 1000.0;
        long avg = _latencySamples > 0 ? _latencySumMs / _latencySamples : -1;
        long onScreenAvg = _compositeSamples > 0 ? _compositeSumMs / _compositeSamples : -1;
        LanLogger.Remote("viewer_stats",
            reason: $"presented={_presented} dropped={Interlocked.Read(ref _dropped)} "
                  + $"fps={_presented / seconds:F1} latency_ms_avg={avg} latency_ms_max={_latencyMaxMs} "
                  + $"onscreen_ms_avg={onScreenAvg} onscreen_ms_max={_compositeMaxMs}");

        _statsClock.Restart();
        _presented = 0;
        _latencySumMs = 0;
        _latencySamples = 0;
        _latencyMaxMs = 0;
        _compositeSumMs = 0;
        _compositeSamples = 0;
        _compositeMaxMs = 0;
    }

    public void Flush()
    {
        RequestKeyframe("flush");
    }

    public void Clear()
    {
        if (_closed) return;
        _dispatcher.TryEnqueue(() =>
        {
            VideoImage.Source = null;
            _bitmap = null;
            _bgra = null;
            _bgraWidth = 0;
            _bgraHeight = 0;
            WaitingText.Visibility = Visibility.Visible;
        });
    }

    /// What the host says about its own state — the secure desktop being up, an
    /// elevated window having focus, the machine being locked. Without this the
    /// viewer sees a frozen picture and no reason for it.
    public void ShowHostState(RemoteHostState state)
    {
        if (_closed) return;
        _dispatcher.TryEnqueue(() =>
        {
            if (!state.IsNotable)
            {
                StatusBar.Visibility = Visibility.Collapsed;
                return;
            }
            StatusText.Text = state.SecureDesktop
                ? "The other machine is showing a Windows security screen, which cannot be shared."
                : state.Locked
                    ? "The other machine is locked."
                    : "An administrator window has focus. Keyboard and mouse will not reach it.";
            StatusBar.Visibility = Visibility.Visible;
        });
    }

    private void RequestKeyframe(string reason)
    {
        if (_keyframeRequested) return;
        _keyframeRequested = true;
        LanLogger.Remote("presenter_needs_keyframe", reason: reason);
        OnKeyframeNeeded?.Invoke(reason);
    }

    /// Shapes the window to the picture. A window whose aspect does not match
    /// letterboxes forever, and the user cannot tell whether that is the app or
    /// the remote screen.
    private void ResizeToAspect()
    {
        try
        {
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);

            double maxWidth = area.WorkArea.Width * 0.8;
            double maxHeight = area.WorkArea.Height * 0.8;
            double scale = Math.Min(Math.Min(maxWidth / _width, maxHeight / _height), 1.0);

            int width = Math.Max(320, (int)(_width * scale));
            int height = Math.Max(240, (int)(_height * scale));

            AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
        }
        catch (Exception ex)
        {
            // A window that is the wrong size is a nuisance; one that threw
            // during resize is a dead session.
            LanLogger.Remote("error", reason: $"viewer resize failed: {ex.Message}");
        }
    }
}
