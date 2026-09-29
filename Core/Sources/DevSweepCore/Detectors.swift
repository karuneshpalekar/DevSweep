import Foundation

enum Detectors {
    static func run(_ rule: Rule, _ ctx: ScanContext) -> [Candidate] {
        switch rule.detector.kind {
        case .paths: return paths(rule, ctx)
        case .versionedSiblings: return versionedSiblings(rule, ctx)
        case .orphanedAppData: return orphanedAppData(rule, ctx)
        case .orphanedLaunchServices: return orphanedLaunchServices(rule, ctx)
        case .androidSystemImages: return androidSystemImages(rule, ctx)
        case .simulatorRuntimes: return simulatorRuntimes(rule, ctx)
        case .unavailableSimulators: return unavailableSimulators(rule, ctx)
        case .editorExtensions: return editorExtensions(rule, ctx)
        case .oldJDKs: return oldJDKs(rule, ctx)
        case .staleProjectArtifacts: return staleProjectArtifacts(rule, ctx)
        }
    }

    // MARK: - paths

    static func paths(_ rule: Rule, _ ctx: ScanContext) -> [Candidate] {
        var seen = Set<String>()
        let urls = (rule.detector.paths ?? [])
            .flatMap { FS.glob($0, home: ctx.home) }
            .filter { seen.insert($0.path).inserted }
        guard !urls.isEmpty else { return [] }
        if rule.detector.groupEach == true {
            return urls.map { url in
                Candidate(key: url.lastPathComponent, paths: [url], facts: ["name": url.lastPathComponent])
            }
        }
        return [Candidate(key: "all", paths: urls)]
    }

    // MARK: - versioned siblings

    static func versionedSiblings(_ rule: Rule, _ ctx: ScanContext) -> [Candidate] {
        let spec = rule.detector
        guard let pattern = spec.pattern,
              let regex = try? NSRegularExpression(pattern: pattern) else { return [] }
        let keep = spec.keep ?? 1
        let hasKeyGroup = pattern.contains("?<key>")

        struct Entry { var url: URL; var key: String; var version: String }
        var entries: [Entry] = []
        for parentPattern in spec.parents ?? [] {
            for parent in FS.glob(parentPattern, home: ctx.home) {
                for child in FS.children(parent) {
                    let name = child.lastPathComponent
                    let range = NSRange(name.startIndex..., in: name)
                    guard let m = regex.firstMatch(in: name, range: range),
                          let vr = Range(m.range(withName: "version"), in: name) else { continue }
                    var key = ""
                    if hasKeyGroup, let kr = Range(m.range(withName: "key"), in: name) { key = String(name[kr]) }
                    entries.append(Entry(url: child, key: key, version: String(name[vr])))
                }
            }
        }

        var out: [Candidate] = []
        for (key, group) in Dictionary(grouping: entries, by: { $0.key }).sorted(by: { $0.key < $1.key }) {
            let versions = Array(Set(group.map(\.version))).sorted { Version.less($1, $0) }
            let kept = Array(versions.prefix(keep))
            let old = versions.filter { !kept.contains($0) }
            guard !old.isEmpty else { continue }

            var baseFacts = ["key": key, "kept": ListFormat.join(kept), "newest": kept.first ?? ""]
            var checks: [Check] = []
            if !kept.isEmpty {
                checks.append(.passed("\(key.isEmpty ? "" : key + " ")\(ListFormat.join(kept)) stay\(kept.count == 1 ? "s" : "") installed"))
            }

            if spec.groupEach == true {
                for v in old {
                    var facts = baseFacts
                    facts["version"] = v
                    let urls = group.filter { $0.version == v }.map(\.url)
                    out.append(Candidate(key: "\(key)\(v)", paths: urls, facts: facts, checks: checks))
                }
            } else {
                baseFacts["versions"] = ListFormat.join(old.sorted(by: Version.less))
                let urls = group.filter { old.contains($0.version) }.map(\.url)
                out.append(Candidate(key: key.isEmpty ? "old" : key, paths: urls,
                                     subtitle: "Versions \(baseFacts["versions"]!)",
                                     facts: baseFacts, checks: checks))
            }
        }
        return out
    }

