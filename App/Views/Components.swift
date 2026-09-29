import DevSweepCore
import SwiftUI

/// One set of timings so every animation in the app feels related.
enum Motion {
    /// Side panel sliding open or closed: a soft spring, no bounce.
    static let panel = Animation.spring(response: 0.38, dampingFraction: 0.9)
    /// Content changing in place (panel item, filter, sheet step).
    static let swap = Animation.easeInOut(duration: 0.2)
    /// Moving between sidebar screens.
    static let screen = Animation.easeInOut(duration: 0.22)
}

extension Risk {
    var color: Color {
        switch self {
        case .rebuilds: return .green
        case .oldVersion: return .orange
        case .leftover: return .purple
        case .holdsData: return .red
        case .needsAdmin: return .gray
        }
    }
}

@MainActor
struct RiskBadge: View {
    let risk: Risk

    var body: some View {
        Text(risk.title)
            .font(.caption)
            .padding(.horizontal, 7)
            .padding(.vertical, 2)
            .foregroundStyle(risk.color)
            .background(risk.color.opacity(0.15), in: RoundedRectangle(cornerRadius: 5))
            .help(risk.summary)
    }
}

@MainActor
struct SizeText: View {
    let bytes: Int64

    var body: some View {
        Text(SizeFormat.string(bytes)).monospacedDigit()
    }
}

/// Free / cleanable / used bar used by Overview and the menu bar.
@MainActor
struct DiskBar: View {
    let disk: DiskInfo
    let cleanable: Int64
    var height: CGFloat = 12

    var body: some View {
        GeometryReader { geo in
            let total = max(Double(disk.total), 1)
            let used = Double(disk.total - disk.free)
            let clean = min(Double(cleanable), used)
            HStack(spacing: 2) {
                Rectangle().fill(.secondary.opacity(0.6))
                    .frame(width: geo.size.width * (used - clean) / total)
                Rectangle().fill(Color.accentColor)
                    .frame(width: geo.size.width * clean / total)
                Rectangle().fill(.quaternary)
            }
            .clipShape(RoundedRectangle(cornerRadius: height / 2))
        }
        .frame(height: height)
        .accessibilityElement()
        .accessibilityLabel("\(SizeFormat.string(cleanable)) can be cleaned, \(SizeFormat.string(disk.free)) free")
    }
}

@MainActor
struct CheckRow: View {
    let check: Check

    var body: some View {
        Label {
            Text(check.text).fixedSize(horizontal: false, vertical: true)
        } icon: {
            Image(systemName: check.status == .passed ? "checkmark.circle.fill" : "exclamationmark.triangle.fill")
                .foregroundStyle(check.status == .passed ? Color.green : Color.orange)
        }
    }
}

/// Fades a screen in when it appears. Opacity only: animating offset or
/// size here triggers a constraint loop in NavigationSplitView on macOS 14.
struct FadeIn: ViewModifier {
    @State private var shown = false

    func body(content: Content) -> some View {
        content
            .opacity(shown ? 1 : 0)
            .onAppear { withAnimation(Motion.screen) { shown = true } }
    }
}
