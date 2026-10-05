import Foundation

/// The commit identity written into a clone's `.git/config`, so commits from
/// one GitHub account are attributed to it whatever the global ~/.gitconfig says.
public struct GitIdentity: Codable, Equatable, Sendable {
    public var name: String
    public var email: String

    public init(name: String = "", email: String = "") {
        self.name = name
        self.email = email
    }

    public var isBlank: Bool {
        name.trimmingCharacters(in: .whitespaces).isEmpty && email.trimmingCharacters(in: .whitespaces).isEmpty
    }
}

public enum CloneStrategy: String, Codable, CaseIterable, Identifiable, Sendable {
    case blobless, shallow, full

    public var id: String { rawValue }

    public var title: String {
        switch self {
        case .blobless: return "Blobless"
        case .shallow: return "Shallow"
        case .full: return "Full"
        }
    }

    public var summary: String {
        switch self {
        case .blobless: return "Full history; file contents download when needed. Best default."
        case .shallow: return "Latest commit only. Smallest, but no history or blame."
        case .full: return "Everything, offline forever. Largest."
        }
    }

    /// Extra flags for `git clone`, after `gh repo clone <slug> <dir> --`.
    public var gitArgs: [String] {
        switch self {
        case .blobless: return ["--filter=blob:none"]
        case .shallow: return ["--depth=1"]
        case .full: return []
        }
    }

    /// Rough fraction of the repo's size on GitHub that ends up on disk.
    public var sizeFactor: Double {
        switch self {
        case .blobless: return 0.35
        case .shallow: return 0.12
        case .full: return 1.0
        }
    }
}

public struct GitHubAccount: Identifiable, Equatable, Sendable {
    public var id: String { login }
    public var login: String
    public var isActive: Bool

    public init(login: String, isActive: Bool) {
        self.login = login
        self.isActive = isActive
    }
}

/// A repo as reported by `gh repo list`.
public struct RemoteRepo: Identifiable, Equatable, Sendable {
    public var id: String { nameWithOwner }
    public var name: String
    public var nameWithOwner: String
    public var description: String
    public var isPrivate: Bool
    public var pushedAt: Date?
    public var url: String
    public var diskUsageKB: Int
    /// Which signed-in account can read it.
    public var account: String
}

/// What would be lost if this folder left the Mac.
public struct GitSafety: Codable, Equatable, Sendable {
    /// Commits on any local branch that exist on no remote branch.
    public var unpushedCommits: Int
    /// Modified or staged files that git already tracks.
    public var changedFiles: Int
    public var stashes: Int
    public var hasRemote: Bool
    /// Files git doesn't track yet and doesn't ignore. A removed folder takes them along.
    public var untrackedFiles: Int
    /// The first few untracked paths, so the panel can name them.
    public var untrackedSample: [String]

    public init(unpushedCommits: Int = 0, changedFiles: Int = 0, stashes: Int = 0, hasRemote: Bool = true,
                untrackedFiles: Int = 0, untrackedSample: [String] = []) {
        self.unpushedCommits = unpushedCommits
        self.changedFiles = changedFiles
        self.stashes = stashes
        self.hasRemote = hasRemote
        self.untrackedFiles = untrackedFiles
        self.untrackedSample = untrackedSample
    }

    /// A project can always be removed; when it isn't safe, the app warns first
    /// and lists `lossSummary`.
    public var canRemove: Bool { true }

    /// What would be lost with the folder, one line each. Empty when it's safe.
    public var lossSummary: [String] {
        var lines: [String] = []
        if !hasRemote { lines.append("There's no copy on GitHub, so this folder is the only copy") }
        if unpushedCommits > 0 { lines.append(unpushedCommits == 1 ? "1 commit that isn't on GitHub" : "\(unpushedCommits) commits that aren't on GitHub") }
        if changedFiles > 0 { lines.append("\(changedFiles) file\(changedFiles == 1 ? "" : "s") with changes that aren't committed") }
        if untrackedFiles > 0 {
            let names = untrackedSample.joined(separator: ", ") + (untrackedFiles > untrackedSample.count ? " and more" : "")
            lines.append("\(untrackedFiles) file\(untrackedFiles == 1 ? "" : "s") git doesn't track: \(names)")
        }
        if stashes > 0 { lines.append("\(stashes) stash\(stashes == 1 ? "" : "es")") }
        return lines
    }

    public var isSafeToRemove: Bool { hasRemote && unpushedCommits == 0 && changedFiles == 0 && untrackedFiles == 0 && stashes == 0 }
}

/// A git working copy found on this Mac.
public struct LocalClone: Sendable {
    public var path: URL
    public var size: Int64
    /// owner/repo parsed from the origin remote; nil when there's no GitHub origin.
    public var slug: String?
    public var hasRemote: Bool
    public var lastCommit: Date?
    public var safety: GitSafety
}

