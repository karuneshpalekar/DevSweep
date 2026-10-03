import AppKit
import DevSweepCore
import Observation
import SwiftUI

/// The four places in the app. Settings is its own window.
enum SidebarItem: Hashable {
    case home
    case cleanUp
    case health
    case history
}

/// Preferences that aren't per-window state.
enum AppSettings {
    static let defaults = UserDefaults.standard

    /// Folders searched for idle projects; nil means DevSweep's defaults.
    static var projectFolders: [String]? {
        get { defaults.stringArray(forKey: "projectFolders") }
        set { defaults.set(newValue, forKey: "projectFolders") }
    }

    static var welcomeDone: Bool {
        get { defaults.bool(forKey: "welcomeDone") }
        set { defaults.set(newValue, forKey: "welcomeDone") }
    }
}

@MainActor
@Observable
final class AppModel {
    var findings: [Finding] = []
    var lastScan: Date?
    var isScanning = false
    var scanStatus = ""
    var disk: DiskInfo? = DiskInfo.current()
    var ruleErrors: [String] = []

    var selection: SidebarItem? = .home
    var checked: Set<String> = []
    var chosenAction: [String: String] = [:]
    var inspectedID: String?

    var showReview = false
    var isCleaning = false
    var cleanStatus = ""
    var outcomes: [ActionOutcome]?

    var versions: VersionsResult?
    var isCheckingVersions = false
    var versionsStatus = ""
    var selectedRuntimeID: String?

    var changes: ScanChanges?
    var showWelcome = !AppSettings.welcomeDone && ProcessInfo.processInfo.environment["DEVSWEEP_SHOTS"] == nil

    var historyEntries: [HistoryEntry] = []
    var ignoredIDs: Set<String> = []
    var errorMessage: String?
    var hasFullDiskAccess = FullDiskAccess.isGranted

    @ObservationIgnored let history = HistoryStore()
    @ObservationIgnored let ignores = IgnoreStore()
    @ObservationIgnored let snapshots = ScanSnapshotStore()

    init() {
        historyEntries = history.entries
        ignoredIDs = ignores.ids
    }

    // MARK: - Derived

    var totalSize: Int64 { findings.reduce(0) { $0 + $1.size } }

    /// What was cleaned in the last 7 days, from History.
    var cleanedThisWeek: (count: Int, bytes: Int64) {
        let since = Date().addingTimeInterval(-7 * 86_400)
        let recent = historyEntries.filter { $0.date >= since && $0.restoredAt == nil }
        return (recent.count, recent.reduce(0) { $0 + $1.size })
    }

    func size(of category: DevSweepCore.Category) -> Int64 {
        findings.filter { $0.category == category }.reduce(0) { $0 + $1.size }
    }

    func count(of category: DevSweepCore.Category) -> Int {
        findings.filter { $0.category == category }.count
    }

    var inspected: Finding? { findings.first { $0.id == inspectedID } }

    func action(for f: Finding) -> CleanAction {
        if let id = chosenAction[f.id], let a = f.actions.first(where: { $0.id == id }) { return a }
        return f.defaultAction ?? CleanAction(kind: .trash)
    }

    func setAction(_ a: CleanAction, for f: Finding) { chosenAction[f.id] = a.id }

    /// Opens the explanation for this item, or closes it if it is already open.
    func toggleExplanation(for f: Finding) {
        inspectedID = inspectedID == f.id ? nil : f.id
    }

    func closeExplanation() { inspectedID = nil }

    var checkedFindings: [Finding] { findings.filter { checked.contains($0.id) } }
    var checkedSize: Int64 { checkedFindings.reduce(0) { $0 + $1.size } }
    var plan: [PlannedAction] { checkedFindings.map { PlannedAction(finding: $0, action: action(for: $0)) } }

    // MARK: - Scanning

    func scan() {
        guard !isScanning else { return }
        isScanning = true
        scanStatus = "Starting"
        hasFullDiskAccess = FullDiskAccess.isGranted
        let ignored = ignores.ids
        let roots = AppSettings.projectFolders
        Task {
            let (rules, errors) = RuleLoader.loadAll()
            let context = await Task.detached { ScanContext(projectRoots: roots) }.value
            let result = await Scanner(rules: rules, context: context).scan(ignored: ignored) { [self] status in
                Task { @MainActor in self.scanStatus = status }
            }
            findings = result.findings
            disk = result.disk ?? DiskInfo.current()
            ruleErrors = errors
            lastScan = result.date
            changes = snapshots.changes(for: findings)
            snapshots.record(findings, diskFree: disk?.free)
            let ids = Set(findings.map(\.id))
            checked = checked.intersection(ids)
            if let i = inspectedID, !ids.contains(i) { inspectedID = nil }
            isScanning = false
            #if DEBUG
            if let dir = ProcessInfo.processInfo.environment["DEVSWEEP_SHOTS"] {
                await ScreenshotTour.run(model: self, to: URL(fileURLWithPath: dir))
            }
            #endif
        }
    }

