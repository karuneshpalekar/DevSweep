import AppKit
import Foundation

/// What a detector found, before sizing and templating.
struct Candidate: Sendable {
    var key: String
    var paths: [URL]
    var title: String? = nil
    var subtitle: String? = nil
    var size: Int64? = nil
    var facts: [String: String] = [:]
    var checks: [Check] = []
    var actions: [CleanAction]? = nil
    var risk: Risk? = nil
}

/// Facts about the machine that several detectors need, gathered once.
public final class ScanContext: @unchecked Sendable {
    public let home: URL
    let arch: String
    let installedBundleIDs: Set<String>
    let runningAppNames: Set<String>
    /// File names of running apps' bundles, e.g. "visual studio code" for an
    /// app whose display name is just "Code".
    let runningBundleNames: Set<String>
    let processArgs: [String]
    /// Project folders chosen in Settings; nil means the rule's defaults.
    let projectRoots: [String]?

    public init(home: URL = FileManager.default.homeDirectoryForCurrentUser, projectRoots: [String]? = nil) {
        self.home = home
        self.projectRoots = projectRoots
        #if arch(arm64)
        arch = "arm64"
        #else
        arch = "x86_64"
        #endif
        let running = NSWorkspace.shared.runningApplications
        runningAppNames = Set(running.compactMap { $0.localizedName?.lowercased() })
        runningBundleNames = Set(running.compactMap { $0.bundleURL?.deletingPathExtension().lastPathComponent.lowercased() })
        installedBundleIDs = Self.findInstalledApps(home: home)
            .union(running.compactMap { $0.bundleIdentifier?.lowercased() })
        processArgs = Shell.run(["ps", "-axo", "args="], timeout: 10).stdout
            .split(separator: "\n").map(String.init)
    }

    /// Running apps or processes matching the given blocker names.
    public func running(_ blockers: [String]) -> [String] {
        blockers.filter {
            Self.isRunning($0, appNames: runningAppNames, bundleNames: runningBundleNames, processArgs: processArgs)
        }
    }

    /// An app counts as running when its display name or bundle name matches.
    /// Command lines are only searched for process-style names with no spaces
    /// ("GradleDaemon"): a short word like "Code" would match any path with
    /// a Code folder in it.
    static func isRunning(_ blocker: String, appNames: Set<String>, bundleNames: Set<String>,
                          processArgs: [String]) -> Bool {
        let b = blocker.lowercased()
        if appNames.contains(b) || bundleNames.contains(b) { return true }
        guard !blocker.contains(" "), blocker.count >= 8 else { return false }
        return processArgs.contains { $0.contains(blocker) }
    }

    func isInstalled(_ bundleID: String) -> Bool {
        let id = bundleID.lowercased()
        if installedBundleIDs.contains(id) { return true }
        for installed in installedBundleIDs {
            if id.hasPrefix(installed + ".") || installed.hasPrefix(id + ".") { return true }
        }
        return NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) != nil
    }

    private static func findInstalledApps(home: URL) -> Set<String> {
        let roots = ["/Applications", "/System/Applications", "/System/Library/CoreServices",
                     home.appendingPathComponent("Applications").path]
        var ids = Set<String>()
        func visit(_ dir: URL, depth: Int) {
            for child in FS.children(dir) {
                if child.pathExtension == "app" {
                    if let id = Bundle(url: child)?.bundleIdentifier { ids.insert(id.lowercased()) }
                } else if depth < 3, FS.isDirectory(child) {
                    visit(child, depth: depth + 1)
                }
            }
        }
        for root in roots { visit(URL(fileURLWithPath: root), depth: 0) }
        return ids
    }
}

public final class Scanner {
    public let rules: [Rule]
    public let context: ScanContext
    /// A check that runs longer than this is skipped, so one slow folder
    /// can't hold up the whole scan.
    public var checkTimeout: Double = 90
    /// Same for measuring one item's size.
    public var measureTimeout: Double = 60
    /// The detection step, replaceable in tests.
    var detect: @Sendable (Rule, ScanContext) -> [Candidate] = { Detectors.run($0, $1) }

    public init(rules: [Rule], context: ScanContext = ScanContext()) {
        self.rules = rules
        self.context = context
    }

