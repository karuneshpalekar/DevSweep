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
    let processArgs: [String]

    public init(home: URL = FileManager.default.homeDirectoryForCurrentUser) {
        self.home = home
        #if arch(arm64)
        arch = "arm64"
        #else
        arch = "x86_64"
        #endif
        let running = NSWorkspace.shared.runningApplications
        runningAppNames = Set(running.compactMap { $0.localizedName?.lowercased() })
        installedBundleIDs = Self.findInstalledApps(home: home)
            .union(running.compactMap { $0.bundleIdentifier?.lowercased() })
        processArgs = Shell.run(["ps", "-axo", "args="], timeout: 10).stdout
            .split(separator: "\n").map(String.init)
    }

    /// Running apps or processes matching the given blocker names.
    public func running(_ blockers: [String]) -> [String] {
        blockers.filter { b in
            runningAppNames.contains(b.lowercased()) || processArgs.contains { $0.contains(b) }
        }
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

    public init(rules: [Rule], context: ScanContext = ScanContext()) {
        self.rules = rules
        self.context = context
    }

    public func scan(ignored: Set<String> = [], progress: (@Sendable (String) -> Void)? = nil) async -> ScanResult {
        let ctx = context
        let active = rules.filter { $0.arch == nil || $0.arch == ctx.arch }

        // 1. Detect, all rules in parallel.
        var detected = [[Candidate]](repeating: [], count: active.count)
        await withTaskGroup(of: (Int, [Candidate]).self) { group in
            for (i, rule) in active.enumerated() {
                group.addTask {
                    progress?(rule.title)
                    return (i, Detectors.run(rule, ctx))
                }
            }
            for await (i, found) in group { detected[i] = found }
        }

        // 2. A path belongs to the first rule that claims it, so an item is
        //    never listed (or counted) twice.
        var claimed = Set<String>()
        var work: [(Rule, Candidate)] = []
        for (i, rule) in active.enumerated() {
            for var c in detected[i] {
                let hadPaths = !c.paths.isEmpty
                c.paths.removeAll { claimed.contains($0.path) }
                if hadPaths && c.paths.isEmpty { continue }
                c.paths.forEach { claimed.insert($0.path) }
                work.append((rule, c))
            }
        }

        // 3. Size everything in parallel.
        progress?("Measuring sizes")
        var sizes = [Int64](repeating: 0, count: work.count)
        await withTaskGroup(of: (Int, Int64).self) { group in
            for (i, item) in work.enumerated() {
                group.addTask {
                    if let s = item.1.size { return (i, s) }
                    return (i, item.1.paths.reduce(0) { $0 + FS.allocatedSize($1) })
                }
            }
            for await (i, s) in group { sizes[i] = s }
        }

        // 4. Build findings.
        var findings: [Finding] = []
        for (i, (rule, c)) in work.enumerated() {
            let size = sizes[i]
            let minBytes = Int64(rule.minSizeMB ?? 1) * 1_000_000
            if size < minBytes { continue }
            let id = "\(rule.id):\(c.key)"
            if ignored.contains(id) { continue }
            findings.append(makeFinding(rule: rule, candidate: c, size: size, id: id))
        }
        findings.sort { ($0.risk, -$0.size) < ($1.risk, -$1.size) }

        return ScanResult(findings: findings, errors: [], date: Date(), disk: DiskInfo.current(for: ctx.home))
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
