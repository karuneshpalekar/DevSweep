import DevSweepCore
import SwiftUI

extension SupportStatus {
    var color: Color {
        switch self {
        case .supported: return .green
        case .endingSoon: return .orange
        case .endOfLife: return .red
        case .unknown: return .gray
        }
    }
}

extension RuntimeIssue.Level {
    var symbol: String {
        switch self {
        case .critical: return "xmark.octagon.fill"
        case .warning: return "exclamationmark.triangle.fill"
        case .info: return "info.circle.fill"
        }
    }
    var color: Color {
        switch self {
        case .critical: return .red
        case .warning: return .orange
        case .info: return .secondary
        }
    }
}

/// One colored pill per installed version.
@MainActor
struct VersionPill: View {
    let install: Installation

    var body: some View {
        let notInUse = install.isRunning == false
        let color: Color = notInUse ? .gray : install.support.color
        HStack(spacing: 4) {
            if install.isDefault { Image(systemName: "terminal").font(.caption2) }
            if install.isRunning == true { Circle().fill(Color.green).frame(width: 6, height: 6) }
            Text(install.version).monospacedDigit()
            Text("· \(install.source.shortTitle)").foregroundStyle(color.opacity(0.8))
        }
        .lineLimit(1)
        .fixedSize()
        .font(.caption)
        .padding(.horizontal, 7).padding(.vertical, 3)
        .foregroundStyle(color)
        .background(color.opacity(0.13), in: RoundedRectangle(cornerRadius: 5))
        .help(helpText)
    }

    private var helpText: String {
        var parts = ["\(install.support.title)"]
        if install.isDefault { parts.append("your shell uses this one") }
        if let r = install.isRunning { parts.append(r ? "running" : "not running") }
        return parts.joined(separator: " · ")
    }
}

@MainActor
struct RuntimesView: View {
    @Environment(AppModel.self) private var model
    @State private var panelID: String?

    private static let panelWidth: CGFloat = 380

    var body: some View {
        let isOpen = model.selectedRuntimeID != nil
        HStack(spacing: 0) {
            list
            Divider().opacity(isOpen ? 1 : 0)
            ZStack(alignment: .topLeading) {
                if let id = panelID { detail(for: id).id(id).transition(.opacity) }
            }
            .frame(width: Self.panelWidth, alignment: .leading)
            .frame(width: isOpen ? Self.panelWidth : 0, alignment: .leading)
            .clipped()
            .background(.background.secondary)
            .opacity(isOpen ? 1 : 0)
        }
        .animation(Motion.panel, value: isOpen)
        .animation(Motion.swap, value: panelID)
        .onChange(of: model.selectedRuntimeID, initial: true) { _, new in if let new { panelID = new } }
        .navigationTitle("Runtimes and versions")
        .navigationSubtitle(subtitle)
    }

    private var subtitle: String {
        if model.isCheckingVersions { return "Checking · \(model.versionsStatus)" }
        guard let v = model.versions else { return "Not checked yet" }
        let when = v.date.formatted(date: .omitted, time: .shortened)
        return v.eolOffline ? "Checked \(when) · support dates may be out of date (offline)"
                            : "Checked \(when) · support dates from endoflife.date"
    }

    // MARK: - List

