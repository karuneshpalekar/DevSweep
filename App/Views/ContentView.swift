import DevSweepCore
import SwiftUI

@MainActor
struct ContentView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        NavigationSplitView {
            SidebarView()
                .navigationSplitViewColumnWidth(min: 190, ideal: 210, max: 260)
        } detail: {
            // Each screen fades in when it appears. A removal transition or an
            // animated container here puts NavigationSplitView into a
            // constraint-update loop on macOS 14, so only the new screen animates.
            ZStack {
                Group {
                    switch model.selection ?? .home {
                    case .home: HomeView()
                    case .cleanUp: CleanUpView()
                    case .health: HealthView()
                    case .history: HistoryView()
                    }
                }
                .modifier(FadeIn())
                .id(model.selection)
            }
            .sheet(isPresented: $model.showWelcome) { WelcomeView() }
        }
        // One toolbar for every screen. Swapping toolbar items per screen is
        // fragile in NavigationSplitView on macOS 14.
        .toolbar {
            ToolbarItem {
                Button { model.scanEverything() } label: { Label("Scan again", systemImage: "arrow.clockwise") }
                    .help(model.isScanning || model.isCheckingVersions ? "Scanning…" : "Scan again")
                    .disabled(model.isScanning || model.isCheckingVersions)
            }
        }
        .sheet(isPresented: $model.showReview) { ReviewSheet() }
        .alert("Something went wrong", isPresented: Binding(
            get: { model.errorMessage != nil }, set: { if !$0 { model.errorMessage = nil } }
        )) {
            Button("OK") { model.errorMessage = nil }
        } message: {
            Text(model.errorMessage ?? "")
        }
        .task {
            // The first scan waits for the welcome screen, so macOS's
            // permission prompts come after the explanation, not before it.
            guard !model.showWelcome else { return }
            if model.lastScan == nil { model.scan() }
            if model.versions == nil { model.checkVersions() }
            if model.security.isEmpty { model.checkSecurity() }
        }
    }
}

@MainActor
struct SidebarView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        List(selection: $model.selection) {
            Label("Home", systemImage: "house").tag(SidebarItem.home)
            Label("Clean up", systemImage: "wand.and.stars")
                .badge(model.findings.count)
                .tag(SidebarItem.cleanUp)
            Label("Health", systemImage: "checkmark.shield")
                .badge(model.healthAttentionCount)
                .tag(SidebarItem.health)
        }
        .listStyle(.sidebar)
        .safeAreaInset(edge: .bottom) {
            VStack(spacing: 2) {
                SidebarFooterRow(title: "History", symbol: "clock.arrow.circlepath",
                                 selected: model.selection == .history) { model.selection = .history }
                SettingsLink {
                    Label("Settings", systemImage: "gearshape")
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.horizontal, 10).frame(height: 28)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
            .padding(.horizontal, 10)
            .padding(.bottom, 12)
        }
    }
}

/// History and Settings sit at the bottom of the sidebar, apart from the
/// three main sections.
@MainActor
struct SidebarFooterRow: View {
    let title: String
    let symbol: String
    let selected: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Label(title, systemImage: symbol)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 10).frame(height: 28)
                .background(selected ? Color.primary.opacity(0.1) : .clear, in: RoundedRectangle(cornerRadius: 6))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}

extension DevSweepCore.Category {
    var symbol: String {
        switch self {
        case .leftovers: return "shippingbox"
        case .ideVersions: return "hammer"
        case .android: return "iphone.gen1"
        case .xcode: return "swift"
        case .toolchains: return "wrench.and.screwdriver"
        case .packageCaches: return "archivebox"
        case .browsers: return "globe"
        case .projects: return "folder"
        case .aiModels: return "brain"
        case .backgroundServices: return "gearshape.2"
        case .largeFiles: return "doc.zipper"
        case .docker: return "shippingbox.circle"
        case .backups: return "externaldrive"
        }
    }
}