public struct Project: Identifiable, Equatable, Sendable {
    public var id: String
    public var name: String
    public var nameWithOwner: String
    public var owner: String
    public var description: String
    public var isPrivate: Bool?
    /// Folder on this Mac; nil when it's only on GitHub.
    public var localPath: String?
    public var localSize: Int64 = 0
    public var remoteKB: Int = 0
    public var onGitHub: Bool
    /// The account that can fetch it.
    public var account: String?
    public var safety: GitSafety?
    public var lastOpened: Date?
    public var lastActivity: Date?
    public var strategy: CloneStrategy?

    public init(id: String, name: String, nameWithOwner: String, owner: String, description: String, isPrivate: Bool?,
                localPath: String? = nil, localSize: Int64 = 0, remoteKB: Int = 0, onGitHub: Bool, account: String? = nil,
                safety: GitSafety? = nil, lastOpened: Date? = nil, lastActivity: Date? = nil, strategy: CloneStrategy? = nil) {
        self.id = id; self.name = name; self.nameWithOwner = nameWithOwner; self.owner = owner; self.description = description
        self.isPrivate = isPrivate; self.localPath = localPath; self.localSize = localSize; self.remoteKB = remoteKB
        self.onGitHub = onGitHub; self.account = account; self.safety = safety; self.lastOpened = lastOpened
        self.lastActivity = lastActivity; self.strategy = strategy
    }

    public var onDisk: Bool { localPath != nil }

    public enum Level: Sendable { case ok, attention, info }

    /// A short status for the list, and how loud it should be.
    public var status: (text: String, level: Level) {
        guard onDisk else { return ("Only on GitHub", .info) }
        guard let s = safety, s.hasRemote else { return ("Only on this Mac", .attention) }
        var parts: [String] = []
        if s.unpushedCommits > 0 { parts.append("\(s.unpushedCommits) commit\(s.unpushedCommits == 1 ? "" : "s") not pushed") }
        if s.changedFiles > 0 { parts.append("\(s.changedFiles) changed file\(s.changedFiles == 1 ? "" : "s")") }
        if s.untrackedFiles > 0 { parts.append("\(s.untrackedFiles) untracked file\(s.untrackedFiles == 1 ? "" : "s")") }
        if s.stashes > 0 && parts.isEmpty { parts.append("\(s.stashes) stash\(s.stashes == 1 ? "" : "es")") }
        return parts.isEmpty ? ("Up to date", .ok) : (parts.joined(separator: " · "), .attention)
    }

    /// How recently it was used: when you opened it here, else its last commit.
    public var lastUsed: Date? { [lastOpened, lastActivity].compactMap { $0 }.max() }
}

/// One line in the Activity trail: a download, removal, publish, push, or account change.
public struct ProjectActivity: Codable, Identifiable, Equatable, Sendable {
    public enum Kind: String, Codable, CaseIterable, Sendable {
        case download, remove, publish, push, addRepo, addAccount, identity
    }

    public var id: UUID
    public var date: Date
    public var kind: Kind
    public var subject: String
    public var detail: String

    public init(id: UUID = UUID(), date: Date = Date(), kind: Kind, subject: String, detail: String = "") {
        self.id = id; self.date = date; self.kind = kind; self.subject = subject; self.detail = detail
    }
}

/// Everything DevSweep remembers about projects.
public struct ProjectsState: Codable, Sendable {
    public var workspaceRoot: String = "~/Code"
    public var identities: [String: GitIdentity] = [:]
    public var strategies: [String: CloneStrategy] = [:]
    public var lastOpened: [String: Date] = [:]
    public var known: [KnownRepo] = []
    /// Newest first.
    public var activity: [ProjectActivity] = []
    /// RepoShelf's activity trail has been brought over (done once).
    public var activityImported = false

    public init() {}

    /// Every field is optional on disk, so a file written by an older version
    /// still loads, and nothing saved (identities, known repos) is lost.
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        workspaceRoot = try c.decodeIfPresent(String.self, forKey: .workspaceRoot) ?? "~/Code"
        identities = try c.decodeIfPresent([String: GitIdentity].self, forKey: .identities) ?? [:]
        strategies = try c.decodeIfPresent([String: CloneStrategy].self, forKey: .strategies) ?? [:]
        lastOpened = try c.decodeIfPresent([String: Date].self, forKey: .lastOpened) ?? [:]
        known = try c.decodeIfPresent([KnownRepo].self, forKey: .known) ?? []
        activity = try c.decodeIfPresent([ProjectActivity].self, forKey: .activity) ?? []
        activityImported = try c.decodeIfPresent(Bool.self, forKey: .activityImported) ?? false
    }

    public struct KnownRepo: Codable, Identifiable, Equatable, Sendable {
        public var id: String { nameWithOwner }
        public var nameWithOwner: String
        public var account: String
        public var isPrivate: Bool?
        public var remoteKB: Int?
        /// ~-relative parent folder it last lived in, so a re-download lands back there.
        public var lastParent: String?

        public init(nameWithOwner: String, account: String, isPrivate: Bool? = nil, remoteKB: Int? = nil, lastParent: String? = nil) {
            self.nameWithOwner = nameWithOwner
            self.account = account
            self.isPrivate = isPrivate
            self.remoteKB = remoteKB
            self.lastParent = lastParent
        }
    }
}
