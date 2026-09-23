import Foundation

/// Which of the eight avatar colours a contact gets.
///
/// The same name has to get the same colour on a Mac and a PC, launch after
/// launch, so the choice is FNV-1a over the name's UTF-16 code units, reduced
/// exactly as Windows `AvatarPalette` does it. It replaced `String.hashValue`,
/// which Swift seeds randomly per process: every contact changed colour on each
/// launch, matched Windows only by chance, and a hash of `Int.min` made `abs`
/// trap.
/// `avatar_palette_vector.json` pins the palette and the choice in both suites.
enum AvatarPalette {
    /// The `avatar-*` design tokens as 0xRRGGBB, in token order. White initials
    /// reach at least 4.5:1 on every one.
    static let rgb: [UInt32] = [
        0x2B6CD4, // avatar-blue
        0x1B7F45, // avatar-green
        0xB55700, // avatar-orange
        0x8E44C9, // avatar-purple
        0xD0352B, // avatar-red
        0x0B7F99, // avatar-teal
        0x9A6500, // avatar-amber
        0x5E6770, // avatar-slate
    ]

    /// Windows computes `(int)(hash % int.MaxValue) % count`. The two have to
    /// agree on the slot, not merely on the hash.
    static func index(for name: String) -> Int {
        Int(fnv1a(name) % UInt32(Int32.max)) % rgb.count
    }

    /// 32-bit FNV-1a. It walks `utf16` because C#'s `foreach (char c in name)`
    /// walks UTF-16 code units: `utf8` would disagree on every non-ASCII name,
    /// and `unicodeScalars` on anything outside the Basic Multilingual Plane,
    /// most emoji included.
    static func fnv1a(_ name: String) -> UInt32 {
        var hash: UInt32 = 2166136261
        for unit in name.utf16 {
            hash ^= UInt32(unit)
            hash &*= 16777619
        }
        return hash
    }
}
