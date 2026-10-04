import Foundation

/// When DevSweep scans on its own. Scheduled scans are read-only, like every
/// scan: nothing is ever cleaned automatically.
public struct ScanSchedule: Codable, Equatable, Sendable {
    public enum Frequency: String, Codable, CaseIterable, Identifiable, Sendable {
        case daily, weekly
        public var id: String { rawValue }
        public var title: String { self == .daily ? "Every day" : "Every week" }
    }

    public var enabled = false
    public var frequency: Frequency = .weekly
    /// Calendar weekday: 1 is Sunday, 2 is Monday.
    public var weekday = 2
    public var hour = 9

    public init(enabled: Bool = false, frequency: Frequency = .weekly, weekday: Int = 2, hour: Int = 9) {
        self.enabled = enabled
        self.frequency = frequency
        self.weekday = weekday
        self.hour = hour
    }

    public func nextRun(after date: Date, calendar: Calendar = .current) -> Date? {
        var c = DateComponents()
        c.hour = hour
        c.minute = 0
        c.second = 0
        if frequency == .weekly { c.weekday = weekday }
        return calendar.nextDate(after: date, matching: c, matchingPolicy: .nextTime)
    }

    /// Due once the first run time after the previous scan has passed. A Mac that
    /// was asleep or off at the scheduled time catches up when it wakes.
    public func isDue(lastRun: Date?, now: Date, calendar: Calendar = .current) -> Bool {
        guard enabled, let last = lastRun, let next = nextRun(after: last, calendar: calendar) else { return false }
        return now >= next
    }

    /// Index 0 is Sunday, matching Calendar's weekday numbering minus one.
    public static let weekdayNames = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"]

    public func summary(calendar: Calendar = .current) -> String {
        let time = String(format: "%02d:00", hour)
        guard frequency == .weekly else { return "Every day at \(time)" }
        return "Every \(Self.weekdayNames[(weekday - 1 + 7) % 7]) at \(time)"
    }
}

/// Which problems are worth a notification.
public struct AlertSettings: Codable, Equatable, Sendable {
    public var diskEnabled = true
    public var diskPercent = 85
    public var growthEnabled = true
    public var growthGB = 2
    public var endOfLifeEnabled = true
    public var secretsEnabled = true

    public init() {}
}

public struct AlertItem: Hashable, Sendable {
    public var id: String
    public var text: String
    public init(id: String, text: String) { self.id = id; self.text = text }
}

public struct AlertGrowth: Sendable {
    public var title: String
    public var bytes: Int64
    public init(title: String, bytes: Int64) { self.title = title; self.bytes = bytes }
}

/// What the latest scan saw.
public struct AlertInputs: Sendable {
    public var diskUsedPercent: Double?
    public var grew: [AlertGrowth]
    public var endOfLife: [AlertItem]
    public var secrets: [AlertItem]
    /// Secret files seen by earlier scheduled scans; nil on the first run, when
    /// everything is a baseline rather than "new".
    public var knownSecretIDs: Set<String>?

    public init(diskUsedPercent: Double?, grew: [AlertGrowth] = [], endOfLife: [AlertItem] = [],
                secrets: [AlertItem] = [], knownSecretIDs: Set<String>? = nil) {
        self.diskUsedPercent = diskUsedPercent
        self.grew = grew
        self.endOfLife = endOfLife
        self.secrets = secrets
        self.knownSecretIDs = knownSecretIDs
    }
}

public enum AlertTarget: String, Codable, Sendable {
    case cleanUp, healthTools, healthSecurity
}

public struct AlertEvent: Equatable, Sendable {
    public var key: String
    public var title: String
    public var body: String
    public var target: AlertTarget
}

/// Remembers what was already announced, so the same problem isn't repeated every scan.
public struct AlertLedger: Codable, Equatable, Sendable {
    public var notified: [String: Date] = [:]

    public init() {}

