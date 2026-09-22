using LanMessenger.Core.Services;
using System.Runtime.InteropServices;

namespace LanMessenger.Core.Networking.Media;

// Turns decoded input records into real events on this machine.
//
// This is the most dangerous object in the feature: everything it does was asked
// for by another computer. Three rules follow, and none of them are optional.
//
//  1. **Nothing is injected without a control grant.** Viewing is agreed
//     separately from control, and the gate is checked here rather than trusted
//     from the caller — a bug upstream must not become keyboard access.
//  2. **The kill shortcut is never injected.** A host being actively controlled
//     has to be able to stop the session, and a viewer that can press the stop
//     combination remotely can prevent exactly that.
//  3. **Coordinates are clamped, never trusted.** They arrive as floats from a
//     peer. A coordinate outside the shared surface is the cheapest way to try to
//     drive something that is not being shared.
//
// SendInput with KEYEVENTF_SCANCODE rather than virtual keys, so Windows applies
// the host's own layout — see HidKeyMap for why that matters.

public sealed class RemoteInputInjector
{
    // ---- Win32 -------------------------------------------------------------

    private const uint INPUT_MOUSE    = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE       = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN   = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP     = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN  = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP    = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP   = 0x0040;
    private const uint MOUSEEVENTF_WHEEL      = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL     = 0x1000;
    private const uint MOUSEEVENTF_ABSOLUTE   = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP       = 0x0002;
    private const uint KEYEVENTF_UNICODE     = 0x0004;
    private const uint KEYEVENTF_SCANCODE    = 0x0008;

    /// One wheel notch, as Windows defines it.
    private const int WHEEL_DELTA = 120;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint cInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_XVIRTUALSCREEN  = 76;
    private const int SM_YVIRTUALSCREEN  = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const int SM_CMONITORS       = 80;

    // ---- State -------------------------------------------------------------

    private readonly Func<RemoteGrant> _grant;
    private readonly Func<(int X, int Y, int Width, int Height)> _surface;
    private readonly HashSet<ushort> _keysDown = [];
    private readonly object _gate = new();
    private long _refused;
    private int _movesLogged;
    private int _buttonsLogged;

    /// <param name="grant">Read at every injection, never cached.</param>
    /// <param name="surface">
    /// The rectangle being shared, in virtual-screen pixels. Normalized
    /// coordinates resolve against this and nothing else — a viewer sees the
    /// shared surface, so that is the only thing its 0..1 can mean.
    /// </param>
    public RemoteInputInjector(Func<RemoteGrant> grant,
                               Func<(int, int, int, int)>? surface = null)
    {
        _grant = grant;
        _surface = surface ?? DefaultSurface;
    }

    /// <summary>How many records were refused for want of a grant.</summary>
    public long Refused => Interlocked.Read(ref _refused);

