import SwiftUI

struct FileTransferBannerView: View {
    let label: String
    let bytes: Int64
    let total: Int64

    private var progress: Double {
        total > 0 ? min(Double(bytes) / Double(total), 1.0) : 0
    }

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: "arrow.up.arrow.down.circle.fill")
                .font(.system(size: 20))
                .foregroundStyle(Theme.accent)
            VStack(alignment: .leading, spacing: 4) {
                Text(label)
                    .font(.system(size: 12, weight: .medium))
                    .lineLimit(1)
                ProgressView(value: progress)
                    .tint(Theme.accent)
            }
            Text(progressLabel)
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
                .monospacedDigit()
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 8)
        .background(.regularMaterial)
    }

    private var progressLabel: String {
        func kb(_ n: Int64) -> String { "\(n / 1024) KB" }
        if total > 0 {
            return "\(kb(bytes)) / \(kb(total))"
        }
        return kb(bytes)
    }
}