    // MARK: - orphaned app data

    static let libraryDirs = [
        "Library/Caches", "Library/Application Support", "Library/Preferences", "Library/HTTPStorages",
        "Library/Saved Application State", "Library/WebKit", "Library/Containers", "Library/Group Containers",
        "Library/Logs", "Library/LaunchAgents", "Library/Cookies",
    ]

    /// "TEAMID.com.foo.bar", "group.com.foo", "com.foo.bar.plist" -> "com.foo.bar"
    static func bundleID(fromName name: String) -> String? {
        var s = name
        for ext in [".plist", ".savedState", ".binarycookies"] where s.hasSuffix(ext) {
            s = String(s.dropLast(ext.count))
        }
        if s.hasPrefix("group.") { s = String(s.dropFirst(6)) }
        if let r = s.range(of: #"^[A-Z0-9]{10}\.?"#, options: .regularExpression), s.count > 12 {
            s = String(s[r.upperBound...])
        }
        guard s.range(of: #"^[A-Za-z][A-Za-z0-9-]*(\.[A-Za-z0-9_-]+){2,}$"#, options: .regularExpression) != nil else { return nil }
        return s
    }

    static func orphanedAppData(_ rule: Rule, _ ctx: ScanContext) -> [Candidate] {
        let known = rule.detector.knownApps ?? []
        let shared = (rule.detector.sharedIDs ?? []).map { $0.lowercased() }

        struct Group { var name: String; var urls: [URL] = []; var ids = Set<String>(); var note: String? }
        var groups: [String: Group] = [:]

        func knownApp(for id: String) -> KnownApp? {
            known.first { k in (k.idPrefixes ?? []).contains { id.lowercased().hasPrefix($0.lowercased()) } }
        }
        func isInstalled(_ k: KnownApp) -> Bool { k.bundleIDs.contains(where: ctx.isInstalled) }

        for dir in libraryDirs {
            for child in FS.children(ctx.home.appendingPathComponent(dir)) {
                guard let id = bundleID(fromName: child.lastPathComponent) else { continue }
                let lower = id.lowercased()
                if lower.hasPrefix("com.apple.") || lower.hasPrefix("apple.") { continue }
                if shared.contains(where: { lower.hasPrefix($0) }) { continue }
                if ctx.isInstalled(id) { continue }
                let key: String
                let name: String
                var note: String?
                if let k = knownApp(for: id) {
                    if isInstalled(k) { continue }
                    key = k.name; name = k.name; note = k.note
                } else {
                    let comps = id.split(separator: ".").prefix(3).joined(separator: ".")
                    key = comps.lowercased(); name = comps
                }
                groups[key, default: Group(name: name, note: note)].urls.append(child)
                groups[key]?.ids.insert(id)
            }
        }
        for k in known where !isInstalled(k) {
            for p in k.paths ?? [] {
                for url in FS.glob(p, home: ctx.home) {
                    groups[k.name, default: Group(name: k.name, note: k.note)].urls.append(url)
                }
            }
        }

        return groups.sorted(by: { $0.key < $1.key }).map { key, g in
            let ids = g.ids.sorted()
            var checks: [Check] = []
            if !ids.isEmpty {
                let shown = ids.prefix(2).joined(separator: ", ") + (ids.count > 2 ? " and \(ids.count - 2) more" : "")
                checks.append(.passed("No installed app uses \(shown)"))
            } else {
                checks.append(.passed("\(g.name) isn't installed"))
            }
            if g.urls.contains(where: { $0.path.contains("/Library/LaunchAgents/") }) {
                checks.append(.passed("Its background service is stopped before removal"))
            }
            if g.urls.contains(where: { $0.path.contains("/Library/Containers/") }) {
                checks.append(.warning("macOS protects sandbox folders. If one can't be moved, drag it to the Trash in Finder."))
            }
            if let note = g.note { checks.append(.warning(note)) }
            return Candidate(
                key: key, paths: g.urls, title: g.name,
                subtitle: "\(g.urls.count) place\(g.urls.count == 1 ? "" : "s") · app not installed",
                facts: ["app": g.name, "places": "\(g.urls.count) place\(g.urls.count == 1 ? "" : "s")"],
                checks: checks
            )
        }
    }

    // MARK: - launchd jobs pointing at missing programs

    static func orphanedLaunchServices(_ rule: Rule, _ ctx: ScanContext) -> [Candidate] {
        var out: [Candidate] = []
        for dirPath in rule.detector.paths ?? [] {
            let dir = FS.expand(dirPath, home: ctx.home)
            let inHome = dir.path.hasPrefix(ctx.home.path + "/")
            for plist in FS.children(dir) where plist.pathExtension == "plist" {
                guard let data = try? Data(contentsOf: plist),
                      let dict = try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any]
                else { continue }
                let label = dict["Label"] as? String ?? plist.deletingPathExtension().lastPathComponent
                if label.hasPrefix("com.apple.") { continue }
                let program = (dict["Program"] as? String) ?? (dict["ProgramArguments"] as? [String])?.first
                guard let program, program.hasPrefix("/"),
                      !FS.exists(URL(fileURLWithPath: program)) else { continue }

                var c = Candidate(
                    key: label, paths: inHome ? [plist] : [],
                    title: "Background service \(label)",
                    subtitle: FS.abbreviate(plist.path, home: ctx.home),
                    size: inHome ? nil : 0,
                    facts: ["label": label, "program": program, "plist": plist.path],
                    checks: [.passed("\(program) no longer exists")]
                )
                if !inHome {
                    let domain = dir.lastPathComponent == "LaunchDaemons" ? "system" : "gui/$(id -u)"
                    c.risk = .needsAdmin
                    c.actions = [CleanAction(
                        kind: .manual, label: "Copy commands",
                        command: ["sudo launchctl bootout \(domain) \"\(plist.path)\"",
                                  "sudo mv \"\(plist.path)\" ~/.Trash/"],
                        detail: "Needs your admin password. Paste these into Terminal."
                    )]
                    c.checks.append(.warning("It's outside your home folder, so you run the commands yourself."))
                }
                out.append(c)
            }
        }
        return out
    }

