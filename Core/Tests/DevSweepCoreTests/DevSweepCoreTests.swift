import XCTest
@testable import DevSweepCore

final class DevSweepCoreTests: XCTestCase {
    var home: URL!

    override func setUpWithError() throws {
        home = FileManager.default.temporaryDirectory.appendingPathComponent("devsweep-test-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: home)
    }

    private func mkdir(_ rel: String, file: String? = nil, bytes: Int = 10) throws {
        let dir = home.appendingPathComponent(rel)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        if let file { try Data(count: bytes).write(to: dir.appendingPathComponent(file)) }
    }

    func testVersionOrdering() {
        XCTAssertTrue(Version.less("1.9.23", "2.0.0"))
        XCTAssertTrue(Version.less("29.0.3", "30.0.2"))
        XCTAssertTrue(Version.less("2024.2", "2025.3.4"))
        XCTAssertFalse(Version.less("36.0.0", "34.0.0"))
    }

    func testTemplateRendering() {
        XCTAssertEqual(Template.render("{{size}} in {{ count }} places{{missing}}", ["size": "1 GB", "count": "3"]),
                       "1 GB in 3 places")
        XCTAssertEqual(Template.render("no braces", [:]), "no braces")
    }

    func testBundleIDParsing() {
        XCTAssertEqual(Detectors.bundleID(fromName: "com.openai.atlas.plist"), "com.openai.atlas")
        XCTAssertEqual(Detectors.bundleID(fromName: "JQ525L2MZD.com.adobe.JQ525.flags"), "com.adobe.JQ525.flags")
        XCTAssertEqual(Detectors.bundleID(fromName: "group.ai.perplexity.app"), "ai.perplexity.app")
        XCTAssertNil(Detectors.bundleID(fromName: "Google"))
        XCTAssertNil(Detectors.bundleID(fromName: "homebrew.mxcl.postgresql@14.plist"))
    }

    func testSafetyRefusesProtectedAndShallowPaths() {
        XCTAssertThrowsError(try Safety.check(home.appendingPathComponent("Library"), home: home))
        XCTAssertThrowsError(try Safety.check(home.appendingPathComponent("Projects"), home: home))
        XCTAssertThrowsError(try Safety.check(URL(fileURLWithPath: "/usr/local/bin"), home: home))
        XCTAssertThrowsError(try Safety.check(home.appendingPathComponent("Library/Caches/../"), home: home))
        XCTAssertNoThrow(try Safety.check(home.appendingPathComponent(".konan"), home: home))
        XCTAssertNoThrow(try Safety.check(home.appendingPathComponent("Library/Caches/pip"), home: home))
    }

    func testVersionedSiblingsKeepsNewest() throws {
        for v in ["28.0.3", "30.0.2", "34.0.0", "35.0.0", "36.0.0"] {
            try mkdir("sdk/build-tools/\(v)", file: "aapt")
        }
        let rule = Rule(id: "bt", title: "Old build tools", category: .android, risk: .oldVersion,
                        detector: DetectorSpec(kind: .versionedSiblings, parents: ["~/sdk/build-tools"],
                                               pattern: "^(?<version>[0-9][0-9.]*)$", keep: 3),
                        explain: Explanation(what: "", why: "{{versions}} / {{kept}}", ifDeleted: ""),
                        actions: [CleanAction(kind: .trash)])
        let found = Detectors.run(rule, ScanContext(home: home))
        XCTAssertEqual(found.count, 1)
        XCTAssertEqual(Set(found[0].paths.map(\.lastPathComponent)), ["28.0.3", "30.0.2"])
        XCTAssertEqual(found[0].facts["kept"], "36.0.0, 35.0.0 and 34.0.0")
    }

    func testTrashAndRestoreRoundTrip() throws {
        try mkdir(".konan/kotlin-native-prebuilt-macos-aarch64-1.9.20", file: "konanc", bytes: 4096)
        let target = home.appendingPathComponent(".konan/kotlin-native-prebuilt-macos-aarch64-1.9.20")
        let finding = Finding(
            id: "t:1", ruleID: "t", title: "Test", subtitle: "", category: .toolchains, risk: .oldVersion,
            paths: [target.path], size: 4096,
            explanation: Explanation(what: "", why: "", ifDeleted: ""), checks: [],
            actions: [CleanAction(kind: .trash)], blockingApps: [], blockers: []
        )
        let history = HistoryStore(url: home.appendingPathComponent("history.json"))
        let outcomes = Cleaner(history: history, home: home)
            .run([PlannedAction(finding: finding, action: CleanAction(kind: .trash))])

        XCTAssertTrue(outcomes[0].succeeded, outcomes[0].message)
        XCTAssertFalse(FileManager.default.fileExists(atPath: target.path))
        XCTAssertEqual(history.entries.count, 1)
        XCTAssertTrue(history.entries[0].canRestore)

        XCTAssertEqual(try history.restore(history.entries[0].id), 1)
        XCTAssertTrue(FileManager.default.fileExists(atPath: target.appendingPathComponent("konanc").path))
    }

    func testBundledRulesParse() {
        let (rules, errors) = RuleLoader.loadAll()
        XCTAssertEqual(errors, [])
        XCTAssertGreaterThan(rules.count, 20)
        XCTAssertEqual(Set(rules.map(\.id)).count, rules.count, "rule ids must be unique")
        for r in rules { XCTAssertFalse(r.actions.isEmpty, "\(r.id) has no actions") }
    }
}
