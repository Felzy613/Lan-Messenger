using LanMessenger.Core.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Threading.Tasks;
using Windows.Foundation;
using WinRT.Interop;
using Rectangle = System.Drawing.Rectangle;

namespace LanMessenger.UI.Chat;

/// <summary>
/// Outcome of <see cref="RegionSelectOverlayWindow.SelectAsync"/>.
/// </summary>
public enum RegionSelectOutcome
{
    /// User dragged a rectangle; <see cref="RegionSelectResult.Region"/> is set.
    Region,
    /// User clicked without dragging — capture the whole display as-is.
    FullDisplay,
    /// User pressed Escape — abandon the screenshot entirely.
    Cancelled,
}

/// <summary>
/// Result of <see cref="RegionSelectOverlayWindow.SelectAsync"/>.
/// </summary>
public readonly record struct RegionSelectResult(RegionSelectOutcome Outcome, Rectangle? Region = null)
{
    public static readonly RegionSelectResult FullDisplay = new(RegionSelectOutcome.FullDisplay);
    public static readonly RegionSelectResult Cancelled   = new(RegionSelectOutcome.Cancelled);
}

/// <summary>
/// Full-screen borderless overlay over the primary display that lets the user
/// drag a rectangle to select a screenshot region. The caller supplies the
/// path to an already-captured full-primary-display PNG; on completion this
/// window reports the outcome via <see cref="RegionSelectResult"/>.
///
/// Scope note: this covers the primary display only (see CLAUDE.md screenshot
/// feature notes). Multi-monitor region selection and per-window hover
/// highlighting are documented follow-ups.
/// </summary>
public sealed class RegionSelectOverlayWindow : Window
{
    private readonly TaskCompletionSource<RegionSelectResult> _result = new();

    private readonly Grid      _root;
    private readonly Image     _background;
    private readonly Microsoft.UI.Xaml.Shapes.Rectangle _selectionRect;

    private Point? _dragStart;
    private bool   _dragging;

    // Pixel size of the captured background image — used together with the
    // overlay root's rendered DIP size to translate pointer coordinates (which
    // are reported in DIPs) into image-pixel coordinates.
    private int _imagePixelWidth;
    private int _imagePixelHeight;

    public RegionSelectOverlayWindow(string backingImagePath)
    {
        Title = "";

        _background = new Image { Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill };
        _selectionRect = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Stroke          = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.LimeGreen),
            StrokeThickness = 2,
            Fill            = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Transparent),
            Visibility      = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Top,
        };
        var dim = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x55, 0, 0, 0)),
            IsHitTestVisible = false,
        };
        var hint = new Border
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xAA, 0, 0, 0)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 24, 0, 0),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "Drag to select an area · Click to capture the whole screen · Esc to cancel",
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.White),
                FontSize = 13,
            },
        };

        _root = new Grid { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Transparent) };
        _root.Children.Add(_background);
        _root.Children.Add(dim);
        _root.Children.Add(_selectionRect);
        _root.Children.Add(hint);

        _root.PointerPressed  += RootGrid_PointerPressed;
        _root.PointerMoved    += RootGrid_PointerMoved;
        _root.PointerReleased += RootGrid_PointerReleased;
        _root.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape });
        _root.KeyboardAccelerators[0].Invoked += (_, args) =>
        {
            args.Handled = true;
            Finish(RegionSelectResult.Cancelled);
        };

        Content = _root;

        // Borderless, topmost, covers the primary display's work area.
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable   = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);

        try
        {
            var bmp = new BitmapImage(new Uri(backingImagePath));
            bmp.ImageOpened += (_, _) =>
            {
                _imagePixelWidth  = bmp.PixelWidth;
                _imagePixelHeight = bmp.PixelHeight;
            };
            _background.Source = bmp;
        }
        catch (Exception ex)
        {
            LanLogger.Warn("Screenshot", $"region overlay background load failed: {ex.Message}");
        }

        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        AppWindow.MoveAndResize(display.OuterBounds);

        Activated += (_, _) =>
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            SetForegroundWindowInternal(hwnd);
        };
    }

    /// <summary>
    /// Shows the overlay and waits for the user to finish selecting (or cancel).
    /// See <see cref="RegionSelectResult"/> / <see cref="RegionSelectOutcome"/>
    /// for how to interpret the result.
    /// </summary>
    public Task<RegionSelectResult> SelectAsync()
    {
        Activate();
        return _result.Task;
    }

    private void RootGrid_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // Explicit capture — without it, PointerMoved/PointerReleased can be
        // routed to whatever child (background image vs. selection rectangle)
        // ends up under the cursor as the drag progresses, which was causing
        // drags to be lost and every capture to fall back to FullDisplay.
        _root.CapturePointer(e.Pointer);
        _dragStart = e.GetCurrentPoint(_root).Position;
        _dragging  = true;
        _selectionRect.Visibility = Visibility.Visible;
        _selectionRect.Width  = 0;
        _selectionRect.Height = 0;
        _selectionRect.Margin = new Thickness(_dragStart.Value.X, _dragStart.Value.Y, 0, 0);
    }

    private void RootGrid_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_dragging || _dragStart is not { } start) return;
        var pos = e.GetCurrentPoint(_root).Position;
        var x = Math.Min(start.X, pos.X);
        var y = Math.Min(start.Y, pos.Y);
        var w = Math.Abs(pos.X - start.X);
        var h = Math.Abs(pos.Y - start.Y);
        _selectionRect.Margin = new Thickness(x, y, 0, 0);
        _selectionRect.Width  = w;
        _selectionRect.Height = h;
    }

    private void RootGrid_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _root.ReleasePointerCapture(e.Pointer);
        if (!_dragging || _dragStart is not { } start) { Finish(RegionSelectResult.FullDisplay); return; }
        _dragging = false;
        var end = e.GetCurrentPoint(_root).Position;

        var x = Math.Min(start.X, end.X);
        var y = Math.Min(start.Y, end.Y);
        var w = Math.Abs(end.X - start.X);
        var h = Math.Abs(end.Y - start.Y);

        // A tiny/no drag means "capture the whole display".
        if (w < 4 || h < 4) { Finish(RegionSelectResult.FullDisplay); return; }

        // _root.ActualWidth/Height are in the same DIP space as the pointer
        // coordinates above, so scale against those rather than the work
        // area's physical pixel size (which is off by the DPI scale factor).
        var scaleX = _imagePixelWidth  > 0 && _root.ActualWidth  > 0 ? _imagePixelWidth  / _root.ActualWidth  : 1.0;
        var scaleY = _imagePixelHeight > 0 && _root.ActualHeight > 0 ? _imagePixelHeight / _root.ActualHeight : 1.0;

        var rect = new Rectangle(
            (int)Math.Round(x * scaleX),
            (int)Math.Round(y * scaleY),
            (int)Math.Round(w * scaleX),
            (int)Math.Round(h * scaleY));
        Finish(new RegionSelectResult(RegionSelectOutcome.Region, rect));
    }

    private void Finish(RegionSelectResult result)
    {
        if (_result.Task.IsCompleted) return;
        _result.TrySetResult(result);
        try { Close(); } catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindowInternal(IntPtr hWnd);
}