    /// Folders the checks look in that macOS protects, plus the project folders.
    static func watchedFolders(_ ctx: ScanContext) -> [String] {
        // Only project folders that exist; the defaults are guesses at common names.
        let projects = (ctx.projectRoots ?? RuleLoader.defaultProjectRoots).filter { FS.folderState(FS.expand($0, home: ctx.home)) != .missing }
        return ["~/Downloads", "~/Desktop", "~/Documents", "~/Movies", "~/Library/Containers"] + projects
    }

    public func scan(ignored: Set<String> = [], progress: (@Sendable (ScanProgress) -> Void)? = nil) async -> ScanResult {
        let ctx = context
        let started = Date()
        let active = rules.filter { $0.arch == nil || $0.arch == ctx.arch }
        let detect = self.detect, checkTimeout = self.checkTimeout, measureTimeout = self.measureTimeout

        // 1. Detect, all rules in parallel, each with a time limit.
        final class Tracker: @unchecked Sendable {
            let lock = NSLock()
            var running: [(Int, String, Date)] = []
            var done = 0
            func start(_ i: Int, _ title: String) { lock.lock(); running.append((i, title, Date())); lock.unlock() }
            func finish(_ i: Int) { lock.lock(); running.removeAll { $0.0 == i }; done += 1; lock.unlock() }
            func snapshot(total: Int) -> ScanProgress {
                lock.lock(); defer { lock.unlock() }
                let r = running.sorted { $0.2 < $1.2 }
                return ScanProgress(phase: .checking, done: done, total: total, running: r.map(\.1), oldestStart: r.first?.2)
            }
        }
        let tracker = Tracker()
        var detected = [[Candidate]](repeating: [], count: active.count)
        var checks = [ScanReport.Check?](repeating: nil, count: active.count)
        progress?(ScanProgress(phase: .checking, done: 0, total: active.count))
        await withTaskGroup(of: (Int, [Candidate]?, Double).self) { group in
            for (i, rule) in active.enumerated() {
                group.addTask {
                    tracker.start(i, rule.displayTitle)
                    progress?(tracker.snapshot(total: active.count))
                    let t0 = Date()
                    let found = await Deadline.run(seconds: checkTimeout) { detect(rule, ctx) }
                    tracker.finish(i)
                    progress?(tracker.snapshot(total: active.count))
                    return (i, found, Date().timeIntervalSince(t0))
                }
            }
            for await (i, found, secs) in group {
                detected[i] = found ?? []
                let r = active[i]
                checks[i] = ScanReport.Check(id: r.id, title: r.displayTitle, category: r.category.title, seconds: secs,
                                             found: 0, outcome: found == nil ? .timedOut : .nothingFound)
            }
        }

        // 2. A path belongs to the first rule that claims it, so an item is
        //    never listed (or counted) twice.
        var claimed = Set<String>()
        var work: [(Int, Rule, Candidate)] = []
        for (i, rule) in active.enumerated() {
            for var c in detected[i] {
                let hadPaths = !c.paths.isEmpty
                c.paths.removeAll { claimed.contains($0.path) }
                if hadPaths && c.paths.isEmpty { continue }
                c.paths.forEach { claimed.insert($0.path) }
                work.append((i, rule, c))
            }
        }

        // 3. Size everything in parallel. An item that takes longer than the
        //    limit keeps the size counted so far, marked "at least".
        let measureStart = Date()
        final class Measuring: @unchecked Sendable {
            let lock = NSLock(); var done = 0; var running: [(Int, String, Date)] = []
            func start(_ i: Int, _ t: String) { lock.lock(); running.append((i, t, Date())); lock.unlock() }
            func finish(_ i: Int) -> (Int, [String], Date?) {
                lock.lock(); defer { lock.unlock() }
                running.removeAll { $0.0 == i }; done += 1
                let r = running.sorted { $0.2 < $1.2 }
                return (done, r.map(\.1), r.first?.2)
            }
            func snapshot() -> (Int, [String], Date?) {
                lock.lock(); defer { lock.unlock() }
                let r = running.sorted { $0.2 < $1.2 }
                return (done, r.map(\.1), r.first?.2)
            }
        }
        let meter = Measuring()
        progress?(ScanProgress(phase: .measuring, done: 0, total: work.count, oldestStart: measureStart))
        struct Measured: Sendable { var bytes: Int64; var files: Int; var complete: Bool; var seconds: Double }
        var measured = [Measured](repeating: Measured(bytes: 0, files: 0, complete: true, seconds: 0), count: work.count)
        let items = work
        await withTaskGroup(of: (Int, Measured).self) { group in
            for (i, item) in items.enumerated() {
                group.addTask {
                    let t0 = Date()
                    if let s = item.2.size { return (i, Measured(bytes: s, files: 0, complete: true, seconds: 0)) }
                    meter.start(i, item.2.title ?? item.1.displayTitle)
                    let snap = meter.snapshot()
                    progress?(ScanProgress(phase: .measuring, done: snap.0, total: items.count, running: snap.1, oldestStart: snap.2))
                    let r = await Task.detached { FS.measure(item.2.paths, until: t0.addingTimeInterval(measureTimeout)) }.value
                    let after = meter.finish(i)
                    progress?(ScanProgress(phase: .measuring, done: after.0, total: items.count, running: after.1, oldestStart: after.2))
                    return (i, Measured(bytes: r.bytes, files: r.files, complete: r.complete, seconds: Date().timeIntervalSince(t0)))
                }
            }
            for await (i, m) in group { measured[i] = m }
        }
        for (i, item) in work.enumerated() { checks[item.0]?.measureSeconds += measured[i].seconds }

        // 4. Build findings.
        var findings: [Finding] = []
        var unmeasured = 0
        for (i, (ruleIndex, rule, c0)) in work.enumerated() {
            var c = c0
            let size = measured[i].bytes
            if !measured[i].complete {
                unmeasured += 1
                c.checks.append(.warning("At least this size. Measuring stopped after \(Int(measureTimeout)) s with \(measured[i].files.formatted()) files counted, so the real size is larger."))
            }
            let minBytes = Int64(rule.minSizeMB ?? 1) * 1_000_000
            if size < minBytes { continue }
            let id = "\(rule.id):\(c.key)"
            if ignored.contains(id) { continue }
            findings.append(makeFinding(rule: rule, candidate: c, size: size, id: id))
            if checks[ruleIndex]?.outcome != .timedOut {
                checks[ruleIndex]?.found += 1
                checks[ruleIndex]?.outcome = .found
            }
        }
        findings.sort { ($0.risk, -$0.size) < ($1.risk, -$1.size) }

        let folders = Self.watchedFolders(ctx).map { p in
            ScanReport.Folder(path: p, state: FS.folderState(FS.expand(p, home: ctx.home)))
        }
        let report = ScanReport(date: Date(), totalSeconds: Date().timeIntervalSince(started),
                                measuringSeconds: Date().timeIntervalSince(measureStart),
                                checks: checks.compactMap { $0 }, unmeasured: unmeasured,
                                folders: Array(Set(folders)).sorted { $0.path < $1.path }, checkTimeout: checkTimeout)
        return ScanResult(findings: findings, errors: [], date: Date(), disk: DiskInfo.current(for: ctx.home), report: report)
    }

