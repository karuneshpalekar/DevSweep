import Foundation

public struct ShellResult: Sendable {
    public var status: Int32
    public var stdout: String
    public var stderr: String
    public var ok: Bool { status == 0 }
}

/// Runs command-line tools. A GUI app doesn't inherit the shell's PATH, so
/// tools are located explicitly and children get a PATH with the usual
/// package-manager locations.
public enum Shell {
    static let searchPaths = ["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin", "/usr/sbin", "/sbin"]

    static var childPATH: String { searchPaths.joined(separator: ":") }

    private static let lock = NSLock()
    private static var cache: [String: String] = [:]

    public static func locate(_ tool: String) -> String? {
        if tool.hasPrefix("/") { return FileManager.default.isExecutableFile(atPath: tool) ? tool : nil }
        lock.lock(); defer { lock.unlock() }
        if let hit = cache[tool] { return hit }
        for dir in searchPaths {
            let candidate = "\(dir)/\(tool)"
            if FileManager.default.isExecutableFile(atPath: candidate) {
                cache[tool] = candidate
                return candidate
            }
        }
        return nil
    }

    @discardableResult
    public static func run(_ args: [String], timeout: TimeInterval = 120) -> ShellResult {
        guard let tool = args.first, let exe = locate(tool) else {
            return ShellResult(status: 127, stdout: "", stderr: "\(args.first ?? "") not found")
        }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: exe)
        p.arguments = Array(args.dropFirst())
        var env = ProcessInfo.processInfo.environment
        env["PATH"] = childPATH
        p.environment = env
        let out = Pipe(), err = Pipe()
        p.standardOutput = out
        p.standardError = err
        p.standardInput = FileHandle.nullDevice

        // Read pipes concurrently so large output can't deadlock the child.
        var outData = Data(), errData = Data()
        let group = DispatchGroup()
        group.enter()
        DispatchQueue.global().async { outData = out.fileHandleForReading.readDataToEndOfFile(); group.leave() }
        group.enter()
        DispatchQueue.global().async { errData = err.fileHandleForReading.readDataToEndOfFile(); group.leave() }

        do { try p.run() } catch {
            return ShellResult(status: 126, stdout: "", stderr: error.localizedDescription)
        }
        let deadline = DispatchTime.now() + timeout
        DispatchQueue.global().asyncAfter(deadline: deadline) { if p.isRunning { p.terminate() } }
        p.waitUntilExit()
        group.wait()
        return ShellResult(status: p.terminationStatus,
                           stdout: String(decoding: outData, as: UTF8.self),
                           stderr: String(decoding: errData, as: UTF8.self))
    }
}
