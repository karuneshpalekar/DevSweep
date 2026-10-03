import AppKit
import DevSweepCore
import SwiftUI

enum AppearanceMode: String, CaseIterable, Identifiable {
    case system, light, dark

    var id: String { rawValue }

    var title: String {
        switch self {
        case .system: return "Match System"
        case .light: return "Light"
        case .dark: return "Dark"
        }
    }

    static let storageKey = "appearance"

    static var current: AppearanceMode {
        AppearanceMode(rawValue: UserDefaults.standard.string(forKey: storageKey) ?? "") ?? .system
    }

    /// Applies to every window, including the menu bar popover.
    @MainActor
    func apply() {
        switch self {
        case .system: NSApp.appearance = nil
        case .light: NSApp.appearance = NSAppearance(named: .aqua)
        case .dark: NSApp.appearance = NSAppearance(named: .darkAqua)
        }
    }
}

extension AppearanceMode {
    var symbol: String {
        switch self {
        case .system: return "circle.lefthalf.filled"
        case .light: return "sun.max"
        case .dark: return "moon"
        }
    }
}

/// Always-visible System / Light / Dark switch for the sidebar.
@MainActor
struct AppearanceToggle: View {
    @AppStorage(AppearanceMode.storageKey) private var appearance = AppearanceMode.system.rawValue

    var body: some View {
        Picker("Appearance", selection: $appearance) {
            ForEach(AppearanceMode.allCases) { mode in
                Image(systemName: mode.symbol)
                    .help(mode.title)
                    .accessibilityLabel(mode.title)
                    .tag(mode.rawValue)
            }
        }
        .pickerStyle(.segmented)
        .labelsHidden()
        .onChange(of: appearance) { _, new in
            (AppearanceMode(rawValue: new) ?? .system).apply()
        }
    }
}

/// The Settings window (⌘,). One list per tab; nothing crowded.
@MainActor
struct SettingsView: View {
    var body: some View {
        TabView {
            GeneralSettings().tabItem { Label("General", systemImage: "gearshape") }
            FolderSettings().tabItem { Label("Folders", systemImage: "folder") }
            IgnoredSettings().tabItem { Label("Ignored", systemImage: "eye.slash") }
            PermissionSettings().tabItem { Label("Permissions", systemImage: "lock") }
        }
        .frame(width: 620, height: 420)
    }
}

@MainActor
struct GeneralSettings: View {
    @AppStorage(AppearanceMode.storageKey) private var appearance = AppearanceMode.system.rawValue

    var body: some View {
        Form {
            Section {
                Picker("Appearance", selection: $appearance) {
                    ForEach(AppearanceMode.allCases) { Text($0.title).tag($0.rawValue) }
                }
                .pickerStyle(.segmented)
            }
            Section {
                LabeledContent("Scans") {
                    Text("Read-only. Nothing is cleaned until you review and confirm.")
                        .foregroundStyle(.secondary)
                }
            }
        }
        .formStyle(.grouped)
        .onChange(of: appearance) { _, new in
            (AppearanceMode(rawValue: new) ?? .system).apply()
        }
    }
}

@MainActor
struct FolderSettings: View {
    @Environment(AppModel.self) private var model
    @State private var folders: [String] = AppSettings.projectFolders ?? RuleLoader.defaultProjectRoots
    @State private var selection: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("DevSweep looks for idle projects in these folders. Only node_modules, virtual environments and Pods are suggested, and only in projects untouched for 30 days.")
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            List(selection: $selection) {
                ForEach(folders, id: \.self) { f in
                    HStack {
                        Image(systemName: "folder").foregroundStyle(.secondary)
                        Text(f)
                        Spacer()
                        if !FileManager.default.fileExists(atPath: (f as NSString).expandingTildeInPath) {
                            Text("Not on this Mac").font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    .tag(f)
                }
            }
            .listStyle(.bordered(alternatesRowBackgrounds: true))
            HStack {
                Button { add() } label: { Image(systemName: "plus") }.accessibilityLabel("Add folder")
                Button { remove() } label: { Image(systemName: "minus") }
                    .disabled(selection == nil)
                    .accessibilityLabel("Remove folder")
                Spacer()
                Button("Restore defaults") { save(RuleLoader.defaultProjectRoots, isDefault: true) }
            }
            .controlSize(.small)
        }
        .padding(20)
    }

    private func add() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = true
        panel.prompt = "Add"
        guard panel.runModal() == .OK else { return }
        let home = NSHomeDirectory()
        let added = panel.urls.map { $0.path.hasPrefix(home) ? "~" + $0.path.dropFirst(home.count) : $0.path }
        save(folders + added.filter { !folders.contains($0) }, isDefault: false)
    }

    private func remove() {
        guard let s = selection else { return }
        save(folders.filter { $0 != s }, isDefault: false)
        selection = nil
    }

    private func save(_ list: [String], isDefault: Bool) {
        folders = list
        AppSettings.projectFolders = isDefault ? nil : list
        model.scan()
    }
}

@MainActor
struct IgnoredSettings: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Group {
            if model.ignoredIDs.isEmpty {
                ContentUnavailableView("Nothing ignored", systemImage: "eye",
                                       description: Text("Items you choose to always ignore show up here, so you can bring them back."))
            } else {
                VStack(alignment: .leading, spacing: 10) {
                    Text("These are left out of every scan.").foregroundStyle(.secondary)
                    List(model.ignoredIDs.sorted(), id: \.self) { id in
                        HStack {
                            Text(id).font(.callout.monospaced()).lineLimit(1).truncationMode(.middle)
                            Spacer()
                            Button("Stop ignoring") { model.unignore(id) }.controlSize(.small)
                        }
                    }
                    .listStyle(.bordered(alternatesRowBackgrounds: true))
                }
                .padding(20)
            }
        }
    }
}

@MainActor
struct PermissionSettings: View {
    @State private var granted = FullDiskAccess.isGranted

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 10) {
                Image(systemName: granted ? "checkmark.shield.fill" : "lock.shield")
                    .font(.title).foregroundStyle(granted ? .green : .orange)
                VStack(alignment: .leading, spacing: 2) {
                    Text(granted ? "Full Disk Access is on" : "Full Disk Access is off").font(.headline)
                    Text(granted ? "DevSweep can check every folder it needs, without prompts."
                                 : "macOS asks before DevSweep looks in some folders, and a few leftovers may be missed.")
                        .foregroundStyle(.secondary)
                }
            }
            Button(granted ? "Open Privacy settings" : "Turn on Full Disk Access") { FullDiskAccess.openSettings() }
            Divider()
            Text("DevSweep never sends anything about your Mac anywhere. The only network request is for public support dates from endoflife.date.")
                .font(.callout).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Spacer()
        }
        .padding(24)
        .onAppear { granted = FullDiskAccess.isGranted }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            granted = FullDiskAccess.isGranted
        }
    }
}
