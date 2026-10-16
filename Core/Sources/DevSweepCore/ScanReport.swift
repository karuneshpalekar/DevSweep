import Foundation

/// Where a scan is, for the status line: which checks are still running, then
/// how far size measuring has got.
public struct ScanProgress: Sendable, Equatable {
    public enum Phase: Sendable, Equatable { case checking, measuring }
    public var phase: Phase
    public var done: Int
    public var total: Int
    /// Checks still running, longest-running first.
    public var running: [String]
    /// When the longest-running check (or the current measuring item) started.
    public var oldestStart: Date?

    public init(phase: Phase, done: Int, total: Int, running: [String] = [], oldestStart: Date? = nil) {
        self.phase = phase; self.done = done; self.total = total; self.running = running; self.oldestStart = oldestStart
    }

    /// "Checking 21 of 50 · Large files, Docker" or "Measuring 40 of 85".
    public var text: String {
        switch phase {
        case .checking:
            let names = running.prefix(2).joined(separator: ", ") + (running.count > 2 ? " and \(running.count - 2) more" : "")
            return "Checking \(min(done + 1, total)) of \(total)" + (names.isEmpty ? "" : " · \(names)")
        case .measuring:
            let names = running.prefix(2).joined(separator: ", ") + (running.count > 2 ? " and \(running.count - 2) more" : "")
            return "Measuring sizes · \(done) of \(total)" + (names.isEmpty ? "" : " · \(names)")
        }
    }
}

/// What a scan did, check by check, so "nothing found" can be told apart from
/// "didn't run". Contains no file names, only check names, counts and timings.
public struct ScanReport: Codable, Sendable, Equatable {
    public enum Outcome: String, Codable, Sendable { case found, nothingFound, timedOut }

    public struct Check: Codable, Sendable, Equatable, Identifiable {
        public var id: String
        public var title: String
        public var category: String
        public var seconds: Double
        public var found: Int
        public var outcome: Outcome
        /// Time spent measuring what this check found.
        public var measureSeconds: Double = 0
    }

    /// A folder DevSweep looks in that macOS wouldn't let it open.
    public struct Folder: Codable, Sendable, Equatable, Hashable {
        public var path: String
        public var state: FolderState
    }

    public var date: Date
    public var totalSeconds: Double
    public var measuringSeconds: Double
    public var checks: [Check]
    /// Items whose measuring hit the time limit; they show an "at least" size.
    public var unmeasured: Int
    public var folders: [Folder]
    public var checkTimeout: Double

    public var timedOut: [Check] { checks.filter { $0.outcome == .timedOut } }
    public var blockedFolders: [Folder] { folders.filter { $0.state == .denied } }

    /// "Finished in 16 s · 50 checks · 2 skipped"
    public var summary: String {
        var parts = ["Finished in \(Self.duration(totalSeconds))", "\(checks.count) checks"]
        if !timedOut.isEmpty { parts.append("\(timedOut.count) skipped (too slow)") }
        if !blockedFolders.isEmpty { parts.append("\(blockedFolders.count) folder\(blockedFolders.count == 1 ? "" : "s") blocked by macOS") }
        if unmeasured > 0 { parts.append("\(unmeasured) only partly measured") }
        return parts.joined(separator: " · ")
    }

    public static func duration(_ s: Double) -> String {
        if s < 1 { return "under a second" }
        if s < 60 { return "\(Int(s.rounded())) s" }
        let m = Int(s) / 60, r = Int(s) % 60
        return r == 0 ? "\(m) min" : "\(m) min \(r) s"
    }
}

/// Whether a folder exists and whether DevSweep may look inside it.
public enum FolderState: String, Codable, Sendable { case ok, missing, denied }

extension FS {
    /// Checks a folder without listing it. A folder inside one macOS protects
    /// (Documents, Desktop, Downloads) reports "denied" rather than "missing"
    /// when this app hasn't been allowed in.
    public static func folderState(_ url: URL) -> FolderState {
        var st = stat()
        if stat(url.path, &st) == 0 {
            guard (st.st_mode & S_IFMT) == S_IFDIR else { return .missing }
            // stat works on a protected folder itself; opening it is what's refused.
            if let d = opendir(url.path) { closedir(d); return .ok }
            return (errno == EPERM || errno == EACCES) ? .denied : .ok
        }
        return (errno == EPERM || errno == EACCES) ? .denied : .missing
    }
}

/// Runs blocking work on a background thread and stops waiting after a limit.
/// The work itself can't be cancelled; its result is simply ignored.
enum Deadline {
    final class Once: @unchecked Sendable {
        private let lock = NSLock(); private var done = false
        func claim() -> Bool { lock.lock(); defer { lock.unlock() }; if done { return false }; done = true; return true }
    }

    static func run<T: Sendable>(seconds: Double, _ work: @escaping @Sendable () -> T) async -> T? {
        await withCheckedContinuation { (c: CheckedContinuation<T?, Never>) in
            let once = Once()
            DispatchQueue.global(qos: .userInitiated).async {
                let r = work()
                if once.claim() { c.resume(returning: r) }
            }
            DispatchQueue.global().asyncAfter(deadline: .now() + seconds) {
                if once.claim() { c.resume(returning: nil) }
            }
        }
    }
}

extension Rule {
    /// The rule's title, unless it's a per-item template like "{{name}}"; then
    /// its id in words ("large-old-files" -> "Large old files").
    public var displayTitle: String {
        guard title.contains("{{") else { return title }
        let words = id.split(separator: "-").joined(separator: " ")
        return words.prefix(1).uppercased() + words.dropFirst()
    }
}

extension FolderState {
    /// Whether a folder exists and DevSweep may look inside, for the app's settings.
    public static func of(_ url: URL) -> FolderState { FS.folderState(url) }

    /// Drops paths that name the same folder twice ("~/Code" and "~/code" on a
    /// case-insensitive disk), keeping the first spelling.
    public static func dedupe(_ paths: [String], home: URL = FileManager.default.homeDirectoryForCurrentUser) -> [String] {
        var seen: [NSObject] = []
        var seenText = Set<String>()
        var out: [String] = []
        for p in paths {
            let url = FS.expand(p, home: home)
            if let id = (try? url.resourceValues(forKeys: [.fileResourceIdentifierKey]))?.fileResourceIdentifier as? NSObject {
                if seen.contains(where: { $0.isEqual(id) }) { continue }
                seen.append(id)
            } else if !seenText.insert(url.path.lowercased()).inserted { continue }
            out.append(p)
        }
        return out
    }
}
