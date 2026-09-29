import Foundation
import os

/// One release line from endoflife.date.
public struct ReleaseCycle: Decodable, Sendable {
    public var cycle: String
    public var releaseDate: Date?
    /// End of security support. nil + `ended` false means no date announced.
    public var eolDate: Date?
    public var ended: Bool
    public var latest: String?
    public var isLTS: Bool

    enum CodingKeys: String, CodingKey { case cycle, releaseDate, eol, latest, lts }

    /// endoflife.date fields can be a date string or a boolean.
    enum DateOrBool { case date(Date), bool(Bool), none }

    static let day: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone(identifier: "UTC")
        f.dateFormat = "yyyy-MM-dd"
        return f
    }()

    static func dateOrBool(_ c: KeyedDecodingContainer<CodingKeys>, _ key: CodingKeys) -> DateOrBool {
        if let b = try? c.decode(Bool.self, forKey: key) { return .bool(b) }
        if let s = try? c.decode(String.self, forKey: key), let d = day.date(from: s) { return .date(d) }
        return .none
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        // Cycles are usually strings, occasionally numbers.
        if let s = try? c.decode(String.self, forKey: .cycle) { cycle = s }
        else if let d = try? c.decode(Double.self, forKey: .cycle) { cycle = d == d.rounded() ? String(Int(d)) : String(d) }
        else { cycle = "" }
        latest = try? c.decode(String.self, forKey: .latest)
        if case .date(let d) = Self.dateOrBool(c, .releaseDate) { releaseDate = d }
        switch Self.dateOrBool(c, .eol) {
        case .date(let d): eolDate = d; ended = d < Date()
        case .bool(let b): eolDate = nil; ended = b
        case .none: eolDate = nil; ended = false
        }
        switch Self.dateOrBool(c, .lts) {
        case .date(let d): isLTS = d <= Date()
        case .bool(let b): isLTS = b
        case .none: isLTS = false
        }
    }

    public init(cycle: String, releaseDate: Date?, eolDate: Date?, ended: Bool, latest: String?, isLTS: Bool) {
        self.cycle = cycle; self.releaseDate = releaseDate; self.eolDate = eolDate
        self.ended = ended; self.latest = latest; self.isLTS = isLTS
    }

    /// Support status on a given day. "Ending soon" means within 120 days.
    public func status(on now: Date = Date()) -> SupportStatus {
        if ended { return .endOfLife }
        guard let eol = eolDate else { return .supported }
        if eol <= now { return .endOfLife }
        return eol.timeIntervalSince(now) < 120 * 86_400 ? .endingSoon : .supported
    }
}

/// Fetches support schedules from endoflife.date, cached for a day so the
/// app works offline with the last known data.
public final class EndOfLifeClient: @unchecked Sendable {
    public let cacheDir: URL
    private let stale = OSAllocatedUnfairLock(initialState: false)
    public var usedStaleData: Bool { stale.withLock { $0 } }

    public init(cacheDir: URL = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Caches/DevSweep/endoflife")) {
        self.cacheDir = cacheDir
    }

    public func cycles(for product: String) async -> (cycles: [ReleaseCycle], fetched: Date?) {
        let file = cacheDir.appendingPathComponent("\(product).json")
        let modified = FS.modificationDate(file)
        if let modified, Date().timeIntervalSince(modified) < 86_400,
           let data = try? Data(contentsOf: file), let cycles = decode(data) {
            return (cycles, modified)
        }
        if let url = URL(string: "https://endoflife.date/api/\(product).json") {
            var req = URLRequest(url: url, timeoutInterval: 10)
            req.setValue("DevSweep (https://github.com/karuneshpalekar/DevSweep)", forHTTPHeaderField: "User-Agent")
            if let (data, resp) = try? await URLSession.shared.data(for: req),
               (resp as? HTTPURLResponse)?.statusCode == 200, let cycles = decode(data) {
                try? FileManager.default.createDirectory(at: cacheDir, withIntermediateDirectories: true)
                try? data.write(to: file, options: .atomic)
                return (cycles, Date())
            }
        }
        // Offline: fall back to whatever we had, however old.
        if let data = try? Data(contentsOf: file), let cycles = decode(data) {
            stale.withLock { $0 = true }
            return (cycles, modified)
        }
        stale.withLock { $0 = true }
        return ([], nil)
    }

    private func decode(_ data: Data) -> [ReleaseCycle]? {
        try? JSONDecoder().decode([ReleaseCycle].self, from: data)
    }
}
