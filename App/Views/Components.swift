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
        // 0 means macOS doesn't report a size (e.g. Time Machine snapshots).
        Text(bytes == 0 ? "—" : SizeFormat.string(bytes)).monospacedDigit()
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

/// A list with a detail panel that slides in from the right. The panel keeps
/// showing its last item while it closes, and cross-fades between items.
/// (Not SwiftUI's .inspector, which crashes in NavigationSplitView on macOS 14.)
@MainActor
struct SidePanelLayout<Content: View, Panel: View>: View {
    let selected: String?
    var width: CGFloat = 360
    @ViewBuilder let content: () -> Content
    @ViewBuilder let panel: (String) -> Panel
    @State private var shown: String?

    var body: some View {
        let isOpen = selected != nil
        HStack(spacing: 0) {
            content()
            Divider().opacity(isOpen ? 1 : 0)
            ZStack(alignment: .topLeading) {
                if let key = shown { panel(key).id(key).transition(.opacity) }
            }
            .frame(width: width, alignment: .leading)
            .frame(width: isOpen ? width : 0, alignment: .leading)
            .clipped()
            .background(.background.secondary)
            .opacity(isOpen ? 1 : 0)
        }
        .animation(Motion.panel, value: isOpen)
        .animation(Motion.swap, value: shown)
        .onChange(of: selected, initial: true) { _, new in if let new { shown = new } }
    }
}

/// A selectable list row: tap to open its panel, tap again to close.
struct SelectableRow: ViewModifier {
    let selected: Bool
    let toggle: () -> Void

    func body(content: Content) -> some View {
        content
            .padding(.vertical, 6)
            .contentShape(Rectangle())
            .onTapGesture(perform: toggle)
            .listRowBackground(
                RoundedRectangle(cornerRadius: 6)
                    .fill(selected ? Color.accentColor.opacity(0.14) : .clear)
                    .padding(.horizontal, 4)
            )
    }
}

/// Small colored status label.
struct StatusTag: View {
    let text: String
    let color: Color

    var body: some View {
        Text(text).font(.caption).lineLimit(1)
            .padding(.horizontal, 7).padding(.vertical, 2)
            .foregroundStyle(color)
            .background(color.opacity(0.13), in: RoundedRectangle(cornerRadius: 5))
    }
}

/// Shown wherever GitHub features need something: the GitHub CLI isn't installed,
/// or it is but no account is signed in. Says which, and what to do.
@MainActor
struct GitHubSetupCard: View {
    @Environment(AppModel.self) private var model
    /// Opens the sign-in sheet in whichever window this card is in.
    var addAccount: () -> Void

    var body: some View {
        switch model.githubStatus {
        case .none:
            card(symbol: "arrow.triangle.2.circlepath", title: "Checking GitHub…",
                 detail: "Looking for the GitHub command-line tool and your accounts.") {
                ProgressView().controlSize(.small)
            }
        case .some(.notInstalled):
            card(symbol: "terminal", title: "Connect GitHub",
                 detail: "DevSweep uses GitHub's command-line tool, gh, to list your repositories and download them. It isn't installed on this Mac. Projects already on your Mac still show below.") {
                if Shell.locate("brew") != nil {
                    Button("Install with Homebrew") {
                        model.runInTerminal(RuntimeStep(kind: .upgrade, title: "Install the GitHub CLI",
                                                        detail: "Installs gh with Homebrew. When it's done, come back and press Check again.",
                                                        commands: ["brew install gh"]))
                    }
                    .buttonStyle(.borderedProminent)
                }
                Button("Get it from cli.github.com") {
                    if let url = URL(string: "https://cli.github.com") { NSWorkspace.shared.open(url) }
                }
                Button("Check again") { model.refreshProjects() }.disabled(model.isLoadingProjects)
            }
        case .some(.notSignedIn):
            card(symbol: "person.crop.circle.badge.plus", title: "Sign in to GitHub",
                 detail: "The GitHub command-line tool is installed, but no account is signed in yet. Sign in to see your repositories, download them, and publish folders. Projects already on your Mac still show below.") {
                Button("Sign in…", action: addAccount).buttonStyle(.borderedProminent)
                Button("Check again") { model.refreshProjects() }.disabled(model.isLoadingProjects)
            }
        case .some(.signedIn):
            EmptyView()
        }
    }

    private func card<Actions: View>(symbol: String, title: String, detail: String, @ViewBuilder actions: () -> Actions) -> some View {
        HStack(alignment: .top, spacing: 14) {
            Image(systemName: symbol).font(.title).foregroundStyle(Color.accentColor).frame(width: 34)
            VStack(alignment: .leading, spacing: 6) {
                Text(title).font(.headline)
                Text(detail).font(.callout).foregroundStyle(.secondary).lineLimit(5)
                HStack(spacing: 8) { actions() }.padding(.top, 4)
            }
            Spacer(minLength: 0)
        }
        .padding(16)
        .background(.background, in: RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).stroke(.separator))
        .layoutPriority(1)
    }
}

/// A screen that has nothing to show because nothing has been scanned yet.
@MainActor
struct NotScannedView: View {
    let title: String
    let detail: String
    let isWorking: Bool
    let status: String
    let action: () -> Void

    var body: some View {
        VStack(spacing: 14) {
            if isWorking {
                ProgressView().controlSize(.large)
                Text("Scanning…").font(.title3.weight(.semibold))
                Text(status).foregroundStyle(.secondary)
            } else {
                Image(systemName: "magnifyingglass").font(.system(size: 34)).foregroundStyle(.secondary)
                Text(title).font(.title3.weight(.semibold))
                Text(detail).foregroundStyle(.secondary).multilineTextAlignment(.center).frame(maxWidth: 340)
                Button("Scan now", action: action).buttonStyle(.borderedProminent).controlSize(.large)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}
