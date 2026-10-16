import XCTest
@testable import DevSweepCore

final class ScanReportTests: XCTestCase {
    private func rule(_ id: String, title: String = "Check") -> Rule {
        Rule(id: id, title: title, category: .packageCaches, risk: .rebuilds, detector: DetectorSpec(kind: .paths),
             explain: Explanation(what: "", why: "", ifDeleted: ""), actions: [CleanAction(kind: .delete)])
    }

    private func tempFile(bytes: Int) throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("devsweep-report-\(UUID().uuidString)")
        try Data(repeating: 1, count: bytes).write(to: url)
        addTeardownBlock { try? FileManager.default.removeItem(at: url) }
        return url
    }

    func testSlowCheckIsSkippedAndTheRestStillReport() async throws {
        let file = try tempFile(bytes: 2_000_000)
        let scanner = Scanner(rules: [rule("fast", title: "Fast"), rule("slow", title: "Slow"), rule("empty", title: "Empty")])
        scanner.checkTimeout = 0.3
        scanner.detect = { rule, _ in
            switch rule.id {
            case "slow": Thread.sleep(forTimeInterval: 3); return []
            case "fast": return [Candidate(key: "k", paths: [file], title: "item")]
            default: return []
            }
        }
        let t0 = Date()
        let result = await scanner.scan()
        XCTAssertLessThan(Date().timeIntervalSince(t0), 2, "the scan doesn't wait for the slow check")
        let report = try XCTUnwrap(result.report)
        let byID = Dictionary(uniqueKeysWithValues: report.checks.map { ($0.id, $0) })
        XCTAssertEqual(byID["fast"]?.outcome, .found)
        XCTAssertEqual(byID["fast"]?.found, 1)
        XCTAssertEqual(byID["slow"]?.outcome, .timedOut)
        XCTAssertEqual(byID["empty"]?.outcome, .nothingFound, "nothing found is reported as none, not as skipped")
        XCTAssertEqual(result.findings.count, 1)
        XCTAssertTrue(report.summary.contains("1 skipped"))
    }

    func testProgressCountsEveryCheckThenMeasuring() async throws {
        let file = try tempFile(bytes: 2_000_000)
        let scanner = Scanner(rules: (0..<6).map { rule("r\($0)") })
        scanner.detect = { r, _ in r.id == "r0" ? [Candidate(key: "k", paths: [file], title: "x")] : [] }
        final class Box: @unchecked Sendable { let lock = NSLock(); var all: [ScanProgress] = [] }
        let box = Box()
        _ = await scanner.scan { p in box.lock.lock(); box.all.append(p); box.lock.unlock() }
        let checking = box.all.filter { $0.phase == .checking }
        XCTAssertEqual(checking.map(\.total).max(), 6)
        XCTAssertEqual(checking.map(\.done).max(), 6)
        XCTAssertTrue(box.all.contains { $0.phase == .measuring && $0.done == 1 && $0.total == 1 })
        XCTAssertTrue(ScanProgress(phase: .checking, done: 2, total: 6, running: ["Large files", "Docker", "Xcode"]).text
            .hasPrefix("Checking 3 of 6 · Large files, Docker and 1 more"))
    }

    func testTemplatedTitlesShowTheCategory() {
        XCTAssertEqual(rule("a", title: "{{name}}").displayTitle, "Package caches")
        XCTAssertEqual(rule("a", title: "npm cache").displayTitle, "npm cache")
    }

    func testFolderStates() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("devsweep-fs-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: dir) }
        XCTAssertEqual(FS.folderState(dir), .ok)
        XCTAssertEqual(FS.folderState(dir.appendingPathComponent("nope")), .missing)
        let file = dir.appendingPathComponent("f"); try Data().write(to: file)
        XCTAssertEqual(FS.folderState(file), .missing, "a file isn't a folder")
    }

    func testMeasuringStopsAtTheDeadlineWithAPartialSize() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("devsweep-measure-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: dir) }
        for i in 0..<4500 { try Data([1]).write(to: dir.appendingPathComponent("f\(i)")) }
        let full = FS.measure(dir, until: nil)
        XCTAssertTrue(full.complete)
        XCTAssertEqual(full.files, 4500)
        let cut = FS.measure(dir, until: Date.distantPast)
        XCTAssertFalse(cut.complete, "a deadline already passed stops early")
        XCTAssertGreaterThan(cut.bytes, 0, "and keeps what it counted")
        XCTAssertLessThan(cut.files, 4500)
        let both = FS.measure([dir, dir], until: nil)
        XCTAssertEqual(both.files, 9000, "several folders add up")
    }

    func testDurations() {
        XCTAssertEqual(ScanReport.duration(0.4), "under a second")
        XCTAssertEqual(ScanReport.duration(16.2), "16 s")
        XCTAssertEqual(ScanReport.duration(130), "2 min 10 s")
    }
}
