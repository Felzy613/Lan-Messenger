namespace LanMessenger.Core.Networking.Media;

// USB HID usage page 0x07 to PS/2 Set 1 scan codes.
//
// The wire carries HID usages — physical key positions — and never characters or
// virtual key codes. See PROTOCOL.md → Input Sub-Channel for why: a usage names
// a position, so the *host's* layout decides what it produces, which is what
// makes dead keys, AltGr and IME work through the host's real text input path.
//
// Scan codes rather than virtual keys, deliberately. SendInput with
// KEYEVENTF_SCANCODE delivers the event the way a physical keyboard does, so the
// host's active layout is applied by Windows itself. Mapping a usage to a
// virtual key here would mean applying OUR idea of the layout and then asking
// Windows to apply the host's on top — which is how a remote desktop ends up
// typing the wrong character on any non-US host.
//
// Extended keys (the grey block, right-hand modifiers, keypad divide, and so on)
// carry a 0xE0 prefix on a real keyboard; SendInput expresses that as
// KEYEVENTF_EXTENDEDKEY rather than a two-byte code, which is why the table
// stores the flag separately instead of a 0xE0-prefixed value.
//
// Pause is deliberately absent. It is the one key whose make code is a
// three-byte sequence (0xE1 0x1D 0x45) that SendInput cannot express as a single
// scan code, and a wrong guess there sends a stray Ctrl. A host that cannot type
// Pause is a smaller problem than one that silently presses Control.

public readonly record struct HidScanCode(ushort ScanCode, bool Extended);

public static class HidKeyMap
{
    /// <summary>
    /// The scan code for a HID usage, or null for one this build cannot express.
    /// </summary>
    public static HidScanCode? ScanCode(ushort usage) => usage switch
    {
        // Letters, in HID order (A-Z), which is not alphabetical on the keyboard.
        0x04 => New(0x1E), 0x05 => New(0x30), 0x06 => New(0x2E), 0x07 => New(0x20),
        0x08 => New(0x12), 0x09 => New(0x21), 0x0A => New(0x22), 0x0B => New(0x23),
        0x0C => New(0x17), 0x0D => New(0x24), 0x0E => New(0x25), 0x0F => New(0x26),
        0x10 => New(0x32), 0x11 => New(0x31), 0x12 => New(0x18), 0x13 => New(0x19),
        0x14 => New(0x10), 0x15 => New(0x13), 0x16 => New(0x1F), 0x17 => New(0x14),
        0x18 => New(0x16), 0x19 => New(0x2F), 0x1A => New(0x11), 0x1B => New(0x2D),
        0x1C => New(0x15), 0x1D => New(0x2C),

        // Digit row, 1-9 then 0.
        0x1E => New(0x02), 0x1F => New(0x03), 0x20 => New(0x04), 0x21 => New(0x05),
        0x22 => New(0x06), 0x23 => New(0x07), 0x24 => New(0x08), 0x25 => New(0x09),
        0x26 => New(0x0A), 0x27 => New(0x0B),

        0x28 => New(0x1C),   // Enter
        0x29 => New(0x01),   // Escape
        0x2A => New(0x0E),   // Backspace
        0x2B => New(0x0F),   // Tab
        0x2C => New(0x39),   // Space
        0x2D => New(0x0C),   // Minus
        0x2E => New(0x0D),   // Equal
        0x2F => New(0x1A),   // Left bracket
        0x30 => New(0x1B),   // Right bracket
        0x31 => New(0x2B),   // Backslash
        0x33 => New(0x27),   // Semicolon
        0x34 => New(0x28),   // Quote
        0x35 => New(0x29),   // Backquote
        0x36 => New(0x33),   // Comma
        0x37 => New(0x34),   // Period
        0x38 => New(0x35),   // Slash
        0x39 => New(0x3A),   // Caps lock

        // F1-F10 are contiguous (0x3B-0x44). F11 and F12 are NOT — they were
        // added after the original 83-key layout had already used the codes that
        // would have followed, so they live at 0x57 and 0x58. Continuing the
        // run through them lands on Num Lock and Scroll Lock, which is a
        // collision that types the wrong key rather than nothing.
        >= 0x3A and <= 0x43 => New((ushort)(0x3B + (usage - 0x3A))),
        0x44 => New(0x57),   // F11
        0x45 => New(0x58),   // F12

        0x46 => New(0x37, extended: true),   // Print screen
        0x47 => New(0x46),                   // Scroll lock
        // 0x48 Pause omitted — see the note above.

        0x49 => New(0x52, extended: true),   // Insert
        0x4A => New(0x47, extended: true),   // Home
        0x4B => New(0x49, extended: true),   // Page up
        0x4C => New(0x53, extended: true),   // Delete
        0x4D => New(0x4F, extended: true),   // End
        0x4E => New(0x51, extended: true),   // Page down
        0x4F => New(0x4D, extended: true),   // Right
        0x50 => New(0x4B, extended: true),   // Left
        0x51 => New(0x50, extended: true),   // Down
        0x52 => New(0x48, extended: true),   // Up

        0x53 => New(0x45, extended: true),   // Num lock
        0x54 => New(0x35, extended: true),   // Keypad divide
        0x55 => New(0x37),                   // Keypad multiply
        0x56 => New(0x4A),                   // Keypad minus
        0x57 => New(0x4E),                   // Keypad plus
        0x58 => New(0x1C, extended: true),   // Keypad enter
        0x59 => New(0x4F), 0x5A => New(0x50), 0x5B => New(0x51),
        0x5C => New(0x4B), 0x5D => New(0x4C), 0x5E => New(0x4D),
        0x5F => New(0x47), 0x60 => New(0x48), 0x61 => New(0x49),
        0x62 => New(0x52),                   // Keypad 0
        0x63 => New(0x53),                   // Keypad decimal

        0xE0 => New(0x1D),                   // Left control
        0xE1 => New(0x2A),                   // Left shift
        0xE2 => New(0x38),                   // Left alt
        0xE3 => New(0x5B, extended: true),   // Left GUI (Windows key)
        0xE4 => New(0x1D, extended: true),   // Right control
        0xE5 => New(0x36),                   // Right shift
        0xE6 => New(0x38, extended: true),   // Right alt (AltGr)
        0xE7 => New(0x5C, extended: true),   // Right GUI

        _ => null,
    };

