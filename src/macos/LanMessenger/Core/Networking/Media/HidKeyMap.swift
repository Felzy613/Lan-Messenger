import CoreGraphics
import Foundation

// USB HID usage page 0x07 to macOS virtual key codes.
//
// The Windows twin of this file maps to PS/2 scan codes; macOS has no scan-code
// injection path, so it maps to the `CGKeyCode` values in `<Carbon/Events.h>`.
// Both exist for the same reason: the wire carries HID usages — physical key
// positions — and never characters. See PROTOCOL.md → Input Sub-Channel.
//
// A virtual key code on macOS is still a *position*, not a character. `kVK_ANSI_A`
// is "the key left of S on a US keyboard", and pressing it on a French system
// produces `q`, because the layout is applied downstream by the text input
// system. That is exactly the behaviour a remote desktop wants, and it is why
// these are the right target despite the name.
//
// The values are the Carbon `kVK_*` constants, written as literals rather than
// imported. Carbon's headers are not available to a Swift package target without
// dragging the framework in, the numbers have been stable since 1984, and a
// table that names its entries is easier to audit than one that resolves them.

struct HidKeyMap {

    /// The macOS virtual key code for a HID usage, or nil for one this build
    /// cannot express.
    static func virtualKey(for usage: UInt16) -> CGKeyCode? {
        switch usage {
        // Letters, in HID order (A-Z), which is not the order they sit in.
        case 0x04: return 0x00   // A
        case 0x05: return 0x0B   // B
        case 0x06: return 0x08   // C
        case 0x07: return 0x02   // D
        case 0x08: return 0x0E   // E
        case 0x09: return 0x03   // F
        case 0x0A: return 0x05   // G
        case 0x0B: return 0x04   // H
        case 0x0C: return 0x22   // I
        case 0x0D: return 0x26   // J
        case 0x0E: return 0x28   // K
        case 0x0F: return 0x25   // L
        case 0x10: return 0x2E   // M
        case 0x11: return 0x2D   // N
        case 0x12: return 0x1F   // O
        case 0x13: return 0x23   // P
        case 0x14: return 0x0C   // Q
        case 0x15: return 0x0F   // R
        case 0x16: return 0x01   // S
        case 0x17: return 0x11   // T
        case 0x18: return 0x20   // U
        case 0x19: return 0x09   // V
        case 0x1A: return 0x0D   // W
        case 0x1B: return 0x07   // X
        case 0x1C: return 0x10   // Y
        case 0x1D: return 0x06   // Z

        // Digit row, 1-9 then 0 — and the codes are not sequential either.
        case 0x1E: return 0x12   // 1
        case 0x1F: return 0x13   // 2
        case 0x20: return 0x14   // 3
        case 0x21: return 0x15   // 4
        case 0x22: return 0x17   // 5
        case 0x23: return 0x16   // 6
        case 0x24: return 0x1A   // 7
        case 0x25: return 0x1C   // 8
        case 0x26: return 0x19   // 9
        case 0x27: return 0x1D   // 0

        case 0x28: return 0x24   // Return
        case 0x29: return 0x35   // Escape
        case 0x2A: return 0x33   // Delete (Backspace)
        case 0x2B: return 0x30   // Tab
        case 0x2C: return 0x31   // Space
        case 0x2D: return 0x1B   // Minus
        case 0x2E: return 0x18   // Equal
        case 0x2F: return 0x21   // Left bracket
        case 0x30: return 0x1E   // Right bracket
        case 0x31: return 0x2A   // Backslash
        case 0x33: return 0x29   // Semicolon
        case 0x34: return 0x27   // Quote
        case 0x35: return 0x32   // Grave
        case 0x36: return 0x2B   // Comma
        case 0x37: return 0x2F   // Period
        case 0x38: return 0x2C   // Slash
        case 0x39: return 0x39   // Caps lock

        // F1-F12. Unlike the Windows scan codes these are genuinely scattered —
        // F1-F4 are 0x7A, 0x78, 0x63, 0x76, which is not a sequence in any
        // direction, so each one is listed rather than derived.
        case 0x3A: return 0x7A   // F1
        case 0x3B: return 0x78   // F2
        case 0x3C: return 0x63   // F3
        case 0x3D: return 0x76   // F4
        case 0x3E: return 0x60   // F5
        case 0x3F: return 0x61   // F6
        case 0x40: return 0x62   // F7
        case 0x41: return 0x64   // F8
        case 0x42: return 0x65   // F9
        case 0x43: return 0x6D   // F10
        case 0x44: return 0x67   // F11
        case 0x45: return 0x6F   // F12

        case 0x49: return 0x72   // Insert / Help
        case 0x4A: return 0x73   // Home
        case 0x4B: return 0x74   // Page up
        case 0x4C: return 0x75   // Forward delete
        case 0x4D: return 0x77   // End
        case 0x4E: return 0x79   // Page down
        case 0x4F: return 0x7C   // Right
        case 0x50: return 0x7B   // Left
        case 0x51: return 0x7D   // Down
        case 0x52: return 0x7E   // Up

        case 0x53: return 0x47   // Clear (where Num Lock sits)
        case 0x54: return 0x4B   // Keypad divide
        case 0x55: return 0x43   // Keypad multiply
        case 0x56: return 0x4E   // Keypad minus
        case 0x57: return 0x45   // Keypad plus
        case 0x58: return 0x4C   // Keypad enter
        case 0x59: return 0x53   // Keypad 1
        case 0x5A: return 0x54
        case 0x5B: return 0x55
        case 0x5C: return 0x56
        case 0x5D: return 0x57
        case 0x5E: return 0x58
        case 0x5F: return 0x59
        case 0x60: return 0x5B
        case 0x61: return 0x5C   // Keypad 9
        case 0x62: return 0x52   // Keypad 0
        case 0x63: return 0x41   // Keypad decimal

        case 0xE0: return 0x3B   // Left control
        case 0xE1: return 0x38   // Left shift
        case 0xE2: return 0x3A   // Left option
        case 0xE3: return 0x37   // Left command
        case 0xE4: return 0x3E   // Right control
        case 0xE5: return 0x3C   // Right shift
        case 0xE6: return 0x3D   // Right option
        case 0xE7: return 0x36   // Right command

        default: return nil
        }
    }

    /// HID usages for the modifier keys, for releasing a stuck one.
    ///
    /// A session that ends mid-chord leaves the host holding whatever was down,
    /// and a Mac with Command held behaves as if possessed. Every teardown path
    /// lifts these.
    static let modifierUsages: [UInt16] = [0xE0, 0xE1, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7]

    /// The `CGEventFlags` a modifier bitmask implies.
    ///
    /// CGEvent needs the flags set on every event, not just on the modifier's own
    /// key-down: a key-down for `c` with no command flag is a literal `c`, no
    /// matter that command was pressed a moment earlier. That is the difference
    /// between copy and typing a letter into the document.
    static func flags(for modifiers: RemoteInputModifiers) -> CGEventFlags {
        var flags: CGEventFlags = []
        if modifiers.contains(.shift)    { flags.insert(.maskShift) }
        if modifiers.contains(.control)  { flags.insert(.maskControl) }
        if modifiers.contains(.option)   { flags.insert(.maskAlternate) }
        if modifiers.contains(.meta)     { flags.insert(.maskCommand) }
        if modifiers.contains(.capsLock) { flags.insert(.maskAlphaShift) }
        return flags
    }
}
