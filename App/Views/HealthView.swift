import DevSweepCore
import SwiftUI

/// Health: three tabs that answer "is this Mac in good shape?"
@MainActor
struct HealthView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        VStack(spacing: 0) {
            HStack(spacing: 12) {
                Picker("Health area", selection: $model.healthTab) {
                    ForEach(HealthTab.allCases) { tab in Text(label(tab)).tag(tab) }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .fixedSize()
                Spacer()
                if model.healthTab == .ports {
                    Button { model.refreshPorts() } label: { Label("Refresh", systemImage: "arrow.clockwise") }
                        .disabled(model.isLoadingPorts)
                }
            }
            .padding(.horizontal, 16).padding(.vertical, 8)
            Divider()
            switch model.healthTab {
            case .security: SecurityView()
            case .tools: RuntimesView()
            case .ports: PortsView()
            }
        }
        .navigationTitle("Health")
        .navigationSubtitle(subtitle)
        .onChange(of: model.healthTab, initial: true) { _, tab in
            if tab == .ports { model.refreshPorts() }
        }
    }

    private func label(_ tab: HealthTab) -> String {
        let n: Int
        switch tab {
        case .security: n = model.visibleSecurity.filter { $0.level != .ok }.count
        case .tools: n = model.versions?.attentionCount ?? 0
        case .ports: n = model.ports.filter(\.isDevelopment).count
        }
        return n > 0 ? "\(tab.title) · \(n)" : tab.title
    }

    private var subtitle: String {
        switch model.healthTab {
        case .security:
            return model.isCheckingSecurity ? "Checking for secrets…" : "Secrets in Downloads, Desktop, Documents and your project folders"
        case .tools:
            if model.isCheckingVersions { return "Checking · \(model.versionsStatus)" }
            guard let v = model.versions else { return "Not checked yet" }
            let when = v.date.formatted(date: .omitted, time: .shortened)
            return v.eolOffline ? "Checked \(when) · support dates may be out of date (offline)"
                                : "Checked \(when) · support dates from endoflife.date"
        case .ports:
            return model.isLoadingPorts ? "Looking…" : "What's listening on this Mac, started by you"
        }
    }
}

// MARK: - Security

@MainActor
struct SecurityView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        let items = model.visibleSecurity
        SidePanelLayout(selected: model.selectedSecurityID) {
            if items.isEmpty {
                ContentUnavailableView(model.isCheckingSecurity ? "Checking…" : "No secrets lying around",
                                       systemImage: "checkmark.shield",
                                       description: Text("DevSweep looks for recovery codes, private keys, cloud keys, password exports and .env files."))
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List {
                    let problems = items.filter { $0.level != .ok }
                    let fine = items.filter { $0.level == .ok }
                    if !problems.isEmpty {
                        Section("Needs action") { ForEach(problems) { row($0) } }
                    }
                    if !fine.isEmpty {
                        Section("Looks fine") { ForEach(fine) { row($0) } }
                    }
                }
                .listStyle(.inset)
            }
        } panel: { id in
            if let f = model.security.first(where: { $0.id == id }) { SecurityDetail(finding: f) }
        }
    }

    private func row(_ f: SecurityFinding) -> some View {
        HStack(spacing: 12) {
            Image(systemName: icon(f.kind)).foregroundStyle(.secondary).frame(width: 18)
            VStack(alignment: .leading, spacing: 2) {
                Text(f.title).fontWeight(.medium).lineLimit(1)
                Text(FS_abbreviate(f.path)).font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
            }
            Spacer()
            StatusTag(text: f.status, color: color(f.level))
            Image(systemName: model.selectedSecurityID == f.id ? "info.circle.fill" : "info.circle")
                .font(.title3).foregroundStyle(model.selectedSecurityID == f.id ? Color.accentColor : .secondary)
        }
        .modifier(SelectableRow(selected: model.selectedSecurityID == f.id) {
            model.selectedSecurityID = model.selectedSecurityID == f.id ? nil : f.id
        })
    }

    func icon(_ k: SecurityFinding.Kind) -> String {
        switch k {
        case .recoveryCodes: return "key.horizontal"
        case .privateKey: return "key"
        case .serviceAccountKey, .cloudAccessKeys: return "cloud"
        case .passwordExport: return "person.badge.key"
        case .envFile: return "doc.text"
        }
    }
}

