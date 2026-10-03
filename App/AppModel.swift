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

enum HealthTab: String, CaseIterable, Identifiable {
    case security, tools, ports
    var id: String { rawValue }
    var title: String {
        switch self {
        case .security: return "Security"
        case .tools: return "Tools and versions"
        case .ports: return "Ports"
        }
    }
}

/// Something worth surfacing on Home and in the menu bar.
struct HealthAlert: Identifiable {
    var id: String { tab.rawValue + ":" + target }
    var tab: HealthTab
    /// Item to open in that tab.
    var target: String
    var critical: Bool
    var text: String
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

    var healthTab: HealthTab = .security
    var security: [SecurityFinding] = []
    var isCheckingSecurity = false
    var selectedSecurityID: String?
    var ports: [ListeningPort] = []
    var isLoadingPorts = false
    var selectedPortID: String?

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
        let roots = AppSettings.projectFolders
        Task {
            let result = await Task.detached { await RuntimeScanner().scan(projectRoots: roots, progress: report) }.value
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

    // MARK: - Security and ports

    func checkSecurity() {
        guard !isCheckingSecurity else { return }
        isCheckingSecurity = true
        let roots = AppSettings.projectFolders
        Task {
            security = await Task.detached { SecurityScanner.scan(projectRoots: roots) }.value
            if let id = selectedSecurityID, !security.contains(where: { $0.id == id }) { selectedSecurityID = nil }
            isCheckingSecurity = false
        }
    }

    /// Security findings minus the ones you've marked as handled.
    var visibleSecurity: [SecurityFinding] { security.filter { !ignoredIDs.contains($0.id) } }

    func refreshPorts() {
        guard !isLoadingPorts else { return }
        isLoadingPorts = true
        let installs = versions?.runtimes.flatMap(\.installs) ?? []
        Task {
            ports = await Task.detached { PortScanner.scan(installs: installs) }.value
            if let id = selectedPortID, !ports.contains(where: { $0.id == id }) { selectedPortID = nil }
            isLoadingPorts = false
        }
    }

    func stop(_ port: ListeningPort) {
        if !PortScanner.stop(port.pid) { errorMessage = "Couldn't stop \(port.label). It may belong to another user." }
        selectedPortID = nil
        Task {
            try? await Task.sleep(for: .milliseconds(700))
            refreshPorts()
        }
    }

    /// Moves a secret file to the Trash through the cleaner, so it shows in History.
    func trash(_ f: SecurityFinding) {
        let finding = Finding(
            id: f.id, ruleID: "security", title: f.title, subtitle: f.path, category: .leftovers, risk: .holdsData,
            paths: [f.path], size: FS_size(f.path), explanation: Explanation(what: f.why, why: f.why, ifDeleted: f.whatToDo),
            checks: [], actions: [CleanAction(kind: .trash)], blockingApps: [], blockers: [])
        let result = Cleaner(history: history).run([PlannedAction(finding: finding, action: CleanAction(kind: .trash))])
        if let r = result.first, !r.succeeded { errorMessage = r.message }
        historyEntries = history.entries
        selectedSecurityID = nil
        checkSecurity()
    }

    func markHandled(_ f: SecurityFinding) {
        ignores.ignore(f.id)
        ignoredIDs = ignores.ids
        selectedSecurityID = nil
    }

    func runFix(_ f: SecurityFinding) {
        guard let fix = f.fix else { return }
        runInTerminal(RuntimeStep(kind: .remove, title: f.title, detail: f.whatToDo, commands: fix))
    }

    /// Everything that needs attention in Health, most serious first.
    var healthAlerts: [HealthAlert] {
        var out: [HealthAlert] = []
        // Files of the same kind in the same folder become one alert.
        let problems = visibleSecurity.filter { $0.level != .ok }
        let groups = Dictionary(grouping: problems) { f in
            f.title + "|" + URL(fileURLWithPath: f.path).deletingLastPathComponent().path
        }
        for f in problems where groups[f.title + "|" + URL(fileURLWithPath: f.path).deletingLastPathComponent().path]?.first?.id == f.id {
            let same = groups[f.title + "|" + URL(fileURLWithPath: f.path).deletingLastPathComponent().path] ?? [f]
            let folder = URL(fileURLWithPath: f.path).deletingLastPathComponent().lastPathComponent
            let text: String
            if f.kind == .envFile { text = f.title }
            else if same.count > 1 { text = "\(same.count) \(f.title.replacingOccurrences(of: "codes", with: "code")) files in \(folder), stored as plain text" }
            else { text = "\(f.title) in \(folder), stored as plain text" }
            out.append(HealthAlert(tab: .security, target: f.id, critical: f.level == .critical, text: text))
        }
        for a in versionAlerts {
            out.append(HealthAlert(tab: .tools, target: a.id, critical: a.issue.level == .critical, text: a.issue.text))
        }
        for p in versions?.projects ?? [] where p.status != .ok {
            out.append(HealthAlert(tab: .tools, target: "projects", critical: false, text: p.message))
        }
        return out.enumerated().sorted { ($0.element.critical ? 0 : 1, $0.offset) < ($1.element.critical ? 0 : 1, $1.offset) }.map(\.element)
    }

    var healthAttentionCount: Int {
        (versions?.attentionCount ?? 0) + visibleSecurity.filter { $0.level != .ok }.count
    }

    func open(_ alert: HealthAlert) {
        selection = .health
        healthTab = alert.tab
        switch alert.tab {
        case .security: selectedSecurityID = alert.target
        case .tools: selectedRuntimeID = alert.target
        case .ports: selectedPortID = alert.target
        }
    }

    /// Critical and warning issues across runtimes.
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
        checkSecurity()
    }

    /// The toolbar's Scan again: everything, read-only.
    func scanEverything() {
        scan()
        checkVersions()
        checkSecurity()
        refreshPorts()
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

func FS_size(_ path: String) -> Int64 {
    let values = try? URL(fileURLWithPath: path).resourceValues(forKeys: [.totalFileAllocatedSizeKey])
    return Int64(values?.totalFileAllocatedSize ?? 0)
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
