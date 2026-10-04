import Foundation

/// Thin wrappers around the `gh` and `git` command-line tools. Anything
/// account-specific passes that account's token instead of running
/// `gh auth switch`, so the account active in your terminal never changes.
public enum GitHubClient {
    public struct Failure: LocalizedError {
        public var message: String
        public var errorDescription: String? { message }
    }

    static func fail(_ message: String) -> Failure { Failure(message: message) }

    static func env(token: String) -> [String: String] {
        ["GH_TOKEN": token, "GH_HOST": "github.com", "GH_PROMPT_DISABLED": "1", "CLICOLOR": "0"]
    }

    /// Runs a tool and returns its output, or throws with its error text.
    @discardableResult
    static func require(_ args: [String], env: [String: String] = [:], timeout: TimeInterval = 600) throws -> String {
        guard let tool = args.first, Shell.locate(tool) != nil else {
            throw fail("\(args.first ?? "The tool") isn't installed. For GitHub features install the GitHub CLI: brew install gh")
        }
        let r = Shell.run(args, env: env, timeout: timeout)
        guard r.ok else {
            let text = (r.stderr.isEmpty ? r.stdout : r.stderr).trimmingCharacters(in: .whitespacesAndNewlines)
            throw fail(text.isEmpty ? "\(args.joined(separator: " ")) failed (exit \(r.status))." : text)
        }
        return r.stdout
    }

    // MARK: - Accounts

    /// Parses `gh auth status` for github.com accounts.
    public static func parseAccounts(_ text: String) -> [GitHubAccount] {
        var accounts: [GitHubAccount] = []
        for line in text.split(separator: "\n", omittingEmptySubsequences: false).map(String.init) {
            if let range = line.range(of: #"Logged in to \S+ account (\S+)"#, options: .regularExpression) {
                let name = line[range].components(separatedBy: " account ").last ?? ""
                accounts.append(GitHubAccount(login: name, isActive: false))
            } else if line.contains("Active account: true"), !accounts.isEmpty {
                accounts[accounts.count - 1].isActive = true
            }
        }
        return accounts
    }

    public enum Status: Equatable, Sendable {
        /// The GitHub command-line tool (gh) isn't on this Mac.
        case notInstalled
        /// gh is installed, but no account is signed in.
        case notSignedIn
        case signedIn([GitHubAccount])
    }

    /// Whether GitHub features can work, and why not when they can't.
    public static func status() -> Status {
        guard Shell.locate("gh") != nil else { return .notInstalled }
        let r = Shell.run(["gh", "auth", "status"], timeout: 30)
        let accounts = parseAccounts(r.stdout + "\n" + r.stderr)
        return accounts.isEmpty ? .notSignedIn : .signedIn(accounts)
    }

    public static func accounts() throws -> [GitHubAccount] {
        guard Shell.locate("gh") != nil else { throw fail("The GitHub CLI isn't installed. Install it with: brew install gh") }
        let r = Shell.run(["gh", "auth", "status"], timeout: 30)
        let accounts = parseAccounts(r.stdout + "\n" + r.stderr)
        if accounts.isEmpty { throw fail("You aren't signed in to GitHub. Run gh auth login in Terminal.") }
        return accounts
    }

    public static func token(for login: String) throws -> String {
        let t = try require(["gh", "auth", "token", "--user", login], timeout: 30).trimmingCharacters(in: .whitespacesAndNewlines)
        guard !t.isEmpty else { throw fail("No token for \(login). Run gh auth login in Terminal.") }
        return t
    }

    // MARK: - Repos

    private struct RepoJSON: Decodable {
        var name: String
        var nameWithOwner: String
        var description: String?
        var isPrivate: Bool
        var pushedAt: String?
        var url: String
        var diskUsage: Int?
    }

    static let repoFields = "name,nameWithOwner,description,isPrivate,pushedAt,url,diskUsage"

    private static func remote(_ r: RepoJSON, account: String) -> RemoteRepo {
        RemoteRepo(name: r.name, nameWithOwner: r.nameWithOwner, description: r.description ?? "", isPrivate: r.isPrivate,
                   pushedAt: r.pushedAt.flatMap { ISO8601DateFormatter().date(from: $0) }, url: r.url,
                   diskUsageKB: r.diskUsage ?? 0, account: account)
    }

    /// Repos owned by `login`, newest push first.
    public static func repos(for login: String, token: String) throws -> [RemoteRepo] {
        let out = try require(["gh", "repo", "list", login, "--no-archived", "--limit", "500", "--json", repoFields],
                              env: env(token: token), timeout: 90)
        let decoded = try JSONDecoder().decode([RepoJSON].self, from: Data(out.utf8))
        return decoded.map { remote($0, account: login) }
            .sorted { ($0.pushedAt ?? .distantPast) > ($1.pushedAt ?? .distantPast) }
    }

