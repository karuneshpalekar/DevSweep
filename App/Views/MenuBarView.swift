import DevSweepCore
import SwiftUI

@MainActor
struct MenuBarView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                Image(systemName: "wand.and.stars").foregroundStyle(Color.accentColor)
                Text("DevSweep").font(.headline)
                Spacer()
                SettingsLink { Image(systemName: "gearshape") }
                    .buttonStyle(.borderless)
                    .help("Settings")
                Button { NSApp.terminate(nil) } label: { Image(systemName: "power") }
                    .buttonStyle(.borderless)
                    .help("Quit DevSweep")
            }

            if let d = model.disk {
                VStack(alignment: .leading, spacing: 8) {
                    HStack {
                        Text("Macintosh HD").fontWeight(.medium)
                        Spacer()
                        Text("\(SizeFormat.string(d.free)) free of \(SizeFormat.string(d.total))")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    DiskBar(disk: d, cleanable: model.totalSize, height: 10)
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        SizeText(bytes: model.totalSize).font(.title2.weight(.semibold)).foregroundStyle(Color.accentColor)
                        Text(model.isScanning ? "scanning…" : "can be cleaned").foregroundStyle(.secondary)
                    }
                }
                .padding(12)
                .background(.background, in: RoundedRectangle(cornerRadius: 10))
            }

            let top = model.findings.sorted { $0.size > $1.size }.prefix(3)
            if !top.isEmpty {
                VStack(alignment: .leading, spacing: 6) {
                    Text("Biggest items").font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                    ForEach(Array(top)) { f in
                        HStack {
                            Text(f.title).lineLimit(1)
                            Spacer()
                            SizeText(bytes: f.size).foregroundStyle(.secondary)
                        }
                    }
                }
            }

            HStack {
                Button("Scan now") { model.scan() }.disabled(model.isScanning)
                    .frame(maxWidth: .infinity)
                Button("Open DevSweep") {
                    if let w = NSApp.windows.first(where: { $0.identifier?.rawValue.hasPrefix("main") == true }) {
                        w.makeKeyAndOrderFront(nil)
                    } else {
                        openWindow(id: "main")
                    }
                    NSApp.activate(ignoringOtherApps: true)
                }
                .buttonStyle(.borderedProminent)
                .frame(maxWidth: .infinity)
            }

            if let date = model.lastScan {
                Text("Last scan \(date.formatted(date: .omitted, time: .shortened))")
                    .font(.caption2).foregroundStyle(.secondary).frame(maxWidth: .infinity)
            }
        }
        .padding(14)
        .frame(width: 320)
    }
}
