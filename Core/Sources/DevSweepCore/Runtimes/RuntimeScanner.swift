import Foundation

/// Inventories language runtimes and databases, checks their support
/// status, and explains what to do. Read-only: it only suggests commands.
public final class RuntimeScanner {
    struct Tool {
        let id: String, name: String, product: String
        /// Prefer LTS release lines when recommending an upgrade.
        let preferLTS: Bool
        let probe: (RuntimeProbe) -> [Installation]
    }

    static let tools: [Tool] = [
        Tool(id: "node", name: "Node.js", product: "nodejs", preferLTS: true) { $0.node() },
        Tool(id: "python", name: "Python", product: "python", preferLTS: false) { $0.python() },
        Tool(id: "java", name: "Java", product: "eclipse-temurin", preferLTS: true) { $0.java() },
        Tool(id: "postgres", name: "PostgreSQL", product: "postgresql", preferLTS: false) { $0.postgres() },
        Tool(id: "go", name: "Go", product: "go", preferLTS: false) { $0.go() },
        Tool(id: "ruby", name: "Ruby", product: "ruby", preferLTS: false) { $0.ruby() },
    ]

    let eol: EndOfLifeClient
    let home: URL

    public init(eol: EndOfLifeClient = EndOfLifeClient(), home: URL = FileManager.default.homeDirectoryForCurrentUser) {
        self.eol = eol
        self.home = home
    }

    public func scan(progress: (@Sendable (String) -> Void)? = nil) async -> VersionsResult {
        progress?("Reading your shell setup")
        let env = ShellEnvironment.load()
        let args = Shell.run(["ps", "-axo", "args="], timeout: 10).stdout.split(separator: "\n").map(String.init)
        let probe = RuntimeProbe(home: home, env: env, processArgs: args)

        var reports: [RuntimeReport] = []
        for tool in Self.tools {
            progress?(tool.name)
            let installs = tool.probe(probe)
            guard !installs.isEmpty else { continue }
            let (cycles, fetched) = await eol.cycles(for: tool.product)
            reports.append(Self.report(tool, installs, cycles, fetched))
        }

        progress?("Homebrew")
        let brew = Self.homebrew()
        progress?("macOS")
        let (macCycles, macFetched) = await eol.cycles(for: "macos")
        let mac = Self.macOSReport(macCycles, macFetched)

        return VersionsResult(runtimes: reports, homebrew: brew, macOS: mac,
                              eolOffline: eol.usedStaleData, date: Date())
    }

    // MARK: - Building a report