    private static (int X, int Y, int Width, int Height) DefaultSurface() =>
        (GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
         Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN)),
         Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN)));

    // ---- Injection ---------------------------------------------------------

    /// <summary>Injects a decoded burst. Silently does nothing without control.</summary>
    public void Inject(IReadOnlyList<RemoteInputRecord> records)
    {
        // Checked here, not trusted from the caller. A bug upstream must not
        // become keyboard access.
        if (_grant() != RemoteGrant.Control)
        {
            Interlocked.Add(ref _refused, records.Count);
            return;
        }

        foreach (var record in records)
        {
            try { InjectOne(record); }
            catch (Exception ex)
            {
                LanLogger.Remote("error", reason: $"input injection failed: {ex.Message}");
            }
        }
    }

    private void InjectOne(RemoteInputRecord record)
    {
        switch (record.Kind)
        {
            case RemoteInputRecordKind.PointerMove:
                MoveTo(record.X, record.Y);
                break;

            case RemoteInputRecordKind.PointerButton:
                MoveTo(record.X, record.Y);
                SendMouse(ButtonFlag(record.Button, record.Down), 0);
                if (Interlocked.Increment(ref _buttonsLogged) <= 6)
                {
                    LanLogger.Remote("input_button",
                        reason: $"{record.Button} {(record.Down ? "down" : "up")} "
                              + $"at ({record.X:F4},{record.Y:F4})");
                }
                break;

            case RemoteInputRecordKind.PointerScroll:
                MoveTo(record.X, record.Y);
                // Lines to notches. Vertical is inverted relative to the wire:
                // positive dy means content moving up, which Windows expresses
                // as a negative wheel delta.
                int vertical = RemoteInputGeometry.ClampScroll(record.Dy);
                int horizontal = RemoteInputGeometry.ClampScroll(record.Dx);
                if (vertical != 0) SendMouse(MOUSEEVENTF_WHEEL, (uint)(-vertical * WHEEL_DELTA));
                if (horizontal != 0) SendMouse(MOUSEEVENTF_HWHEEL, (uint)(horizontal * WHEEL_DELTA));
                break;

            case RemoteInputRecordKind.Key:
                SendKey(record.Usage, record.Down, record.Repeating);
                break;

            case RemoteInputRecordKind.Text:
                SendText(record.Text ?? "");
                break;
        }
    }

    private void MoveTo(float normalizedX, float normalizedY)
    {
        var (originX, originY, width, height) = _surface();

        // Clamped, not trusted. These are floats from a peer.
        double clampedX = RemoteInputGeometry.ClampUnit(normalizedX);
        double clampedY = RemoteInputGeometry.ClampUnit(normalizedY);

        int pixelX = originX + (int)Math.Round(clampedX * (width - 1));
        int pixelY = originY + (int)Math.Round(clampedY * (height - 1));

        // Absolute coordinates are 0..65535 across the VIRTUAL DESKTOP, not
        // across the shared display — MOUSEEVENTF_VIRTUALDESK says so. Scaling
        // against the surface instead put a centre click on a 1920-wide shared
        // display at 32784/65535 of a 3840-wide desktop, which is the left edge
        // of the *second* monitor: exactly a factor-of-two error that is
        // invisible on any single-monitor machine.
        var virtualScreen = new VirtualScreen(
            GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
            Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN)),
            Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN)));
        var (absX, absY) = RemoteInputGeometry.ToAbsolute(pixelX, pixelY, virtualScreen);

        // The first few resolutions, said out loud.
        //
        // "the pointer lands in the wrong place" has several possible causes —
        // the shared surface not being where we think, the virtual desktop
        // spanning more than one monitor, or the normalisation being against
        // the wrong rectangle — and none of them can be told apart from
        // outside. This prints every number involved, a bounded number of times.
        if (Interlocked.Increment(ref _movesLogged) <= 5)
        {
            LanLogger.Remote("input_pointer",
                reason: $"norm=({normalizedX:F4},{normalizedY:F4}) "
                      + $"surface=({originX},{originY},{width}x{height}) "
                      + $"pixel=({pixelX},{pixelY}) abs=({absX},{absY}) "
                      + $"virtual=({GetSystemMetrics(SM_XVIRTUALSCREEN)},"
                      + $"{GetSystemMetrics(SM_YVIRTUALSCREEN)},"
                      + $"{GetSystemMetrics(SM_CXVIRTUALSCREEN)}x"
                      + $"{GetSystemMetrics(SM_CYVIRTUALSCREEN)}) "
                      + $"monitors={GetSystemMetrics(SM_CMONITORS)}");
        }

        SendMouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                  0, absX, absY);
    }

    private static uint ButtonFlag(RemotePointerButton button, bool down) => button switch
    {
        RemotePointerButton.Left   => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
        RemotePointerButton.Right  => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
        RemotePointerButton.Middle => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
        _                          => 0,
    };

    private void SendKey(ushort usage, bool down, bool repeating)
    {
        // Defence in depth. The viewer is required never to forward the kill
        // shortcut, but a host that trusts that is a host whose stop control can
        // be held down remotely.
        if (IsKillSwitchComponent(usage) && down && _keysDownContainsKillChord(usage))
        {
            LanLogger.Remote("input_refused", reason: "kill shortcut may not be injected");
            return;
        }

        if (HidKeyMap.ScanCode(usage) is not { } mapped)
        {
            // A key this build cannot express. Logged once per usage rather than
            // per keystroke: a held key would otherwise fill the log.
            lock (_gate)
            {
                if (_unmappedSeen.Add(usage))
                {
                    LanLogger.Remote("input_unmapped", reason: $"HID usage 0x{usage:X2}");
                }
            }
            return;
        }

        uint flags = KEYEVENTF_SCANCODE;
        if (mapped.Extended) flags |= KEYEVENTF_EXTENDEDKEY;
        if (!down) flags |= KEYEVENTF_KEYUP;

        SendKeyboard(0, mapped.ScanCode, flags);

        lock (_gate)
        {
            if (down) _keysDown.Add(usage); else _keysDown.Remove(usage);
        }
        _ = repeating;   // Windows repeats from the same down events; nothing extra to do.
    }

    private readonly HashSet<ushort> _unmappedSeen = [];

    private bool _keysDownContainsKillChord(ushort usage)
    {
        // Ctrl+Alt+Shift+Esc. Only the Escape completes it, so that is the one
        // that gets refused — refusing the modifiers would break every ordinary
        // chord that uses them.
        if (usage != 0x29) return false;      // Escape
        lock (_gate)
        {
            bool ctrl = _keysDown.Contains(0xE0) || _keysDown.Contains(0xE4);
            bool alt = _keysDown.Contains(0xE2) || _keysDown.Contains(0xE6);
            bool shift = _keysDown.Contains(0xE1) || _keysDown.Contains(0xE5);
            return ctrl && alt && shift;
        }
    }

    private static bool IsKillSwitchComponent(ushort usage) => usage == 0x29;

    private void SendText(string text)
    {
        // The escape hatch for layout mismatch, emoji and IME. Injected as
        // Unicode rather than as key positions, because there is no key position
        // for most of what arrives here.
        foreach (char unit in text)
        {
            SendKeyboard(0, unit, KEYEVENTF_UNICODE);
            SendKeyboard(0, unit, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP);
        }
    }

    /// <summary>
    /// Lifts every key this injector is holding.
    /// </summary>
    /// <remarks>
    /// A session that ends mid-chord otherwise leaves the host holding whatever
    /// was down, and a machine with Alt stuck behaves as if possessed. Called
    /// from every teardown path, including the ones that are themselves errors.
    /// </remarks>
    public void ReleaseEverything()
    {
        ushort[] held;
        lock (_gate)
        {
            held = [.. _keysDown];
            _keysDown.Clear();
        }
        foreach (ushort usage in held)
        {
            if (HidKeyMap.ScanCode(usage) is not { } mapped) continue;
            uint flags = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP;
            if (mapped.Extended) flags |= KEYEVENTF_EXTENDEDKEY;
            try { SendKeyboard(0, mapped.ScanCode, flags); } catch { /* going away anyway */ }
        }
        if (held.Length > 0)
        {
            LanLogger.Remote("input_released", reason: $"{held.Length} key(s) lifted at teardown");
        }
    }

    // ---- Send --------------------------------------------------------------

    private static void SendMouse(uint flags, uint data, int dx = 0, int dy = 0)
    {
        if (flags == 0) return;
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION
            {
                mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags },
            },
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private static void SendKeyboard(ushort virtualKey, ushort scan, uint flags)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT { wVk = virtualKey, wScan = scan, dwFlags = flags },
            },
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }
}
