import DevSweepCore
import SwiftUI

/// The first screen: how much space there is, what changed, and the few
/// things that need attention. Everything else lives in its own section.
@MainActor
struct HomeView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 26) {
                if !model.hasFullDiskAccess && AppSettings.welcomeDone { fullDiskAccessHint }
                disk
                HStack(alignment: .top, spacing: 28) {
                    sinceLastWeek.frame(maxWidth: .infinity, alignment: .leading)
                    attention.frame(width: 380, alignment: .leading)
                }
                if !model.ruleErrors.isEmpty {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("Some rule packs couldn't be read").font(.headline)
                        ForEach(model.ruleErrors, id: \.self) { Text($0).font(.caption).foregroundStyle(.secondary) }
                    }
                }
            }
            .padding(28)
        }
        .navigationTitle("Home")
        .navigationSubtitle(scanSubtitle)
    }

    private var scanSubtitle: String {
        if model.isScanning { return "Scanning · \(model.scanStatus)" }
        guard let date = model.lastScan else { return "Not scanned yet" }
        return "Scanned \(date.formatted(.relative(presentation: .named)))"
    }

    private var fullDiskAccessHint: some View {
        HStack(spacing: 10) {
            Image(systemName: "lock.shield").foregroundStyle(.orange)
            Text("Without Full Disk Access, a few leftovers can be missed.").font(.callout)
            Spacer()
            Button("Open Settings") { FullDiskAccess.openSettings() }.controlSize(.small)
        }
        .padding(.horizontal, 14).padding(.vertical, 9)
        .background(Color.orange.opacity(0.1), in: RoundedRectangle(cornerRadius: 8))
    }

    @ViewBuilder
    private var disk: some View {
        if let d = model.disk {
            VStack(alignment: .leading, spacing: 12) {
                HStack(alignment: .firstTextBaseline, spacing: 28) {
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        SizeText(bytes: d.free).font(.system(size: 34, weight: .semibold))
                        Text("free").foregroundStyle(.secondary)
                    }
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        SizeText(bytes: model.totalSize).font(.system(size: 34, weight: .semibold)).foregroundStyle(Color.accentColor)
                        Text("can be cleaned").foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button("Review \(model.findings.count) item\(model.findings.count == 1 ? "" : "s")") {
                        model.selection = .cleanUp
                    }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.large)
                    .disabled(model.findings.isEmpty)
                }
                DiskBar(disk: d, cleanable: model.totalSize, height: 12)
                Text("Macintosh HD · \(SizeFormat.string(d.total))").font(.caption).foregroundStyle(.secondary)
            }
            .padding(20)
            .background(.background, in: RoundedRectangle(cornerRadius: 12))
            .overlay(RoundedRectangle(cornerRadius: 12).stroke(.separator))
        }
    }

    // MARK: - Since last week

    private var sinceLastWeek: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text("Since last week").font(.headline).foregroundStyle(.secondary).padding(.bottom, 8)
            let cleaned = model.cleanedThisWeek
            if let c = model.changes {
                ForEach(c.grew.prefix(3)) { changeRow("Grew", .orange, $0.title, "+" + SizeFormat.string($0.bytes)) }
                ForEach(c.new.prefix(3)) { changeRow("New", .blue, $0.title, SizeFormat.string($0.bytes)) }
            }
            if cleaned.count > 0 {
                changeRow("Cleaned", .green, "You cleaned \(cleaned.count) item\(cleaned.count == 1 ? "" : "s")",
                          SizeFormat.string(cleaned.bytes))
            }
            if model.changes == nil && cleaned.count == 0 {
                Text("This is the first scan. From next week, this shows what grew and what's new.")
                    .foregroundStyle(.secondary).padding(.vertical, 8)
            } else if let c = model.changes, c.isEmpty, cleaned.count == 0 {
                Text("Nothing changed much since \(c.since.formatted(date: .abbreviated, time: .omitted)).")
                    .foregroundStyle(.secondary).padding(.vertical, 8)
            }
        }
    }

    private func changeRow(_ tag: String, _ color: Color, _ title: String, _ value: String) -> some View {
        VStack(spacing: 0) {
            HStack(spacing: 10) {
                Text(tag).font(.caption).padding(.horizontal, 7).padding(.vertical, 2)
                    .foregroundStyle(color).background(color.opacity(0.13), in: RoundedRectangle(cornerRadius: 5))
                Text(title).lineLimit(1)
                Spacer()
                Text(value).foregroundStyle(.secondary).monospacedDigit()
            }
            .padding(.vertical, 9)
            Divider()
        }
    }

    // MARK: - Needs attention

    @ViewBuilder
    private var attention: some View {
        let alerts = Array(model.versionAlerts.prefix(4))
        VStack(alignment: .leading, spacing: 8) {
            Text("Needs attention").font(.headline).foregroundStyle(.secondary)
            if alerts.isEmpty {
                Label(model.isCheckingVersions ? "Checking…" : "Nothing needs attention right now.",
                      systemImage: "checkmark.circle").foregroundStyle(.secondary).padding(.vertical, 8)
            } else {
                VStack(spacing: 0) {
                    ForEach(Array(alerts.enumerated()), id: \.offset) { i, alert in
                        Button {
                            model.selection = .health
                            model.selectedRuntimeID = alert.id
                        } label: {
                            HStack(alignment: .top, spacing: 10) {
                                Image(systemName: alert.issue.level.symbol).foregroundStyle(alert.issue.level.color)
                                Text(alert.issue.text).multilineTextAlignment(.leading).fixedSize(horizontal: false, vertical: true)
                                Spacer(minLength: 0)
                            }
                            .padding(12)
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        if i < alerts.count - 1 { Divider() }
                    }
                }
                .background(.background, in: RoundedRectangle(cornerRadius: 12))
                .overlay(RoundedRectangle(cornerRadius: 12).stroke(.separator))
                if model.versionAlerts.count > alerts.count {
                    Text("Showing the \(alerts.count) most important. Health has the rest.")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
        }
    }
}
