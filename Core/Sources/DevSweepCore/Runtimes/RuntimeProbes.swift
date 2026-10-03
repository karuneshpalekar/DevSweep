import Foundation

/// PATH and a few variables as the user's interactive shell sees them.
/// A GUI app's own environment is much thinner, so ask the shell.
struct ShellEnvironment {
    var path: [String]
    var javaHome: String?

    static func load() -> ShellEnvironment {
        let shell = ProcessInfo.processInfo.environment["SHELL"].flatMap { $0.isEmpty ? nil : $0 } ?? "/bin/zsh"
        let script = #"printf '__PATH__%s\n__JAVA__%s\n' "$PATH" "$JAVA_HOME""#
        let out = Shell.run([shell, "-ilc", script], timeout: 15).stdout
        var path: [String] = []
        var javaHome: String?
        for line in out.split(separator: "\n") {
            if line.hasPrefix("__PATH__") { path = line.dropFirst(8).split(separator: ":").map(String.init) }
            if line.hasPrefix("__JAVA__"), line.count > 8 { javaHome = String(line.dropFirst(8)) }
        }
        if path.isEmpty { path = Shell.searchPaths }
        return ShellEnvironment(path: path, javaHome: javaHome)
    }

    /// Real location of the executable the shell would run for `command`.
    func resolve(_ command: String) -> String? {
        for dir in path {
            let candidate = (dir as NSString).expandingTildeInPath + "/" + command
            if FileManager.default.isExecutableFile(atPath: candidate) {
                return URL(fileURLWithPath: candidate).resolvingSymlinksInPath().path
            }
        }
        return nil
    }
}

/// Finds every installation of a tool, whoever installed it.
struct RuntimeProbe {
    let home: URL
    let env: ShellEnvironment
    let processArgs: [String]
    let fm = FileManager.default

    static let brewPrefixes = ["/opt/homebrew", "/usr/local"]

    private func glob(_ pattern: String) -> [URL] { FS.glob(pattern, home: home) }
    private func real(_ path: String) -> String { URL(fileURLWithPath: path).resolvingSymlinksInPath().path }
    private func exists(_ path: String) -> Bool { fm.fileExists(atPath: path) }

    /// Homebrew kegs: (formula, version without revision, keg folder).
    func brewKegs(_ formulaPattern: String) -> [(formula: String, version: String, dir: String)] {
        guard let regex = try? NSRegularExpression(pattern: "^\(formulaPattern)$") else { return [] }
        var out: [(String, String, String)] = []
        for prefix in Self.brewPrefixes {
            for formulaDir in FS.children(URL(fileURLWithPath: "\(prefix)/Cellar")) {
                let name = formulaDir.lastPathComponent
                guard regex.firstMatch(in: name, range: NSRange(name.startIndex..., in: name)) != nil else { continue }
                for keg in FS.children(formulaDir) where FS.isDirectory(keg) {
                    let v = keg.lastPathComponent.split(separator: "_").first.map(String.init) ?? keg.lastPathComponent
                    out.append((name, v, keg.path))
                }
            }
        }
        return out
    }

