import AppKit
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
                    case .settings: SettingsView()
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
    /// The highlighted section. Mirrors model.selection but animates on its
    /// own, so the sliding highlight never animates the detail screen
    /// (animating NavigationSplitView's detail crashes on macOS 14).
    @State private var shown: SidebarItem = .home
    @Namespace private var highlight

    var body: some View {
        @Bindable var model = model
        ScrollView {
            VStack(spacing: 2) {
                mainRow(.home, "Home", "house", badge: "")

                mainRow(.cleanUp, "Clean up", "wand.and.stars",
                        badge: model.hasScanned && cleanable > 0 ? SizeFormat.string(cleanable) : "")
                if shown == .cleanUp && model.hasScanned {
                    group {
                        subRow("All", count: "\(model.findings.count)", selected: model.cleanupKind == nil, section: .cleanUp) { model.cleanupKind = nil }
                        ForEach(CleanupKind.allCases.filter { k in model.findings.contains { CleanupKind.of($0) == k } }) { k in
                            subRow(k.title, count: SizeFormat.string(size(of: k)), selected: model.cleanupKind == k, section: .cleanUp) { model.cleanupKind = k }
                        }
                    }
                }

                mainRow(.projects, "Projects", "folder", badge: count(model.projectsNeedingPush.count))
                if shown == .projects {
                    group {
                        ForEach(ProjectsTab.allCases) { t in
                            subRow(t.title, count: projectCount(t), selected: model.projectsTab == t, section: .projects) { model.projectsTab = t }
                        }
                    }
                }

                mainRow(.health, "Health", "checkmark.shield", badge: model.hasCheckedHealth ? count(model.healthAttentionCount) : "")
                if shown == .health {
                    group {
                        ForEach(HealthTab.allCases) { t in
                            subRow(t.title, count: healthCount(t), selected: model.healthTab == t, section: .health) { model.healthTab = t }
                        }
                    }
                }

                mainRow(.history, "History", "clock.arrow.circlepath", badge: "")

                mainRow(.settings, "Settings", "gearshape", badge: "")
                if shown == .settings {
                    group {
                        ForEach(SettingsTab.allCases) { t in
                            subRow(t.title, count: "", selected: model.settingsTab == t, section: .settings) { model.settingsTab = t }
                        }
                    }
                }
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 4)
        }
        .background(SidebarMaterial())
        .onAppear { shown = model.selection ?? .home }
        // Keeps the highlight in step when something else changes the section
        // (an alert, the menu bar, ⌘,).
        .onChange(of: model.selection) { _, new in
            if let new, new != shown { withAnimation(Motion.slide) { shown = new } }
        }
        .safeAreaInset(edge: .top, spacing: 0) { diskCard.padding(.horizontal, 12).padding(.top, 4).padding(.bottom, 6) }
        .safeAreaInset(edge: .bottom, spacing: 0) {
            VStack(alignment: .leading, spacing: 2) {
                needsYou
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

    private func mainRow(_ item: SidebarItem, _ title: String, _ symbol: String, badge: String) -> some View {
        let on = shown == item
        return Button {
            withAnimation(Motion.slide) { shown = item }
            model.selection = item
        } label: {
            HStack(spacing: 9) {
                Image(systemName: symbol).frame(width: 18)
                Text(title).fontWeight(on ? .medium : .regular)
                Spacer(minLength: 4)
                if !badge.isEmpty {
                    Text(badge).font(.caption).foregroundStyle(on ? Color.white.opacity(0.85) : .secondary)
                }
            }
            .foregroundStyle(on ? Color.white : Color.primary)
            .padding(.horizontal, 10).frame(height: 30)
            .background {
                if on {
                    RoundedRectangle(cornerRadius: 7).fill(Color.accentColor)
                        .matchedGeometryEffect(id: "mainHighlight", in: highlight)
                }
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(on ? .isSelected : [])
    }

    private func group<Content: View>(@ViewBuilder _ content: () -> Content) -> some View {
        VStack(spacing: 1) { content() }
            .padding(.leading, 12).padding(.bottom, 3)
            .transition(.opacity)
    }

    private func subRow(_ title: String, count: String, selected: Bool, section: SidebarItem, action: @escaping () -> Void) -> some View {
        Button {
            withAnimation(Motion.slide) { action() }
        } label: {
            HStack {
                Text(title).fontWeight(selected ? .semibold : .regular)
                Spacer(minLength: 4)
                Text(count).font(.caption).foregroundStyle(.secondary)
            }
            .font(.callout)
            .padding(.horizontal, 10)
            .frame(height: 26)
            .background {
                if selected {
                    RoundedRectangle(cornerRadius: 6).fill(Color.primary.opacity(0.1))
                        .matchedGeometryEffect(id: "subHighlight-\(section)", in: highlight)
                }
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(selected ? .isSelected : [])
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


/// The translucent sidebar background SwiftUI's List would have given.
struct SidebarMaterial: NSViewRepresentable {
    func makeNSView(context: Context) -> NSVisualEffectView {
        let v = NSVisualEffectView()
        v.material = .sidebar
        v.blendingMode = .behindWindow
        v.state = .followsWindowActiveState
        return v
    }
    func updateNSView(_ v: NSVisualEffectView, context: Context) {}
}
