import DevSweepCore
import SwiftUI

@MainActor
struct OverviewView: View {
    @Environment(AppModel.self) private var model

    private let columns = [GridItem(.adaptive(minimum: 220), spacing: 12)]

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 24) {
                if !model.hasFullDiskAccess { fullDiskAccessBanner }
                disk
                HStack {
                    Spacer()
                    Button("Review \(model.findings.count) items") { model.selection = .all }
                        .buttonStyle(.borderedProminent)
                        .controlSize(.large)
                        .disabled(model.findings.isEmpty)
                }
                categories
                biggest
                if !model.ruleErrors.isEmpty {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("Some rule packs couldn't be read").font(.headline)
                        ForEach(model.ruleErrors, id: \.self) { Text($0).font(.caption).foregroundStyle(.secondary) }
                    }
                }
            }
            .padding(28)
        }
        .navigationTitle("Overview")
        .navigationSubtitle(scanSubtitle)
    }

    private var scanSubtitle: String {
        if model.isScanning { return "Scanning · \(model.scanStatus)" }
        guard let date = model.lastScan else { return "Not scanned yet" }
        return "Scanned \(date.formatted(date: .omitted, time: .shortened)) · read-only, nothing changed"
    }

    private var fullDiskAccessBanner: some View {
        HStack(spacing: 12) {
            Image(systemName: "lock.shield").font(.title2).foregroundStyle(.orange)
            VStack(alignment: .leading, spacing: 2) {
                Text("Give DevSweep Full Disk Access for complete results").fontWeight(.medium)
                Text("Without it, macOS hides some app folders (like sandbox containers), so leftovers can be missed.")
                    .font(.callout).foregroundStyle(.secondary)
            }
            Spacer()
            Button("Open Settings") { FullDiskAccess.openSettings() }
        }
        .padding(14)
        .background(Color.orange.opacity(0.1), in: RoundedRectangle(cornerRadius: 10))
    }

    @ViewBuilder
    private var disk: some View {
        if let d = model.disk {
            VStack(alignment: .leading, spacing: 10) {
                Text("Macintosh HD").font(.headline).foregroundStyle(.secondary)
                HStack(alignment: .firstTextBaseline, spacing: 28) {
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        SizeText(bytes: d.free).font(.system(size: 34, weight: .semibold))
                        Text("free now").foregroundStyle(.secondary)
                    }
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        SizeText(bytes: model.totalSize).font(.system(size: 34, weight: .semibold)).foregroundStyle(Color.accentColor)
                        Text("more can be cleaned").foregroundStyle(.secondary)
                    }
                    Spacer()
                    Text("\(SizeFormat.string(d.total)) total").foregroundStyle(.secondary)
                }
                DiskBar(disk: d, cleanable: model.totalSize, height: 14)
            }
        }
    }

    private var categories: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Where the space is").font(.headline).foregroundStyle(.secondary)
            let cats = DevSweepCore.Category.allCases.filter { model.count(of: $0) > 0 }
                .sorted { model.size(of: $0) > model.size(of: $1) }
            if cats.isEmpty {
                Text(model.isScanning ? "Scanning…" : "Nothing to clean. Nice.").foregroundStyle(.secondary)
            }
            LazyVGrid(columns: columns, spacing: 12) {
                ForEach(cats, id: \.self) { c in
                    Button { model.selection = .category(c) } label: {
                        VStack(alignment: .leading, spacing: 4) {
                            Label(c.title, systemImage: c.symbol).fontWeight(.medium)
                            SizeText(bytes: model.size(of: c)).font(.title.weight(.semibold))
                            Text("\(model.count(of: c)) item\(model.count(of: c) == 1 ? "" : "s")")
                                .font(.callout).foregroundStyle(.secondary)
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(16)
                        .background(.background, in: RoundedRectangle(cornerRadius: 10))
                        .overlay(RoundedRectangle(cornerRadius: 10).stroke(.separator))
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                }
            }
        }
    }

    @ViewBuilder
    private var biggest: some View {
        let top = model.findings.sorted { $0.size > $1.size }.prefix(5)
        if !top.isEmpty {
            VStack(alignment: .leading, spacing: 0) {
                Text("Biggest items").font(.headline).foregroundStyle(.secondary).padding(.bottom, 8)
                ForEach(Array(top)) { f in
                    Button {
                        model.selection = .all
                        model.inspectedID = f.id
                    } label: {
                        HStack {
                            RiskBadge(risk: f.risk)
                            Text(f.title)
                            Spacer()
                            SizeText(bytes: f.size).foregroundStyle(.secondary)
                        }
                        .padding(.vertical, 8)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    Divider()
                }
            }
        }
    }
}
