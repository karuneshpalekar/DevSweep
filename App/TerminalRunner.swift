import AppKit
import DevSweepCore

/// Runs a suggested fix in a visible Terminal window. The script lists the
/// commands and waits for Return first, so nothing runs without the user
/// seeing it, and sudo can ask for the password there.
enum TerminalRunner {
    static var directory: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Caches/DevSweep/terminal")
    }

    static func run(_ step: RuntimeStep) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let file = directory.appendingPathComponent("devsweep-\(Int(Date().timeIntervalSince1970)).command")
        try step.terminalScript.write(to: file, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: file.path)
        NSWorkspace.shared.open(file)
    }

    static func copy(_ step: RuntimeStep) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(step.commands.joined(separator: "\n"), forType: .string)
    }
}