    @ViewBuilder
    private var list: some View {
        if let v = model.versions {
            VStack(spacing: 0) {
                legend
                Divider()
                List {
                    ForEach(v.runtimes) { r in runtimeRow(r) }
                    if let b = v.homebrew { homebrewRow(b) }
                    if let m = v.macOS { runtimeRow(m) }
                }
                .listStyle(.inset)
            }
        } else {
            ProgressView("Checking installed versions · \(model.versionsStatus)")
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    private var legend: some View {
        HStack(spacing: 14) {
            ForEach([SupportStatus.supported, .endingSoon, .endOfLife], id: \.self) { s in
                Label(s.title, systemImage: "circle.fill").foregroundStyle(s.color)
            }
            Label("Not running", systemImage: "circle.fill").foregroundStyle(.gray)
            Label("Your shell uses this", systemImage: "terminal").foregroundStyle(.secondary)
            Spacer()
        }
        .font(.caption)
        .labelStyle(LegendLabelStyle())
        .padding(.horizontal, 16).padding(.vertical, 8)
    }

    private func runtimeRow(_ r: RuntimeReport) -> some View {
        row(id: r.id, title: r.name, summary: summary(r), issues: r.issues) {
            FlowPills(installs: r.installs)
        }
    }

    private func homebrewRow(_ b: HomebrewReport) -> some View {
        row(id: "homebrew", title: "Homebrew",
            summary: "\(b.outdated.count) outdated · \(b.deprecated.count) deprecated", issues: b.issues) {
            HStack(spacing: 6) {
                if !b.deprecated.isEmpty {
                    Text("\(b.deprecated.count) deprecated").font(.caption).padding(.horizontal, 7).padding(.vertical, 3)
                        .foregroundStyle(.red).background(Color.red.opacity(0.13), in: RoundedRectangle(cornerRadius: 5))
                }
                if !b.outdated.isEmpty {
                    Text("\(b.outdated.count) outdated").font(.caption).padding(.horizontal, 7).padding(.vertical, 3)
                        .foregroundStyle(.orange).background(Color.orange.opacity(0.13), in: RoundedRectangle(cornerRadius: 5))
                }
            }
        }
    }

    private func row<Pills: View>(id: String, title: String, summary: String, issues: [RuntimeIssue],
                                  @ViewBuilder pills: () -> Pills) -> some View {
        let selected = model.selectedRuntimeID == id
        let alerts = issues.filter { $0.level != .info }
        return HStack(alignment: .center, spacing: 14) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title).fontWeight(.semibold)
                Text(summary).font(.caption).foregroundStyle(.secondary)
            }
            .frame(width: 150, alignment: .leading)
            pills()
            Spacer(minLength: 8)
            if alerts.isEmpty {
                Label("All good", systemImage: "checkmark.circle.fill").font(.caption).foregroundStyle(.green)
            } else {
                Label("\(alerts.count) to look at", systemImage: alerts.contains { $0.level == .critical } ? "xmark.octagon.fill" : "exclamationmark.triangle.fill")
                    .font(.caption).foregroundStyle(alerts.contains { $0.level == .critical } ? .red : .orange)
            }
            Image(systemName: selected ? "info.circle.fill" : "info.circle")
                .font(.title3)
                .foregroundStyle(selected ? Color.accentColor : .secondary)
                .accessibilityLabel("Show details for \(title)")
        }
        .padding(.vertical, 8)
        .contentShape(Rectangle())
        .onTapGesture { model.toggleRuntime(id) }
        .listRowBackground(
            RoundedRectangle(cornerRadius: 6)
                .fill(selected ? Color.accentColor.opacity(0.14) : .clear)
                .padding(.horizontal, 4)
        )
    }

    private func summary(_ r: RuntimeReport) -> String {
        let count = r.installs.count
        var s = "\(count) installed"
        let running = r.installs.filter { $0.isRunning == true }.count
        if r.installs.contains(where: { $0.isRunning != nil }) { s += " · \(running) running" }
        if r.id == "macos", let i = r.installs.first { s = "This Mac · \(i.version)" }
        return s
    }

    // MARK: - Detail panel

    @ViewBuilder
    private func detail(for id: String) -> some View {
        if let v = model.versions {
            if id == "homebrew", let b = v.homebrew {
                HomebrewDetail(report: b)
            } else if let r = (v.runtimes + [v.macOS].compactMap { $0 }).first(where: { $0.id == id }) {
                RuntimeDetail(report: r)
            }
        }
    }
}

/// Pills that wrap onto more lines when they don't fit.
@MainActor
struct FlowPills: View {
    let installs: [Installation]

    var body: some View {
        FlowLayout(spacing: 6) { ForEach(installs) { VersionPill(install: $0) } }
    }
}

struct FlowLayout: Layout {
    var spacing: CGFloat = 6

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let rows = arrange(width: proposal.width ?? .infinity, subviews)
        let height = rows.map(\.height).reduce(0, +) + spacing * CGFloat(max(rows.count - 1, 0))
        let width = rows.map(\.width).max() ?? 0
        return CGSize(width: proposal.width.map { min($0, width) } ?? width, height: height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var y = bounds.minY
        for row in arrange(width: bounds.width, subviews) {
            var x = bounds.minX
            for i in row.indices {
                let size = subviews[i].sizeThatFits(.unspecified)
                subviews[i].place(at: CGPoint(x: x, y: y), proposal: ProposedViewSize(size))
                x += size.width + spacing
            }
            y += row.height + spacing
        }
    }

    private struct Row { var indices: [Int] = []; var width: CGFloat = 0; var height: CGFloat = 0 }

