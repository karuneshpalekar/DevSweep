import Foundation

public enum SafetyError: LocalizedError, Equatable {
    case outsideHome(String)
    case protected(String)
    case tooShallow(String)

    public var errorDescription: String? {
        switch self {
        case .outsideHome(let p): return "\(p) is outside your home folder, so DevSweep won't touch it."
        case .protected(let p): return "\(p) is a protected folder."
        case .tooShallow(let p): return "\(p) is too close to your home folder to remove safely."
        }
    }
}

/// Last line of defence before anything is removed, whatever a rule says.
public enum Safety {
    static let protectedRelative: Set<String> = [
        "Library", "Library/Application Support", "Library/Caches", "Library/Preferences",
        "Library/Containers", "Library/Group Containers", "Library/Logs", "Library/LaunchAgents",
        "Library/Mobile Documents", "Library/Mail", "Library/Messages", "Library/Keychains",
        "Library/Developer", "Library/Android", "Library/Android/sdk", "Library/Java",
        "Library/Application Support/Google", "Library/Caches/Google",
        "Documents", "Desktop", "Downloads", "Pictures", "Movies", "Music", "Public", "Applications",
        ".Trash", ".cache", ".config", ".local", ".ssh", ".gnupg", ".aws", ".kube", ".docker",
        ".android", ".gradle", ".vscode", ".vscode/extensions", ".cursor/extensions",
        ".claude", ".npm",
    ]

    public static func check(_ url: URL, home: URL) throws {
        let path = url.standardizedFileURL.path
        let homePath = home.standardizedFileURL.path
        guard path.hasPrefix(homePath + "/") else { throw SafetyError.outsideHome(path) }
        let relative = String(path.dropFirst(homePath.count + 1))
        if protectedRelative.contains(relative) { throw SafetyError.protected(path) }
        let depth = relative.split(separator: "/").count
        // Top-level items are only allowed when they are hidden tool folders
        // like ~/.konan; never a visible folder such as ~/Projects.
        if depth < 2 && !relative.hasPrefix(".") { throw SafetyError.tooShallow(path) }
    }
}
