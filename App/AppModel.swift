import AppKit
import DevSweepCore
import Observation
import SwiftUI

/// The four places in the app. Settings is its own window.
enum SidebarItem: Hashable {
    case home
    case cleanUp
    case projects
    case health
    case history
}

enum ProjectFilter: String, CaseIterable, Identifiable {
    case onMac, idle, onGitHub
    var id: String { rawValue }
    var title: String {
        switch self {
        case .onMac: return "On this Mac"
        case .idle: return "Not opened lately"
        case .onGitHub: return "Only on GitHub"
        }
    }
}

enum ProjectSheet: Identifiable {
    case clone(String)
    case publish
    case addByURL
    var id: String {
        switch self {
        case .clone(let id): return "clone:" + id
        case .publish: return "publish"
        case .addByURL: return "add"
        }
    }
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
    /// Set for alerts about a project; they open Projects instead.
    var projectID: String? = nil
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

    var projects: [Project] = []
    var githubAccounts: [GitHubAccount] = []
    var isLoadingProjects = false
    var hasLoadedProjects = false
    var projectsMessage: String?
    var projectFilter: ProjectFilter = .onMac
    var accountFilter: String?
    var selectedProjectID: String?
    var projectSheet: ProjectSheet?
    var busyProjectID: String?
    var projectsState = ProjectsState()

    var changes: ScanChanges?
    var showWelcome = !AppSettings.welcomeDone && ProcessInfo.processInfo.environment["DEVSWEEP_SHOTS"] == nil

    var historyEntries: [HistoryEntry] = []
    var ignoredIDs: Set<String> = []
    var errorMessage: String?
    var hasFullDiskAccess = FullDiskAccess.isGranted

    @ObservationIgnored let history = HistoryStore()
    @ObservationIgnored let ignores = IgnoreStore()
    @ObservationIgnored let snapshots = ScanSnapshotStore()
    @ObservationIgnored let projectStore = ProjectStore()