    private func arrange(width: CGFloat, _ subviews: Subviews) -> [Row] {
        var rows: [Row] = [Row()]
        for i in subviews.indices {
            let size = subviews[i].sizeThatFits(.unspecified)
            let extra = rows[rows.count - 1].indices.isEmpty ? size.width : size.width + spacing
            if rows[rows.count - 1].width + extra > width, !rows[rows.count - 1].indices.isEmpty {
                rows.append(Row())
            }
            let isFirst = rows[rows.count - 1].indices.isEmpty
            rows[rows.count - 1].indices.append(i)
            rows[rows.count - 1].width += isFirst ? size.width : size.width + spacing
            rows[rows.count - 1].height = max(rows[rows.count - 1].height, size.height)
        }
        return rows
    }
}

struct LegendLabelStyle: LabelStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 4) { configuration.icon.font(.system(size: 7)); configuration.title.foregroundStyle(.secondary) }
    }
}

// MARK: - Panels

@MainActor
struct PanelHeader: View {
    @Environment(AppModel.self) private var model
    let title: String
    let subtitle: String?

    var body: some View {
        HStack(alignment: .top) {
            VStack(alignment: .leading, spacing: 4) {
                Text(title).font(.title3.weight(.semibold))
                if let subtitle { Text(subtitle).font(.callout).foregroundStyle(.secondary) }
            }
            Spacer()
            Button { model.selectedRuntimeID = nil } label: {
                Image(systemName: "xmark").font(.system(size: 11, weight: .bold)).foregroundStyle(.secondary)
                    .frame(width: 22, height: 22).background(.quaternary, in: Circle())
            }
            .buttonStyle(.plain)
            .keyboardShortcut(.cancelAction)
            .help("Close (Esc)")
            .accessibilityLabel("Close details")
        }
    }
}

@MainActor
struct RuntimeDetail: View {
    let report: RuntimeReport

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                PanelHeader(title: report.name, subtitle: report.recommended.map { "Recommended: \($0)" })
                if !report.issues.isEmpty {
                    VStack(alignment: .leading, spacing: 8) {
                        PanelHeading("What DevSweep noticed")
                        ForEach(report.issues, id: \.self) { issue in
                            Label {
                                Text(issue.text).fixedSize(horizontal: false, vertical: true)
                            } icon: {
                                Image(systemName: issue.level.symbol).foregroundStyle(issue.level.color)
                            }
                        }
                    }
                }
                StepsSection(steps: report.steps)
                VStack(alignment: .leading, spacing: 8) {
                    PanelHeading("Installed")
                    ForEach(report.installs) { InstallCard(install: $0) }
                }
                if let note = report.note {
                    Text(note).font(.caption).foregroundStyle(.secondary)
                }
                if let source = report.dataSource {
                    Text("Support dates: \(source)\(report.checkedAt.map { ", checked \($0.formatted(date: .abbreviated, time: .shortened))" } ?? "")")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            .padding(20)
        }
    }
}

@MainActor
struct InstallCard: View {
    let install: Installation

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack {
                Text(install.version).font(.headline).monospacedDigit()
                Text(install.support.title).font(.caption)
                    .padding(.horizontal, 6).padding(.vertical, 2)
                    .foregroundStyle(install.support.color)
                    .background(install.support.color.opacity(0.13), in: RoundedRectangle(cornerRadius: 4))
                Spacer()
                if install.isDefault {
                    Label("Your shell uses this", systemImage: "terminal").font(.caption).foregroundStyle(.secondary)
                }
            }
            line("Installed by", install.source.title + (install.sourceDetail.map { " (\($0))" } ?? ""))
            if let running = install.isRunning { line("Server", running ? "Running" : "Not running") }
            if let d = install.releaseDate { line("Released", d.formatted(date: .abbreviated, time: .omitted)) }
            if let e = install.supportEnds { line(install.support == .endOfLife ? "Support ended" : "Support ends", e.formatted(date: .abbreviated, time: .omitted)) }
            if install.patchAvailable, let latest = install.latestInCycle { line("Latest patch", latest) }
            if let data = install.dataPath {
                line("Data", FS_abbreviate(data) + (install.dataSize.map { " · \(SizeFormat.string($0))" } ?? ""))
            }
            Text(FS_abbreviate(install.path)).font(.caption.monospaced()).foregroundStyle(.secondary)
                .lineLimit(1).truncationMode(.middle).textSelection(.enabled)
        }
        .padding(10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.background, in: RoundedRectangle(cornerRadius: 8))
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(.separator))
    }

    private func line(_ label: String, _ value: String) -> some View {
        HStack(alignment: .firstTextBaseline) {
            Text(label).font(.caption).foregroundStyle(.secondary).frame(width: 92, alignment: .leading)
            Text(value).font(.caption).textSelection(.enabled)
        }
    }
}

