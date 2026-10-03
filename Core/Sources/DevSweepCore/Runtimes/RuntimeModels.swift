import Foundation

/// How well a version is still looked after upstream.
public enum SupportStatus: String, Codable, Sendable, Comparable {
    case endOfLife
    case endingSoon
    case supported
    case unknown

    public var title: String {
        switch self {
        case .endOfLife: return "End of life"
        case .endingSoon: return "Ending soon"
        case .supported: return "Supported"
        case .unknown: return "Not checked"
        }
    }

    public static func < (a: SupportStatus, b: SupportStatus) -> Bool {
        let order: [SupportStatus] = [.endOfLife, .endingSoon, .supported, .unknown]
        return order.firstIndex(of: a)! < order.firstIndex(of: b)!
    }
}

/// Who put an installation on the Mac. Decides how it is upgraded or removed.
public enum InstallSource: String, Codable, Sendable {
    case homebrew, nvm, fnm, volta, asdf, mise, pyenv, uv, rbenv, rvm, sdkman
    case pythonOrg, nodejsOrg, goOrg, postgresApp
    case jdkFolder, ideBundled, apple, unknown
    case rustup, dotnetInstaller, fvm, sdkFolder, conda, denoInstaller, bunInstaller

    public var title: String {
        switch self {
        case .homebrew: return "Homebrew"
        case .nvm: return "nvm"
        case .fnm: return "fnm"
        case .volta: return "Volta"
        case .asdf: return "asdf"
        case .mise: return "mise"
        case .pyenv: return "pyenv"
        case .uv: return "uv"
        case .rbenv: return "rbenv"
        case .rvm: return "RVM"
        case .sdkman: return "SDKMAN"
        case .pythonOrg: return "python.org installer"
        case .nodejsOrg: return "nodejs.org installer"
        case .goOrg: return "go.dev installer"
        case .postgresApp: return "Postgres.app"
        case .jdkFolder: return "Downloaded JDK"
        case .ideBundled: return "Bundled with an IDE"
        case .apple: return "macOS / Xcode tools"
        case .unknown: return "Unknown"
        case .rustup: return "rustup"
        case .dotnetInstaller: return "Microsoft installer"
        case .fvm: return "FVM"
        case .sdkFolder: return "SDK folder"
        case .conda: return "conda"
        case .denoInstaller: return "Deno installer"
        case .bunInstaller: return "Bun installer"
        }
    }

    /// Short label for tight spaces like version pills.
    public var shortTitle: String {
        switch self {
        case .pythonOrg: return "python.org"
        case .nodejsOrg: return "nodejs.org"
        case .goOrg: return "go.dev"
        case .jdkFolder: return "JDK folder"
        case .dotnetInstaller: return "Microsoft"
        case .ideBundled, .apple: return "system"
        default: return title
        }
    }
}

public struct Installation: Identifiable, Codable, Hashable, Sendable {
    public var id: String { path }
    /// Full version, e.g. "23.2.0".
    public var version: String
    /// Release line used for support dates, e.g. "23", "3.12", "17".
    public var cycle: String
    /// Executable or home folder.
    public var path: String
    public var source: InstallSource
    /// Formula, folder or package name, when there is one.
    public var sourceDetail: String?
    /// The one your shell runs when you type the command.
    public var isDefault = false
    /// For servers such as PostgreSQL.
    public var isRunning: Bool?
    /// Databases or other data that belong to this install.
    public var dataPath: String?
    public var dataSize: Int64?
    public var support: SupportStatus = .unknown
    public var releaseDate: Date?
    public var supportEnds: Date?
    public var latestInCycle: String?
    public var isLTS = false
    /// Can't or shouldn't be removed by the user (part of macOS or an IDE).
    public var isSystem: Bool { source == .apple || source == .ideBundled }

    public var patchAvailable: Bool {
        guard let latest = latestInCycle else { return false }
        return Version.less(version, latest)
    }
}

public struct RuntimeIssue: Codable, Hashable, Sendable {
    public enum Level: String, Codable, Sendable { case critical, warning, info }
    public var level: Level
    public var text: String

    public init(level: Level, text: String) {
        self.level = level
        self.text = text
    }
}

/// A fix DevSweep can show and run in Terminal. Nothing runs without the user.
public struct RuntimeStep: Codable, Hashable, Sendable, Identifiable {
    public enum Kind: String, Codable, Sendable { case upgrade, remove, switchDefault, backup, guided }
    public var id: String { title }
    public var kind: Kind
    public var title: String
    public var detail: String
    public var commands: [String]

    public init(kind: Kind, title: String, detail: String, commands: [String]) {
        self.kind = kind
        self.title = title
        self.detail = detail
        self.commands = commands
    }

    /// Commands contain sudo, so Terminal will ask for the password.
    public var needsAdmin: Bool { commands.contains { $0.contains("sudo ") } }
}

public struct RuntimeReport: Identifiable, Codable, Hashable, Sendable {
    public var id: String
    public var name: String
    public var installs: [Installation]
    public var issues: [RuntimeIssue]
    public var steps: [RuntimeStep]
    /// Newest supported release line, and whether it's LTS.
    public var recommended: String?
    public var dataSource: String?
    public var checkedAt: Date?
    public var note: String?

    public var worst: SupportStatus { installs.map(\.support).min() ?? .unknown }
    public var needsAttention: Bool { issues.contains { $0.level != .info } }
}

public struct HomebrewReport: Codable, Hashable, Sendable {
    public struct Package: Codable, Hashable, Sendable {
        public var name: String
        public var installed: String
        public var latest: String?
        public var reason: String?
        public var date: String?
    }
    public var outdated: [Package]
    public var deprecated: [Package]
    public var issues: [RuntimeIssue] = []
    public var steps: [RuntimeStep] = []
}

public struct VersionsResult: Sendable {
    public var runtimes: [RuntimeReport]
    /// Versions your projects ask for, and whether they're met.
    public var projects: [ProjectRequirement] = []
    public var homebrew: HomebrewReport?
    public var macOS: RuntimeReport?
    public var eolOffline: Bool
    public var date: Date

    public init(runtimes: [RuntimeReport], projects: [ProjectRequirement] = [], homebrew: HomebrewReport?,
                macOS: RuntimeReport?, eolOffline: Bool, date: Date) {
        self.runtimes = runtimes; self.projects = projects; self.homebrew = homebrew
        self.macOS = macOS; self.eolOffline = eolOffline; self.date = date
    }

    public var attentionCount: Int {
        runtimes.filter(\.needsAttention).count
            + (projects.contains { $0.status != .ok } ? 1 : 0)
            + (homebrew.map { $0.deprecated.isEmpty ? 0 : 1 } ?? 0)
            + (macOS?.needsAttention == true ? 1 : 0)
    }
}