    /// One repo by `owner/name`, for repos outside your own list.
    public static func repoView(slug: String, account: String, token: String) throws -> RemoteRepo {
        let out = try require(["gh", "repo", "view", slug, "--json", repoFields], env: env(token: token), timeout: 60)
        return remote(try JSONDecoder().decode(RepoJSON.self, from: Data(out.utf8)), account: account)
    }

    // MARK: - Clone

    /// The `gh repo clone` arguments for a strategy.
    public static func cloneArguments(slug: String, destination: URL, strategy: CloneStrategy) -> [String] {
        var args = ["gh", "repo", "clone", slug, destination.path]
        if !strategy.gitArgs.isEmpty { args += ["--"] + strategy.gitArgs }
        return args
    }

    public static func applyIdentity(_ identity: GitIdentity?, to folder: URL) {
        guard let identity, !identity.isBlank else { return }
        if !identity.name.isEmpty { Shell.run(["git", "-C", folder.path, "config", "user.name", identity.name], timeout: 15) }
        if !identity.email.isEmpty { Shell.run(["git", "-C", folder.path, "config", "user.email", identity.email], timeout: 15) }
    }

    public static func clone(slug: String, into destination: URL, strategy: CloneStrategy, token: String,
                             identity: GitIdentity?) throws {
        guard !FileManager.default.fileExists(atPath: destination.path) else {
            throw fail("\(FS.abbreviate(destination.path, home: FileManager.default.homeDirectoryForCurrentUser)) already exists.")
        }
        try FileManager.default.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
        try require(cloneArguments(slug: slug, destination: destination, strategy: strategy), env: env(token: token))
        applyIdentity(identity, to: destination)
    }

    // MARK: - Publish

    public static func isGitRepo(_ dir: URL) -> Bool {
        FileManager.default.fileExists(atPath: dir.appendingPathComponent(".git").path)
    }

    /// Turns a folder into a new GitHub repo: `git init` and a first commit if
    /// needed, then `gh repo create --source --push`.
    public static func publish(folder: URL, owner: String, name: String, description: String, isPrivate: Bool,
                               token: String, identity: GitIdentity?) throws {
        let path = folder.path
        if !isGitRepo(folder) { try require(["git", "-C", path, "init"], timeout: 30) }
        applyIdentity(identity, to: folder)

        let hasHead = Shell.run(["git", "-C", path, "rev-parse", "--verify", "HEAD"], timeout: 15).ok
        let dirty = !Shell.run(["git", "-C", path, "status", "--porcelain"], timeout: 30).stdout
            .trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        if !hasHead || dirty {
            try require(["git", "-C", path, "add", "-A"], timeout: 120)
            let staged = Shell.run(["git", "-C", path, "diff", "--cached", "--name-only"], timeout: 30).stdout
                .trimmingCharacters(in: .whitespacesAndNewlines)
            if staged.isEmpty, !hasHead { throw fail("The folder has no files to commit.") }
            if !staged.isEmpty {
                try require(["git", "-C", path, "commit", "-m", hasHead ? "Add files" : "Initial commit"], timeout: 120)
            }
        }
        var args = ["gh", "repo", "create", "\(owner)/\(name)", "--source", path, "--remote", "origin", "--push",
                    isPrivate ? "--private" : "--public"]
        if !description.trimmingCharacters(in: .whitespaces).isEmpty { args += ["--description", description] }
        try require(args, env: env(token: token))
    }

    // MARK: - Slugs

    /// `owner/name` from a pasted URL or `owner/name` text, or nil.
    public static func parseSlug(_ input: String) -> String? {
        var s = input.trimmingCharacters(in: .whitespacesAndNewlines)
        s = s.replacingOccurrences(of: "git@github.com:", with: "")
        if let r = s.range(of: "://") { s = String(s[r.upperBound...]) }
        // Strip credentials from https://user:token@github.com/...
        if let at = s.firstIndex(of: "@"), s[..<at].allSatisfy({ $0 != "/" }) { s = String(s[s.index(after: at)...]) }
        if s.hasPrefix("github.com/") { s = String(s.dropFirst("github.com/".count)) }
        if s.hasSuffix(".git") { s = String(s.dropLast(4)) }
        s = s.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        let parts = s.split(separator: "/")
        guard parts.count == 2, parts.allSatisfy({ $0.range(of: #"^[\w.-]+$"#, options: .regularExpression) != nil }) else { return nil }
        return "\(parts[0])/\(parts[1])"
    }
}
