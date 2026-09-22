using LanMessenger.Core.Networking.Media;
using LanMessenger.Core.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Runtime.InteropServices;

namespace LanMessenger.UI.RemoteDesktop;

// The strip a host sees while their screen is being shared. Mirror of
// RemoteHostIndicatorView.swift.
//
// PROTOCOL.md requires it in one sentence — "while a session is live the host
// must show a persistent indicator naming the viewer and the current grant
// level, with a stop control" — and every word carries a decision:
//
//  * **Persistent** means it cannot be dismissed, only stopped. No close button,
//    no title bar, and Escape does nothing. An indicator with a close button is
//    one people close.
//  * **Naming the viewer** means the name. A host has to tell an expected
//    session from an unexpected one at a glance.
//  * **The grant level** means viewing and control read differently and
//    obviously. They are one escalation apart and wildly different in
//    consequence.
//
// And the decision that is not in the spec: **it does not move.** A draggable
// indicator is a real hole once control has been granted — a viewer holding the
// mouse could drag the host's own warning off the edge of the screen and carry
// on working unobserved, and nothing can tell an injected drag from a real one.
// So its position is re-derived from the work area rather than remembered, and
// WS_EX_NOACTIVATE keeps a click on Stop from stealing focus from whatever the
// host was doing.

public sealed partial class RemoteHostIndicatorWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;

    /// Gap from the top of the work area, clear of most title bars.
    private const int TopInset = 8;

    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly DateTime _startedAt;
    private bool _faded;

    public Action? OnStopSharing { get; set; }
    public Action? OnStopControl { get; set; }

    public RemoteHostIndicatorWindow(string peerName, RemoteGrant grant, DateTime startedAt)
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _startedAt = startedAt;

        Title = "Screen sharing";
        ExtendsContentIntoTitleBar = true;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            // Borderless, above everything, and not in the task switcher.
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        AppWindow.IsShownInSwitchers = false;

        ApplyGrant(peerName, grant);
        UpdateElapsed();
        Reposition();
        ApplyNoActivate();

        // One tick a second. Elapsed time is the quiet part of the indicator
        // that catches the session somebody forgot they left open.
        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => { UpdateElapsed(); Pulse(); };
        _timer.Start();
        Pulse();
    }

    /// Called again whenever the grant changes, so the strip follows the session
    /// rather than being rebuilt.
    public void ApplyGrant(string peerName, RemoteGrant grant)
    {
        bool controlled = grant == RemoteGrant.Control;

        HeadlineText.Text = controlled
            ? $"{peerName} is controlling your screen"
            : $"{peerName} is viewing your screen";

        // Red for control, amber for viewing. Not a subtle tint of the accent
        // colour — this is the one piece of chrome that should be slightly
        // unwelcome.
        Strip.Background = new SolidColorBrush(controlled
            ? Windows.UI.Color.FromArgb(0xFF, 0xD9, 0x30, 0x25)
            : Windows.UI.Color.FromArgb(0xFF, 0xE5, 0x8C, 0x1A));

        StopControlButton.Visibility = controlled ? Visibility.Visible : Visibility.Collapsed;
        Reposition();
    }

    private void UpdateElapsed()
    {
        // Clamped at zero: NTP corrections and sleep/wake both move the clock
        // backwards, and a negative timer looks broken.
        var span = DateTime.UtcNow - _startedAt;
        int seconds = Math.Max(0, (int)span.TotalSeconds);
        int hours = seconds / 3600, minutes = (seconds % 3600) / 60, rest = seconds % 60;
        ElapsedText.Text = hours > 0
            ? $"{hours}:{minutes:D2}:{rest:D2}"
            : $"{minutes}:{rest:D2}";
    }

    private void Pulse()
    {
        _faded = !_faded;
        PulseDot.Opacity = _faded ? 0.35 : 1.0;
    }

    /// Re-derived from the work area rather than remembered. A display being
    /// unplugged cannot leave the indicator stranded off-screen, which to the
    /// host would be indistinguishable from not sharing at all.
    private void Reposition()
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            int width = Math.Min(560, Math.Max(320, area.WorkArea.Width / 3));
            int height = 52;

            int x = area.WorkArea.X + (area.WorkArea.Width - width) / 2;
            int y = area.WorkArea.Y + TopInset;

            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
        }
        catch (Exception ex)
        {
            // An indicator in the wrong place is a nuisance; one that threw
            // while positioning is a session with no indicator at all.
            LanLogger.Remote("error", reason: $"indicator reposition failed: {ex.Message}");
        }
    }

    /// WS_EX_NOACTIVATE so clicking Stop does not pull the app forward and
    /// interrupt whatever the host was doing; WS_EX_TOOLWINDOW to keep it out of
    /// Alt+Tab.
    private void ApplyNoActivate()
    {
        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            uint style = GetWindowLongPtrW(hwnd, GWL_EXSTYLE).ToUInt32();
            SetWindowLongPtrW(hwnd, GWL_EXSTYLE,
                              new UIntPtr(style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
        }
        catch (Exception ex)
        {
            LanLogger.Remote("error", reason: $"indicator style failed: {ex.Message}");
        }
    }

    public void Hide()
    {
        try { _timer.Stop(); } catch { }
        try { Close(); } catch { }
    }

    private void StopSharing_Click(object sender, RoutedEventArgs e) => OnStopSharing?.Invoke();
    private void StopControl_Click(object sender, RoutedEventArgs e) => OnStopControl?.Invoke();

    // GetWindowLongPtrW and SetWindowLongPtrW are the real 64-bit exports.
    // A DllImport naming something that is not exported fails at the first call
    // rather than at load, which is exactly how the screenshot overlay crashed.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern UIntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern UIntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, UIntPtr dwNewLong);
}
