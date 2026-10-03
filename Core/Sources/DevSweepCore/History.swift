import Foundation

public struct MovedItem: Codable, Hashable, Sendable {
    public var original: String
    /// Where the item landed in the Trash; nil if it was deleted outright.
    public var trashed: String?
}

public struct HistoryEntry: Codable, Identifiable, Hashable, Sendable {
    public var id: UUID
    public var date: Date
    public var title: String
    public var findingID: String
    public var ruleID: String
    public var actionKind: CleanAction.Kind
    public var actionLabel: String
    public var size: Int64
    public var items: [MovedItem]
    public var command: [String]?
    public var note: String?
    public var restoredAt: Date?

    public init(id: UUID = UUID(), date: Date = Date(), title: String, findingID: String = "", ruleID: String = "",
                actionKind: CleanAction.Kind, actionLabel: String, size: Int64 = 0, items: [MovedItem] = [],
                command: [String]? = nil, note: String? = nil, restoredAt: Date? = nil) {
        self.id = id; self.date = date; self.title = title; self.findingID = findingID; self.ruleID = ruleID
        self.actionKind = actionKind; self.actionLabel = actionLabel; self.size = size; self.items = items
        self.command = command; self.note = note; self.restoredAt = restoredAt
    }

    /// Restorable while at least one trashed item is still in the Trash.
    public var canRestore: Bool {
        restoredAt == nil && items.contains { $0.trashed.map { FileManager.default.fileExists(atPath: $0) } ?? false }
    }
}

public enum RestoreError: LocalizedError {
    case nothingToRestore
    case partial([String])

    public var errorDescription: String? {
        switch self {
        case .nothingToRestore: return "Nothing left to restore. The Trash may have been emptied."
        case .partial(let errors): return errors.joined(separator: "\n")
        }
    }
}

/// Append-only log of everything DevSweep changed, kept as JSON.
public final class HistoryStore: @unchecked Sendable {
    public let url: URL
    public private(set) var entries: [HistoryEntry] = []
    private let lock = NSLock()

    public static var defaultURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/DevSweep/history.json")
    }

    public init(url: URL = HistoryStore.defaultURL) {
        self.url = url
        load()
    }

    public func load() {
        lock.lock(); defer { lock.unlock() }
        guard let data = try? Data(contentsOf: url) else { entries = []; return }
        let d = JSONDecoder()
        d.dateDecodingStrategy = .iso8601
        entries = (try? d.decode([HistoryEntry].self, from: data)) ?? []
    }

    public func append(_ entry: HistoryEntry) {
        lock.lock()
        entries.insert(entry, at: 0)
        lock.unlock()
        save()
    }

    /// Moves every trashed item of an entry back where it was.
    @discardableResult
    public func restore(_ id: UUID) throws -> Int {
        guard let index = entries.firstIndex(where: { $0.id == id }) else { throw RestoreError.nothingToRestore }
        let fm = FileManager.default
        var restored = 0
        var errors: [String] = []
        for item in entries[index].items {
            guard let trashed = item.trashed, fm.fileExists(atPath: trashed) else { continue }
            if fm.fileExists(atPath: item.original) {
                errors.append("\(item.original) already exists, so it was left in the Trash.")
                continue
            }
            do {
                try fm.createDirectory(at: URL(fileURLWithPath: item.original).deletingLastPathComponent(),
                                       withIntermediateDirectories: true)
                try fm.moveItem(atPath: trashed, toPath: item.original)
                restored += 1
            } catch {
                errors.append(error.localizedDescription)
            }
        }
        if restored > 0 {
            lock.lock(); entries[index].restoredAt = Date(); lock.unlock()
            save()
        }
        if restored == 0 && errors.isEmpty { throw RestoreError.nothingToRestore }
        if !errors.isEmpty { throw RestoreError.partial(errors) }
        return restored
    }

    private func save() {
        lock.lock(); let snapshot = entries; lock.unlock()
        let e = JSONEncoder()
        e.dateEncodingStrategy = .iso8601
        e.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? e.encode(snapshot) else { return }
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: url, options: .atomic)
    }
}

/// Findings the user chose to always ignore.
public final class IgnoreStore: @unchecked Sendable {
    public let url: URL
    public private(set) var ids: Set<String> = []

    public init(url: URL = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Application Support/DevSweep/ignored.json")) {
        self.url = url
        if let data = try? Data(contentsOf: url), let list = try? JSONDecoder().decode([String].self, from: data) {
            ids = Set(list)
        }
    }

    public func ignore(_ id: String) { ids.insert(id); save() }
    public func unignore(_ id: String) { ids.remove(id); save() }

    private func save() {
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? JSONEncoder().encode(ids.sorted()).write(to: url, options: .atomic)
    }
}
