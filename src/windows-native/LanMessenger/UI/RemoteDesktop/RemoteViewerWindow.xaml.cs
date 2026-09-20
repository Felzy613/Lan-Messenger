using LanMessenger.Core.Networking.Media;
using LanMessenger.Core.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using System.Linq;
using System.Collections.Generic;
using Microsoft.UI.Input;
using Windows.UI.Core;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Diagnostics;
using System.IO;
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
    private Stream? _pixels;
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

    /// <summary>
    /// Makes <c>now - capture_us</c> mean milliseconds whichever clock stamped
    /// the frame. See <see cref="RemoteLatencyClock"/> — across two machines the
    /// raw subtraction is the gap between their Stopwatch origins, not latency.
    /// </summary>
    private readonly RemoteLatencyClock _latencyClock = new();

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

    // ---- Input capture -----------------------------------------------------
    //
    // The mirror of RemoteInputInjector, and the easier half: nothing here is
    // dangerous on its own, because the host decides what it will act on. What
    // it must get right is geometry and the one shortcut it must never send.
    //
    // Coordinates are normalized to the video rectangle, not the control. The
    // image is Stretch=Uniform, so there are letterbox bars whenever the window
    // shape does not match the remote screen's, and a click in a bar is not a
    // click on the remote machine at all — normalizing against the control would
    // make every coordinate wrong by the width of the bars, and worse the
    // further the window is from the right aspect.

    /// Where captured input goes. Set by the controller.
    public Action<IReadOnlyList<RemoteInputRecord>>? OnInput { get; set; }

    /// Asks the host for keyboard and mouse.
    public Action? OnRequestControl { get; set; }

    private bool _capturing;
    private readonly HashSet<ushort> _heldUsages = [];

    /// <summary>True only while the host has granted control.</summary>
    public bool IsCapturing
    {
        get => _capturing;
        set
        {
            if (_capturing == value) return;
            _capturing = value;
            if (value) InputSurface.Focus(FocusState.Programmatic);
            else ReleaseHeldKeys();
            UpdateControlButton();
        }
    }

    /// <summary>The rectangle the video actually occupies inside the surface.</summary>
    private Rect VideoRect()
    {
        double surfaceWidth = InputSurface.ActualWidth, surfaceHeight = InputSurface.ActualHeight;
        if (_width <= 0 || _height <= 0 || surfaceWidth <= 0 || surfaceHeight <= 0)
            return new Rect(0, 0, surfaceWidth, surfaceHeight);

        double scale = Math.Min(surfaceWidth / _width, surfaceHeight / _height);
        double w = _width * scale, h = _height * scale;
        return new Rect((surfaceWidth - w) / 2, (surfaceHeight - h) / 2, w, h);
    }

    /// <summary>
    /// Normalized position inside the video, or null for a point in a letterbox
    /// bar — which is not a click at the edge of the remote screen, and
    /// pretending otherwise puts the pointer where the user did not aim.
    /// </summary>
    private (float X, float Y)? Normalized(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(InputSurface).Position;
        var rect = VideoRect();
        if (rect.Width <= 0 || rect.Height <= 0) return null;
        if (point.X < rect.Left || point.X > rect.Right
            || point.Y < rect.Top || point.Y > rect.Bottom) return null;

        return ((float)((point.X - rect.Left) / rect.Width),
                (float)((point.Y - rect.Top) / rect.Height));
    }

    private void Surface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_capturing || Normalized(e) is not { } p) return;
        OnInput?.Invoke([RemoteInputRecord.PointerMove(p.X, p.Y)]);
    }

    private void Surface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_capturing || Normalized(e) is not { } p) return;
        InputSurface.CapturePointer(e.Pointer);
        var properties = e.GetCurrentPoint(InputSurface).Properties;
        var button = properties.IsRightButtonPressed ? RemotePointerButton.Right
                   : properties.IsMiddleButtonPressed ? RemotePointerButton.Middle
                   : RemotePointerButton.Left;
        OnInput?.Invoke([RemoteInputRecord.PointerButton(button, true, p.X, p.Y)]);
    }

    private void Surface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_capturing || Normalized(e) is not { } p) return;
        InputSurface.ReleasePointerCapture(e.Pointer);
        var update = e.GetCurrentPoint(InputSurface).Properties.PointerUpdateKind;
        var button = update == PointerUpdateKind.RightButtonReleased ? RemotePointerButton.Right
                   : update == PointerUpdateKind.MiddleButtonReleased ? RemotePointerButton.Middle
                   : RemotePointerButton.Left;
        OnInput?.Invoke([RemoteInputRecord.PointerButton(button, false, p.X, p.Y)]);
    }

    private void Surface_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!_capturing || Normalized(e) is not { } p) return;
        // WHEEL_DELTA is one notch; the wire carries lines.
        int delta = e.GetCurrentPoint(InputSurface).Properties.MouseWheelDelta;
        if (delta == 0) return;
        OnInput?.Invoke([RemoteInputRecord.PointerScroll(0, delta / 120f, p.X, p.Y)]);
    }

    private void Surface_KeyDown(object sender, KeyRoutedEventArgs e) => ForwardKey(e, true);
    private void Surface_KeyUp(object sender, KeyRoutedEventArgs e) => ForwardKey(e, false);

    private void ForwardKey(KeyRoutedEventArgs e, bool down)
    {
        if (!_capturing) return;
        e.Handled = true;

        // From the scan code, not the VirtualKey. KeyRoutedEventArgs carries the
        // hardware scan code, so the usage can be recovered by inverting the
        // injector's own table — a second hand-written map would be a second
        // place for the same typo, and this direction decides what gets sent.
        ushort? mapped = HidKeyMap.UsageForScanCode(
            (ushort)e.KeyStatus.ScanCode, e.KeyStatus.IsExtendedKey);
        if (mapped is not { } usage) return;

        var modifiers = CurrentModifiers();

        // Never forwarded. This is the host's way out of a session they have
        // lost control of, and a viewer able to press it remotely could stop the
        // host stopping them. Refused again on the host, in the injector.
        if (down && usage == 0x29
            && modifiers.HasFlag(RemoteInputModifiers.Control)
            && modifiers.HasFlag(RemoteInputModifiers.Alt)
            && modifiers.HasFlag(RemoteInputModifiers.Shift))
        {
            LanLogger.Remote("input_withheld", reason: "kill shortcut not forwarded");
            return;
        }

        if (down) _heldUsages.Add(usage); else _heldUsages.Remove(usage);
        OnInput?.Invoke([RemoteInputRecord.Key(usage, down, e.KeyStatus.WasKeyDown, modifiers)]);
    }

    private static RemoteInputModifiers CurrentModifiers()
    {
        var modifiers = RemoteInputModifiers.None;
        if (IsDown(Windows.System.VirtualKey.Shift))   modifiers |= RemoteInputModifiers.Shift;
        if (IsDown(Windows.System.VirtualKey.Control)) modifiers |= RemoteInputModifiers.Control;
        if (IsDown(Windows.System.VirtualKey.Menu))    modifiers |= RemoteInputModifiers.Alt;
        if (IsDown(Windows.System.VirtualKey.LeftWindows) || IsDown(Windows.System.VirtualKey.RightWindows))
            modifiers |= RemoteInputModifiers.Meta;
        return modifiers;

        static bool IsDown(Windows.System.VirtualKey key) =>
            InputKeyboardSource.GetKeyStateForCurrentThread(key)
                .HasFlag(CoreVirtualKeyStates.Down);
    }

    /// <summary>
    /// Lifts anything this window believes is held.
    /// </summary>
    /// <remarks>
    /// Releasing a key outside the window means we never see the key-up, so we
    /// never send one, and the host is left holding it. Called when capture
    /// stops, when focus leaves, and at teardown.
    /// </remarks>
    private void ReleaseHeldKeys()
    {
        if (_heldUsages.Count == 0) return;
        var records = _heldUsages
            .Select(u => RemoteInputRecord.Key(u, false, false, RemoteInputModifiers.None))
            .ToList();
        _heldUsages.Clear();
        OnInput?.Invoke(records);
        LanLogger.Remote("input_released", reason: $"{records.Count} key(s) lifted by the viewer");
    }

    private void UpdateControlButton()
    {
        ControlButton.Content = _capturing ? "Controlling" : "Request Control";
        ControlButton.IsEnabled = !_capturing;
    }

    private void ControlButton_Click(object sender, RoutedEventArgs e) => OnRequestControl?.Invoke();

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
                // Opened once with the bitmap, not once per frame. AsStream
                // wraps the same underlying WinRT buffer every time, so calling
                // it per frame allocates a wrapper for a buffer we already have.
                _pixels?.Dispose();
                _pixels = _bitmap.PixelBuffer.AsStream();
                VideoImage.Source = _bitmap;
                ResizeToAspect();
                LanLogger.Remote("viewer_format", reason: $"{_width}x{_height}");
            }

            _pixels!.Seek(0, SeekOrigin.Begin);
            _pixels.Write(_bgra, 0, _bgra.Length);
            _bitmap!.Invalidate();
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
        long ms = _latencyClock.Adjust(nowUs - (long)captureUs) / 1000;
        if (ms < 0) return;
        _compositeSumMs += ms;
        _compositeSamples++;
        if (ms > _compositeMaxMs) _compositeMaxMs = ms;
    }

    /// Latency, measured rather than reasoned about.
    ///
    /// capture_us has ridden along with the frame since capture handed it over,
    /// through encode, decode and conversion, so in self-view the subtraction
    /// here is the whole pipeline and nothing else. Across two machines it is
    /// delay above the best frame of the session instead, because the stamp is
    /// on the host's clock — `RemoteLatencyClock` decides which, and the stats
    /// line says which one it printed. Without any of it, "it feels laggy" and
    /// "it feels smooth" are the only two available measurements.
    private void NotePresented(ulong captureUs)
    {
        _presented++;
        long nowUs = Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1_000_000L);
        long latencyMs = _latencyClock.Adjust(nowUs - (long)captureUs) / 1000;
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

        // Managed heap beside the process total. If gc_mb stays flat while the
        // process keeps growing, the growth is native — a COM object released
        // once too few — and no amount of staring at the C# will show it.
        long gcMb = GC.GetTotalMemory(false) / (1024 * 1024);
        long processMb = Environment.WorkingSet / (1024 * 1024);
        // Say which kind of latency these are. "shared" is the whole pipeline,
        // capture to glass; "rel" is delay above the best frame of the session,
        // because the host stamped capture_us on a clock that is not ours.
        string clock = _latencyClock.Label;
        LanLogger.Remote("viewer_stats",
            reason: $"presented={_presented} dropped={Interlocked.Read(ref _dropped)} "
                  + $"fps={_presented / seconds:F1} clock={clock} "
                  + $"latency_ms_avg={avg} latency_ms_max={_latencyMaxMs} "
                  + $"onscreen_ms_avg={onScreenAvg} onscreen_ms_max={_compositeMaxMs} "
                  + $"gc_mb={gcMb} ws_mb={processMb} gen2={GC.CollectionCount(2)}");

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
        // The next thing presented here may come from a different machine, and
        // the latency floor is only meaningful against the clock that set it.
        _latencyClock.Reset();
        _dispatcher.TryEnqueue(() =>
        {
            VideoImage.Source = null;
            _pixels?.Dispose();
            _pixels = null;
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
