import Foundation
import Yams

/// A rule pack entry. Rules are YAML so adding support for a new tool is a
/// data change, not a code change; the detector kinds are the only Swift.
public struct Rule: Codable, Sendable {
    public var id: String
    public var title: String
    public var category: Category
    public var risk: Risk
    public var detector: DetectorSpec
    public var explain: Explanation
    public var actions: [CleanAction]
    /// App names or process-argument substrings that must not be running.
    public var blockers: [String]?
    /// Findings smaller than this are not shown.
    public var minSizeMB: Int?
    /// Only run on this CPU architecture ("arm64" or "x86_64").
    public var arch: String?
}

public struct DetectorSpec: Codable, Sendable {
    public enum Kind: String, Codable, Sendable {
        /// Fixed paths or globs.
        case paths
        /// Versioned folders where only the newest `keep` are needed.
        case versionedSiblings
        /// Library data whose app is no longer installed.
        case orphanedAppData
        /// launchd jobs whose program no longer exists.
        case orphanedLaunchServices
        /// Android system images no emulator uses.
        case androidSystemImages
        /// Simulator runtimes that are superseded or unused.
        case simulatorRuntimes
        /// Simulators Xcode marks unavailable.
        case unavailableSimulators
        /// Editor extension folders the editor no longer loads.
        case editorExtensions
        /// Old Java versions when a newer one is installed.
        case oldJDKs
        /// node_modules / venvs in projects nobody touched in a while.
        case staleProjectArtifacts
    }

    public var kind: Kind
    public var paths: [String]?
    public var parents: [String]?
    public var pattern: String?
    public var keep: Int?
    public var groupEach: Bool?
    public var roots: [String]?
    public var names: [String]?
    public var staleDays: Int?
    public var knownApps: [KnownApp]?
    public var sharedIDs: [String]?
    public var minMajor: Int?
    public var unusedDays: Int?
}

public struct KnownApp: Codable, Sendable {
    public var name: String
    /// If any of these is installed, the app is not gone.
    public var bundleIDs: [String]
    /// Bundle-ID folders with these prefixes belong to this app.
    public var idPrefixes: [String]?
    /// Folders named after the app rather than its bundle ID.
    public var paths: [String]?
    /// Shown as a warning, e.g. "May contain collections you never synced."
    public var note: String?
}

struct RulePack: Codable {
    var rules: [Rule]
}

public enum RuleLoader {
    /// Folder users can drop extra rule packs into.
    public static var userRulesDirectory: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/DevSweep/Rules", isDirectory: true)
    }

    public static var bundledRulesDirectory: URL? {
        Bundle.module.url(forResource: "Rules", withExtension: nil)
    }

    /// Loads the bundled packs plus any user packs. A pack that fails to
    /// parse is reported and skipped rather than failing the whole scan.
    public static func loadAll() -> (rules: [Rule], errors: [String]) {
        var dirs: [URL] = []
        if let bundled = bundledRulesDirectory { dirs.append(bundled) }
        dirs.append(userRulesDirectory)

        var rules: [Rule] = []
        var errors: [String] = []
        var seen = Set<String>()
        for dir in dirs {
            let files = (try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)) ?? []
            for file in files.sorted(by: { $0.lastPathComponent < $1.lastPathComponent })
            where ["yml", "yaml"].contains(file.pathExtension) {
                do {
                    for rule in try load(file) {
                        // A user pack can override a bundled rule by reusing its id.
                        if seen.contains(rule.id) { rules.removeAll { $0.id == rule.id } }
                        seen.insert(rule.id)
                        rules.append(rule)
                    }
                } catch {
                    errors.append("\(file.lastPathComponent): \(error)")
                }
            }
        }
        return (rules, errors)
    }

    public static func load(_ url: URL) throws -> [Rule] {
        let text = try String(contentsOf: url, encoding: .utf8)
        return try YAMLDecoder().decode(RulePack.self, from: text).rules
    }
}

enum Template {
    /// Replaces `{{key}}` with facts[key]; unknown keys become empty.
    static func render(_ text: String, _ facts: [String: String]) -> String {
        var out = ""
        var rest = Substring(text)
        while let open = rest.range(of: "{{") {
            out += rest[..<open.lowerBound]
            guard let close = rest[open.upperBound...].range(of: "}}") else {
                out += rest[open.lowerBound...]
                return out
            }
            let key = rest[open.upperBound..<close.lowerBound].trimmingCharacters(in: .whitespaces)
            out += facts[key] ?? ""
            rest = rest[close.upperBound...]
        }
        out += rest
        return out
    }
}
