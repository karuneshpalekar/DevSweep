import Foundation

/// What a scan found, reduced to sizes, so later scans can say what changed.
public struct ScanSnapshot: Codable, Sendable {
    public var date: Date
    public var sizes: [String: Int64]
    public var titles: [String: String]
    public var diskFree: Int64?
}

public struct ScanChanges: Sendable {
    public struct Item: Sendable, Identifiable {
        public var id: String
        public var title: String
        /// Growth for items that grew; total size for new items.
        public var bytes: Int64
    }
    public var since: Date
    public var grew: [Item]
    public var new: [Item]

    public var isEmpty: Bool { grew.isEmpty && new.isEmpty }
}

/// Keeps the last few scan snapshots in Application Support.
public final class ScanSnapshotStore: @unchecked Sendable {
    public let url: URL
    public private(set) var snapshots: [ScanSnapshot] = []
    static let keep = 40

    public init(url: URL = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Application Support/DevSweep/scans.json")) {
        self.url = url
        let d = JSONDecoder()
        d.dateDecodingStrategy = .iso8601
        if let data = try? Data(contentsOf: url), let list = try? d.decode([ScanSnapshot].self, from: data) {
            snapshots = list
        }
    }

    /// Compares a scan with the most recent snapshot at least `days` old, or
    /// failing that the oldest one from an earlier day. nil on a first scan.
    public func changes(for findings: [Finding], days: Int = 7, now: Date = Date(),
                        minimum: Int64 = 200_000_000) -> ScanChanges? {
        let cutoff = now.addingTimeInterval(-Double(days - 1) * 86_400)
        let earlierDay = now.addingTimeInterval(-12 * 3600)
        guard let base = snapshots.last(where: { $0.date <= cutoff })
            ?? snapshots.first(where: { $0.date <= earlierDay }) else { return nil }
        var grew: [ScanChanges.Item] = []
        var new: [ScanChanges.Item] = []
        for f in findings {
            if let before = base.sizes[f.id] {
                if f.size - before >= minimum { grew.append(.init(id: f.id, title: f.title, bytes: f.size - before)) }
            } else if f.size >= minimum {
                new.append(.init(id: f.id, title: f.title, bytes: f.size))
            }
        }
        return ScanChanges(since: base.date, grew: grew.sorted { $0.bytes > $1.bytes },
                           new: new.sorted { $0.bytes > $1.bytes })
    }

    public func record(_ findings: [Finding], diskFree: Int64?, at date: Date = Date()) {
        var sizes: [String: Int64] = [:]
        var titles: [String: String] = [:]
        for f in findings { sizes[f.id] = f.size; titles[f.id] = f.title }
        snapshots.append(ScanSnapshot(date: date, sizes: sizes, titles: titles, diskFree: diskFree))
        if snapshots.count > Self.keep { snapshots.removeFirst(snapshots.count - Self.keep) }
        let e = JSONEncoder()
        e.dateEncodingStrategy = .iso8601
        guard let data = try? e.encode(snapshots) else { return }
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: url, options: .atomic)
    }
}