    private func makeFinding(rule: Rule, candidate c: Candidate, size: Int64, id: String) -> Finding {
        let paths = c.paths.map { $0.path }
        var facts = c.facts
        facts["size"] = SizeFormat.string(size)
        facts["count"] = String(paths.count)
        if let first = paths.first { facts["path"] = FS.abbreviate(first, home: context.home) }

        let blockers = rule.blockers ?? []
        let blocking = context.running(blockers)
        var checks = c.checks
        if !blocking.isEmpty {
            checks.append(.warning("\(ListFormat.join(blocking)) is running. It has to quit first."))
        } else if !blockers.isEmpty {
            checks.append(.passed("\(ListFormat.join(blockers)) is not running"))
        }

        let title = c.title ?? Template.render(rule.title, facts)
        facts["title"] = title
        var subtitle = c.subtitle ?? ""
        if subtitle.isEmpty, let first = facts["path"] {
            subtitle = paths.count > 1 ? "\(first) and \(paths.count - 1) more" : first
        }
        let actions = (c.actions ?? rule.actions).map { a -> CleanAction in
            var a = a
            a.command = a.command?.map { Template.render($0, facts) }
            a.detail = a.detail.map { Template.render($0, facts) }
            return a
        }

        return Finding(
            id: id, ruleID: rule.id, title: title, subtitle: subtitle,
            category: rule.category, risk: c.risk ?? rule.risk,
            paths: paths, size: size,
            explanation: rule.explain.rendered(with: facts),
            checks: checks, actions: actions,
            blockingApps: blocking, blockers: blockers
        )
    }
}