    init() {
        historyEntries = history.entries
        ignoredIDs = ignores.ids
        projectsState = projectStore.state
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

    /// Debug screenshots use sample data; real scans must not replace it.
    @ObservationIgnored var useSampleData = false

    func checkSecurity() {
        guard !isCheckingSecurity, !useSampleData else { return }
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
        guard !isLoadingPorts, !useSampleData else { return }
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
        for p in projectsNeedingPush {
            let n = p.safety?.unpushedCommits ?? 0
            out.append(HealthAlert(tab: .tools, target: p.id, critical: false,
                                   text: "\(p.name) has \(n) commit\(n == 1 ? "" : "s") that aren't on GitHub", projectID: p.id))
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
        if let id = alert.projectID {
            selection = .projects
            projectFilter = .onMac
            selectedProjectID = id
            return
        }
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

    // MARK: - Projects

    /// Folders searched for clones: your project folders plus where downloads land.
    var projectScanRoots: [URL] {
        let home = FileManager.default.homeDirectoryForCurrentUser
        let paths = (AppSettings.projectFolders ?? RuleLoader.defaultProjectRoots) + [projectsState.workspaceRoot]
        return FS_uniqueDirectories(paths, home: home)
    }

    func refreshProjects() {
        guard !isLoadingProjects, !useSampleData else { return }
        isLoadingProjects = true
        let roots = projectScanRoots
        let store = projectStore
        Task {
            let result = await Task.detached { () -> ([Project], [GitHubAccount], String?) in
                var message: String?
                var remote: [RemoteRepo] = []
                var accounts: [GitHubAccount] = []
                do {
                    accounts = try GitHubClient.accounts()
                    for a in accounts {
                        do { remote += try GitHubClient.repos(for: a.login, token: try GitHubClient.token(for: a.login)) }
                        catch { message = "Couldn't list \(a.login)'s repositories: \(error.localizedDescription)" }
                    }
                } catch { message = error.localizedDescription }
                let local = ProjectScanner.scanLocal(roots: roots)
                // Remember where each clone lives, so a removed one comes back to the same folder.
                store.update { s in
                    for c in local {
                        guard let slug = c.slug else { continue }
                        let account = remote.first { $0.nameWithOwner.lowercased() == slug.lowercased() }?.account
                            ?? s.known.first { $0.nameWithOwner.lowercased() == slug.lowercased() }?.account
                            ?? String(slug.split(separator: "/")[0])
                        let parent = FS_abbreviate(c.path.deletingLastPathComponent().path)
                        if let i = s.known.firstIndex(where: { $0.nameWithOwner.lowercased() == slug.lowercased() }) {
                            s.known[i].lastParent = parent
                        } else {
                            s.known.append(.init(nameWithOwner: slug, account: account, lastParent: parent))
                        }
                    }
                }
                let merged = ProjectScanner.merge(local: local, remote: remote, state: store.state,
                                                  home: FileManager.default.homeDirectoryForCurrentUser)
                return (merged, accounts, message)
            }.value
            // Screenshots use sample data; a real scan finishing late must not replace it.
            guard !useSampleData else { isLoadingProjects = false; return }
            projects = result.0
            githubAccounts = result.1
            projectsMessage = result.2
            projectsState = projectStore.state
            hasLoadedProjects = true
            isLoadingProjects = false
            if let id = selectedProjectID, !projects.contains(where: { $0.id == id }) { selectedProjectID = nil }
        }
    }

    var selectedProject: Project? { projects.first { $0.id == selectedProjectID } }

    func toggleProject(_ p: Project) { selectedProjectID = selectedProjectID == p.id ? nil : p.id }

    /// Projects with commits that exist only on this Mac.
    var projectsNeedingPush: [Project] {
        projects.filter { $0.onDisk && ($0.safety?.hasRemote ?? false) && ($0.safety?.unpushedCommits ?? 0) > 0 }
    }

    var visibleProjects: [Project] {
        let idleBefore = Date().addingTimeInterval(-21 * 86_400)
        return projects.filter { p in
            if let a = accountFilter, (p.account ?? p.owner) != a { return false }
            switch projectFilter {
            case .onMac: return p.onDisk
            case .idle: return p.onDisk && (p.lastUsed ?? .distantPast) < idleBefore
            case .onGitHub: return !p.onDisk
            }
        }
    }

    func openInEditor(_ p: Project) {
        guard let path = p.localPath else { return }
        projectStore.update { $0.lastOpened[p.nameWithOwner] = Date() }
        projectsState = projectStore.state
        if let i = projects.firstIndex(where: { $0.id == p.id }) { projects[i].lastOpened = Date() }
        if Shell.locate("code") != nil {
            Shell.run(["code", path], timeout: 20)
        } else {
            NSWorkspace.shared.open([URL(fileURLWithPath: path)], withApplicationAt: URL(fileURLWithPath: "/Applications/Visual Studio Code.app"),
                                    configuration: NSWorkspace.OpenConfiguration())
        }
    }

    func openOnGitHub(_ p: Project) {
        if let url = URL(string: "https://github.com/\(p.nameWithOwner)") { NSWorkspace.shared.open(url) }
    }

    /// Pushing uses your normal git setup, so it runs where you can see it.
    func push(_ p: Project) {
        guard let path = p.localPath else { return }
        runInTerminal(RuntimeStep(kind: .upgrade, title: "Push \(p.name)",
                                  detail: "Pushes the current branch to GitHub. Other branches aren't pushed.",
                                  commands: ["cd \(shellQuote(path))", "git push || git push -u origin HEAD"]))
    }

    /// Moves the folder to the Trash (restorable from History). Only when nothing would be lost.
    func removeFromMac(_ p: Project) {
        guard let path = p.localPath, p.safety?.isSafeToRemove == true else { return }
        let finding = Finding(id: "project:" + p.id, ruleID: "projects", title: p.nameWithOwner, subtitle: path, category: .projects,
                              risk: .holdsData, paths: [path], size: p.localSize,
                              explanation: Explanation(what: "A project folder.", why: "", ifDeleted: "Download it again from GitHub.",
                                                       undo: "Download it again from Projects, or restore it from History."),
                              checks: [], actions: [CleanAction(kind: .trash)], blockingApps: [], blockers: [])
        busyProjectID = p.id
        let history = self.history
        Task {
            let results = await Task.detached { Cleaner(history: history).run([PlannedAction(finding: finding, action: CleanAction(kind: .trash))]) }.value
            if let r = results.first, !r.succeeded { errorMessage = r.message }
            historyEntries = history.entries
            disk = DiskInfo.current()
            busyProjectID = nil
            selectedProjectID = nil
            refreshProjects()
        }
    }

    /// Removes every project that's fully backed up and idle.
    var safeIdleProjects: [Project] { visibleProjects.filter { $0.onDisk && $0.safety?.isSafeToRemove == true } }

    func removeAllSafeIdle() {
        let list = safeIdleProjects
        guard !list.isEmpty else { return }
        let findings = list.map { p in
            Finding(id: "project:" + p.id, ruleID: "projects", title: p.nameWithOwner, subtitle: p.localPath ?? "", category: .projects,
                    risk: .holdsData, paths: [p.localPath ?? ""], size: p.localSize,
                    explanation: Explanation(what: "A project folder.", why: "", ifDeleted: "Download it again from GitHub."),
                    checks: [], actions: [CleanAction(kind: .trash)], blockingApps: [], blockers: [])
        }
        let history = self.history
        busyProjectID = "all"
        Task {
            let results = await Task.detached {
                Cleaner(history: history).run(findings.map { PlannedAction(finding: $0, action: CleanAction(kind: .trash)) })
            }.value
            if let bad = results.first(where: { !$0.succeeded }) { errorMessage = bad.message }
            historyEntries = history.entries
            disk = DiskInfo.current()
            busyProjectID = nil
            refreshProjects()
        }
    }

    func identity(for account: String) -> GitIdentity? { projectsState.identities[account] }

    func setIdentity(_ identity: GitIdentity, for account: String) {
        projectStore.update { $0.identities[account] = identity }
        projectsState = projectStore.state
    }

    func setWorkspaceRoot(_ path: String) {
        projectStore.update { $0.workspaceRoot = path }
        projectsState = projectStore.state
    }

    /// Records a clone or publish in History, so it shows up next to cleanups.
    private func logActivity(_ title: String, _ label: String, size: Int64 = 0) {
        history.append(HistoryEntry(title: title, actionKind: .manual, actionLabel: label, size: size))
        historyEntries = history.entries
    }

    func download(_ p: Project, strategy: CloneStrategy, to destination: URL, completion: @escaping (String?) -> Void) {
        guard let account = p.account ?? githubAccounts.first(where: { $0.isActive })?.login else {
            completion("Sign in to GitHub first: run gh auth login in Terminal."); return
        }
        let identity = projectsState.identities[account]
        busyProjectID = p.id
        Task {
            let error: String? = await Task.detached {
                do {
                    let token = try GitHubClient.token(for: account)
                    try GitHubClient.clone(slug: p.nameWithOwner, into: destination, strategy: strategy, token: token, identity: identity)
                    return nil
                } catch { return error.localizedDescription }
            }.value
            busyProjectID = nil
            if error == nil {
                projectStore.update { s in
                    s.strategies[p.nameWithOwner] = strategy
                    s.lastOpened[p.nameWithOwner] = Date()
                }
                projectsState = projectStore.state
                logActivity("Downloaded \(p.nameWithOwner)", "Downloaded (\(strategy.title.lowercased()))", size: FS_size(destination.path))
                refreshProjects()
            }
            completion(error)
        }
    }

    func publish(folder: URL, name: String, account: String, description: String, isPrivate: Bool, completion: @escaping (String?) -> Void) {
        let identity = projectsState.identities[account]
        busyProjectID = "publish"
        Task {
            let error: String? = await Task.detached {
                do {
                    let token = try GitHubClient.token(for: account)
                    try GitHubClient.publish(folder: folder, owner: account, name: name, description: description,
                                             isPrivate: isPrivate, token: token, identity: identity)
                    return nil
                } catch { return error.localizedDescription }
            }.value
            busyProjectID = nil
            if error == nil {
                logActivity("Published \(account)/\(name)", isPrivate ? "Published (private)" : "Published (public)")
                refreshProjects()
            }
            completion(error)
        }
    }

    /// Adds a repo by URL, for ones outside your own lists (forks, other orgs).
    func addByURL(_ text: String, completion: @escaping (String?) -> Void) {
        guard let slug = GitHubClient.parseSlug(text) else {
            completion("That doesn't look like a GitHub repo. Use owner/name or a github.com URL."); return
        }
        let accounts = githubAccounts
        Task {
            let found: (RemoteRepo?, String?) = await Task.detached {
                var lastError = "Sign in to GitHub first."
                for a in accounts {
                    do {
                        let r = try GitHubClient.repoView(slug: slug, account: a.login, token: try GitHubClient.token(for: a.login))
                        return (r, nil)
                    } catch { lastError = error.localizedDescription }
                }
                return (nil, lastError)
            }.value
            guard let repo = found.0 else { completion("Couldn't find \(slug) with your signed-in accounts. \(found.1 ?? "")"); return }
            projectStore.update { s in
                if !s.known.contains(where: { $0.nameWithOwner.lowercased() == repo.nameWithOwner.lowercased() }) {
                    s.known.append(.init(nameWithOwner: repo.nameWithOwner, account: repo.account, isPrivate: repo.isPrivate, remoteKB: repo.diskUsageKB))
                }
            }
            projectsState = projectStore.state
            projectFilter = .onGitHub
            refreshProjects()
            completion(nil)
        }
    }

    /// Dismisses the first-launch screen and starts the first scan.
    func finishWelcome() {
        AppSettings.welcomeDone = true
        showWelcome = false
        if lastScan == nil { scan() }
        if versions == nil { checkVersions() }
        checkSecurity()
        refreshProjects()
    }

    /// The toolbar's Scan again: everything, read-only.
    func scanEverything() {
        refreshProjects()
        scan()
        checkVersions()
        checkSecurity()
        refreshPorts()
    }

    /// Manual actions: opening an app happens directly; anything else runs
    /// in Terminal, where the user sees it and sudo can ask for a password.
    func runManualAction(for f: Finding) {
        guard let a = f.defaultAction, let commands = a.command else { return }
        if commands.allSatisfy({ $0.hasPrefix("open ") }) {
            for c in commands { Shell.run(["/bin/sh", "-c", c]) }
        } else {
            runInTerminal(RuntimeStep(kind: .remove, title: f.title, detail: a.displayDetail, commands: commands))
        }
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

    /// Asks an app to quit, matching its display name or its bundle name
    /// (VS Code is shown as "Code" but lives in "Visual Studio Code.app").
    /// Names that aren't apps, like a Gradle daemon, are stopped as processes.
    /// Then it re-checks, so the warning goes away once the app has quit.
    func quit(_ name: String) {
        let apps = NSWorkspace.shared.runningApplications.filter {
            $0.localizedName == name || $0.bundleURL?.deletingPathExtension().lastPathComponent == name
        }
        if apps.isEmpty {
            Shell.run(["pkill", "-f", name], timeout: 10)
        } else {
            apps.forEach { $0.terminate() }
        }
        Task {
            try? await Task.sleep(for: .seconds(2))
            if ScanContext(home: FileManager.default.homeDirectoryForCurrentUser).running([name]).isEmpty {
                scan()
            } else {
                errorMessage = "\(name) didn't quit. It may be asking you to save something. Quit it yourself, then scan again."
            }
        }
    }

    func reveal(_ path: String) {
        NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: path)])
    }
}

func shellQuote(_ s: String) -> String { "'" + s.replacingOccurrences(of: "'", with: "'\\''") + "'" }

func FS_uniqueDirectories(_ paths: [String], home: URL) -> [URL] {
    var seen = Set<String>()
    var out: [URL] = []
    for p in paths {
        let url = p.hasPrefix("~/") ? home.appendingPathComponent(String(p.dropFirst(2))) : URL(fileURLWithPath: p)
        var isDir: ObjCBool = false
        guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir), isDir.boolValue else { continue }
        let key = (try? url.resourceValues(forKeys: [.canonicalPathKey]))?.canonicalPath ?? url.path
        if seen.insert(key.lowercased()).inserted { out.append(url) }
    }
    return out
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
