import DevSweepCore
import SwiftUI

@MainActor
struct ContentView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        NavigationSplitView {
            SidebarView()
                .navigationSplitViewColumnWidth(min: 220, ideal: 240, max: 280)
        } detail: {
            // Each screen fades in when it appears. A removal transition or an
            // animated container here puts NavigationSplitView into a
            // constraint-update loop on macOS 14, so only the new screen animates.
            ZStack {
                Group {
                    switch model.selection ?? .home {
                    case .home: HomeView()
                    case .cleanUp: CleanUpView()
                    case .projects: ProjectsView()
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
            if !model.hasLoadedProjects { model.refreshProjects() }
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
                .badge(Text(model.hasScanned && cleanable > 0 ? SizeFormat.string(cleanable) : ""))
                .tag(SidebarItem.cleanUp)
            if model.selection == .cleanUp && model.hasScanned {
                subRow("All", count: "\(model.findings.count)", selected: model.cleanupKind == nil) { model.cleanupKind = nil }
                ForEach(CleanupKind.allCases.filter { k in model.findings.contains { CleanupKind.of($0) == k } }) { k in
                    subRow(k.title, count: SizeFormat.string(size(of: k)), selected: model.cleanupKind == k) { model.cleanupKind = k }
                }
            }

            Label("Projects", systemImage: "folder")
                .badge(model.projectsNeedingPush.count)
                .tag(SidebarItem.projects)
            if model.selection == .projects {
                ForEach(ProjectsTab.allCases) { t in
                    subRow(t.title, count: projectCount(t), selected: model.projectsTab == t) { model.projectsTab = t }
                }
            }

            Label("Health", systemImage: "checkmark.shield")
                .badge(model.hasCheckedHealth ? model.healthAttentionCount : 0)
                .tag(SidebarItem.health)
            if model.selection == .health {
                ForEach(HealthTab.allCases) { t in
                    subRow(t.title, count: healthCount(t), selected: model.healthTab == t) { model.healthTab = t }
                }
            }
        }
        .listStyle(.sidebar)
        .safeAreaInset(edge: .top, spacing: 0) { diskCard.padding(.horizontal, 12).padding(.top, 4).padding(.bottom, 6) }
        .safeAreaInset(edge: .bottom, spacing: 0) {
            VStack(alignment: .leading, spacing: 2) {
                needsYou
                SidebarFooterRow(title: "History", symbol: "clock.arrow.circlepath",
                                 selected: model.selection == .history) { model.selection = .history }
                SettingsLink {
                    Label("Settings", systemImage: "gearshape")
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.horizontal, 10).frame(height: 28)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                Text(statusLine)
                    .font(.caption2).foregroundStyle(.secondary)
                    .lineLimit(2)
                    .padding(.horizontal, 10).padding(.top, 2)
            }
            .padding(.horizontal, 10)
            .padding(.bottom, 10)
        }
    }

    // MARK: - Pieces

    private var cleanable: Int64 { model.findings.reduce(0) { $0 + $1.size } }

    private func size(of kind: CleanupKind) -> Int64 {
        model.findings.filter { CleanupKind.of($0) == kind }.reduce(0) { $0 + $1.size }
    }

    private func projectCount(_ t: ProjectsTab) -> String {
        let n: Int
        switch t {
        case .projects: n = model.projects.filter(\.onDisk).count
        case .cleanup: n = model.idleProjects.count
        case .accounts: n = model.githubAccounts.count
        case .activity: n = 0
        }
        return n > 0 ? "\(n)" : ""
    }

    private func healthCount(_ t: HealthTab) -> String {
        switch t {
        case .security: return model.hasCheckedSecurity ? count(model.visibleSecurity.filter { $0.level != .ok }.count) : ""
        case .tools: return model.hasCheckedHealth ? count(model.versions?.attentionCount ?? 0) : ""
        case .ports: return model.hasLoadedPorts ? count(model.ports.filter(\.isDevelopment).count) : ""
        }
    }

    private func count(_ n: Int) -> String { n > 0 ? "\(n)" : "" }

    private func subRow(_ title: String, count: String, selected: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack {
                Text(title).fontWeight(selected ? .semibold : .regular)
                Spacer(minLength: 4)
                Text(count).font(.caption).foregroundStyle(.secondary)
            }
            .font(.callout)
            .padding(.leading, 26).padding(.trailing, 6)
            .frame(height: 24)
            .background(selected ? Color.primary.opacity(0.08) : .clear, in: RoundedRectangle(cornerRadius: 6))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .listRowSeparator(.hidden)
    }

    @ViewBuilder
    private var diskCard: some View {
        if let disk = model.disk {
            VStack(alignment: .leading, spacing: 6) {
                HStack(alignment: .firstTextBaseline, spacing: 5) {
                    Text(SizeFormat.string(disk.free)).font(.title3.weight(.semibold)).monospacedDigit()
                    Text("free").font(.caption).foregroundStyle(.secondary)
                }
                DiskBar(disk: disk, cleanable: model.hasScanned ? cleanable : 0, height: 6)
                if model.hasScanned {
                    HStack(spacing: 4) {
                        Text(SizeFormat.string(cleanable)).foregroundStyle(Color.accentColor).fontWeight(.semibold)
                        Text("can be cleaned").foregroundStyle(.secondary)
                    }
                    .font(.caption)
                } else {
                    Text(model.isScanning ? "Scanning…" : "Not scanned yet").font(.caption).foregroundStyle(.secondary)
                }
            }
            .padding(10)
            .background(.background, in: RoundedRectangle(cornerRadius: 10))
            .overlay(RoundedRectangle(cornerRadius: 10).stroke(.separator))
        }
    }

    @ViewBuilder
    private var needsYou: some View {
        let alerts = Array(model.healthAlerts.prefix(2))
        if model.hasCheckedHealth, !alerts.isEmpty {
            Text("NEEDS YOU").font(.caption2.weight(.semibold)).foregroundStyle(.secondary).padding(.horizontal, 10)
            ForEach(Array(alerts.enumerated()), id: \.offset) { _, alert in
                Button { model.open(alert) } label: {
                    HStack(alignment: .top, spacing: 7) {
                        Circle().fill(alert.critical ? Color.red : Color.orange).frame(width: 7, height: 7).padding(.top, 4)
                        Text(alert.text).font(.caption).multilineTextAlignment(.leading).lineLimit(2)
                        Spacer(minLength: 0)
                    }
                    .padding(.horizontal, 10).padding(.vertical, 6)
                    .background(.background, in: RoundedRectangle(cornerRadius: 8))
                    .overlay(RoundedRectangle(cornerRadius: 8).stroke(.separator))
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
            Spacer().frame(height: 6)
        }
    }

    private var statusLine: String {
        if model.isScanning || model.isCheckingVersions { return "Scanning…" }
        guard let last = model.lastScan else { return model.schedule.enabled ? "Never scanned" : "Never scanned · scheduled scans off" }
        let ago = last.formatted(.relative(presentation: .named))
        return model.nextScanText.map { "Last scan \(ago) · next \($0)" } ?? "Last scan \(ago)"
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