func FS_abbreviate(_ path: String) -> String {
    path.replacingOccurrences(of: NSHomeDirectory(), with: "~")
}

@MainActor
struct PanelHeading: View {
    let text: String
    init(_ text: String) { self.text = text }
    var body: some View { Text(text).font(.caption.weight(.semibold)).foregroundStyle(.secondary) }
}

@MainActor
struct StepsSection: View {
    let steps: [RuntimeStep]

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            PanelHeading("What you can do")
            if steps.isEmpty {
                Text("Nothing needed right now.").foregroundStyle(.secondary)
            }
            ForEach(steps) { StepCard(step: $0) }
            if !steps.isEmpty {
                Text("Run in Terminal opens a Terminal window that lists the commands and waits for you to press Return. Nothing runs before that.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
    }
}

@MainActor
struct StepCard: View {
    @Environment(AppModel.self) private var model
    let step: RuntimeStep
    @State private var showAll = false

    var body: some View {
        let long = step.commands.count > 4
        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .firstTextBaseline) {
                Image(systemName: icon).foregroundStyle(step.kind == .guided ? Color.accentColor : .secondary)
                Text(step.title).fontWeight(.semibold)
            }
            Text(step.detail).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            Text((showAll || !long ? step.commands : Array(step.commands.prefix(4))).joined(separator: "\n"))
                .font(.system(.caption, design: .monospaced))
                .textSelection(.enabled)
                .padding(8)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 6))
            if long {
                Button(showAll ? "Show less" : "Show the full script (\(step.commands.count) lines)") {
                    withAnimation(Motion.swap) { showAll.toggle() }
                }
                .buttonStyle(.link).font(.caption)
            }
            HStack {
                if step.needsAdmin {
                    Label("Asks for your password", systemImage: "lock").font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                Button("Copy") { TerminalRunner.copy(step) }
                Button("Run in Terminal") { model.runInTerminal(step) }
                    .buttonStyle(.borderedProminent)
            }
        }
        .padding(12)
        .background(.background, in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(step.kind == .guided ? Color.accentColor.opacity(0.6) : Color.secondary.opacity(0.25)))
    }

    private var icon: String {
        switch step.kind {
        case .guided: return "list.number"
        case .upgrade: return "arrow.up.circle"
        case .switchDefault: return "arrow.left.arrow.right.circle"
        case .remove: return "minus.circle"
        case .backup: return "externaldrive"
        }
    }
}

@MainActor
struct HomebrewDetail: View {
    let report: HomebrewReport

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                PanelHeader(title: "Homebrew", subtitle: "\(report.outdated.count) outdated · \(report.deprecated.count) deprecated")
                if !report.deprecated.isEmpty {
                    VStack(alignment: .leading, spacing: 6) {
                        PanelHeading("Deprecated")
                        ForEach(report.deprecated, id: \.name) { p in
                            VStack(alignment: .leading, spacing: 2) {
                                Text("\(p.name) \(p.installed)").fontWeight(.medium)
                                Text(RuntimeScanner.explain(reason: p.reason).prefix(1).capitalized + RuntimeScanner.explain(reason: p.reason).dropFirst()
                                     + (p.date.map { " · since \($0)" } ?? ""))
                                    .font(.caption).foregroundStyle(.secondary)
                            }
                        }
                        if report.deprecated.contains(where: { $0.name.hasPrefix("postgresql") }) {
                            Text("For PostgreSQL, use the guided upgrade in the PostgreSQL panel instead of uninstalling it.")
                                .font(.caption).foregroundStyle(.orange)
                        }
                    }
                }
                if !report.outdated.isEmpty {
                    DisclosureGroup("\(report.outdated.count) outdated package\(report.outdated.count == 1 ? "" : "s")") {
                        VStack(alignment: .leading, spacing: 3) {
                            ForEach(report.outdated, id: \.name) { p in
                                HStack {
                                    Text(p.name)
                                    Spacer()
                                    Text("\(p.installed) → \(p.latest ?? "?")").foregroundStyle(.secondary).monospacedDigit()
                                }
                                .font(.caption)
                            }
                        }
                        .padding(.top, 4)
                    }
                }
                StepsSection(steps: report.steps)
            }
            .padding(20)
        }
    }
}
