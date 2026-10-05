import Foundation

/// Finds git working copies on this Mac, checks what's at risk if they were
/// removed, and joins them with what GitHub reports.
public enum ProjectScanner {
    static let skipDirs: Set<String> = [
        "node_modules", "Library", ".Trash", "Pods", "Carthage", "vendor", ".build", "DerivedData", "dist", "build",
        ".next", "target", ".gradle", ".venv", "venv", "__pycache__", "Applications",
    ]

    static func git(_ dir: URL, _ args: String...) -> ShellResult {
        Shell.run(["git", "-C", dir.path] + args, timeout: 60)
    }

    // MARK: - Safety

    /// What would be lost: unpushed commits, uncommitted files, stashes.
    /// "Unpushed" is measured against remote branches as of the last fetch.
    public static func safety(of dir: URL) -> GitSafety {
        let origin = git(dir, "config", "--get", "remote.origin.url").stdout.trimmingCharacters(in: .whitespacesAndNewlines)
        let hasRemote = !origin.isEmpty || !git(dir, "remote").stdout.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        let unpushed = Int(git(dir, "rev-list", "--branches", "--not", "--remotes", "--count").stdout
            .trimmingCharacters(in: .whitespacesAndNewlines)) ?? 0
        let lines = git(dir, "status", "--porcelain").stdout.split(separator: "\n")
        let untracked = lines.filter { $0.hasPrefix("??") }
        let changed = lines.count - untracked.count
        let sample = untracked.prefix(5).map { String($0.dropFirst(3)) }
        let stashes = git(dir, "stash", "list").stdout.split(separator: "\n").count
        return GitSafety(unpushedCommits: hasRemote ? unpushed : 0, changedFiles: changed, stashes: stashes, hasRemote: hasRemote,
                         untrackedFiles: untracked.count, untrackedSample: sample)
    }

    // MARK: - Local scan

    /// Finds working copies under `roots`, keyed by the owner/repo in each
    /// clone's `origin`, not by folder name. Duplicate checkouts keep the largest.
    public static func scanLocal(roots: [URL], maxDepth: Int = 4) -> [LocalClone] {
        let fm = FileManager.default
        var byPath: [String: LocalClone] = [:]

        func isRepo(_ dir: URL) -> Bool { fm.fileExists(atPath: dir.appendingPathComponent(".git").path) }

        func record(_ dir: URL) {
            let originURL = git(dir, "config", "--get", "remote.origin.url").stdout.trimmingCharacters(in: .whitespacesAndNewlines)
            let slug = originURL.contains("github.com") ? GitHubClient.parseSlug(originURL) : nil
            let committed = TimeInterval(git(dir, "log", "-1", "--format=%ct").stdout.trimmingCharacters(in: .whitespacesAndNewlines))
            let s = safety(of: dir)
            byPath[dir.path] = LocalClone(path: dir, size: FS.allocatedSize(dir), slug: slug, hasRemote: s.hasRemote,
                                          lastCommit: committed.map { Date(timeIntervalSince1970: $0) }, safety: s)
        }

        func walk(_ dir: URL, depth: Int) {
            if isRepo(dir) { record(dir); return }
            guard depth < maxDepth else { return }
            for child in FS.children(dir) where FS.isDirectory(child) && !skipDirs.contains(child.lastPathComponent)
                && !child.lastPathComponent.hasPrefix(".") && child.pathExtension != "app" {
                walk(child, depth: depth + 1)
            }
        }

        var seen = Set<String>()
        for root in roots {
            let r = root.standardizedFileURL
            if fm.fileExists(atPath: r.path), seen.insert(r.path).inserted { walk(r, depth: 0) }
        }
        var best: [String: LocalClone] = [:]
        for c in byPath.values {
            let key = c.slug?.lowercased() ?? "local:" + c.path.path
            if let existing = best[key], existing.size >= c.size { continue }
            best[key] = c
        }
        return Array(best.values)
    }

