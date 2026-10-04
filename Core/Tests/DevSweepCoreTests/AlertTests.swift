import XCTest
@testable import DevSweepCore

final class AlertTests: XCTestCase {
    var cal: Calendar!
    let day: TimeInterval = 86_400

    override func setUp() {
        cal = Calendar(identifier: .gregorian)
        cal.timeZone = TimeZone(identifier: "UTC")!
    }

    private func date(_ s: String) -> Date {
        let f = ISO8601DateFormatter()
        return f.date(from: s)!
    }

    // MARK: - Schedule

    func testWeeklyScheduleFindsTheNextMondayAtNine() {
        let s = ScanSchedule(enabled: true, frequency: .weekly, weekday: 2, hour: 9)
        // 2026-10-04 is a Sunday.
        XCTAssertEqual(s.nextRun(after: date("2026-10-04T12:00:00Z"), calendar: cal), date("2026-10-05T09:00:00Z"))
        // After Monday 09:00 has passed, it's the following Monday.
        XCTAssertEqual(s.nextRun(after: date("2026-10-05T09:30:00Z"), calendar: cal), date("2026-10-12T09:00:00Z"))
        // Exactly at 09:00 counts as already run.
        XCTAssertEqual(s.nextRun(after: date("2026-10-05T09:00:00Z"), calendar: cal), date("2026-10-12T09:00:00Z"))
    }

    func testDailyScheduleAndSummaries() {
        let s = ScanSchedule(enabled: true, frequency: .daily, hour: 7)
        XCTAssertEqual(s.nextRun(after: date("2026-10-04T08:00:00Z"), calendar: cal), date("2026-10-05T07:00:00Z"))
        XCTAssertEqual(s.summary(calendar: cal), "Every day at 07:00")
        XCTAssertEqual(ScanSchedule(enabled: true, frequency: .weekly, weekday: 2, hour: 9).summary(calendar: cal), "Every Monday at 09:00")
        XCTAssertEqual(ScanSchedule(enabled: true, frequency: .weekly, weekday: 1, hour: 18).summary(calendar: cal), "Every Sunday at 18:00")
    }

    func testDueLogicCatchesUpAfterSleepAndRespectsTheSwitch() {
        var s = ScanSchedule(enabled: true, frequency: .weekly, weekday: 2, hour: 9)
        let lastRun = date("2026-10-05T09:01:00Z")   // Monday, just ran
        XCTAssertFalse(s.isDue(lastRun: lastRun, now: date("2026-10-11T12:00:00Z"), calendar: cal), "not until next Monday")
        XCTAssertTrue(s.isDue(lastRun: lastRun, now: date("2026-10-12T09:00:00Z"), calendar: cal))
        XCTAssertTrue(s.isDue(lastRun: lastRun, now: date("2026-10-20T15:00:00Z"), calendar: cal), "a Mac that was off still catches up")
        XCTAssertFalse(s.isDue(lastRun: nil, now: date("2026-10-20T15:00:00Z"), calendar: cal), "no baseline yet")
        s.enabled = false
        XCTAssertFalse(s.isDue(lastRun: lastRun, now: date("2026-10-20T15:00:00Z"), calendar: cal))
    }

    // MARK: - Alerts

    func testDiskAlertFiresAtTheLimitThenStaysQuietUntilItDropsAndReturns() {
        var ledger = AlertLedger()
        let s = AlertSettings()
        let t0 = date("2026-10-05T09:00:00Z")
        XCTAssertTrue(AlertEvaluator.evaluate(AlertInputs(diskUsedPercent: 70), settings: s, ledger: &ledger, now: t0).isEmpty)

        let first = AlertEvaluator.evaluate(AlertInputs(diskUsedPercent: 86.4), settings: s, ledger: &ledger, now: t0)
        XCTAssertEqual(first.map(\.title), ["Your disk is 86% full"])
        XCTAssertEqual(first.first?.target, .cleanUp)

        XCTAssertTrue(AlertEvaluator.evaluate(AlertInputs(diskUsedPercent: 88), settings: s, ledger: &ledger, now: t0.addingTimeInterval(day)).isEmpty,
                      "not repeated within 3 days")
        XCTAssertEqual(AlertEvaluator.evaluate(AlertInputs(diskUsedPercent: 88), settings: s, ledger: &ledger, now: t0.addingTimeInterval(4 * day)).count, 1,
                       "reminded after the cool-down")

        _ = AlertEvaluator.evaluate(AlertInputs(diskUsedPercent: 60), settings: s, ledger: &ledger, now: t0.addingTimeInterval(5 * day))
        XCTAssertEqual(AlertEvaluator.evaluate(AlertInputs(diskUsedPercent: 90), settings: s, ledger: &ledger, now: t0.addingTimeInterval(6 * day)).count, 1,
                       "crossing the limit again after dropping below it is announced immediately")
    }