    static func report(_ tool: Tool, _ found: [Installation], _ cycles: [ReleaseCycle], _ fetched: Date?) -> RuntimeReport {
        var installs = found.map { i -> Installation in
            var i = i
            if let c = cycles.first(where: { $0.cycle == i.cycle }) {
                i.support = c.status()
                i.releaseDate = c.releaseDate
                i.supportEnds = c.eolDate
                i.latestInCycle = c.latest
                if tool.id == "java", let l = c.latest {
                    i.latestInCycle = Version.parts(l).prefix(3).map(String.init).joined(separator: ".")
                    i.version = Version.parts(i.version).prefix(3).map(String.init).joined(separator: ".")
                }
                i.isLTS = c.isLTS
            } else if !cycles.isEmpty {
                // Older than anything listed means long out of support.
                if let oldest = cycles.last, Version.less(i.cycle, oldest.cycle) { i.support = .endOfLife }
            }
            return i
        }
        // Running servers first, then the one your shell runs, then your own installs, newest first.
        installs.sort { a, b in
            if (a.isRunning == true) != (b.isRunning == true) { return a.isRunning == true }
            if a.isDefault != b.isDefault { return a.isDefault }
            if a.isSystem != b.isSystem { return !a.isSystem }
            return Version.less(b.version, a.version)
        }

        let recommended = cycles.first { c in
            !c.ended && (c.releaseDate.map { $0 <= Date() } ?? true) && (!tool.preferLTS || c.isLTS)
        }
        var issues: [RuntimeIssue] = []
        var steps: [RuntimeStep] = []
        let name = tool.name
        let userInstalls = installs.filter { !$0.isSystem }

        for i in installs {
            let label = "\(name) \(i.version) (\(i.source.title))"
            let when = i.supportEnds.map { $0.formatted(date: .abbreviated, time: .omitted) }
            switch i.support {
            case .endOfLife where i.isSystem:
                issues.append(.init(level: .info, text: "\(label) is past end of life, but it belongs to macOS or an IDE. Leave it; your own copy is what matters."))
            case .endOfLife:
                issues.append(.init(level: .critical, text: "\(label) reached end of life\(when.map { " on \($0)" } ?? ""). It no longer gets security fixes." + (i.isDefault ? " It's the one your shell runs." : "")))
            case .endingSoon:
                let days = i.supportEnds.map { Int($0.timeIntervalSinceNow / 86_400) } ?? 0
                issues.append(.init(level: .warning, text: "\(label): support ends \(when ?? "soon") (in \(days) days)."))
            default: break
            }
        }
        let patches = installs.filter { $0.patchAvailable && $0.support != .endOfLife && !$0.isSystem }
            .compactMap { i in i.latestInCycle.map { "\(i.version) → \($0)" } }
        if !patches.isEmpty {
            issues.append(.init(level: .info, text: "Newer patch releases (security and bug fixes, same release line): \(ListFormat.join(patches))."))
        }

        let sources = Set(userInstalls.map(\.source))
        if sources.count > 1 {
            issues.append(.init(level: .warning, text: "Installed by \(sources.count) different tools (\(ListFormat.join(sources.map(\.title).sorted()))). Mixing them makes it easy to run a different version than you expect."))
        }
        if let d = installs.first(where: \.isDefault),
           let newer = userInstalls.filter({ !$0.isDefault && Version.less(d.cycle, $0.cycle) && $0.support != .endOfLife })
            .max(by: { Version.less($0.version, $1.version) }) {
            issues.append(.init(level: .warning, text: "Your shell runs \(name) \(d.version) from \(d.source.title), even though \(newer.version) from \(newer.source.title) is installed, because it comes first in your PATH."))
        }

        var seenText = Set<String>()
        issues = issues.filter { seenText.insert($0.text).inserted }

        // Suggested fixes.
        let def = installs.first(where: \.isDefault) ?? userInstalls.first
        if tool.id == "postgres" {
            let active = installs.first { $0.isRunning == true } ?? def
            if let a = active, a.source == .homebrew, a.support == .endOfLife || a.support == .endingSoon,
               let target = recommended, let from = a.sourceDetail {
                steps.append(RuntimeStep(kind: .guided, title: "Upgrade PostgreSQL \(a.cycle) to \(target.cycle)",
                    detail: "Backs up every database first, counts rows before and after, and asks before each step. Nothing is removed until the counts match and you confirm.",
                    commands: PostgresUpgrade.script(from: from, to: "postgresql@\(target.cycle)")))
            }
            for i in userInstalls where i.isRunning != true && !i.isDefault && i.id != active?.id {
                guard let cmd = Commands.remove(i, tool: tool.id) else { continue }
                let data = i.dataPath.map { " Its data folder (\(FS.abbreviate($0, home: FileManager.default.homeDirectoryForCurrentUser))\(i.dataSize.map { ", " + SizeFormat.string($0) } ?? "")) is kept; delete it yourself if you're sure." } ?? ""
                steps.append(RuntimeStep(kind: .remove, title: "Remove PostgreSQL \(i.version) (not running)",
                                         detail: "Nothing is using this server." + data, commands: cmd))
            }
        } else if let d = def, !d.isSystem, d.support == .endOfLife || d.support == .endingSoon, let target = recommended,
                  let cmds = Commands.install(tool: tool.id, cycle: target.cycle, via: d.source) {
            steps.append(RuntimeStep(kind: .switchDefault, title: "Switch to \(name) \(target.cycle)\(target.isLTS ? " LTS" : "")",
                detail: "Installs \(name) \(target.cycle) with \(d.source.title) and makes it the default. Your projects may need testing on the new version.",
                commands: cmds))
        }
        if tool.id != "postgres" {
            for i in userInstalls where !i.isDefault && (i.support == .endOfLife || i.support == .endingSoon) {
                if let cmd = Commands.remove(i, tool: tool.id) {
                    let why = i.support == .endOfLife ? "It's out of support" : "Its support ends soon"
                    steps.append(RuntimeStep(kind: .remove, title: "Remove \(name) \(i.version) (\(i.source.title))",
                                             detail: "\(why) and it isn't the one your shell runs.", commands: cmd))
                }
            }
            // The shell runs a python.org copy although Homebrew has the same or a newer version.
            if let d = def, d.source == .pythonOrg,
               let brew = userInstalls.filter({ $0.source == .homebrew && !Version.less($0.cycle, d.cycle) && $0.support != .endOfLife })
                .max(by: { Version.less($0.version, $1.version) }),
               let cmd = Commands.remove(d, tool: tool.id) {
                steps.append(RuntimeStep(kind: .switchDefault, title: "Use Homebrew's Python instead of the python.org copy",
                    detail: "Removes the python.org Python \(d.cycle) and the line it added to your shell profile (backed up first), so python3 becomes Homebrew's \(brew.version). Recreate any virtual environments made with the old copy.",
                    commands: cmd + ["exec $SHELL -l", "python3 --version"]))
            }
            // A python.org copy next to a Homebrew copy of the same version.
            for i in userInstalls where i.source == .pythonOrg && !i.isDefault
                && userInstalls.contains(where: { $0.source == .homebrew && $0.cycle == i.cycle }) {
                if let cmd = Commands.remove(i, tool: tool.id), !steps.contains(where: { $0.commands == cmd }) {
                    steps.append(RuntimeStep(kind: .remove, title: "Remove the python.org copy of Python \(i.cycle)",
                                             detail: "Homebrew has the same version, so this copy only adds confusion.", commands: cmd))
                }
            }
        }
        let patchable = userInstalls.filter { $0.source == .homebrew && $0.patchAvailable && $0.support != .endOfLife }
        if !patchable.isEmpty {
            steps.append(RuntimeStep(kind: .upgrade, title: "Install the latest patch releases",
                detail: "Security and bug fixes within the same release line. Safe for your projects.",
                commands: ["brew upgrade " + patchable.compactMap(\.sourceDetail).joined(separator: " ")]))
        }

        var rec = recommended.map { "\(tool.name) \($0.cycle)\($0.isLTS ? " LTS" : "")" }
        if let r = recommended, let ends = r.eolDate { rec! += ", supported until \(ends.formatted(date: .abbreviated, time: .omitted))" }
        return RuntimeReport(id: tool.id, name: tool.name, installs: installs, issues: issues, steps: steps,
                             recommended: rec, dataSource: "endoflife.date/\(tool.product)", checkedAt: fetched,
                             note: tool.id == "java" ? "Java support dates follow Eclipse Temurin; other vendors differ slightly." : nil)
    }

