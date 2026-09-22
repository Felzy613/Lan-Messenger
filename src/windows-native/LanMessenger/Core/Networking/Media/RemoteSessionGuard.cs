using LanMessenger.Core.Services;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace LanMessenger.Core.Networking.Media;

// Everything that can stop a live session without anyone clicking Stop.
// Mirror of RemoteSessionGuard.swift.
//
// The indicator's Stop button is the obvious exit and the weakest one: once
// control is granted the host's mouse is contested, so pressing a button is a
// race with the person you are trying to stop. This owns the exits that are not
// a race.
//
// All three arrive as window messages, so all three share one **message-only
// window on its own thread**:
//
//   WM_HOTKEY            the reserved kill shortcut
//   WM_WTSSESSION_CHANGE the workstation locking, or a fast user switch
//   WM_POWERBROADCAST    the machine suspending
//
// A message-only window (HWND_MESSAGE as parent) never appears, never takes
// focus and is not enumerated — it exists purely to receive these. Its own
// thread matters: the WinUI dispatcher is busy presenting video at 30 fps, and a
// safety mechanism must not queue behind the thing it exists to stop.
//
// Every DllImport here names a real exported entry point. CLAUDE.md carries a
// scar from one that did not — the failure is EntryPointNotFoundException at the
// first call rather than at load, so it looks fine until the feature runs.