func color(_ level: SecurityFinding.Level) -> Color {
    switch level {
    case .critical: return .red
    case .warning: return .orange
    case .ok: return .green
    }
}

@MainActor
struct SecurityDetail: View {
    @Environment(AppModel.self) private var model
    let finding: SecurityFinding

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                HStack {
                    StatusTag(text: finding.status, color: color(finding.level))
                    Spacer()
                    CloseButton { model.selectedSecurityID = nil }
                }
                Text(finding.title).font(.title3.weight(.semibold))
                Text(FS_abbreviate(finding.path)).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                section("Why it matters", finding.why)
                section("What to do", finding.whatToDo)
                VStack(alignment: .leading, spacing: 6) {
                    PanelHeading("What DevSweep checked")
                    ForEach(finding.checks, id: \.self) { CheckRow(check: $0) }
                }
                if let fix = finding.fix {
                    StepCard(step: RuntimeStep(kind: .remove, title: finding.kind == .privateKey ? "Move it to ~/.ssh" : "Fix it in git",
                                               detail: "Runs in Terminal. You see the commands first.", commands: fix))
                }
                FlowLayout(spacing: 8) {
                    if finding.kind != .envFile { Button("Move to Trash") { model.trash(finding) } }
                    Button("Show in Finder") { model.reveal(finding.path) }
                    if finding.level != .ok { Button("I've handled it") { model.markHandled(finding) } }
                }
                Text("Moving to the Trash is logged in History. Marking it handled hides it until you undo that in Settings, Ignored.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            .padding(20)
        }
    }

    private func section(_ title: String, _ text: String) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            PanelHeading(title)
            Text(text).fixedSize(horizontal: false, vertical: true)
        }
    }
}

@MainActor
struct CloseButton: View {
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: "xmark").font(.system(size: 11, weight: .bold)).foregroundStyle(.secondary)
                .frame(width: 22, height: 22).background(.quaternary, in: Circle())
        }
        .buttonStyle(.plain)
        .keyboardShortcut(.cancelAction)
        .help("Close (Esc)")
        .accessibilityLabel("Close details")
    }
}

// MARK: - Ports

@MainActor
struct PortsView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        SidePanelLayout(selected: model.selectedPortID, width: 340) {
            if model.ports.isEmpty {
                ContentUnavailableView(model.isLoadingPorts ? "Looking…" : "Nothing is listening",
                                       systemImage: "network",
                                       description: Text("Dev servers and databases you start show up here."))
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List {
                    let dev = model.ports.filter(\.isDevelopment)
                    let other = model.ports.filter { !$0.isDevelopment }
                    if !dev.isEmpty { Section("Development") { ForEach(dev) { row($0) } } }
                    if !other.isEmpty { Section("Apps and macOS") { ForEach(other) { row($0) } } }
                }
                .listStyle(.inset)
            }
        } panel: { id in
            if let p = model.ports.first(where: { $0.id == id }) { PortDetail(port: p) }
        }
    }

    private func row(_ p: ListeningPort) -> some View {
        HStack(spacing: 14) {
            Text(String(p.port)).font(.system(.title3, design: .monospaced).weight(.semibold)).frame(width: 70, alignment: .leading)
            VStack(alignment: .leading, spacing: 2) {
                Text(p.label).fontWeight(.medium).lineLimit(1)
                Text(p.folder ?? "\(p.command) · pid \(p.pid)").font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer()
            if p.reachableFromNetwork { StatusTag(text: "On your network", color: .orange) }
            Image(systemName: model.selectedPortID == p.id ? "info.circle.fill" : "info.circle")
                .font(.title3).foregroundStyle(model.selectedPortID == p.id ? Color.accentColor : .secondary)
        }
        .modifier(SelectableRow(selected: model.selectedPortID == p.id) {
            model.selectedPortID = model.selectedPortID == p.id ? nil : p.id
        })
    }
}

