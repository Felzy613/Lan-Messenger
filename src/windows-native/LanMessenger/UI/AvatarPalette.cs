namespace LanMessenger.UI;

/// <summary>
/// Which of the eight avatar colours a contact gets.
///
/// The same name has to get the same colour on a PC and a Mac, launch after
/// launch, so the choice is FNV-1a over the name's UTF-16 code units, and macOS
/// AvatarPalette.swift reduces it exactly the same way.
/// avatar_palette_vector.json pins the palette and the choice in both suites.
///
/// Deliberately free of WinRT types, like ClipboardAttachments, so the choice
/// compiles and tests away from a Windows UI host. Theme turns it into brushes.
/// </summary>
public static class AvatarPalette
{
    /// <summary>
    /// The avatar-* design tokens as 0xRRGGBB, in token order. White initials
    /// reach at least 4.5:1 on every one.
    /// </summary>
    public static readonly uint[] Rgb =
    [
        0x2B6CD4,  // avatar-blue
        0x1B7F45,  // avatar-green
        0xB55700,  // avatar-orange
        0x8E44C9,  // avatar-purple
        0xD0352B,  // avatar-red
        0x0B7F99,  // avatar-teal
        0x9A6500,  // avatar-amber
        0x5E6770,  // avatar-slate
    ];

    public static int Index(string name)
        => (int)(Fnv1a(name) % int.MaxValue) % Rgb.Length;

    // FNV-1a. string.GetHashCode() is randomized per process on .NET, which
    // made every contact's avatar colour reshuffle on each launch. foreach
    // walks UTF-16 code units, which is what macOS hashes too.
    public static uint Fnv1a(string name)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var c in name)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return hash;
        }
    }
}