    // MARK: - Runtimes and versions

    func checkVersions() {
        guard !isCheckingVersions else { return }
        isCheckingVersions = true
        versionsStatus = "Starting"
        let report: @Sendable (String) -> Void = { [self] s in Task { @MainActor in self.versionsStatus = s } }
        Task {
            let result = await Task.detached { await RuntimeScanner().scan(progress: report) }.value
            versions = result
            if let id = selectedRuntimeID, !allRuntimeIDs.contains(id) { selectedRuntimeID = nil }
            isCheckingVersions = false
        }
    }

    var allRuntimeIDs: [String] {
        guard let v = versions else { return [] }
        return v.runtimes.map(\.id) + (v.homebrew != nil ? ["homebrew"] : []) + (v.macOS != nil ? ["macos"] : [])
    }

    func toggleRuntime(_ id: String) {
        selectedRuntimeID = selectedRuntimeID == id ? nil : id
    }

    func runInTerminal(_ step: RuntimeStep) {
        do { try TerminalRunner.run(step) } catch { errorMessage = error.localizedDescription }
    }

    /// Critical and warning issues across runtimes, for Home.
    var versionAlerts: [(id: String, issue: RuntimeIssue)] {
        guard let v = versions else { return [] }
        var out: [(String, RuntimeIssue)] = []
        for r in v.runtimes + [v.macOS].compactMap({ $0 }) {
            out += r.issues.filter { $0.level != .info }.map { (r.id, $0) }
        }
        if let b = v.homebrew { out += b.issues.filter { $0.level != .info }.map { ("homebrew", $0) } }
        return out.sorted { ($0.1.level == .critical ? 0 : 1) < ($1.1.level == .critical ? 0 : 1) }
    }

    /// Dismisses the first-launch screen and starts the first scan.
    func finishWelcome() {
        AppSettings.welcomeDone = true
        showWelcome = false
        if lastScan == nil { scan() }
        if versions == nil { checkVersions() }
    }

    func runAdminCommands(for f: Finding) {
        guard let a = f.defaultAction, let commands = a.command else { return }
        runInTerminal(RuntimeStep(kind: .remove, title: f.title, detail: a.displayDetail, commands: commands))
    }

    // MARK: - Cleaning

    func clean() {
        let plan = self.plan
        guard !plan.isEmpty, !isCleaning else { return }
        isCleaning = true
        outcomes = nil
        let history = self.history
        let report: @Sendable (String) -> Void = { [self] title in
            Task { @MainActor in self.cleanStatus = title }
        }
        Task {
            let results = await Task.detached {
                Cleaner(history: history).run(plan, progress: report)
            }.value
            outcomes = results
            let done = Set(results.filter(\.succeeded).map(\.finding.id))
            findings.removeAll { done.contains($0.id) }
            checked.subtract(done)
            if let i = inspectedID, done.contains(i) { inspectedID = nil }
            historyEntries = history.entries
            disk = DiskInfo.current()
            isCleaning = false
        }
    }

    func restore(_ entry: HistoryEntry) {
        do {
            try history.restore(entry.id)
        } catch {
            errorMessage = error.localizedDescription
        }
        historyEntries = history.entries
        disk = DiskInfo.current()
    }

    // MARK: - Ignoring

    func ignore(_ f: Finding) {
        ignores.ignore(f.id)
        ignoredIDs = ignores.ids
        findings.removeAll { $0.id == f.id }
        checked.remove(f.id)
        if inspectedID == f.id { inspectedID = nil }
    }

    func unignore(_ id: String) {
        ignores.unignore(id)
        ignoredIDs = ignores.ids
    }

    // MARK: - Apps that block cleaning

    func quit(_ appName: String) {
        for app in NSWorkspace.shared.runningApplications where app.localizedName == appName {
            app.terminate()
        }
    }

    func reveal(_ path: String) {
        NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: path)])
    }
}

enum FullDiskAccess {
    /// The TCC database can only be opened with Full Disk Access.
    static var isGranted: Bool {
        let path = NSHomeDirectory() + "/Library/Application Support/com.apple.TCC/TCC.db"
        guard let handle = FileHandle(forReadingAtPath: path) else { return false }
        try? handle.close()
        return true
    }

    static func openSettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles") {
            NSWorkspace.shared.open(url)
        }
    }
}
