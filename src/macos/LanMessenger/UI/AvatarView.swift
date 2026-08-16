import SwiftUI
import AppKit

// Decoded avatar photos, keyed by their base64 source string. AvatarView is a
// value-type struct that SwiftUI recreates on every list diff (e.g. every row
// in PeerScannerView during a live scan), so caching must be content-keyed
// rather than per-instance state — otherwise every re-render redecodes the
// same base64 data from scratch. Photos are already capped to ~256px/~30KB by
// ContactEditorView.compressedJPEG, so an unbounded, self-evicting NSCache
// needs no manual size limit.
private final class AvatarImageCache {
    static let shared = AvatarImageCache()
    private let cache = NSCache<NSString, NSImage>()

    func image(for base64: String) -> NSImage? {
        let key = base64 as NSString
        if let cached = cache.object(forKey: key) { return cached }
        guard let data = Data(base64Encoded: base64), let img = NSImage(data: data) else { return nil }
        cache.setObject(img, forKey: key)
        return img
    }
}

struct AvatarView: View {
    let name: String
    let size: CGFloat
    var photoB64: String? = nil

    private var photoImage: NSImage? {
        guard let b64 = photoB64 else { return nil }
        return AvatarImageCache.shared.image(for: b64)
    }

    var body: some View {
        ZStack {
            if let img = photoImage {
                Image(nsImage: img)
                    .resizable()
                    .scaledToFill()
                    .frame(width: size, height: size)
                    .clipShape(Circle())
            } else {
                Circle()
                    .fill(Theme.avatarColor(for: name))
                Text(Theme.initials(for: name))
                    .font(.system(size: size * 0.38, weight: .semibold))
                    .foregroundStyle(.white)
            }
        }
        .frame(width: size, height: size)
    }
}