    private static HidScanCode New(ushort code, bool extended = false) => new(code, extended);

    /// <summary>
    /// The inverse: a hardware scan code back to its HID usage, or null.
    /// </summary>
    /// <remarks>
    /// Built by inverting the forward table rather than written out again, so
    /// the two can never disagree — a second hand-written table is a second
    /// place for the same typo, and this direction is the one that decides what
    /// a viewer sends.
    ///
    /// The keypad is why the extended flag is part of the key: keypad 1-9, 0 and
    /// decimal share scan codes with the navigation block, and only the flag
    /// tells Home from keypad 7.
    /// </remarks>
    public static ushort? UsageForScanCode(ushort scanCode, bool extended)
        => Inverse.TryGetValue((scanCode, extended), out ushort usage) ? usage : null;

    private static readonly Dictionary<(ushort, bool), ushort> Inverse = BuildInverse();

    private static Dictionary<(ushort, bool), ushort> BuildInverse()
    {
        var map = new Dictionary<(ushort, bool), ushort>();
        // Ascending, so where two usages share a code the lower one wins — that
        // is the navigation block rather than the keypad, which is what an
        // extended-flagged key means.
        for (ushort usage = 0; usage < 0x100; usage++)
        {
            if (ScanCode(usage) is not { } mapped) continue;
            map.TryAdd((mapped.ScanCode, mapped.Extended), usage);
        }
        return map;
    }

    /// <summary>HID usages for the modifier keys, for releasing a stuck one.</summary>
    /// <remarks>
    /// A session that ends mid-chord leaves the host holding whatever was down —
    /// Alt held forever is a machine that behaves as if possessed. Every teardown
    /// path lifts these.
    /// </remarks>
    public static readonly ushort[] ModifierUsages =
        [0xE0, 0xE1, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7];
}
