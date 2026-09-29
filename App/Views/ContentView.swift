import DevSweepCore
import SwiftUI

@MainActor
struct ContentView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        NavigationSplitView {
            SidebarView()
                .navigationSplitViewColumnWidth(min: 200, ideal: 232, max: 280)
        } detail: {
            // Each screen fades in when it appears. A removal transition or an
            // animated container here puts NavigationSplitView into a
            // constraint-update loop on macOS 14, so only the new screen animates.
            ZStack {
                Group {
                    switch model.selection ?? .overview {
                    case .overview: OverviewView()
                    case .history: HistoryView()
                    case .ignored: IgnoredView()
                    case .all, .category: FindingsView(item: model.selection ?? .all)
                    }
                }
                .modifier(FadeIn())
                .id(model.selection)
            }
        }
        // One toolbar for every screen. Swapping toolbar items per screen is
        // fragile in NavigationSplitView on macOS 14.
        .toolbar {
            ToolbarItem {
                Button { model.scan() } label: { Label("Scan again", systemImage: "arrow.clockwise") }
                    .help(model.isScanning ? "Scanning…" : "Scan again")
                    .disabled(model.isScanning)
            }
        }
        .sheet(isPresented: $model.showReview) { ReviewSheet() }
        .alert("Couldn't restore everything", isPresented: Binding(
            get: { model.errorMessage != nil }, set: { if !$0 { model.errorMessage = nil } }
        )) {
            Button("OK") { model.errorMessage = nil }
        } message: {
            Text(model.errorMessage ?? "")
        }
        .task { if model.lastScan == nil { model.scan() } }
    }
}

@MainActor
struct SidebarView: View {
    @Environment(AppModel.self) private var model

    private let cleanUp: [DevSweepCore.Category] = [.leftovers, .ideVersions, .android, .xcode, .toolchains, .packageCaches, .projects]
    private let watch: [DevSweepCore.Category] = [.aiModels, .browsers]

    var body: some View {
        @Bindable var model = model
        List(selection: $model.selection) {
            Label("Overview", systemImage: "gauge.with.dots.needle.33percent").tag(SidebarItem.overview)
            Section("Clean up") {
                row("All items", "square.stack.3d.up", .all, model.findings.count)
                ForEach(cleanUp, id: \.self) { c in row(c.title, c.symbol, .category(c), model.count(of: c)) }
            }
            Section("Keep current") {
                row(DevSweepCore.Category.backgroundServices.title, DevSweepCore.Category.backgroundServices.symbol,
                    .category(.backgroundServices), model.count(of: .backgroundServices))
            }
            Section("Watch") {
                ForEach(watch, id: \.self) { c in row(c.title, c.symbol, .category(c), model.count(of: c)) }
            }
            Section {
                Label("History", systemImage: "clock.arrow.circlepath").tag(SidebarItem.history)
                row("Ignored", "eye.slash", .ignored, model.ignoredIDs.count)
            }
        }
        .listStyle(.sidebar)
        .safeAreaInset(edge: .bottom) {
            HStack {
                Text("Appearance").font(.caption).foregroundStyle(.secondary)
                Spacer()
                AppearanceToggle().frame(width: 120)
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 10)
        }
    }

    private func row(_ title: String, _ symbol: String, _ item: SidebarItem, _ count: Int) -> some View {
        Label(title, systemImage: symbol)
            .badge(count)
            .tag(item)
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
        }
    }
}