    // MARK: - Android system images

    static func androidSystemImages(_ rule: Rule, _ ctx: ScanContext) -> [Candidate] {
        let env = ProcessInfo.processInfo.environment
        var roots: [URL] = [env["ANDROID_HOME"], env["ANDROID_SDK_ROOT"]].compactMap { $0 }.map { URL(fileURLWithPath: $0) }
        roots.append(ctx.home.appendingPathComponent("Library/Android/sdk"))
        var seenRoots = Set<String>()
        roots = roots.filter { FS.isDirectory($0) && seenRoots.insert($0.standardizedFileURL.path).inserted }

        let avdDir = env["ANDROID_AVD_HOME"].map { URL(fileURLWithPath: $0) }
            ?? ctx.home.appendingPathComponent(".android/avd")
        var used: [String: [String]] = [:]  // "android-33/google_apis/arm64-v8a" -> AVD names
        for avd in FS.children(avdDir) where avd.pathExtension == "avd" {
            guard let text = try? String(contentsOf: avd.appendingPathComponent("config.ini"), encoding: .utf8) else { continue }
            for line in text.split(separator: "\n") where line.hasPrefix("image.sysdir.1") {
                var v = line.split(separator: "=", maxSplits: 1).last.map(String.init)?.trimmingCharacters(in: .whitespaces) ?? ""
                if v.hasPrefix("system-images/") { v = String(v.dropFirst("system-images/".count)) }
                while v.hasSuffix("/") { v.removeLast() }
                used[v, default: []].append(avd.deletingPathExtension().lastPathComponent)
            }
        }
        let usedLevels = Set(used.keys.compactMap { $0.split(separator: "/").first.map(String.init) })
        let avdNames = used.values.flatMap { $0 }.sorted()

        var out: [Candidate] = []
        for root in roots {
            for level in FS.children(root.appendingPathComponent("system-images")) where FS.isDirectory(level) {
                var unused: [URL] = []
                var anyUsed = false
                for tag in FS.children(level) where FS.isDirectory(tag) {
                    for abi in FS.children(tag) where FS.isDirectory(abi) {
                        let key = "\(level.lastPathComponent)/\(tag.lastPathComponent)/\(abi.lastPathComponent)"
                        if used[key] != nil { anyUsed = true } else { unused.append(abi) }
                    }
                }
                let levelName = level.lastPathComponent
                let api = levelName.replacingOccurrences(of: "android-", with: "API ")
                let paths = anyUsed ? unused : [level]
                guard !paths.isEmpty else { continue }
                var checks: [Check] = [.passed("No emulator uses \(api)")]
                if !avdNames.isEmpty {
                    let levels = usedLevels.sorted().map { $0.replacingOccurrences(of: "android-", with: "API ") }
                    checks.append(.passed("Your emulators (\(ListFormat.join(avdNames))) use \(ListFormat.join(levels))"))
                } else {
                    checks.append(.passed("You have no emulators set up"))
                }
                out.append(Candidate(key: levelName, paths: paths, facts: ["level": api], checks: checks))
            }
        }
        return out
    }

