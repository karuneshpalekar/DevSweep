import Foundation

/// Persists ProjectsState and imports what RepoShelf remembered.
public final class ProjectStore: @unchecked Sendable {
    public let url: URL
    public private(set) var state = ProjectsState()
    private let lock = NSLock()

    public static var defaultURL: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/DevSweep/projects.json")
    }

    public static var repoShelfStateURL: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/RepoShelf/state.json")
    }

    public init(url: URL = ProjectStore.defaultURL, importFrom repoShelf: URL? = ProjectStore.repoShelfStateURL) {
        self.url = url
        if let data = try? Data(contentsOf: url), let s = try? decoder.decode(ProjectsState.self, from: data) {
            state = s
        } else if let repoShelf, let imported = Self.importRepoShelf(repoShelf) {
            state = imported
            save()
        }
    }

    var decoder: JSONDecoder { let d = JSONDecoder(); d.dateDecodingStrategy = .iso8601; return d }

    /// Identities, clone strategies, last-opened times and known repos from RepoShelf's state file.
    public static func importRepoShelf(_ file: URL) -> ProjectsState? {
        guard let data = try? Data(contentsOf: file),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        var s = ProjectsState()
        if let root = obj["workspaceRootPath"] as? String, !root.isEmpty { s.workspaceRoot = root }
        if let ids = obj["identities"] as? [String: [String: Any]] {
            for (login, v) in ids {
                s.identities[login] = GitIdentity(name: v["name"] as? String ?? "", email: v["email"] as? String ?? "")
            }
        }
        if let st = obj["cloneStrategies"] as? [String: String] {
            for (slug, raw) in st { if let c = CloneStrategy(rawValue: raw) { s.strategies[slug] = c } }
        }
        // RepoShelf stored dates as seconds since 2001 (Foundation's default).
        if let lo = obj["lastOpened"] as? [String: Double] {
            for (slug, t) in lo { s.lastOpened[slug] = Date(timeIntervalSinceReferenceDate: t) }
        }
        for key in ["knownRepos", "addedRepos"] {
            for r in obj[key] as? [[String: Any]] ?? [] {
                guard let slug = r["nameWithOwner"] as? String, let login = r["login"] as? String,
                      !s.known.contains(where: { $0.nameWithOwner == slug }) else { continue }
                s.known.append(.init(nameWithOwner: slug, account: login, lastParent: r["lastParentPath"] as? String))
            }
        }
        return s
    }

    public func update(_ change: (inout ProjectsState) -> Void) {
        lock.lock(); change(&state); lock.unlock()
        save()
    }

    private func save() {
        lock.lock(); let snapshot = state; lock.unlock()
        let e = JSONEncoder()
        e.dateEncodingStrategy = .iso8601
        e.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? e.encode(snapshot) else { return }
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: url, options: .atomic)
    }
}
