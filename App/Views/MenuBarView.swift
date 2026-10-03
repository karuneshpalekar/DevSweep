import DevSweepCore
import SwiftUI

@MainActor
struct MenuBarView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 8) {
                Image(systemName: "wand.and.stars").foregroundStyle(Color.accentColor)
                Text("DevSweep").font(.headline)
                Spacer()
                if let d = model.disk {
                    Text("\(SizeFormat.string(d.free)) free").font(.caption).foregroundStyle(.secondary)
                }
                SettingsLink { Image(systemName: "gearshape") }
                    .buttonStyle(.borderless)
                    .help("Settings")
                Button { NSApp.terminate(nil) } label: { Image(systemName: "power") }
                    .buttonStyle(.borderless)
                    .help("Quit DevSweep")
            }

            HStack(spacing: 10) {
                VStack(alignment: .leading, spacing: 2) {
                    SizeText(bytes: model.totalSize).font(.title2.weight(.semibold)).foregroundStyle(Color.accentColor)
                    Text(model.isScanning ? "scanning…" : "can be cleaned").font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                Button("Review") { show(.cleanUp) }
                    .buttonStyle(.borderedProminent)
                    .disabled(model.findings.isEmpty)
            }
            .padding(12)
            .background(.background, in: RoundedRectangle(cornerRadius: 10))

            let alerts = Array(model.healthAlerts.prefix(2))
            if !alerts.isEmpty {
                VStack(alignment: .leading, spacing: 6) {
                    Text("Needs attention").font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                    ForEach(Array(alerts.enumerated()), id: \.offset) { _, alert in
                        Button {
                            model.open(alert)
                            show(nil)
                        } label: {
                            HStack(alignment: .top, spacing: 8) {
                                Image(systemName: alert.critical ? "xmark.octagon.fill" : "exclamationmark.triangle.fill")
                                    .foregroundStyle(alert.critical ? .red : .orange)
                                Text(alert.text).lineLimit(2).multilineTextAlignment(.leading)
                                Spacer(minLength: 0)
                            }
                            .font(.callout)
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                    }
                }
            }

            let recent = Array(model.projects.filter(\.onDisk).sorted { ($0.lastUsed ?? .distantPast) > ($1.lastUsed ?? .distantPast) }.prefix(3))
            if !recent.isEmpty {
                VStack(alignment: .leading, spacing: 6) {
                    Text("Recent projects").font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                    ForEach(recent) { p in
                        HStack(spacing: 8) {
                            Text(p.name).lineLimit(1)
                            if let n = p.safety?.unpushedCommits, n > 0, p.safety?.hasRemote == true {
                                Text("\(n) not pushed").font(.caption2).foregroundStyle(.orange).lineLimit(1)
                            }
                            Spacer(minLength: 4)
                            Button("Open") { model.openInEditor(p) }.controlSize(.small)
                        }
                        .font(.callout)
                    }
                    Button("All projects") { show(.projects) }.buttonStyle(.link).font(.caption)
                }
            }

            HStack {
                Button("Scan now") { model.scanEverything() }
                .disabled(model.isScanning)
                .frame(maxWidth: .infinity)
                Button("Open DevSweep") { show(nil) }
                    .frame(maxWidth: .infinity)
            }

            if let date = model.lastScan {
                Text("Last scan \(date.formatted(.relative(presentation: .named)))")
                    .font(.caption2).foregroundStyle(.secondary).frame(maxWidth: .infinity)
            }
        }
        .padding(14)
        .frame(width: 320)
    }

    /// Brings the main window forward, optionally on a given section.
    private func show(_ section: SidebarItem?) {
        if let section { model.selection = section }
        if let w = NSApp.windows.first(where: { $0.identifier?.rawValue.hasPrefix("main") == true }) {
            w.makeKeyAndOrderFront(nil)
        } else {
            openWindow(id: "main")
        }
        NSApp.activate(ignoringOtherApps: true)
    }
}
