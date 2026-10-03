import Foundation

/// A secret stored somewhere it shouldn't be. DevSweep only records what
/// kind of file it is and where; it never keeps or shows the secret itself.
public struct SecurityFinding: Codable, Hashable, Sendable, Identifiable {
    public enum Kind: String, Codable, Sendable {
        case recoveryCodes, privateKey, serviceAccountKey, cloudAccessKeys, passwordExport, envFile
    }
    public enum Level: String, Codable, Sendable, Comparable {
        case critical, warning, ok
        public static func < (a: Level, b: Level) -> Bool {
            let order: [Level] = [.critical, .warning, .ok]
            return order.firstIndex(of: a)! < order.firstIndex(of: b)!
        }
    }

    public var id: String { "security:" + path }
    public var kind: Kind
    public var level: Level
    public var title: String
    public var path: String
    /// Short status shown in the list ("Plain text", "Committed to git").
    public var status: String
    public var why: String
    public var whatToDo: String
    public var checks: [Check]
    /// Commands for fixes that aren't just "move to Trash" (e.g. stop tracking a .env).
    public var fix: [String]?

    public init(kind: Kind, level: Level, title: String, path: String, status: String, why: String,
                whatToDo: String, checks: [Check], fix: [String]? = nil) {
        self.kind = kind; self.level = level; self.title = title; self.path = path; self.status = status
        self.why = why; self.whatToDo = whatToDo; self.checks = checks; self.fix = fix
    }
}

public enum SecurityScanner {
    /// Loose folders where secrets tend to pile up.
    static let looseFolders = ["~/Downloads", "~/Desktop", "~/Documents"]
    static let maxRead = 64 * 1024

    public static func scan(home: URL = FileManager.default.homeDirectoryForCurrentUser,
                            projectRoots: [String]? = nil) -> [SecurityFinding] {
        var out: [SecurityFinding] = []
        var seen = Set<String>()
        func add(_ f: SecurityFinding?) {
            guard let f, seen.insert(f.path).inserted else { return }
            out.append(f)
        }
        for folder in looseFolders {
            walk(FS.expand(folder, home: home), depth: 2) { add(classify($0, home: home)) }
        }
        for root in FS.uniqueDirectories(projectRoots ?? RuleLoader.defaultProjectRoots, home: home) {
            walk(root, depth: 4, onlyEnv: true) { add(envFinding($0, home: home)) }
        }
        return out.sorted { ($0.level, $0.title) < ($1.level, $1.title) }
    }

    static let skipDirs: Set<String> = ["node_modules", ".git", "Library", ".venv", "venv", "Pods", "build", "dist", ".Trash", ".gradle"]