    // MARK: - Homebrew

    static func homebrew() -> HomebrewReport? {
        guard Shell.locate("brew") != nil else { return nil }
        let r = Shell.run(["brew", "info", "--json=v2", "--installed"], timeout: 120)
        guard r.ok, let data = r.stdout.data(using: .utf8),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        var outdated: [HomebrewReport.Package] = []
        var deprecated: [HomebrewReport.Package] = []
        for f in json["formulae"] as? [[String: Any]] ?? [] {
            let name = f["name"] as? String ?? "?"
            let installed = ((f["installed"] as? [[String: Any]])?.first?["version"] as? String) ?? ""
            let latest = (f["versions"] as? [String: Any])?["stable"] as? String
            if f["outdated"] as? Bool == true {
                outdated.append(.init(name: name, installed: installed, latest: latest))
            }
            if f["deprecated"] as? Bool == true || f["disabled"] as? Bool == true {
                let reason = (f["deprecation_reason"] as? String) ?? (f["disable_reason"] as? String)
                let date = (f["deprecation_date"] as? String) ?? (f["disable_date"] as? String)
                deprecated.append(.init(name: name, installed: installed, latest: latest, reason: reason, date: date))
            }
        }
        for c in json["casks"] as? [[String: Any]] ?? [] where c["outdated"] as? Bool == true {
            outdated.append(.init(name: c["token"] as? String ?? "?", installed: c["installed"] as? String ?? "",
                                  latest: c["version"] as? String))
        }
        var report = HomebrewReport(outdated: outdated, deprecated: deprecated)
        for d in deprecated {
            report.issues.append(.init(level: .warning, text: "\(d.name) is deprecated in Homebrew: \(explain(reason: d.reason))."))
        }
        if !outdated.isEmpty {
            report.issues.append(.init(level: .info, text: "\(outdated.count) package\(outdated.count == 1 ? " has" : "s have") newer versions."))
            report.steps.append(RuntimeStep(kind: .upgrade, title: "Upgrade \(outdated.count) outdated package\(outdated.count == 1 ? "" : "s")",
                detail: "Runs brew upgrade. Services like PostgreSQL restart on their new patch version.",
                commands: ["brew upgrade"]))
        }
        let removable = deprecated.filter { !$0.name.hasPrefix("postgresql") }
        if !removable.isEmpty {
            report.steps.append(RuntimeStep(kind: .remove, title: "Remove deprecated packages",
                detail: "Homebrew no longer maintains these. If another package depends on one, Homebrew will say so and stop.",
                commands: ["brew uninstall " + removable.map(\.name).joined(separator: " "), "brew autoremove"]))
        }
        return report
    }