public sealed class RemoteSessionGuard : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int WM_WTSSESSION_CHANGE = 0x02B1;
    private const int WM_POWERBROADCAST = 0x0218;
    private const int WM_DESTROY = 0x0002;
    private const int WM_CLOSE = 0x0010;

    private const int WTS_SESSION_LOCK = 0x7;
    private const int WTS_CONSOLE_DISCONNECT = 0x2;
    private const int WTS_SESSION_LOGOFF = 0x6;
    private const int PBT_APMSUSPEND = 0x4;

    private const int NOTIFY_FOR_THIS_SESSION = 0;
    private const int HOTKEY_ID = 0xB0B;
    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private Action<RemoteStopReason>? _onStop;
    private Thread? _thread;
    private IntPtr _hwnd;
    /// <summary>
    /// The window procedure, rooted for the life of the PROCESS — not the
    /// guard.
    /// </summary>
    /// <remarks>
    /// This was an instance field, which is a delegate lifetime one session
    /// shorter than the thing that holds the pointer. A window class registered
    /// by a process stays registered until the process exits, so the second
    /// session's <c>RegisterClassExW</c> answers
    /// <c>ERROR_CLASS_ALREADY_EXISTS</c> and its window uses the class the
    /// FIRST guard registered — still pointing at that guard's delegate, which
    /// has since been collected. Windows then calls a freed thunk and the CLR
    /// ends the process with
    /// <c>"A callback was made on a garbage collected delegate"</c>, through
    /// <c>Environment.FailFast</c> — so no exception is thrown, no handler
    /// runs, and nothing is written to the crash log. It landed inside
    /// <c>CreateWindowExW</c>, because the procedure is called for
    /// <c>WM_NCCREATE</c> before the call returns.
    ///
    /// It presented as "the second remote-desktop session of a run kills the
    /// app a second after it starts" — and, because the process died before the
    /// two-second stats timer, as a host that produced no video and no
    /// `encoder_stats` line to say why.
    ///
    /// The delegate now lives exactly as long as the registration does, and the
    /// per-session state is found from the window handle instead.
    /// </remarks>
    private static readonly WndProcDelegate SharedWndProc = StaticWndProc;

    /// <summary>Which guard owns which message window.</summary>
    /// <remarks>
    /// The class is shared, so the procedure cannot close over one guard. A
    /// window is added the moment it is created and removed when it is
    /// destroyed; a message for a window nobody owns falls through to
    /// <c>DefWindowProcW</c> rather than reaching a disposed session.
    /// </remarks>
    private static readonly ConcurrentDictionary<IntPtr, RemoteSessionGuard> Guards = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly object _gate = new();

    public bool IsArmed => _onStop is not null;
    /// False when the shortcut could not be registered — another app owns it.
    /// The session is still stoppable from the indicator, but the guarantee is
    /// gone, so this is worth surfacing rather than swallowing.
    public bool HotkeyRegistered { get; private set; }

    /// Starts watching. Re-arming replaces the handler rather than stacking a
    /// second set of registrations.
    public void Arm(Action<RemoteStopReason> onStop)
    {
        Disarm();
        _onStop = onStop;

        _ready.Reset();
        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "remote-desktop-guard",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));

        LanLogger.Remote("guard_armed",
            reason: $"kill={RemoteKillSwitch.Shortcut.DisplayName} registered={HotkeyRegistered}");
    }

    /// Safe to call when not armed, and called from the stop path itself — so it
    /// must not fire the callback it is tearing down.
    public void Disarm()
    {
        lock (_gate)
        {
            _onStop = null;
            if (_hwnd != IntPtr.Zero)
            {
                // Out of the table first. WM_DESTROY removes it too, but that
                // depends on the message loop still draining — and a disarmed
                // guard must not be reached by a late message either way. An
                // unowned window falls through to DefWindowProcW, which is the
                // right answer for one nobody is listening to any more.
                Guards.TryRemove(_hwnd, out _);
                PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                _hwnd = IntPtr.Zero;
            }
        }
        var thread = _thread;
        _thread = null;
        if (thread is not null && thread.IsAlive && thread != Thread.CurrentThread)
        {
            thread.Join(TimeSpan.FromSeconds(1));
        }
        HotkeyRegistered = false;
    }

    /// Called by the network layer when every usable interface has gone. Not a
    /// window message, but it belongs in the same funnel.
    public void NetworkBecameUnavailable() => Trigger(RemoteStopReason.NetworkLost);

    /// Single funnel. The first reason wins: a closing lid produces sleep *and*
    /// a session lock, and the session should stop once with the first cause
    /// rather than twice — the second callback would arrive after teardown.
    private void Trigger(RemoteStopReason reason)
    {
        Action<RemoteStopReason>? handler;
        lock (_gate)
        {
            handler = _onStop;
            _onStop = null;
        }
        if (handler is null) return;

        LanLogger.Remote("guard_triggered", reason: reason.ToToken());
        handler(reason);
    }

    private void MessageLoop()
    {
        try
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(SharedWndProc),
                hInstance = GetModuleHandleW(null),
                lpszClassName = "LanMessengerRemoteGuard",
            };
            // A window class registered by this process stays registered for the
            // life of the process, so the second Arm() of a session always fails
            // with ERROR_CLASS_ALREADY_EXISTS. Treating that as fatal is how the
            // kill switch came to be armed for the first remote session of a run
            // and dead for every one after it — the log said
            // "Ctrl+Alt+Shift+Esc unavailable" and the only way out of a live
            // session was the indicator's Stop button, which is precisely the
            // exit that is a race once control has been granted.
            ushort atom = RegisterClassExW(ref wc);
            if (atom == 0)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != ERROR_CLASS_ALREADY_EXISTS)
                {
                    LanLogger.Remote("error",
                        reason: $"guard window class registration failed ({err})");
                    _ready.Set();
                    return;
                }
                // Already registered by an earlier session: carry on and use it.
            }

            // HWND_MESSAGE: never shown, never focused, not enumerated.
            IntPtr hwnd = CreateWindowExW(0, wc.lpszClassName, "", 0, 0, 0, 0, 0,
                                          HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                LanLogger.Remote("error", reason: "guard window creation failed");
                _ready.Set();
                return;
            }

            Guards[hwnd] = this;
            lock (_gate) { _hwnd = hwnd; }

            var shortcut = RemoteKillSwitch.Shortcut;
            HotkeyRegistered = RegisterHotKey(hwnd, HOTKEY_ID, shortcut.Modifiers, shortcut.VirtualKey);
            if (!HotkeyRegistered)
            {
                // Another app already owns the combination. Worth an error: the
                // session is still stoppable, but the guarantee is gone.
                LanLogger.Remote("error",
                    reason: $"kill switch unavailable — {shortcut.DisplayName} is taken");
            }

            // Lock, unlock and fast user switching all arrive here once
            // registered. Without this a locked PC keeps streaming a lock
            // screen, and then whatever is behind it when the host returns.
            WTSRegisterSessionNotification(hwnd, NOTIFY_FOR_THIS_SESSION);

            _ready.Set();

            while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }
        catch (Exception ex)
        {
            LanLogger.Remote("error", reason: $"guard loop: {ex.Message}");
            _ready.Set();
        }
    }

    /// <summary>
    /// The shared entry point. Finds the guard that owns this window, if any.
    /// </summary>
    /// <remarks>
    /// Messages arrive here before the window is in the table — the procedure
    /// runs for <c>WM_NCCREATE</c> and <c>WM_CREATE</c> inside
    /// <c>CreateWindowExW</c> — and after it leaves, at <c>WM_NCDESTROY</c>.
    /// Both must fall through to the default handling rather than fault.
    /// </remarks>
    private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (Guards.TryGetValue(hwnd, out var guard))
        {
            return guard.WndProc(hwnd, msg, wParam, lParam);
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_HOTKEY when wParam.ToInt32() == HOTKEY_ID:
                Trigger(RemoteStopReason.KillSwitch);
                return IntPtr.Zero;

            case WM_WTSSESSION_CHANGE:
                switch (wParam.ToInt32())
                {
                    case WTS_SESSION_LOCK:
                        Trigger(RemoteStopReason.ScreenLocked);
                        break;
                    case WTS_CONSOLE_DISCONNECT:
                    case WTS_SESSION_LOGOFF:
                        // The session belongs to the account that agreed to it,
                        // not to whoever sits down next.
                        Trigger(RemoteStopReason.UserSwitched);
                        break;
                }
                return IntPtr.Zero;

            case WM_POWERBROADCAST when wParam.ToInt32() == PBT_APMSUSPEND:
                Trigger(RemoteStopReason.SystemSleep);
                return IntPtr.Zero;

            case WM_DESTROY:
                UnregisterHotKey(hwnd, HOTKEY_ID);
                WTSUnRegisterSessionNotification(hwnd);
                Guards.TryRemove(hwnd, out _);
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        Disarm();
        _ready.Dispose();
    }

    // ---- Win32 -------------------------------------------------------------
    // Every name below is a real exported entry point. A DllImport whose managed
    // name is not the real export fails with EntryPointNotFoundException at the
    // first call rather than at load — it looks fine until the feature runs.

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