    // MARK: - Simulators

    static func simulatorRuntimes(_ rule: Rule, _ ctx: ScanContext) -> [Candidate] {
        guard Shell.locate("xcrun") != nil else { return [] }
        let res = Shell.run(["xcrun", "simctl", "runtime", "list", "-j"], timeout: 30)
        guard res.ok, let data = res.stdout.data(using: .utf8),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: [String: Any]] else { return [] }

        let devicesRes = Shell.run(["xcrun", "simctl", "list", "devices", "-j"], timeout: 30)
        var deviceCounts: [String: Int] = [:]
        if let d = devicesRes.stdout.data(using: .utf8),
           let obj = try? JSONSerialization.jsonObject(with: d) as? [String: Any],
           let devices = obj["devices"] as? [String: [Any]] {
            for (runtime, list) in devices { deviceCounts[runtime] = list.count }
        }

        struct RT { var id: String; var platform: String; var version: String; var runtimeID: String; var size: Int64; var lastUsed: Date? }
        let iso = ISO8601DateFormatter()
        let runtimes: [RT] = json.values.compactMap { r in
            guard let id = r["identifier"] as? String, let version = r["version"] as? String else { return nil }
            let runtimeID = r["runtimeIdentifier"] as? String ?? ""
            let platform = runtimeID.components(separatedBy: "SimRuntime.").last?.components(separatedBy: "-").first ?? "Simulator"
            let size = (r["sizeBytes"] as? NSNumber)?.int64Value ?? 0
            let lastUsed = (r["lastUsedAt"] as? String).flatMap { iso.date(from: $0) }
            return RT(id: id, platform: platform, version: version, runtimeID: runtimeID, size: size, lastUsed: lastUsed)
        }