    public static func explain(reason: String?) -> String {
        switch reason {
        case "repo_archived": return "its project was archived"
        case "unsupported": return "no longer supported upstream"
        case "versioned_formula": return "an older versioned copy Homebrew stopped updating"
        case "does_not_build": return "it no longer builds"
        case "no_license", "no_longer_meets_criteria": return "it no longer meets Homebrew's rules"
        case "deprecated_upstream": return "deprecated by its authors"
        case let r?: return r.replacingOccurrences(of: "_", with: " ")
        case nil: return "no reason given"
        }
    }

    // MARK: - macOS

    static func macOSReport(_ cycles: [ReleaseCycle], _ fetched: Date?) -> RuntimeReport {
        let v = ProcessInfo.processInfo.operatingSystemVersion
        let version = "\(v.majorVersion).\(v.minorVersion)" + (v.patchVersion > 0 ? ".\(v.patchVersion)" : "")
        var i = Installation(version: version, cycle: String(v.majorVersion), path: "/", source: .apple, sourceDetail: "macOS")
        i.isDefault = true
        var issues: [RuntimeIssue] = []
        if let c = cycles.first(where: { $0.cycle == i.cycle }) {
            i.support = c.status()
            i.releaseDate = c.releaseDate
            i.supportEnds = c.eolDate
            i.latestInCycle = c.latest
            if i.support == .endOfLife {
                issues.append(.init(level: .critical, text: "macOS \(v.majorVersion) no longer gets security updates."))
            }
            if i.patchAvailable, let latest = c.latest {
                issues.append(i.support == .endOfLife
                    ? .init(level: .warning, text: "Install macOS \(latest) at least. It's the final update for macOS \(v.majorVersion) and has security fixes you don't have (you're on \(version)).")
                    : .init(level: .warning, text: "macOS \(latest) is available with security fixes for your version (you have \(version))."))
            }
        }
        if let newest = cycles.first, newest.cycle != i.cycle {
            issues.append(.init(level: .info, text: "macOS \(newest.cycle) is the newest release. Tools like Homebrew focus on recent versions."))
        }
        let steps = [RuntimeStep(kind: .upgrade, title: "Open Software Update", detail: "Check for updates in System Settings.",
                                 commands: [#"open "x-apple.systempreferences:com.apple.Software-Update-Settings.extension""#])]
        return RuntimeReport(id: "macos", name: "macOS", installs: [i], issues: issues, steps: steps,
                             recommended: cycles.first.map { "macOS \($0.cycle)" }, dataSource: "endoflife.date/macos",
                             checkedAt: fetched, note: nil)
    }
}

/// Upgrade and removal commands, per installer.
enum Commands {
    static func q(_ s: String) -> String { "\"" + s.replacingOccurrences(of: "\"", with: "\\\"") + "\"" }
    static let nvm = #"source "${NVM_DIR:-$HOME/.nvm}/nvm.sh""#
    static let sdk = #"source "$HOME/.sdkman/bin/sdkman-init.sh""#

    static func asdfName(_ tool: String) -> String {
        ["node": "nodejs", "go": "golang"][tool] ?? tool
    }

    static func remove(_ i: Installation, tool: String) -> [String]? {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        switch i.source {
        case .homebrew: return i.sourceDetail.map { ["brew uninstall \($0)"] }
        case .nvm: return [nvm, "nvm uninstall \(i.version)"]
        case .fnm: return ["fnm uninstall \(i.version)"]
        case .asdf: return ["asdf uninstall \(asdfName(tool)) \(i.version)"]
        case .mise: return ["mise uninstall \(tool)@\(i.version)"]
        case .pyenv: return ["pyenv uninstall -f \(i.version)"]
        case .uv: return ["uv python uninstall \(i.version)"]
        case .rbenv: return ["rbenv uninstall -f \(i.version)"]
        case .rvm: return ["rvm remove \(i.version)"]
        case .sdkman: return i.sourceDetail.map { [sdk, "sdk uninstall java \($0)"] }
        case .volta: return ["mv \(q(i.path)) ~/.Trash/"]
        case .jdkFolder:
            return i.path.hasPrefix(home + "/") ? ["mv \(q(i.path)) ~/.Trash/"] : ["sudo mv \(q(i.path)) ~/.Trash/"]
        case .pythonOrg:
            let mm = i.cycle
            return [
                "sudo rm -rf \(q("/Library/Frameworks/Python.framework/Versions/\(mm)"))",
                "sudo rm -rf \(q("/Applications/Python \(mm)"))",
                #"for f in /usr/local/bin/*; do [ -L "$f" ] && readlink "$f" | grep -q "Python.framework/Versions/\#(mm)/" && sudo rm "$f"; done"#,
                #"for f in ~/.zprofile ~/.bash_profile ~/.profile; do [ -f "$f" ] && grep -q "Python.framework/Versions/\#(mm)/" "$f" && cp "$f" "$f.devsweep-backup" && sed -i '' '/Python.framework\/Versions\/\#(mm)\//d' "$f"; done"#,
            ]
        case .goOrg: return ["sudo rm -rf /usr/local/go /etc/paths.d/go"]
        case .nodejsOrg:
            return ["sudo rm -rf /usr/local/bin/node /usr/local/bin/npm /usr/local/bin/npx /usr/local/lib/node_modules/npm /usr/local/include/node",
                    "sudo pkgutil --forget org.nodejs.node.pkg"]
        case .postgresApp, .ideBundled, .apple, .unknown: return nil
        }
    }

    static func install(tool: String, cycle: String, via source: InstallSource) -> [String]? {
        switch (tool, source) {
        case ("node", .nvm): return [nvm, "nvm install \(cycle)", "nvm alias default \(cycle)"]
        case ("node", .fnm): return ["fnm install \(cycle)", "fnm default \(cycle)"]
        case ("node", .volta): return ["volta install node@\(cycle)"]
        case ("node", .nodejsOrg): return ["open https://nodejs.org/en/download"]
        case ("node", _): return ["brew install node@\(cycle)", "brew unlink node 2>/dev/null; brew link --overwrite --force node@\(cycle)", "node --version"]
        case ("python", .pyenv): return ["pyenv install \(cycle)", "pyenv global \(cycle)"]
        case ("python", .uv): return ["uv python install \(cycle)"]
        case ("python", .pythonOrg): return ["open https://www.python.org/downloads/"]
        case ("python", _): return ["brew install python@\(cycle)", "python\(cycle) --version"]
        case ("java", .sdkman): return [sdk, "sdk install java \(cycle)-tem"]
        case ("java", _):
            return ["brew install openjdk@\(cycle)",
                    "sudo ln -sfn \"$(brew --prefix)/opt/openjdk@\(cycle)/libexec/openjdk.jdk\" /Library/Java/JavaVirtualMachines/openjdk-\(cycle).jdk"]
        case ("go", _): return ["brew install go", "go version"]
        case ("ruby", .rbenv): return ["rbenv install \(cycle)", "rbenv global \(cycle)"]
        case ("ruby", _): return ["brew install ruby"]
        case (_, .asdf): return ["asdf install \(asdfName(tool)) latest:\(cycle)", "asdf set -u \(asdfName(tool)) latest:\(cycle)"]
        case (_, .mise): return ["mise use -g \(tool)@\(cycle)"]
        default: return nil
        }
    }
}

/// A careful, interactive PostgreSQL major upgrade for Homebrew installs.
enum PostgresUpgrade {
    static func script(from: String, to: String) -> [String] {
        [
            "set -e",
            "FROM=\(from); TO=\(to)",
            #"BREW="$(brew --prefix)"; BIN="$BREW/opt/$TO/bin""#,
            #"BACKUPS="$HOME/DevSweep Backups"; STAMP="$(date +%Y%m%d-%H%M%S)"; mkdir -p "$BACKUPS""#,
            #"ask() { read -r -p "$1 [y/N] " a; [ "$a" = "y" ] || { echo "Stopped. Nothing else was changed."; exit 1; }; }"#,
            #"count_rows() { "$BIN/psql" -h localhost -d postgres -At -c "select datname from pg_database where not datistemplate" | while read -r db; do "$BIN/psql" -h localhost -d "$db" -At -c "select format('select %L, count(*) from %I.%I', '$db.'||schemaname||'.'||tablename, schemaname, tablename) from pg_tables where schemaname not in ('pg_catalog','information_schema')" | while read -r q; do "$BIN/psql" -h localhost -d "$db" -At -c "$q"; done; done | sort; }"#,
            #"echo "1/6  Installing $TO if needed"; brew list "$TO" >/dev/null 2>&1 || brew install "$TO""#,
            #"echo "2/6  Backing up every database from $FROM"; brew services start "$FROM" >/dev/null; sleep 2"#,
            #""$BIN/pg_dumpall" -h localhost > "$BACKUPS/$FROM-$STAMP.sql"; ls -lh "$BACKUPS/$FROM-$STAMP.sql""#,
            #"echo "3/6  Counting rows in $FROM"; count_rows > "$BACKUPS/$FROM-$STAMP.counts"; echo "$(wc -l < "$BACKUPS/$FROM-$STAMP.counts") tables counted""#,
            #"ask "Backup saved. Stop $FROM and start $TO?""#,
            #"echo "4/6  Switching servers"; brew services stop "$FROM"; brew services start "$TO"; for i in $(seq 30); do "$BIN/pg_isready" -q -h localhost && break; sleep 1; done"#,
            #"echo "5/6  Restoring into $TO (log: $BACKUPS/restore-$STAMP.log)"; "$BIN/psql" -h localhost -d postgres -q -f "$BACKUPS/$FROM-$STAMP.sql" > "$BACKUPS/restore-$STAMP.log" 2>&1 || true"#,
            #"echo "6/6  Comparing row counts"; count_rows > "$BACKUPS/$TO-$STAMP.counts""#,
            #"if ! diff "$BACKUPS/$FROM-$STAMP.counts" "$BACKUPS/$TO-$STAMP.counts"; then echo "Counts differ (shown above). $FROM is stopped but untouched. To go back: brew services stop $TO && brew services start $FROM"; exit 1; fi"#,
            #"echo "All tables match.""#,
            #"brew unlink "$FROM" 2>/dev/null || true; brew link --force --overwrite "$TO""#,
            #"ask "Remove $FROM? Its data folder stays in $BREW/var/$FROM and the backup stays in $BACKUPS.""#,
            #"brew uninstall "$FROM"; echo "Done. PostgreSQL is now $TO.""#,
        ]
    }
}