    /// Runs `<binary> <flag>` and returns the first version-looking token.
    func versionFromBinary(_ binary: String, _ flag: String = "--version") -> String? {
        guard fm.isExecutableFile(atPath: binary) else { return nil }
        let r = Shell.run([binary, flag], timeout: 10)
        let text = r.stdout + r.stderr
        guard let range = text.range(of: #"\d+(\.\d+)+"#, options: .regularExpression) else { return nil }
        return String(text[range])
    }

    /// Versions kept by version managers in "<root>/<version>" folders.
    func managed(_ patterns: [(String, InstallSource)], versionFrom: (String) -> String? = { name in
        name.range(of: #"\d+(\.\d+)+"#, options: .regularExpression).map { String(name[$0]) }
    }) -> [(version: String, dir: String, source: InstallSource)] {
        patterns.flatMap { pattern, source in
            glob(pattern).compactMap { url -> (String, String, InstallSource)? in
                let name = url.lastPathComponent
                guard FS.isDirectory(url), let v = versionFrom(name) else { return nil }
                return (v, url.path, source)
            }
        }
    }

    private func install(_ version: String, cycle: String, path: String, source: InstallSource,
                         detail: String? = nil) -> Installation {
        Installation(version: version, cycle: cycle, path: path, source: source, sourceDetail: detail)
    }

    /// Marks the install whose folder contains the executable the shell runs.
    func markDefault(_ installs: inout [Installation], command: String) {
        guard let resolved = env.resolve(command) else { return }
        let best = installs.indices
            .filter { resolved.hasPrefix(real(installs[$0].path) + "/") || resolved == real(installs[$0].path) }
            .max { installs[$0].path.count < installs[$1].path.count }
        if let best { installs[best].isDefault = true }
    }

    /// Drops installs that are the same folder reached through a symlink.
    func dedupe(_ installs: [Installation]) -> [Installation] {
        var seen = Set<String>()
        return installs.filter { seen.insert(real($0.path)).inserted }
    }

    static func major(_ v: String) -> String { Version.parts(v).first.map(String.init) ?? v }
    static func majorMinor(_ v: String) -> String { Version.parts(v).prefix(2).map(String.init).joined(separator: ".") }

    // MARK: - Node.js

    func node() -> [Installation] {
        var list = brewKegs(#"node(@\d+)?"#).map {
            install($0.version, cycle: Self.major($0.version), path: $0.dir, source: .homebrew, detail: $0.formula)
        }
        list += managed([
            ("~/.nvm/versions/node/v*", .nvm),
            ("~/Library/Application Support/fnm/node-versions/v*", .fnm),
            ("~/.local/share/fnm/node-versions/v*", .fnm),
            ("~/.volta/tools/image/node/*", .volta),
            ("~/.asdf/installs/nodejs/*", .asdf),
            ("~/.local/share/mise/installs/node/*", .mise),
        ]).map { install($0.version, cycle: Self.major($0.version), path: $0.dir, source: $0.source) }
        let official = "/usr/local/bin/node"
        if exists(official), !real(official).contains("/Cellar/"), let v = versionFromBinary(official) {
            list.append(install(v, cycle: Self.major(v), path: official, source: .nodejsOrg))
        }
        var out = dedupe(list)
        markDefault(&out, command: "node")
        return out
    }

    // MARK: - Python

    func python() -> [Installation] {
        var list = brewKegs(#"python@3\.\d+"#).map {
            install($0.version, cycle: Self.majorMinor($0.version), path: $0.dir, source: .homebrew, detail: $0.formula)
        }
        for v in glob("/Library/Frameworks/Python.framework/Versions/3.*") {
            let mm = v.lastPathComponent
            if let full = versionFromBinary(v.path + "/bin/python\(mm)") {
                list.append(install(full, cycle: mm, path: v.path, source: .pythonOrg, detail: "Python \(mm)"))
            }
        }
        list += managed([
            ("~/.pyenv/versions/*", .pyenv),
            ("~/.asdf/installs/python/*", .asdf),
            ("~/.local/share/mise/installs/python/*", .mise),
        ], versionFrom: { name in name.range(of: #"^\d+\.\d+\.\d+$"#, options: .regularExpression) != nil ? name : nil })
            .map { install($0.version, cycle: Self.majorMinor($0.version), path: $0.dir, source: $0.source) }
        list += managed([("~/.local/share/uv/python/cpython-*", .uv)])
            .map { install($0.version, cycle: Self.majorMinor($0.version), path: $0.dir, source: .uv) }
        for fw in ["/Library/Developer/CommandLineTools/Library/Frameworks/Python3.framework/Versions",
                   "/Applications/Xcode.app/Contents/Developer/Library/Frameworks/Python3.framework/Versions"] {
            for v in FS.children(URL(fileURLWithPath: fw)) where v.lastPathComponent != "Current" {
                if let full = versionFromBinary(v.path + "/bin/python3") {
                    list.append(install(full, cycle: Self.majorMinor(full), path: v.path, source: .apple,
                                        detail: fw.contains("Xcode.app") ? "Xcode" : "Command Line Tools"))
                }
            }
        }
        list += conda()
        var out = dedupe(list)
        markDefault(&out, command: "python3")
        return out
    }

    // MARK: - Java

    func java() -> [Installation] {
        func home(of dir: URL) -> URL? {
            for rel in ["Contents/Home", "libexec/openjdk.jdk/Contents/Home", ""] {
                let h = rel.isEmpty ? dir : dir.appendingPathComponent(rel)
                if exists(h.appendingPathComponent("release").path) { return h }
            }
            return nil
        }
        var candidates: [(URL, InstallSource, String?)] = []
        candidates += glob("/Library/Java/JavaVirtualMachines/*").map { ($0, .jdkFolder, $0.lastPathComponent) }
        candidates += glob("~/Library/Java/JavaVirtualMachines/*").map { ($0, .jdkFolder, $0.lastPathComponent) }
        candidates += glob("~/jdks/*").map { ($0, .jdkFolder, $0.lastPathComponent) }
        candidates += glob("~/.sdkman/candidates/java/*").filter { $0.lastPathComponent != "current" }
            .map { ($0, .sdkman, $0.lastPathComponent) }
        candidates += brewKegs(#"openjdk(@\d+)?"#).map { (URL(fileURLWithPath: $0.dir), .homebrew, $0.formula) }
        candidates += glob("/Applications/*.app/Contents/jbr").map {
            ($0, .ideBundled, $0.deletingLastPathComponent().deletingLastPathComponent().deletingPathExtension().lastPathComponent)
        }

        var list: [Installation] = []
        for (dir, source, detail) in candidates {
            guard let h = home(of: dir), let major = Detectors.javaMajor(dir) ?? Detectors.javaMajor(h),
                  let text = try? String(contentsOf: h.appendingPathComponent("release"), encoding: .utf8) else { continue }
            let full = text.split(separator: "\n").first { $0.hasPrefix("JAVA_VERSION=") }
                .map { $0.dropFirst(13).trimmingCharacters(in: CharacterSet(charactersIn: "\"")) } ?? String(major)
            list.append(install(full, cycle: String(major), path: dir.path, source: source, detail: detail))
        }
        var out = dedupe(list)
        // JAVA_HOME wins; otherwise the java on PATH; otherwise macOS's pick.
        var target = env.javaHome
        if target == nil, let onPath = env.resolve("java"), onPath != "/usr/bin/java" { target = onPath }
        if target == nil {
            target = Shell.run(["/usr/libexec/java_home"], timeout: 10).stdout.trimmingCharacters(in: .whitespacesAndNewlines)
        }
        if let t = target, !t.isEmpty {
            let r = real(t)
            if let i = out.indices.filter({ r.hasPrefix(real(out[$0].path)) }).max(by: { out[$0].path.count < out[$1].path.count }) {
                out[i].isDefault = true
            }
        }
        return out
    }

    // MARK: - PostgreSQL

    func postgres() -> [Installation] {
        var list: [Installation] = []
        for keg in brewKegs(#"postgresql(@\d+)?"#) {
            var i = install(keg.version, cycle: Self.pgCycle(keg.version), path: keg.dir, source: .homebrew, detail: keg.formula)
            let prefix = keg.dir.components(separatedBy: "/Cellar/").first ?? "/opt/homebrew"
            let dataDirs = keg.formula == "postgresql" ? ["\(prefix)/var/postgresql", "\(prefix)/var/postgres"]
                                                       : ["\(prefix)/var/\(keg.formula)"]
            if let data = dataDirs.first(where: exists) {
                i.dataPath = data
                i.dataSize = FS.allocatedSize(URL(fileURLWithPath: data))
            }
            i.isRunning = processArgs.contains { a in
                a.contains("postgres") && (a.contains("/\(keg.formula)/") || (i.dataPath.map { a.contains($0) } ?? false))
            }
            list.append(i)
        }
        for v in glob("/Applications/Postgres.app/Contents/Versions/*") where v.lastPathComponent != "latest" {
            guard let full = versionFromBinary(v.path + "/bin/postgres") else { continue }
            var i = install(full, cycle: Self.pgCycle(full), path: v.path, source: .postgresApp, detail: "Postgres.app")
            let data = home.appendingPathComponent("Library/Application Support/Postgres/var-\(Self.pgCycle(full))").path
            if exists(data) { i.dataPath = data; i.dataSize = FS.allocatedSize(URL(fileURLWithPath: data)) }
            i.isRunning = processArgs.contains { $0.contains(v.path) }
            list.append(i)
        }
        var out = dedupe(list)
        markDefault(&out, command: "psql")
        return out
    }

    static func pgCycle(_ v: String) -> String {
        let p = Version.parts(v)
        guard let first = p.first else { return v }
        return first >= 10 ? String(first) : p.prefix(2).map(String.init).joined(separator: ".")
    }

    // MARK: - Go

    func go() -> [Installation] {
        var list = brewKegs(#"go(@\d+\.\d+)?"#).map {
            install($0.version, cycle: Self.majorMinor($0.version), path: $0.dir, source: .homebrew, detail: $0.formula)
        }
        let goVersion: (URL) -> String? = { dir in
            (try? String(contentsOf: dir.appendingPathComponent("VERSION"), encoding: .utf8))?
                .split(separator: "\n").first.map { String($0.dropFirst(2)) }
        }
        if let v = goVersion(URL(fileURLWithPath: "/usr/local/go")) {
            list.append(install(v, cycle: Self.majorMinor(v), path: "/usr/local/go", source: .goOrg))
        }
        for dir in glob("~/sdk/go1.*") {
            if let v = goVersion(dir) { list.append(install(v, cycle: Self.majorMinor(v), path: dir.path, source: .unknown, detail: "golang.org/dl")) }
        }
        list += managed([("~/.asdf/installs/golang/*", .asdf), ("~/.local/share/mise/installs/go/*", .mise)])
            .map { install($0.version, cycle: Self.majorMinor($0.version), path: $0.dir, source: $0.source) }
        var out = dedupe(list)
        markDefault(&out, command: "go")
        return out
    }

    // MARK: - Ruby

    func ruby() -> [Installation] {
        var list = brewKegs(#"ruby(@\d+\.\d+)?"#).map {
            install($0.version, cycle: Self.majorMinor($0.version), path: $0.dir, source: .homebrew, detail: $0.formula)
        }
        list += managed([
            ("~/.rbenv/versions/*", .rbenv),
            ("~/.rvm/rubies/ruby-*", .rvm),
            ("~/.asdf/installs/ruby/*", .asdf),
            ("~/.local/share/mise/installs/ruby/*", .mise),
        ]).map { install($0.version, cycle: Self.majorMinor($0.version), path: $0.dir, source: $0.source) }
        for v in glob("/System/Library/Frameworks/Ruby.framework/Versions/*") where v.lastPathComponent != "Current" {
            if let full = versionFromBinary("/usr/bin/ruby", "-v") {
                list.append(install(full, cycle: Self.majorMinor(full), path: v.path, source: .apple, detail: "macOS"))
            }
        }
        var out = dedupe(list)
        markDefault(&out, command: "ruby")
        return out
    }
}

// MARK: - More tools (v0.4)

extension RuntimeProbe {
    func php() -> [Installation] {
        var list = brewKegs(#"php(@\d+\.\d+)?"#).map {
            Installation(version: $0.version, cycle: Self.majorMinor($0.version), path: $0.dir, source: .homebrew, sourceDetail: $0.formula)
        }
        list += managed([("~/.asdf/installs/php/*", .asdf), ("~/.local/share/mise/installs/php/*", .mise)])
            .map { Installation(version: $0.version, cycle: Self.majorMinor($0.version), path: $0.dir, source: $0.source) }
        var out = dedupe(list)
        markDefault(&out, command: "php")
        return out
    }

    /// rustup toolchains are named "stable-aarch64-apple-darwin", "1.75.0-…" or "nightly-…".
    func rust() -> [Installation] {
        var list: [Installation] = []
        for dir in FS.children(home.appendingPathComponent(".rustup/toolchains")) where FS.isDirectory(dir) {
            let name = dir.lastPathComponent
            guard let v = versionFromBinary(dir.path + "/bin/rustc") else { continue }
            list.append(Installation(version: v, cycle: Self.majorMinor(v), path: dir.path, source: .rustup, sourceDetail: name))
        }
        list += brewKegs("rust").map {
            Installation(version: $0.version, cycle: Self.majorMinor($0.version), path: $0.dir, source: .homebrew, sourceDetail: $0.formula)
        }
        var out = dedupe(list)
        markDefault(&out, command: "rustc")
        // rustup's proxy in ~/.cargo/bin hides which toolchain runs; ask it.
        if !out.contains(where: \.isDefault), Shell.locate("rustup") != nil || FileManager.default.fileExists(atPath: home.path + "/.cargo/bin/rustup") {
            let rustup = FileManager.default.fileExists(atPath: home.path + "/.cargo/bin/rustup") ? home.path + "/.cargo/bin/rustup" : "rustup"
            let active = Shell.run([rustup, "show", "active-toolchain"], timeout: 10).stdout.split(separator: " ").first.map(String.init) ?? ""
            if let i = out.firstIndex(where: { $0.sourceDetail == active }) { out[i].isDefault = true }
        }
        return out
    }

    /// One entry per installed .NET SDK.
    func dotnet() -> [Installation] {
        var list: [Installation] = []
        let roots: [(String, InstallSource)] = [("/usr/local/share/dotnet", .dotnetInstaller), (home.path + "/.dotnet", .sdkFolder)]
            + brewKegs("dotnet").map { ($0.dir + "/libexec", .homebrew) }
        for (root, source) in roots {
            for sdk in FS.children(URL(fileURLWithPath: root + "/sdk")) where FS.isDirectory(sdk) {
                let v = sdk.lastPathComponent
                guard v.first?.isNumber == true else { continue }
                list.append(Installation(version: v, cycle: Self.major(v), path: sdk.path, source: source, sourceDetail: ".NET SDK"))
            }
        }
        var out = dedupe(list)
        // dotnet picks the newest SDK unless a project's global.json says otherwise.
        if let resolved = env.resolve("dotnet") {
            let root = URL(fileURLWithPath: resolved).deletingLastPathComponent().path
            if let newest = out.indices.filter({ out[$0].path.hasPrefix(root) })
                .max(by: { Version.less(out[$0].version, out[$1].version) }) { out[newest].isDefault = true }
        }
        return out
    }

    func flutter() -> [Installation] {
        func version(_ root: URL) -> String? {
            if let data = try? Data(contentsOf: root.appendingPathComponent("bin/cache/flutter.version.json")),
               let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
               let v = obj["frameworkVersion"] as? String { return v }
            return (try? String(contentsOf: root.appendingPathComponent("version"), encoding: .utf8))?
                .trimmingCharacters(in: .whitespacesAndNewlines)
        }
        var list: [Installation] = []
        for path in ["~/flutter", "~/development/flutter", "~/dev/flutter", "~/sdk/flutter", "/opt/homebrew/Caskroom/flutter/*/flutter"] {
            for root in FS.glob(path, home: home) {
                if let v = version(root) {
                    let source: InstallSource = root.path.contains("/Caskroom/") ? .homebrew : .sdkFolder
                    list.append(Installation(version: v, cycle: Self.majorMinor(v), path: root.path, source: source, sourceDetail: "Flutter SDK"))
                }
            }
        }
        for root in FS.glob("~/fvm/versions/*", home: home) {
            if let v = version(root) {
                list.append(Installation(version: v, cycle: Self.majorMinor(v), path: root.path, source: .fvm, sourceDetail: root.lastPathComponent))
            }
        }
        var out = dedupe(list)
        markDefault(&out, command: "flutter")
        return out
    }

    func deno() -> [Installation] {
        var list = brewKegs("deno").map {
            Installation(version: $0.version, cycle: Self.majorMinor($0.version), path: $0.dir, source: .homebrew, sourceDetail: $0.formula)
        }
        let own = home.path + "/.deno"
        if let v = versionFromBinary(own + "/bin/deno") {
            list.append(Installation(version: v, cycle: Self.majorMinor(v), path: own, source: .denoInstaller))
        }
        var out = dedupe(list)
        markDefault(&out, command: "deno")
        return out
    }

    func bun() -> [Installation] {
        var list = brewKegs("bun").map {
            Installation(version: $0.version, cycle: Self.major($0.version), path: $0.dir, source: .homebrew, sourceDetail: $0.formula)
        }
        let own = home.path + "/.bun"
        if let v = versionFromBinary(own + "/bin/bun") {
            list.append(Installation(version: v, cycle: Self.major(v), path: own, source: .bunInstaller))
        }
        var out = dedupe(list)
        markDefault(&out, command: "bun")
        return out
    }

    /// conda's base Python and each environment's Python.
    func conda() -> [Installation] {
        var list: [Installation] = []
        let roots = ["~/miniconda3", "~/anaconda3", "~/miniforge3", "~/mambaforge", "/opt/miniconda3", "/opt/anaconda3",
                     "/opt/homebrew/Caskroom/miniconda/base", "/opt/homebrew/Caskroom/miniforge/base"]
        for root in roots.flatMap({ FS.glob($0, home: home) }) {
            if let v = versionFromBinary(root.path + "/bin/python3") {
                list.append(Installation(version: v, cycle: Self.majorMinor(v), path: root.path, source: .conda, sourceDetail: "base"))
            }
            for envDir in FS.children(root.appendingPathComponent("envs")) where FS.isDirectory(envDir) {
                if let v = versionFromBinary(envDir.path + "/bin/python3") {
                    list.append(Installation(version: v, cycle: Self.majorMinor(v), path: envDir.path, source: .conda,
                                             sourceDetail: "env \(envDir.lastPathComponent)"))
                }
            }
        }
        return dedupe(list)
    }
}