    func testGrowthAlertNeedsTheThresholdAndGroupsItems() {
        var ledger = AlertLedger()
        let s = AlertSettings()   // 2 GB
        let small = AlertInputs(diskUsedPercent: nil, grew: [AlertGrowth(title: "npm cache", bytes: 1_500_000_000)])
        XCTAssertTrue(AlertEvaluator.evaluate(small, settings: s, ledger: &ledger).isEmpty)

        let big = AlertInputs(diskUsedPercent: nil, grew: [AlertGrowth(title: "npm cache", bytes: 1_500_000_000),
                                                           AlertGrowth(title: "Chrome cache", bytes: 3_100_000_000),
                                                           AlertGrowth(title: "Gradle cache", bytes: 2_400_000_000)])
        let events = AlertEvaluator.evaluate(big, settings: s, ledger: &ledger)
        XCTAssertEqual(events.count, 1, "several items become one notification")
        XCTAssertTrue(events[0].title.hasPrefix("Chrome cache grew by"), "the biggest leads")
        XCTAssertTrue(events[0].body.contains("1 other item"))
        XCTAssertTrue(AlertEvaluator.evaluate(big, settings: s, ledger: &ledger).isEmpty, "the same growth isn't announced twice")
    }

    func testEndOfLifeAndNewSecretsAndTheFirstRunBaseline() {
        var ledger = AlertLedger()
        let s = AlertSettings()
        let eol = [AlertItem(id: "node", text: "Node.js 23 reached end of life.")]
        let secrets = [AlertItem(id: "a.txt", text: "GitHub recovery codes in Downloads"),
                       AlertItem(id: "b.json", text: "A cloud key in Downloads")]

        // First run: existing secrets are a baseline, not news. End of life still counts.
        var events = AlertEvaluator.evaluate(AlertInputs(diskUsedPercent: nil, endOfLife: eol, secrets: secrets, knownSecretIDs: nil),
                                             settings: s, ledger: &ledger)
        XCTAssertEqual(events.map(\.target), [.healthTools])

        // A later run with one new file mentions only that one.
        events = AlertEvaluator.evaluate(AlertInputs(diskUsedPercent: nil, endOfLife: eol, secrets: secrets + [AlertItem(id: "c.pem", text: "A private key in Desktop")],
                                                      knownSecretIDs: ["a.txt", "b.json"]), settings: s, ledger: &ledger)
        XCTAssertEqual(events.count, 1)
        XCTAssertEqual(events[0].title, "A new file looks like a secret")
        XCTAssertEqual(events[0].body, "A private key in Desktop")
        XCTAssertEqual(events[0].target, .healthSecurity)
    }

    func testSwitchedOffAlertsStaySilent() {
        var ledger = AlertLedger()
        var s = AlertSettings()
        s.diskEnabled = false; s.growthEnabled = false; s.endOfLifeEnabled = false; s.secretsEnabled = false
        let input = AlertInputs(diskUsedPercent: 99, grew: [AlertGrowth(title: "x", bytes: 9_000_000_000)],
                                endOfLife: [AlertItem(id: "n", text: "t")], secrets: [AlertItem(id: "s", text: "t")], knownSecretIDs: [])
        XCTAssertTrue(AlertEvaluator.evaluate(input, settings: s, ledger: &ledger).isEmpty)
        XCTAssertTrue(ledger.notified.isEmpty)
    }

    func testStoreRoundTrips() throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("alerts-\(UUID().uuidString).json")
        defer { try? FileManager.default.removeItem(at: url) }
        let store = AlertStore(url: url)
        let when = date("2026-10-05T09:00:00Z")
        store.update { $0.lastScheduledRun = when; $0.knownSecretIDs = ["a"]; $0.ledger.notified["disk"] = when }
        let reopened = AlertStore(url: url)
        XCTAssertEqual(reopened.data.lastScheduledRun, when)
        XCTAssertEqual(reopened.data.knownSecretIDs, ["a"])
        XCTAssertEqual(reopened.data.ledger.notified["disk"], when)
    }
}