    // MARK: - Joining local and remote

    /// One list: repos on GitHub, clones on disk (matched by origin), folders
    /// with no GitHub origin, and repos you removed earlier that you can download again.
    public static func merge(local: [LocalClone], remote: [RemoteRepo], state: ProjectsState, home: URL) -> [Project] {
        var projects: [String: Project] = [:]
        func key(_ slug: String) -> String { slug.lowercased() }

        for r in remote {
            projects[key(r.nameWithOwner)] = Project(
                id: r.nameWithOwner, name: r.name, nameWithOwner: r.nameWithOwner, owner: String(r.nameWithOwner.split(separator: "/")[0]),
                description: r.description, isPrivate: r.isPrivate, onGitHub: true, account: r.account,
                lastActivity: r.pushedAt, strategy: nil)
            projects[key(r.nameWithOwner)]?.remoteKB = r.diskUsageKB
        }
        var knownOnly = Set<String>()
        for k in state.known where projects[key(k.nameWithOwner)] == nil {
            let parts = k.nameWithOwner.split(separator: "/").map(String.init)
            guard parts.count == 2 else { continue }
            var p = Project(id: k.nameWithOwner, name: parts[1], nameWithOwner: k.nameWithOwner, owner: parts[0], description: "",
                            isPrivate: k.isPrivate, onGitHub: true, account: k.account)
            p.remoteKB = k.remoteKB ?? 0
            projects[key(k.nameWithOwner)] = p
            knownOnly.insert(key(k.nameWithOwner))
        }
        for c in local {
            let attach = { (p: inout Project) in
                p.localPath = c.path.path
                p.localSize = c.size
                p.safety = c.safety
                if let d = c.lastCommit { p.lastActivity = max(p.lastActivity ?? d, d) }
            }
            if let slug = c.slug {
                if var p = projects[key(slug)] {
                    attach(&p)
                    projects[key(slug)] = p
                } else {
                    // A clone from an org or account you're not signed in to.
                    let parts = slug.split(separator: "/").map(String.init)
                    var p = Project(id: slug, name: parts[1], nameWithOwner: slug, owner: parts[0], description: "", isPrivate: nil, onGitHub: true)
                    attach(&p)
                    projects[key(slug)] = p
                }
            } else {
                let name = c.path.lastPathComponent
                var p = Project(id: "local:" + c.path.path, name: name, nameWithOwner: name, owner: "", description: "",
                                isPrivate: nil, onGitHub: false)
                attach(&p)
                projects["local:" + c.path.path] = p
            }
        }
        // A repo that moved to another owner or org leaves its old name in the
        // remembered list. If a clone with the same repo name is on this Mac,
        // the old entry isn't a second project that needs downloading.
        let onDiskNames = Set(projects.values.filter(\.onDisk).map { $0.name.lowercased() })
        for k in knownOnly {
            if let p = projects[k], !p.onDisk, onDiskNames.contains(p.name.lowercased()) { projects[k] = nil }
        }
        return projects.values.map { p in
            var p = p
            p.lastOpened = state.lastOpened[p.nameWithOwner]
            p.strategy = state.strategies[p.nameWithOwner]
            return p
        }.sorted { ($0.onDisk ? 0 : 1, -($0.lastUsed?.timeIntervalSince1970 ?? 0)) < ($1.onDisk ? 0 : 1, -($1.lastUsed?.timeIntervalSince1970 ?? 0)) }
    }

    /// Where a (re)download goes: the folder it last lived in, else ~/Code/<account>/<repo>.
    public static func destination(for p: Project, state: ProjectsState, home: URL) -> URL {
        if let parent = state.known.first(where: { $0.nameWithOwner == p.nameWithOwner })?.lastParent {
            return FS.expand(parent, home: home).appendingPathComponent(p.name)
        }
        return FS.expand(state.workspaceRoot, home: home)
            .appendingPathComponent(p.account ?? p.owner)
            .appendingPathComponent(p.name)
    }
}
