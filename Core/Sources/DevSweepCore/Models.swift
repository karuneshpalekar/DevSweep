import Foundation

/// How risky it is to remove something. Decides which actions are offered
/// and how the item is labelled in the UI.
public enum Risk: String, Codable, CaseIterable, Sendable, Comparable {
    case rebuilds
    case oldVersion
    case leftover
    case holdsData
    case needsAdmin

    public var title: String {
        switch self {
        case .rebuilds: return "Rebuilds itself"
        case .oldVersion: return "Old version"
        case .leftover: return "Leftover from a removed app"
        case .holdsData: return "Holds your data"
        case .needsAdmin: return "Needs admin"
        }
    }

    public var summary: String {
        switch self {
        case .rebuilds: return "Comes back on its own when a tool needs it."
        case .oldVersion: return "A newer version is installed or nothing uses it."
        case .leftover: return "The app that made it is gone."
        case .holdsData: return "May contain your own data. Back up first."
        case .needsAdmin: return "Lives outside your home folder and needs your password."
        }
    }

    public static func < (lhs: Risk, rhs: Risk) -> Bool {
        allCases.firstIndex(of: lhs)! < allCases.firstIndex(of: rhs)!
    }
}

public enum Category: String, Codable, CaseIterable, Sendable {
    case leftovers
    case ideVersions
    case android
    case xcode
    case toolchains
    case packageCaches
    case browsers
    case projects
    case aiModels
    case backgroundServices

    public var title: String {
        switch self {
        case .leftovers: return "Leftovers"
        case .ideVersions: return "Old IDE versions"
        case .android: return "Android"
        case .xcode: return "Xcode"
        case .toolchains: return "Toolchains"
        case .packageCaches: return "Package caches"
        case .browsers: return "Browsers"
        case .projects: return "Projects"
        case .aiModels: return "AI models"
        case .backgroundServices: return "Background services"
        }
    }
}

/// The text behind the ⓘ button. Rule packs write it with `{{fact}}`
/// placeholders; the scanner fills them with what it actually found.
public struct Explanation: Codable, Hashable, Sendable {
    public var what: String
    public var why: String
    public var ifDeleted: String
    public var wontLose: String?
    public var before: String?
    public var undo: String?
    public var better: String?

    public init(what: String, why: String, ifDeleted: String, wontLose: String? = nil,
                before: String? = nil, undo: String? = nil, better: String? = nil) {
        self.what = what
        self.why = why
        self.ifDeleted = ifDeleted
        self.wontLose = wontLose
        self.before = before
        self.undo = undo
        self.better = better
    }

    func rendered(with facts: [String: String]) -> Explanation {
        func r(_ s: String) -> String { Template.render(s, facts) }
        return Explanation(
            what: r(what), why: r(why), ifDeleted: r(ifDeleted),
            wontLose: wontLose.map(r), before: before.map(r),
            undo: undo.map(r), better: better.map(r)
        )
    }
}

/// One line in "What was checked".
public struct Check: Codable, Hashable, Sendable {
    public enum Status: String, Codable, Sendable { case passed, warning }
    public var status: Status
    public var text: String

    public init(_ status: Status, _ text: String) {
        self.status = status
        self.text = text
    }

    public static func passed(_ text: String) -> Check { Check(.passed, text) }
    public static func warning(_ text: String) -> Check { Check(.warning, text) }
}

public struct CleanAction: Codable, Hashable, Sendable, Identifiable {
    public enum Kind: String, Codable, Sendable {
        /// Remove immediately. Only for things that rebuild themselves.
        case delete
        /// Move to the Trash; restorable from History.
        case trash
        /// Run the owning tool's own command (npm cache clean, simctl ...).
        case command
        /// Show commands for the user to run (e.g. ones that need sudo).
        case manual
    }

    public var kind: Kind
    public var label: String?
    public var command: [String]?
    public var detail: String?

    public init(kind: Kind, label: String? = nil, command: [String]? = nil, detail: String? = nil) {
        self.kind = kind
        self.label = label
        self.command = command
        self.detail = detail
    }

    public var id: String { kind.rawValue + ":" + (command?.joined(separator: " ") ?? "") }

    public var displayLabel: String {
        if let label { return label }
        switch kind {
        case .delete: return "Delete"
        case .trash: return "Move to Trash"
        case .command: return "Run \(command?.first ?? "command")"
        case .manual: return "Show commands"
        }
    }

    public var displayDetail: String {
        if let detail { return detail }
        switch kind {
        case .delete: return "Removed right away. It rebuilds when needed."
        case .trash: return "Restorable from History until you empty the Trash."
        case .command: return "Runs \(command?.joined(separator: " ") ?? "the tool's own command")."
        case .manual: return "DevSweep shows the commands; you run them."
        }
    }

    public var isRestorable: Bool { kind == .trash }
}

public struct Finding: Identifiable, Hashable, Codable, Sendable {
    public var id: String
    public var ruleID: String
    public var title: String
    public var subtitle: String
    public var category: Category
    public var risk: Risk
    public var paths: [String]
    public var size: Int64
    public var explanation: Explanation
    public var checks: [Check]
    public var actions: [CleanAction]
    /// Running apps or processes that must quit before cleaning.
    public var blockingApps: [String]
    /// Process names/patterns to re-check right before cleaning.
    public var blockers: [String]

    public var defaultAction: CleanAction? { actions.first }

    public init(id: String, ruleID: String, title: String, subtitle: String, category: Category, risk: Risk,
                paths: [String], size: Int64, explanation: Explanation, checks: [Check], actions: [CleanAction],
                blockingApps: [String], blockers: [String]) {
        self.id = id; self.ruleID = ruleID; self.title = title; self.subtitle = subtitle
        self.category = category; self.risk = risk; self.paths = paths; self.size = size
        self.explanation = explanation; self.checks = checks; self.actions = actions
        self.blockingApps = blockingApps; self.blockers = blockers
    }
}

public struct ScanResult: Sendable {
    public var findings: [Finding]
    public var errors: [String]
    public var date: Date
    public var disk: DiskInfo?

    public var totalSize: Int64 { findings.reduce(0) { $0 + $1.size } }
}

public struct DiskInfo: Sendable, Hashable {
    public var total: Int64
    public var free: Int64

    public static func current(for url: URL = URL(fileURLWithPath: NSHomeDirectory())) -> DiskInfo? {
        let keys: Set<URLResourceKey> = [.volumeTotalCapacityKey, .volumeAvailableCapacityForImportantUsageKey]
        guard let values = try? url.resourceValues(forKeys: keys),
              let total = values.volumeTotalCapacity,
              let free = values.volumeAvailableCapacityForImportantUsage else { return nil }
        return DiskInfo(total: Int64(total), free: free)
    }
}

public enum SizeFormat {
    public static func string(_ bytes: Int64) -> String {
        let f = ByteCountFormatter()
        f.countStyle = .file
        f.allowedUnits = [.useKB, .useMB, .useGB, .useTB]
        f.allowsNonnumericFormatting = false
        return f.string(fromByteCount: bytes)
    }
}