    mutating func shouldNotify(_ key: String, now: Date, cooldownDays: Double) -> Bool {
        guard let last = notified[key] else { return true }
        return now.timeIntervalSince(last) >= cooldownDays * 86_400
    }
}

public enum AlertEvaluator {
    /// Decides what to notify about, and updates the ledger.
    /// Each problem is announced once, then again only after a cool-down if it's still there.
    public static func evaluate(_ input: AlertInputs, settings: AlertSettings, ledger: inout AlertLedger,
                                now: Date = Date()) -> [AlertEvent] {
        var events: [AlertEvent] = []
        func emit(_ key: String, cooldown: Double, _ make: () -> AlertEvent) {
            guard ledger.shouldNotify(key, now: now, cooldownDays: cooldown) else { return }
            ledger.notified[key] = now
            events.append(make())
        }

        if settings.diskEnabled, let used = input.diskUsedPercent {
            if used >= Double(settings.diskPercent) {
                emit("disk", cooldown: 3) {
                    AlertEvent(key: "disk", title: "Your disk is \(Int(used.rounded()))% full",
                               body: "DevSweep can show what's safe to clean.", target: .cleanUp)
                }
            } else {
                // Back under the limit: the next time it's crossed, say so straight away.
                ledger.notified["disk"] = nil
            }
        }

        if settings.growthEnabled {
            let limit = Int64(settings.growthGB) * 1_000_000_000
            let big = input.grew.filter { $0.bytes >= limit }.sorted { $0.bytes > $1.bytes }
            if !big.isEmpty {
                let key = "growth:" + big.map(\.title).sorted().joined(separator: ",")
                emit(key, cooldown: 7) {
                    let top = big[0]
                    let body = big.count == 1 ? "It may be worth a look in Clean up."
                                              : "\(big.count - 1) other item\(big.count == 2 ? "" : "s") grew a lot too."
                    return AlertEvent(key: key, title: "\(top.title) grew by \(SizeFormat.string(top.bytes))",
                                      body: body, target: .cleanUp)
                }
            }
        }

        if settings.endOfLifeEnabled, !input.endOfLife.isEmpty {
            let key = "eol:" + input.endOfLife.map(\.id).sorted().joined(separator: ",")
            emit(key, cooldown: 7) {
                AlertEvent(key: key, title: input.endOfLife.count == 1 ? "A tool you use is past end of life" : "\(input.endOfLife.count) tools you use are past end of life",
                           body: input.endOfLife[0].text, target: .healthTools)
            }
        }

        if settings.secretsEnabled, let known = input.knownSecretIDs {
            let fresh = input.secrets.filter { !known.contains($0.id) }
            if !fresh.isEmpty {
                let key = "secrets:" + fresh.map(\.id).sorted().joined(separator: ",")
                emit(key, cooldown: 7) {
                    AlertEvent(key: key, title: fresh.count == 1 ? "A new file looks like a secret" : "\(fresh.count) new files look like secrets",
                               body: fresh[0].text, target: .healthSecurity)
                }
            }
        }
        return events
    }
}

/// Last scheduled run, the ledger, and the secret files seen so far.
public final class AlertStore: @unchecked Sendable {
    public struct Data: Codable, Sendable {
        public var lastScheduledRun: Date?
        public var ledger = AlertLedger()
        public var knownSecretIDs: [String]?
    }

    public let url: URL
    public private(set) var data = Data()
    private let lock = NSLock()

    public init(url: URL = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Application Support/DevSweep/alerts.json")) {
        self.url = url
        let d = JSONDecoder()
        d.dateDecodingStrategy = .iso8601
        if let raw = try? Foundation.Data(contentsOf: url), let decoded = try? d.decode(Data.self, from: raw) { data = decoded }
    }

    public func update(_ change: (inout Data) -> Void) {
        lock.lock(); change(&data); let snapshot = data; lock.unlock()
        let e = JSONEncoder()
        e.dateEncodingStrategy = .iso8601
        e.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let raw = try? e.encode(snapshot) else { return }
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? raw.write(to: url, options: .atomic)
    }
}
