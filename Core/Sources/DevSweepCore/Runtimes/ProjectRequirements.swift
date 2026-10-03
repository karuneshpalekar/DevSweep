import Foundation

/// A version a project asks for, from files like .nvmrc or go.mod.
public struct ProjectRequirement: Codable, Hashable, Sendable, Identifiable {
    public enum Status: String, Codable, Sendable {
        /// An installed version satisfies it.
        case ok
        /// No installed version satisfies it.
        case notInstalled
        /// It asks for a release line that's out of support.
        case endOfLife
    }

    public var id: String { "\(path)|\(tool)|\(file)" }
    public var project: String
    public var path: String
    /// Tool id, as in RuntimeScanner ("node", "python", …).
    public var tool: String
    public var toolName: String
    /// What the file says, e.g. ">=18" or "3.11.4".
    public var spec: String
    /// The file it came from, e.g. ".nvmrc".
    public var file: String
    public var status: Status = .ok
    public var message: String = ""
    public var fix: [String]?
}

/// Reads version requirements from projects and checks them against what's installed.
public enum ProjectRequirementScanner {
    struct Raw { var project: URL; var tool: String; var spec: String; var file: String }

    static let skip: Set<String> = ["node_modules", ".git", "Library", ".venv", "venv", "build", "dist", "Pods", ".gradle", "target"]

    /// Folders that look like projects, up to `depth` levels below each root.
    static func projects(in roots: [String], home: URL, depth: Int = 3) -> [URL] {
        var out: [URL] = []
        let markers: Set<String> = [".git", "package.json", "pyproject.toml", "go.mod", "Gemfile", "build.gradle",
                                    "build.gradle.kts", "composer.json", "Cargo.toml", ".tool-versions", ".nvmrc", ".python-version"]
        func walk(_ dir: URL, _ level: Int) {
            let names = Set((try? FileManager.default.contentsOfDirectory(atPath: dir.path)) ?? [])
            if level > 0, !names.isDisjoint(with: markers) { out.append(dir); return }
            guard level < depth else { return }
            for name in names.sorted() where !skip.contains(name) && !name.hasPrefix(".") {
                let child = dir.appendingPathComponent(name)
                if FS.isDirectory(child), child.pathExtension != "app" { walk(child, level + 1) }
            }
        }
        for u in FS.uniqueDirectories(roots, home: home) { walk(u, 0) }
        return out
    }