@MainActor
struct PortDetail: View {
    @Environment(AppModel.self) private var model
    let port: ListeningPort
    @State private var confirmStop = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                HStack {
                    Text("Port " + String(port.port)).font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                    Spacer()
                    CloseButton { model.selectedPortID = nil }
                }
                Text(port.label).font(.title3.weight(.semibold))
                VStack(alignment: .leading, spacing: 5) {
                    line("Process", "\(port.command) (pid \(port.pid))")
                    if let s = port.started { line("Started", s.formatted(.relative(presentation: .named))) }
                    if let f = port.folder { line("Folder", f) }
                    line("Reachable", port.reachableFromNetwork ? "From other devices on your network" : "Only from this Mac")
                }
                if port.reachableFromNetwork && port.isDevelopment {
                    Text("It listens on every network interface. That's handy for testing on a phone, but on public Wi-Fi others can connect too. Most dev servers have an option to listen on localhost only.")
                        .font(.callout).padding(10)
                        .background(Color.orange.opacity(0.1), in: RoundedRectangle(cornerRadius: 8))
                        .fixedSize(horizontal: false, vertical: true)
                }
                VStack(alignment: .leading, spacing: 4) {
                    PanelHeading("Command")
                    Text(port.arguments).font(.system(.caption, design: .monospaced)).textSelection(.enabled)
                        .padding(8).frame(maxWidth: .infinity, alignment: .leading)
                        .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 6))
                }
                FlowLayout(spacing: 8) {
                    if let f = port.folder { Button("Show folder") { model.reveal((f as NSString).expandingTildeInPath) } }
                    Button("Copy port") {
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.setString("localhost:\(port.port)", forType: .string)
                    }
                    if port.isDevelopment {
                        Button("Stop…") { confirmStop = true }
                    }
                }
            }
            .padding(20)
        }
        .confirmationDialog("Stop \(port.label)?", isPresented: $confirmStop) {
            Button("Stop", role: .destructive) { model.stop(port) }
        } message: {
            Text("This is like pressing Ctrl-C in its terminal. Unsaved work in that program may be lost.")
        }
    }

    private func line(_ label: String, _ value: String) -> some View {
        HStack(alignment: .firstTextBaseline) {
            Text(label).font(.caption).foregroundStyle(.secondary).frame(width: 74, alignment: .leading)
            Text(value).font(.callout).textSelection(.enabled)
        }
    }
}

// MARK: - Projects (inside Tools and versions)

@MainActor
struct ProjectsDetail: View {
    @Environment(AppModel.self) private var model
    let requirements: [ProjectRequirement]

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                HStack(alignment: .top) {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("Your projects").font(.title3.weight(.semibold))
                        Text("Versions your projects ask for, from files like .nvmrc, package.json engines, .python-version, go.mod and Gradle toolchains.")
                            .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    }
                    Spacer()
                    CloseButton { model.selectedRuntimeID = nil }
                }
                ForEach(requirements) { r in
                    VStack(alignment: .leading, spacing: 6) {
                        HStack {
                            Text(r.project).fontWeight(.semibold)
                            Spacer()
                            StatusTag(text: r.status == .ok ? "OK" : (r.status == .endOfLife ? "End of life" : "Not installed"),
                                      color: r.status == .ok ? .green : (r.status == .endOfLife ? .red : .orange))
                        }
                        Text(r.message).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                        if let fix = r.fix {
                            StepCard(step: RuntimeStep(kind: .upgrade, title: "Install \(r.toolName) \(r.spec)",
                                                       detail: "Installs it alongside what you have; your default doesn't change unless the commands say so.",
                                                       commands: fix))
                        }
                    }
                    .padding(10)
                    .background(.background, in: RoundedRectangle(cornerRadius: 8))
                    .overlay(RoundedRectangle(cornerRadius: 8).stroke(.separator))
                }
                Text("Project folders can be changed in Settings, Folders.").font(.caption).foregroundStyle(.secondary)
            }
            .padding(20)
        }
    }
}
