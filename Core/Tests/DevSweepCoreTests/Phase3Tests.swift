import XCTest
@testable import DevSweepCore

final class Phase3Tests: XCTestCase {
    var home: URL!
    let day: TimeInterval = 86_400

    override func setUpWithError() throws {
        home = FileManager.default.temporaryDirectory.appendingPathComponent("devsweep-p3-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: home)
    }

    @discardableResult
    private func file(_ rel: String, bytes: Int = 10, modified: Date? = nil) throws -> URL {
        let url = home.appendingPathComponent(rel)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(repeating: 7, count: bytes).write(to: url)
        if let modified { try FileManager.default.setAttributes([.modificationDate: modified], ofItemAtPath: url.path) }
        return url
    }

    private func rule(_ kind: DetectorSpec.Kind, _ configure: (inout DetectorSpec) -> Void = { _ in }) -> Rule {
        var spec = DetectorSpec(kind: kind)
        configure(&spec)
        return Rule(id: "t", title: "t", category: .largeFiles, risk: .holdsData, detector: spec,
                    explain: Explanation(what: "", why: "", ifDeleted: ""), actions: [CleanAction(kind: .trash)])
    }

    // MARK: - Docker

    func testParsesDockerSizesAndSystemDF() {
        XCTAssertEqual(Detectors.dockerBytes("1.2GB (45%)"), 1_200_000_000)
        XCTAssertEqual(Detectors.dockerBytes("512.5MB"), 512_500_000)
        XCTAssertEqual(Detectors.dockerBytes("0B"), 0)
        XCTAssertEqual(Detectors.dockerBytes("15kB"), 15_000)
        let df = """
        {"Active":"2","Reclaimable":"3.1GB (71%)","Size":"4.4GB","TotalCount":"9","Type":"Images"}
        {"Active":"0","Reclaimable":"812MB","Size":"812MB","TotalCount":"0","Type":"Build Cache"}
        """
        let rows = Detectors.parseDockerDF(df)
        XCTAssertEqual(rows["Images"]?["TotalCount"], "9")
        XCTAssertEqual(Detectors.dockerBytes(rows["Build Cache"]?["Reclaimable"] ?? ""), 812_000_000)
    }

    // MARK: - Large old files

    func testFindsOnlyLargeFilesUnusedForLong() throws {
        let old = Date().addingTimeInterval(-400 * day)
        try file("Downloads/old-video.mov", bytes: 3_000_000, modified: old)
        try file("Downloads/recent-video.mov", bytes: 3_000_000)
        try file("Downloads/small-old.txt", bytes: 1_000, modified: old)
        try file("Downloads/Thing.app/Contents/huge.bin", bytes: 3_000_000, modified: old)
        try file("Downloads/Installer.dmg", bytes: 3_000_000, modified: old)

        let ctx = ScanContext(home: home)
        let large = Detectors.largeOldFiles(rule(.largeOldFiles) { $0.roots = ["~/Downloads"]; $0.fileMinMB = 2; $0.unusedDays = 120 }, ctx)
        XCTAssertEqual(Set(large.map { $0.paths[0].lastPathComponent }), ["old-video.mov", "Installer.dmg"],
                       "skips recent files, small files and anything inside app bundles")

        let installers = Detectors.largeOldFiles(rule(.largeOldFiles) {
            $0.roots = ["~/Downloads"]; $0.fileMinMB = 2; $0.unusedDays = 14; $0.extensions = ["dmg", "pkg"]
        }, ctx)
        XCTAssertEqual(installers.map { $0.paths[0].lastPathComponent }, ["Installer.dmg"])
    }

    // MARK: - iPhone backups

    private func backup(_ id: String, device: String, udid: String, daysAgo: Double) throws {
        let dir = home.appendingPathComponent("Library/Application Support/MobileSync/Backup/\(id)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let info: [String: Any] = ["Device Name": device, "Target Identifier": udid, "Product Name": "iPhone 13",
                                   "Last Backup Date": Date().addingTimeInterval(-daysAgo * day)]
        try PropertyListSerialization.data(fromPropertyList: info, format: .xml, options: 0).write(to: dir.appendingPathComponent("Info.plist"))
    }

    func testFlagsOldAndSupersededBackups() throws {
        try backup("a-old", device: "Karunesh's iPhone", udid: "AAA", daysAgo: 300)
        try backup("a-new", device: "Karunesh's iPhone", udid: "AAA", daysAgo: 2)
        try backup("b-only", device: "Old iPad", udid: "BBB", daysAgo: 400)
        try backup("c-recent", device: "Work iPhone", udid: "CCC", daysAgo: 10)

        let found = Detectors.iosBackups(rule(.iosBackups) { $0.unusedDays = 90 }, ScanContext(home: home))
        let keys = Set(found.map(\.key))
        XCTAssertEqual(keys, ["a-old", "b-only"], "the newest recent backup of each device is kept")
        let superseded = found.first { $0.key == "a-old" }!
        XCTAssertTrue(superseded.checks.contains { $0.status == .passed && $0.text.contains("newer backup") })
        let only = found.first { $0.key == "b-only" }!
        XCTAssertTrue(only.checks.contains { $0.status == .warning }, "warns when it's the only backup")
    }

    // MARK: - Time Machine

    func testCountsOnlyTimeMachineSnapshots() {
        let text = """
        Snapshots for volume group containing disk /:
        com.apple.os.update-39AFBADD5AD7
        com.apple.TimeMachine.2026-10-01-101500.local
        com.apple.TimeMachine.2026-10-01-111500.local
        """
        XCTAssertEqual(Detectors.timeMachineSnapshotNames(text).count, 2)
    }

    // MARK: - Browser cache per profile

    func testChromeCacheIsOneItemWithAPartPerProfile() async throws {
        let home = FileManager.default.temporaryDirectory.appendingPathComponent("devsweep-chrome-\(UUID().uuidString)")
        addTeardownBlock { try? FileManager.default.removeItem(at: home) }
        let caches = home.appendingPathComponent("Library/Caches/Google/Chrome")
        for d in ["Default", "Profile 3", "Profile 7", "Guest Profile"] {
            try FileManager.default.createDirectory(at: caches.appendingPathComponent(d + "/Cache"), withIntermediateDirectories: true)
        }
        let profiles = home.appendingPathComponent("Library/Application Support/Google/Chrome")
        try FileManager.default.createDirectory(at: profiles, withIntermediateDirectories: true)
        let state = #"{"profile":{"last_used":"Default","info_cache":{"Default":{"name":"Karunesh","user_name":"me@example.com","active_time":1792158369},"Profile 3":{"name":"Work","user_name":"","active_time":1700000000},"Profile 7":{"name":"Karunesh","user_name":"other@example.com","active_time":1791000000}}}}"#
        try Data(state.utf8).write(to: profiles.appendingPathComponent("Local State"))

        var spec = DetectorSpec(kind: .browserProfileCaches)
        spec.browser = "Chrome"; spec.cacheRoot = "~/Library/Caches/Google/Chrome"; spec.profileRoot = "~/Library/Application Support/Google/Chrome"
        let r = Rule(id: "chrome-cache", title: "Chrome cache", category: .browsers, risk: .rebuilds, detector: spec,
                     explain: Explanation(what: "", why: "", ifDeleted: ""), actions: [CleanAction(kind: .delete)])
        let found = Detectors.browserProfileCaches(r, ScanContext(home: home))
        XCTAssertEqual(found.count, 1, "one item for the browser")
        let c = try XCTUnwrap(found.first)
        XCTAssertEqual(c.title, "Chrome cache")
        XCTAssertEqual(c.paths.count, 4)
        let parts = try XCTUnwrap(c.parts)
        let byFolder = Dictionary(uniqueKeysWithValues: zip(c.paths.map(\.lastPathComponent), parts).map { ($0, $1) })
        XCTAssertEqual(byFolder["Profile 3"]?.title, "Work")
        XCTAssertEqual(byFolder["Guest Profile"]?.title, "Guest")
        XCTAssertTrue(byFolder["Default"]?.subtitle.contains("Default") == true, "two profiles named Karunesh show their folder")
        XCTAssertTrue(byFolder["Default"]?.subtitle.contains("the profile Chrome opens with") == true)
        XCTAssertTrue(byFolder["Profile 7"]?.subtitle.contains("other@example.com") == true)
        XCTAssertFalse(byFolder["Profile 3"]?.subtitle.contains("Profile 3") == true, "a unique name needs no folder")

        // Through the scanner: each profile gets its own measured size.
        try Data(repeating: 1, count: 3_000_000).write(to: caches.appendingPathComponent("Profile 3/Cache/data"))
        try Data(repeating: 1, count: 1_000_000).write(to: caches.appendingPathComponent("Default/Cache/data"))
        var rr = r; rr.minSizeMB = 1
        let result = await Scanner(rules: [rr], context: ScanContext(home: home)).scan()
        let f = try XCTUnwrap(result.findings.first)
        let fp = try XCTUnwrap(f.parts)
        XCTAssertEqual(fp.first?.title, "Work", "parts are sorted biggest first")
        XCTAssertEqual(fp.reduce(0) { $0 + $1.size }, f.size)
        XCTAssertGreaterThan(fp.first?.size ?? 0, 2_900_000)

        // Leaving parts out in Review.
        let keepOne = try XCTUnwrap(f.restricted(excluding: Set(fp.dropFirst().map(\.path))))
        XCTAssertEqual(keepOne.paths, [fp[0].path])
        XCTAssertEqual(keepOne.size, fp[0].size)
        XCTAssertEqual(keepOne.title, "Chrome cache · 1 of 4 profiles")
        XCTAssertNil(f.restricted(excluding: Set(fp.map(\.path))), "nothing left means nothing to clean")
        XCTAssertEqual(f.restricted(excluding: [])?.title, "Chrome cache")
    }

    // MARK: - Chrome profiles

    func testFlagsUnusedChromeProfilesButNotTheCurrentOne() throws {
        let base = "Library/Application Support/Google/Chrome"
        let now = Date().timeIntervalSince1970
        let state: [String: Any] = ["profile": [
            "last_used": "Profile 9",
            "info_cache": [
                "Default": ["name": "Personal", "active_time": now - 2 * day],
                "Profile 3": ["name": "Old work", "user_name": "me@old.example", "active_time": now - 400 * day],
                "Profile 7": ["name": "Testing", "active_time": now - 300 * day],
                "Profile 9": ["name": "Opens first", "active_time": now - 500 * day],
            ],
        ]]
        try file("\(base)/Profile 3/Bookmarks", bytes: 100)
        try file("\(base)/Profile 7/Bookmarks", bytes: 100)
        let data = try JSONSerialization.data(withJSONObject: state)
        try data.write(to: home.appendingPathComponent("\(base)/Local State"))

        let found = Detectors.chromeProfiles(rule(.chromeProfiles) { $0.unusedDays = 180 }, ScanContext(home: home))
        XCTAssertEqual(Set(found.map(\.key)), ["Profile 3", "Profile 7"])
        XCTAssertTrue(found.first { $0.key == "Profile 3" }!.checks.contains { $0.text.contains("me@old.example") })
        XCTAssertTrue(found.first { $0.key == "Profile 7" }!.checks.contains { $0.status == .warning }, "unsynced profile warns")
    }

    func testPhase3RulesLoad() {
        let ids = Set(RuleLoader.loadAll().rules.map(\.id))
        for id in ["docker-build-cache", "docker-images", "docker-containers", "docker-volumes", "old-installers",
                   "large-old-files", "ios-backups", "time-machine-snapshots", "chrome-profiles"] {
            XCTAssertTrue(ids.contains(id), id)
        }
    }
}

final class BlockerTests: XCTestCase {
    func testVSCodeIsMatchedByBundleNameNotByTheWordCode() {
        // VS Code's display name is "Code"; its bundle is "Visual Studio Code.app".
        let vscode = ScanContext.isRunning("Visual Studio Code", appNames: ["code", "finder"],
                                           bundleNames: ["visual studio code", "finder"], processArgs: [])
        XCTAssertTrue(vscode)

        // A program running from a folder called Code is not VS Code.
        let args = ["/Users/me/Code/karuneshpalekar/DevSweep/build/DevSweep.app/Contents/MacOS/DevSweep"]
        XCTAssertFalse(ScanContext.isRunning("Visual Studio Code", appNames: ["finder"], bundleNames: ["finder"], processArgs: args))
        XCTAssertFalse(ScanContext.isRunning("Code", appNames: ["finder"], bundleNames: ["finder"], processArgs: args),
                       "short names are never matched against command lines")
    }

    func testProcessStyleBlockersStillMatchCommandLines() {
        let args = ["/opt/homebrew/bin/java -Dfoo org.gradle.launcher.daemon.bootstrap.GradleDaemon 8.11"]
        XCTAssertTrue(ScanContext.isRunning("GradleDaemon", appNames: [], bundleNames: [], processArgs: args))
        XCTAssertFalse(ScanContext.isRunning("GradleDaemon", appNames: [], bundleNames: [], processArgs: ["/usr/bin/true"]))
        XCTAssertTrue(ScanContext.isRunning("Google Chrome", appNames: ["google chrome"], bundleNames: ["google chrome"], processArgs: []))
    }

    func testVSCodeRuleOnlyWaitsForTheApp() {
        let rule = RuleLoader.loadAll().rules.first { $0.id == "vscode-caches" }
        XCTAssertEqual(rule?.blockers, ["Visual Studio Code"])
    }
}