    /// Everything a project folder says about versions.
    static func requirements(of project: URL) -> [Raw] {
        var out: [Raw] = []
        func read(_ name: String) -> String? {
            let u = project.appendingPathComponent(name)
            guard let attrs = try? FileManager.default.attributesOfItem(atPath: u.path),
                  (attrs[.size] as? NSNumber)?.intValue ?? 0 < 256_000 else { return nil }
            return try? String(contentsOf: u, encoding: .utf8)
        }
        func firstLine(_ s: String) -> String {
            s.split(separator: "\n").first.map { $0.trimmingCharacters(in: .whitespaces) } ?? ""
        }
        func add(_ tool: String, _ spec: String, _ file: String) {
            let s = spec.trimmingCharacters(in: .whitespacesAndNewlines.union(CharacterSet(charactersIn: "\"'")))
            guard !s.isEmpty, s.rangeOfCharacter(from: .decimalDigits) != nil else { return }
            out.append(Raw(project: project, tool: tool, spec: s, file: file))
        }
        func capture(_ text: String, _ pattern: String) -> String? {
            guard let r = text.range(of: pattern, options: .regularExpression) else { return nil }
            let m = String(text[r])
            return m.range(of: #"[<>=^~]*\s*v?\d+(\.\d+)*(\.x)?"#, options: .regularExpression)
                .map { String(m[$0]).replacingOccurrences(of: " ", with: "") }
        }

        if let s = read(".nvmrc") { add("node", firstLine(s), ".nvmrc") }
        else if let s = read(".node-version") { add("node", firstLine(s), ".node-version") }
        if let s = read("package.json"), let data = s.data(using: .utf8),
           let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           let engines = obj["engines"] as? [String: Any], let n = engines["node"] as? String {
            add("node", n, "package.json")
        }
        if let s = read(".python-version") { let l = firstLine(s); if !l.contains("/") { add("python", l, ".python-version") } }
        if let s = read("runtime.txt"), firstLine(s).hasPrefix("python-") { add("python", String(firstLine(s).dropFirst(7)), "runtime.txt") }
        if let s = read("pyproject.toml") {
            if let v = capture(s, #"requires-python\s*=\s*"[^"]+""#) { add("python", v, "pyproject.toml") }
            else if let v = capture(s, #"(?m)^python\s*=\s*"[^"]+""#) { add("python", v, "pyproject.toml") }
        }
        if let s = read(".ruby-version") { add("ruby", firstLine(s).replacingOccurrences(of: "ruby-", with: ""), ".ruby-version") }
        if let s = read(".java-version") { add("java", firstLine(s), ".java-version") }
        for gradle in ["build.gradle.kts", "build.gradle", "app/build.gradle.kts", "app/build.gradle"] {
            guard let s = read(gradle) else { continue }
            if let v = capture(s, #"(jvmToolchain\(\s*\d+|JavaLanguageVersion\.of\(\s*\d+)"#) { add("java", v, gradle); break }
        }
        if let s = read("go.mod"), let v = capture(s, #"(?m)^go\s+\d+(\.\d+)*"#) { add("go", ">=" + v, "go.mod") }
        if let s = read("composer.json"), let data = s.data(using: .utf8),
           let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           let req = obj["require"] as? [String: Any], let p = req["php"] as? String {
            add("php", p.components(separatedBy: "|").first ?? p, "composer.json")
        }
        if let s = read("rust-toolchain.toml"), let v = capture(s, #"channel\s*=\s*"\d+[^"]*""#) { add("rust", v, "rust-toolchain.toml") }
        else if let s = read("rust-toolchain"), firstLine(s).first?.isNumber == true { add("rust", firstLine(s), "rust-toolchain") }
        if let s = read(".tool-versions") {
            let names = ["nodejs": "node", "python": "python", "ruby": "ruby", "golang": "go", "java": "java", "php": "php", "rust": "rust"]
            for line in s.split(separator: "\n") {
                let parts = line.split(separator: " ").map(String.init)
                if parts.count >= 2, let tool = names[parts[0]] { add(tool, parts[1], ".tool-versions") }
            }
        }
        // One requirement per tool and file; .tool-versions wins over package.json engines when both exist.
        var seen = Set<String>()
        return out.filter { seen.insert($0.tool + "|" + $0.file).inserted }
    }

    /// The parts of a spec: a minimum ("&gt;=18", "^3.10", "~1.2") or an exact release line.
    static func parse(_ spec: String, tool: String) -> (cycle: String, minimum: Bool)? {
        let isMin = spec.hasPrefix(">") || spec.hasPrefix("^") || spec.hasPrefix("~")
        let nums = Version.parts(spec.replacingOccurrences(of: "v", with: ""))
        guard let first = nums.first else { return nil }
        let cycle: String
        switch tool {
        case "node", "java", "bun", "dotnet": cycle = String(first)
        default:
            cycle = nums.count > 1 ? "\(first).\(nums[1])" : String(first)
        }
        return (cycle, isMin)
    }

    static func evaluate(_ raw: [Raw], reports: [RuntimeReport], cycles: [String: [ReleaseCycle]],
                         toolNames: [String: String], now: Date = Date()) -> [ProjectRequirement] {
        raw.compactMap { r in
            guard let (cycle, isMin) = parse(r.spec, tool: r.tool) else { return nil }
            let name = toolNames[r.tool] ?? r.tool
            var req = ProjectRequirement(project: r.project.lastPathComponent, path: r.project.path, tool: r.tool,
                                         toolName: name, spec: r.spec, file: r.file)
            let installs = reports.first { $0.id == r.tool }?.installs.filter { !$0.isSystem } ?? []
            let matching = installs.filter { isMin ? !Version.less($0.cycle, cycle) : $0.cycle == cycle }
            let wantedCycle = cycles[r.tool]?.first { $0.cycle == cycle }
            if !isMin, let c = wantedCycle, c.status(on: now) == .endOfLife {
                req.status = .endOfLife
                req.message = "\(req.project) asks for \(name) \(cycle) (\(r.file)), which is past end of life. Consider moving the project to a supported version."
            } else if matching.isEmpty {
                req.status = .notInstalled
                let have = installs.map(\.version).sorted(by: Version.less)
                req.message = "\(req.project) asks for \(name) \(r.spec) (\(r.file)), but " +
                    (have.isEmpty ? "\(name) isn't installed." : "you have \(ListFormat.join(have)).")
                let via = installs.first(where: \.isDefault)?.source ?? installs.first?.source ?? .homebrew
                req.fix = Commands.install(tool: r.tool, cycle: cycle, via: via)
            } else {
                let best = matching.first(where: \.isDefault) ?? matching.max { Version.less($0.version, $1.version) }!
                req.message = "\(req.project) asks for \(name) \(r.spec) (\(r.file)). \(best.version) from \(best.source.title) works" +
                    (best.isDefault ? " and is the one your shell runs." : ", though it isn't your shell's default.")
            }
            return req
        }
        .sorted { ($0.status == .ok ? 1 : 0, $0.project) < ($1.status == .ok ? 1 : 0, $1.project) }
    }
}
