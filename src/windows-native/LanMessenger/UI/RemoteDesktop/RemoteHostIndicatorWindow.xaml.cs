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

        // The state colour lives in the dot, and for control also in the
        // window's outline: the riskier state is noticeable without the whole
        // strip turning into an alarm. The words say the state too, so colour
        // is never the only signal.
        PulseDot.Fill = controlled ? SignalControlBrush : SignalViewBrush;
        _controlled = controlled;

        StopControlButton.Visibility = controlled ? Visibility.Visible : Visibility.Collapsed;
        Reposition();
    }

    // hud-* tokens are the same in both themes, so one brush each will do.
    private static readonly SolidColorBrush SignalViewBrush    = new(GlassTokens.HudSignalViewLight);
    private static readonly SolidColorBrush SignalControlBrush = new(GlassTokens.HudSignalControlLight);
    private bool _controlled;

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
            int height = 44;

            int x = area.WorkArea.X + (area.WorkArea.Width - width) / 2;
            int y = area.WorkArea.Y + TopInset;

            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
            ApplyWindowShape();
        }
        catch (Exception ex)
        {
            // An indicator in the wrong place is a nuisance; one that threw
            // while positioning is a session with no indicator at all.
            LanLogger.Remote("error", reason: $"indicator reposition failed: {ex.Message}");
        }
    }

    /// Rounded corners and the controlled outline, both from DWM. Re-applied
    /// after every reposition. Windows 11 only: on Windows 10 the call returns
    /// an error, which is logged once and otherwise ignored — the indicator is
    /// still there, just square and without the outline, and the headline
    /// still names the state.
    private void ApplyWindowShape()
    {
        SetDwmAttribute(DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);
        SetDwmAttribute(DWMWA_BORDER_COLOR, _controlled ? ColorRef(GlassTokens.HudSignalControlLight) : DWMWA_COLOR_DEFAULT);
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWA_COLOR_DEFAULT = unchecked((int)0xFFFFFFFF);
    private static bool _dwmFailureLogged;

    /// A COLORREF is 0x00BBGGRR.
    private static int ColorRef(Windows.UI.Color c) => (c.B << 16) | (c.G << 8) | c.R;

    private void SetDwmAttribute(int attribute, int value)
    {
        // Nothing here may throw: this runs from the constructor and from
        // grant changes, and an indicator that failed to build is a session
        // with no indicator at all.
        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            int hr = DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
            if (hr != 0 && !_dwmFailureLogged)
            {
                _dwmFailureLogged = true;
                LanLogger.Remote("error", reason: $"indicator dwm attribute {attribute} failed: 0x{hr:X8}");
            }
        }
        catch (Exception ex)
        {
            if (_dwmFailureLogged) return;
            _dwmFailureLogged = true;
            LanLogger.Remote("error", reason: $"indicator dwm attribute {attribute} threw: {ex.Message}");
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

    // DwmSetWindowAttribute is the real export name.
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // GetWindowLongPtrW and SetWindowLongPtrW are the real 64-bit exports.
    // A DllImport naming something that is not exported fails at the first call
    // rather than at load, which is exactly how the screenshot overlay crashed.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern UIntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern UIntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, UIntPtr dwNewLong);
}
