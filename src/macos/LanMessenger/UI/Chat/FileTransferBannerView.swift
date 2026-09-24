import SwiftUI

/// A glass capsule under the thread showing a file transfer's live progress:
/// an accent-wash disc with the transfer glyph, the label, a 4pt brand bar on
/// an inset-fill track, and the byte counts in mono.
struct FileTransferBannerView: View {
    let label: String
    let bytes: Int64
    let total: Int64

    private var progress: Double {
        total > 0 ? min(Double(bytes) / Double(total), 1.0) : 0
    }

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: "arrow.up.arrow.down")
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(Theme.accentInk)
                .frame(width: 32, height: 32)
                .background(Theme.accentWash, in: Circle())
            VStack(alignment: .leading, spacing: 5) {
                Text(label)
                    .font(GlassTokens.Typography.labelStrong)
                    .foregroundStyle(Theme.ink)
                    .lineLimit(1)
                GeometryReader { geo in
                    ZStack(alignment: .leading) {
                        Capsule().fill(Theme.insetFill)
                        Capsule().fill(Theme.accent)
                            .frame(width: geo.size.width * progress)
                    }
                }
                .frame(height: 4)
                .accessibilityElement()
                .accessibilityLabel(Text("Progress"))
                .accessibilityValue(Text("\(Int(progress * 100)) percent"))
            }
            Text(progressLabel)
                .font(.system(size: 11, design: .monospaced))
                .monospacedDigit()
                .foregroundStyle(Theme.inkSecondary)
        }
        .padding(EdgeInsets(top: 8, leading: 10, bottom: 8, trailing: 16))
        .glassSurface(.regular, in: Capsule())
    }

    private var progressLabel: String {
        func kb(_ n: Int64) -> String { "\(n / 1024) KB" }
        if total > 0 {
            return "\(kb(bytes)) / \(kb(total))"
        }
        return kb(bytes)
    }
}
