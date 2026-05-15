import SwiftUI
import AppKit

struct AvatarView: View {
    let name: String
    let size: CGFloat
    var photoB64: String? = nil

    private var photoImage: NSImage? {
        guard let b64 = photoB64, let data = Data(base64Encoded: b64) else { return nil }
        return NSImage(data: data)
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