        let unusedDays = rule.detector.unusedDays ?? 90
        var out: [Candidate] = []
        for (platform, list) in Dictionary(grouping: runtimes, by: \.platform) {
            let newest = list.max { Version.less($0.version, $1.version) }
            for rt in list {
                let superseded = rt.id != newest?.id
                let idleDays = rt.lastUsed.map { Int(Date().timeIntervalSince($0) / 86_400) }
                let idle = (idleDays ?? 0) >= unusedDays
                guard superseded || idle else { continue }
                var checks: [Check] = []
                if superseded, let n = newest { checks.append(.passed("\(platform) \(n.version) stays installed")) }
                if let idleDays, idle { checks.append(.passed("Last used \(idleDays) days ago")) }
                let devices = deviceCounts[rt.runtimeID] ?? 0
                if devices > 0 { checks.append(.warning("\(devices) simulator\(devices == 1 ? "" : "s") on this runtime will be removed with it")) }
                out.append(Candidate(
                    key: rt.id, paths: [], title: "\(platform) \(rt.version) simulator runtime",
                    subtitle: superseded ? "Newer \(platform) runtime installed" : "Not used in \(idleDays ?? 0) days",
                    size: rt.size,
                    facts: ["platform": platform, "version": rt.version, "runtimeID": rt.id],
                    checks: checks
                ))
            }
        }
        return out
    }

    static func unavailableSimulators(_ rule: Rule, _ ctx: ScanContext) -> [Candidate] {
        guard Shell.locate("xcrun") != nil else { return [] }
        let res = Shell.run(["xcrun", "simctl", "list", "devices", "unavailable", "-j"], timeout: 30)
        guard res.ok, let data = res.stdout.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let devices = obj["devices"] as? [String: [[String: Any]]] else { return [] }
        let udids = devices.values.flatMap { $0 }.compactMap { $0["udid"] as? String }
        guard !udids.isEmpty else { return [] }
        let base = ctx.home.appendingPathComponent("Library/Developer/CoreSimulator/Devices")
        return [Candidate(
            key: "unavailable", paths: udids.map { base.appendingPathComponent($0) },
            title: "\(udids.count) unavailable simulator\(udids.count == 1 ? "" : "s")",
            subtitle: "Their runtime is no longer installed",
            facts: ["devices": String(udids.count)],
            checks: [.passed("Xcode marks them unavailable, so they can't run")]
        )]
    }

    // MARK: - Editor extensions

    static func editorExtensions(_ rule: Rule, _ ctx: ScanContext) -> [Candidate] {
        var out: [Candidate] = []
        for rootPath in rule.detector.paths ?? [] {
            let root = FS.expand(rootPath, home: ctx.home)
            guard let data = try? Data(contentsOf: root.appendingPathComponent("extensions.json")),
                  let list = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { continue }
            var referenced = Set<String>()
            for e in list {
                if let rel = e["relativeLocation"] as? String { referenced.insert(rel) }
                if let loc = e["location"] as? [String: Any], let p = loc["path"] as? String {
                    referenced.insert(URL(fileURLWithPath: p).lastPathComponent)
                }
            }
            var obsolete = Set<String>()
            if let d = try? Data(contentsOf: root.appendingPathComponent(".obsolete")),
               let o = try? JSONSerialization.jsonObject(with: d) as? [String: Any] {
                obsolete = Set(o.keys)
            }
            let stale = FS.children(root).filter { url in
                let name = url.lastPathComponent
                return FS.isDirectory(url) && !name.hasPrefix(".")
                    && (!referenced.contains(name) || obsolete.contains(name))
            }
            guard !stale.isEmpty else { continue }
            let editor: String
            switch root.deletingLastPathComponent().lastPathComponent {
            case ".cursor": editor = "Cursor"
            case ".windsurf": editor = "Windsurf"
            case ".vscode-insiders": editor = "VS Code Insiders"
            default: editor = "VS Code"
            }
            let names = stale.map { $0.lastPathComponent }
            out.append(Candidate(
                key: editor, paths: stale, title: "Old \(editor) extension versions",
                subtitle: names.prefix(2).joined(separator: ", ") + (names.count > 2 ? " and \(names.count - 2) more" : ""),
                facts: ["editor": editor],
                checks: [.passed("Not in \(editor)'s list of installed extensions")]
            ))
        }
        return out
    }

    // MARK: - Java

    static func javaMajor(_ jdk: URL) -> Int? {
        for rel in ["Contents/Home/release", "release", "libexec/openjdk.jdk/Contents/Home/release"] {
            guard let text = try? String(contentsOf: jdk.appendingPathComponent(rel), encoding: .utf8) else { continue }
            for line in text.split(separator: "\n") where line.hasPrefix("JAVA_VERSION=") {
                let v = line.dropFirst("JAVA_VERSION=".count).trimmingCharacters(in: CharacterSet(charactersIn: "\""))
                let parts = Version.parts(v)
                guard let first = parts.first else { return nil }
                return first == 1 && parts.count > 1 ? parts[1] : first
            }
        }
        return nil
    }

    static func oldJDKs(_ rule: Rule, _ ctx: ScanContext) -> [Candidate] {
        let removable = (rule.detector.paths ?? []).flatMap { FS.glob($0, home: ctx.home) }
        let referenceOnly = ["/Library/Java/JavaVirtualMachines/*", "/opt/homebrew/opt/openjdk*", "/usr/local/opt/openjdk*",
                             "~/.sdkman/candidates/java/*"].flatMap { FS.glob($0, home: ctx.home) }
        let majors = Set((removable + referenceOnly).compactMap(javaMajor))
        let minMajor = rule.detector.minMajor ?? 11
        let javaHome = ProcessInfo.processInfo.environment["JAVA_HOME"] ?? ""

        return removable.compactMap { jdk in
            guard let major = javaMajor(jdk), major < minMajor else { return nil }
            let newer = majors.filter { $0 >= minMajor }.sorted()
            guard !newer.isEmpty else { return nil }
            var checks: [Check] = [.passed("Java \(ListFormat.join(newer.map(String.init))) \(newer.count == 1 ? "is" : "are") installed")]
            if !javaHome.isEmpty && javaHome.hasPrefix(jdk.path) {
                checks.append(.warning("JAVA_HOME points to it. Update your shell profile after removing it."))
            } else {
                checks.append(.passed("JAVA_HOME doesn't point to it"))
            }
            return Candidate(
                key: jdk.lastPathComponent, paths: [jdk],
                title: "Java \(major) (\(jdk.lastPathComponent))",
                facts: ["major": String(major), "newer": ListFormat.join(newer.map(String.init)), "name": jdk.lastPathComponent],
                checks: checks
            )
        }
    }

    // MARK: - Project artifacts

    static let manifests: [String: (files: [String], reinstall: String)] = [
        "node_modules": (["package.json"], "npm install"),
        ".venv": (["pyproject.toml", "requirements.txt", "setup.py", "Pipfile"], "recreate the virtual environment"),
        "venv": (["pyproject.toml", "requirements.txt", "setup.py", "Pipfile"], "recreate the virtual environment"),
        "Pods": (["Podfile"], "pod install"),
        ".gradle": (["settings.gradle", "settings.gradle.kts", "build.gradle", "build.gradle.kts"], "the next Gradle build"),
    ]

    static func staleProjectArtifacts(_ rule: Rule, _ ctx: ScanContext) -> [Candidate] {
        let names = Set(rule.detector.names ?? Array(manifests.keys))
        let staleDays = rule.detector.staleDays ?? 30
        let cutoff = Date().addingTimeInterval(-Double(staleDays) * 86_400)
        let skip: Set<String> = [".git", "Library", ".Trash", "Applications"]
        var out: [Candidate] = []

        func walk(_ dir: URL, depth: Int) {
            guard depth <= 4 else { return }
            for child in FS.children(dir) where FS.isDirectory(child) {
                let name = child.lastPathComponent
                if names.contains(name), let m = manifests[name] {
                    if let c = artifact(child, manifest: m, cutoff: cutoff) { out.append(c) }
                    continue
                }
                if skip.contains(name) || name.hasPrefix(".") || child.pathExtension == "app" { continue }
                walk(child, depth: depth + 1)
            }
        }

        func artifact(_ url: URL, manifest m: (files: [String], reinstall: String), cutoff: Date) -> Candidate? {
            let project = url.deletingLastPathComponent()
            guard let found = m.files.first(where: { FS.exists(project.appendingPathComponent($0)) }) else { return nil }
            let newest = FS.children(project)
                .filter { !manifests.keys.contains($0.lastPathComponent) && $0.lastPathComponent != ".git" && $0.lastPathComponent != ".DS_Store" }
                .compactMap(FS.modificationDate).max() ?? .distantPast
            guard newest < cutoff else { return nil }
            let days = Int(Date().timeIntervalSince(newest) / 86_400)
            return Candidate(
                key: url.path, paths: [url],
                title: "\(url.lastPathComponent) in \(project.lastPathComponent)",
                facts: ["project": project.lastPathComponent, "days": String(days), "reinstall": m.reinstall, "manifest": found],
                checks: [.passed("\(found) is here, so \(m.reinstall) brings it back"),
                         .passed("Project untouched for \(days) days")]
            )
        }

        for rootPath in rule.detector.roots ?? [] {
            let root = FS.expand(rootPath, home: ctx.home)
            if FS.isDirectory(root) { walk(root, depth: 1) }
        }
        return out
    }
}
