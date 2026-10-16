import DevSweepCore
import Foundation

/// The last scan's report, kept so "Copy scan report" works after a restart.
enum ScanReportStore {
    static var url: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/DevSweep/last-scan.json")
    }

    static func load() -> ScanReport? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        let d = JSONDecoder(); d.dateDecodingStrategy = .iso8601
        return try? d.decode(ScanReport.self, from: data)
    }

    static func save(_ r: ScanReport) {
        let e = JSONEncoder(); e.dateEncodingStrategy = .iso8601; e.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? e.encode(r) else { return }
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: url, options: .atomic)
    }
}

/// Numbers progress updates from any thread.
final class SequenceCounter: @unchecked Sendable {
    private let lock = NSLock()
    private var n = 0
    func next() -> Int { lock.lock(); defer { lock.unlock() }; n += 1; return n }
}