    static func walk(_ dir: URL, depth: Int, onlyEnv: Bool = false, _ visit: (URL) -> Void) {
        guard depth >= 0, let names = try? FileManager.default.contentsOfDirectory(atPath: dir.path) else { return }
        for name in names {
            let url = dir.appendingPathComponent(name)
            var isDir: ObjCBool = false
            FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir)
            if isDir.boolValue {
                if !skipDirs.contains(name), url.pathExtension != "app", !name.hasPrefix(".") || name == ".config" {
                    walk(url, depth: depth - 1, onlyEnv: onlyEnv, visit)
                }
            } else if !onlyEnv || isEnvName(name) {
                visit(url)
            }
        }
    }

    // MARK: - Recognising files

    static func head(_ url: URL) -> Data? {
        guard let h = FileHandle(forReadingAtPath: url.path) else { return nil }
        defer { try? h.close() }
        return try? h.read(upToCount: maxRead)
    }

    static func matches(_ name: String, _ pattern: String) -> Bool {
        name.range(of: pattern, options: [.regularExpression, .caseInsensitive]) != nil
    }

    static func service(in name: String) -> String? {
        let known = ["github": "GitHub", "google": "Google", "twilio": "Twilio", "aws": "AWS", "amazon": "AWS",
                     "microsoft": "Microsoft", "apple": "Apple", "gitlab": "GitLab", "slack": "Slack", "stripe": "Stripe",
                     "firebase": "Firebase", "discord": "Discord", "binance": "Binance", "coinbase": "Coinbase"]
        let lower = name.lowercased()
        return known.first { lower.contains($0.key) }?.value
    }

    static func classify(_ url: URL, home: URL) -> SecurityFinding? {
        let name = url.lastPathComponent
        let ext = url.pathExtension.lowercased()
        let display = FS.abbreviate(url.path, home: home)
        if isEnvName(name) { return envFinding(url, home: home) }

        if matches(name, #"(recovery|backup)[-_ ]?codes?|2fa|two[-_ ]?factor"#),
           ["txt", "pdf", "md", "rtf", "png", "jpg", "csv", ""].contains(ext) {
            let svc = service(in: name)
            return SecurityFinding(
                kind: .recoveryCodes, level: .critical,
                title: "\(svc.map { "\($0) " } ?? "")recovery codes", path: url.path, status: "Plain text",
                why: "Anyone who gets this file can sign in to your \(svc ?? "") account without your password or phone.",
                whatToDo: "Save the codes in your password manager, then delete the file. \(svc ?? "The service") can also generate new codes, which makes these useless.",
                checks: [.passed("Recognised from the file name"), .passed("The codes themselves were never read into DevSweep")])
        }

        if ["p12", "pfx"].contains(ext) {
            return keyFinding(url, display, "certificate with a private key")
        }
        if ["pem", "key", ""].contains(ext) || matches(name, #"^id_(rsa|ed25519|ecdsa|dsa)$"#) {
            if let d = head(url), let text = String(data: d.prefix(200), encoding: .utf8),
               text.contains("-----BEGIN"), text.contains("PRIVATE KEY-----") {
                return keyFinding(url, display, "private key")
            }
        }

        if ["json", "b64", "txt"].contains(ext) || matches(name, #"credential|service[-_ ]?account|firebase|key"#) {
            if let d = head(url), isServiceAccount(d) {
                return SecurityFinding(
                    kind: .serviceAccountKey, level: .critical,
                    title: "\(service(in: name) ?? "Google Cloud") service-account key", path: url.path, status: "Plain text",
                    why: "It gives full access to whatever that service account can reach, with no password and no expiry.",
                    whatToDo: "If you still need it, keep it outside Downloads (or in a secrets manager). If you don't, delete it and revoke the key in the cloud console.",
                    checks: [.passed("Recognised from the file's structure (a service-account key)"),
                             .passed("The key itself was never read into DevSweep")])
            }
        }

        if ext == "csv", matches(name, #"accesskeys?|credentials"#), let d = head(url),
           let text = String(data: d.prefix(400), encoding: .utf8), text.localizedCaseInsensitiveContains("access key id") {
            return SecurityFinding(
                kind: .cloudAccessKeys, level: .critical, title: "AWS access keys", path: url.path, status: "Plain text",
                why: "These keys can use your AWS account and run up costs if anyone gets them.",
                whatToDo: "Store them with `aws configure` or a secrets manager, delete this file, and rotate the keys if the file was ever shared.",
                checks: [.passed("Recognised from the column headers")])
        }

        if ["csv", "json", "1pux"].contains(ext), matches(name, #"passwords?|bitwarden|1password|lastpass|keepass|logins"#) {
            return SecurityFinding(
                kind: .passwordExport, level: .critical, title: "Password export", path: url.path, status: "Plain text",
                why: "An export from a password manager or browser holds every saved password unencrypted.",
                whatToDo: "Delete it once you've imported it where you needed it. If it was synced or shared, change the important passwords.",
                checks: [.passed("Recognised from the file name")])
        }
        return nil
    }

    static func keyFinding(_ url: URL, _ display: String, _ what: String) -> SecurityFinding {
        SecurityFinding(
            kind: .privateKey, level: .critical, title: "A \(what) outside ~/.ssh", path: url.path, status: "Exposed",
            why: "Private keys prove who you are to servers and services. One sitting in \(display.components(separatedBy: "/").dropLast().joined(separator: "/")) is easy to copy, sync or attach by accident.",
            whatToDo: "Move it to ~/.ssh (or your keychain) with permissions limited to you, or delete it if it's no longer used.",
            checks: [.passed("Recognised from the file's header"), .passed("The key itself was never read into DevSweep")],
            fix: ["mkdir -p ~/.ssh && chmod 700 ~/.ssh", "mv \(Commands.q(url.path)) ~/.ssh/", "chmod 600 ~/.ssh/\(Commands.q(url.lastPathComponent))"])
    }

    static func isServiceAccount(_ data: Data) -> Bool {
        func check(_ d: Data) -> Bool {
            guard let t = String(data: d, encoding: .utf8) else { return false }
            return t.contains("\"private_key\"") && (t.contains("service_account") || t.contains("client_email"))
        }
        if check(data) { return true }
        // Some people store the JSON base64-encoded.
        let compact = String(decoding: data, as: UTF8.self).filter { !$0.isWhitespace }
        if let decoded = Data(base64Encoded: compact) { return check(decoded) }
        return false
    }

    // MARK: - .env files

    static func isEnvName(_ name: String) -> Bool {
        guard name == ".env" || name.hasPrefix(".env.") else { return false }
        return !["example", "sample", "template", "dist", "defaults"].contains { name.lowercased().hasSuffix($0) }
    }

    /// Counts secret-looking assignments without keeping their values.
    static func secretCount(_ url: URL) -> Int {
        guard let d = head(url), let text = String(data: d, encoding: .utf8) else { return 0 }
        return text.split(separator: "\n").filter { line in
            let l = line.trimmingCharacters(in: .whitespaces)
            guard !l.hasPrefix("#"), let eq = l.firstIndex(of: "=") else { return false }
            let key = l[..<eq].uppercased()
            let value = l[l.index(after: eq)...].trimmingCharacters(in: CharacterSet(charactersIn: " \"'"))
            return !value.isEmpty && ["SECRET", "TOKEN", "KEY", "PASSWORD", "PASS", "PRIVATE", "CREDENTIAL", "DSN"].contains { key.contains($0) }
        }.count
    }

    static func envFinding(_ url: URL, home: URL) -> SecurityFinding? {
        let count = secretCount(url)
        guard count > 0 else { return nil }
        let dir = url.deletingLastPathComponent()
        let name = url.lastPathComponent
        let project = dir.lastPathComponent
        let keys = "\(count) secret\(count == 1 ? "" : "s")"
        let inRepo = Shell.run(["git", "-C", dir.path, "rev-parse", "--is-inside-work-tree"], timeout: 10).ok
        if !inRepo {
            return SecurityFinding(
                kind: .envFile, level: .warning, title: "\(name) with \(keys) in \(project)", path: url.path, status: "Not in git",
                why: "This folder isn't a git repository, so nothing protects the file if you later run git init and commit everything.",
                whatToDo: "When you put this project in git, add \(name) to .gitignore first.",
                checks: [.passed("Only key names were checked; values were never kept")])
        }
        if Shell.run(["git", "-C", dir.path, "ls-files", "--error-unmatch", name], timeout: 10).ok {
            return SecurityFinding(
                kind: .envFile, level: .critical, title: "\(name) with \(keys) is committed in \(project)", path: url.path, status: "Committed to git",
                why: "Everyone with access to this repository, now or later, can read these secrets, including in its history.",
                whatToDo: "Stop tracking the file and ignore it, then rotate the secrets: removing it now doesn't remove it from past commits.",
                checks: [.warning("git tracks this file"), .passed("Only key names were checked; values were never kept")],
                fix: ["cd \(Commands.q(dir.path))", "git rm --cached \(Commands.q(name))",
                      "grep -qxF \(Commands.q(name)) .gitignore 2>/dev/null || echo \(Commands.q(name)) >> .gitignore",
                      "git commit -m \"Stop tracking \(name)\""])
        }
        if Shell.run(["git", "-C", dir.path, "check-ignore", "-q", name], timeout: 10).ok {
            return SecurityFinding(
                kind: .envFile, level: .ok, title: "\(name) with \(keys) in \(project)", path: url.path, status: "Kept out of git",
                why: "This is the right way to keep local secrets: git ignores the file.",
                whatToDo: "Nothing to do. Keep it out of shared folders and backups that sync publicly.",
                checks: [.passed("git ignores this file"), .passed("Only key names were checked; values were never kept")])
        }
        return SecurityFinding(
            kind: .envFile, level: .warning, title: "\(name) with \(keys) in \(project) isn't ignored", path: url.path, status: "Not ignored",
            why: "git doesn't track it yet, but it isn't in .gitignore either, so one `git add .` would commit your secrets.",
            whatToDo: "Add it to .gitignore.",
            checks: [.warning("Not in .gitignore"), .passed("Only key names were checked; values were never kept")],
            fix: ["cd \(Commands.q(dir.path))", "echo \(Commands.q(name)) >> .gitignore", "git check-ignore -v \(Commands.q(name))"])
    }
}
