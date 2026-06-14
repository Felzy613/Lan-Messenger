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
/// Full-screen borderless overlay over the primary display that lets the user
/// drag a rectangle to select a screenshot region. The caller supplies the
/// path to an already-captured full-primary-display PNG; on completion this
/// window reports the selected rectangle in that image's pixel coordinates
/// (or null if the user clicked without dragging / pressed Escape, meaning
/// "use the whole display").
///
/// Scope note: this covers the primary display only (see CLAUDE.md screenshot
/// feature notes). Multi-monitor region selection and per-window hover
/// highlighting are documented follow-ups.
/// </summary>
public sealed class RegionSelectOverlayWindow : Window
{
    private readonly TaskCompletionSource<Rectangle?> _result = new();

    private readonly Grid      _root;
    private readonly Image     _background;
    private readonly Microsoft.UI.Xaml.Shapes.Rectangle _selectionRect;

    private Point? _dragStart;
    private bool   _dragging;

    // Ratio between the captured image's pixel size and this window's DIP size —
    // used to translate pointer coordinates into image-pixel coordinates.
    private double _scaleX = 1.0;
    private double _scaleY = 1.0;

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
            Finish(null);
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
                if (bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
                {
                    var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
                    _scaleX = bmp.PixelWidth  / (double)work.Width;
                    _scaleY = bmp.PixelHeight / (double)work.Height;
                }
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
    /// Returns the selected rectangle in image-pixel coordinates, or null if the
    /// user pressed Escape or clicked without dragging (caller should fall back
    /// to the full display).
    /// </summary>
    public Task<Rectangle?> SelectAsync()
    {
        Activate();
        return _result.Task;
    }

    private void RootGrid_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
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
        if (!_dragging || _dragStart is not { } start) { Finish(null); return; }
        _dragging = false;
        var end = e.GetCurrentPoint(_root).Position;

        var x = Math.Min(start.X, end.X);
        var y = Math.Min(start.Y, end.Y);
        var w = Math.Abs(end.X - start.X);
        var h = Math.Abs(end.Y - start.Y);

        // A tiny/no drag means "capture the whole display".
        if (w < 4 || h < 4) { Finish(null); return; }

        var rect = new Rectangle(
            (int)Math.Round(x * _scaleX),
            (int)Math.Round(y * _scaleY),
            (int)Math.Round(w * _scaleX),
            (int)Math.Round(h * _scaleY));
        Finish(rect);
    }

    private void Finish(Rectangle? rect)
    {
        if (_result.Task.IsCompleted) return;
        _result.TrySetResult(rect);
        try { Close(); } catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindowInternal(IntPtr hWnd);
}
