import XCTest
@testable import DevSweepCore

final class RuntimeTests: XCTestCase {
    let day: TimeInterval = 86_400

    private func cycles(_ json: String) throws -> [ReleaseCycle] {
        try JSONDecoder().decode([ReleaseCycle].self, from: Data(json.utf8))
    }

    func testDecodesDatesAndBooleans() throws {
        let c = try cycles("""
        [{"cycle":"24","releaseDate":"2025-05-06","eol":"2099-04-30","latest":"24.9.0","lts":"2025-10-28"},
         {"cycle":"1.27","releaseDate":"2026-08-19","eol":false,"latest":"1.27.1","lts":false},
         {"cycle":2.6,"releaseDate":"2018-12-25","eol":true,"latest":"2.6.10","lts":false}]
        """)
        XCTAssertEqual(c.map(\.cycle), ["24", "1.27", "2.6"])
        XCTAssertTrue(c[0].isLTS)
        XCTAssertEqual(c[0].status(), .supported)
        XCTAssertEqual(c[1].status(), .supported, "no end date announced")
        XCTAssertEqual(c[2].status(), .endOfLife)
    }

    func testEndingSoonWindow() {
        let soon = ReleaseCycle(cycle: "14", releaseDate: nil, eolDate: Date().addingTimeInterval(40 * day), ended: false, latest: nil, isLTS: false)
        let later = ReleaseCycle(cycle: "15", releaseDate: nil, eolDate: Date().addingTimeInterval(400 * day), ended: false, latest: nil, isLTS: false)
        XCTAssertEqual(soon.status(), .endingSoon)
        XCTAssertEqual(later.status(), .supported)
    }

    func testReleaseLines() {
        XCTAssertEqual(RuntimeProbe.major("23.2.0"), "23")
        XCTAssertEqual(RuntimeProbe.majorMinor("3.12.2"), "3.12")
        XCTAssertEqual(RuntimeProbe.pgCycle("14.19"), "14")
        XCTAssertEqual(RuntimeProbe.pgCycle("9.6.24"), "9.6")
    }

    private func node(_ version: String, _ source: InstallSource, isDefault: Bool = false, detail: String? = nil) -> Installation {
        var i = Installation(version: version, cycle: RuntimeProbe.major(version), path: "/x/\(version)-\(source)", source: source, sourceDetail: detail)
        i.isDefault = isDefault
        return i
    }

    private var nodeCycles: [ReleaseCycle] {
        let past = Date().addingTimeInterval(-100 * day), future = Date().addingTimeInterval(900 * day)
        return [
            ReleaseCycle(cycle: "25", releaseDate: past, eolDate: future, ended: false, latest: "25.1.0", isLTS: false),
            ReleaseCycle(cycle: "24", releaseDate: past, eolDate: future, ended: false, latest: "24.9.0", isLTS: true),
            ReleaseCycle(cycle: "23", releaseDate: past, eolDate: past, ended: true, latest: "23.11.1", isLTS: false),
        ]
    }

    func testEndOfLifeDefaultGetsSwitchStepToLTS() {
        let tool = RuntimeScanner.tools.first { $0.id == "node" }!
        let r = RuntimeScanner.report(tool, [node("23.2.0", .homebrew, isDefault: true, detail: "node")], nodeCycles, Date())
        XCTAssertEqual(r.installs[0].support, .endOfLife)
        XCTAssertTrue(r.issues.contains { $0.level == .critical && $0.text.contains("shell runs") })
        let step = r.steps.first { $0.kind == .switchDefault }
        XCTAssertEqual(step?.title, "Switch to Node.js 24 LTS", "prefers the LTS line over the newer non-LTS 25")
        XCTAssertEqual(step?.commands.first, "brew install node@24")
    }

    func testMixedInstallersAndShadowingAreFlagged() {
        let tool = RuntimeScanner.tools.first { $0.id == "node" }!
        let r = RuntimeScanner.report(tool, [node("24.1.0", .nvm, isDefault: false), node("23.2.0", .homebrew, isDefault: true, detail: "node")],
                                      nodeCycles, Date())
        XCTAssertTrue(r.issues.contains { $0.text.hasPrefix("Installed by 2 different tools") })
        XCTAssertTrue(r.issues.contains { $0.text.contains("comes first in your PATH") })
    }

    func testSystemInstallsAreNeverOfferedForRemoval() {
        let tool = RuntimeScanner.tools.first { $0.id == "python" }!
        var system = Installation(version: "3.9.6", cycle: "3.9", path: "/Library/Developer/CommandLineTools/x", source: .apple)
        system.isDefault = false
        let old = ReleaseCycle(cycle: "3.9", releaseDate: nil, eolDate: Date().addingTimeInterval(-day), ended: true, latest: "3.9.25", isLTS: false)
        let r = RuntimeScanner.report(tool, [system], [old], Date())
        XCTAssertTrue(r.steps.isEmpty)
        XCTAssertTrue(r.issues.allSatisfy { $0.level == .info })
    }

    func testPostgresUpgradeScriptBacksUpBeforeSwitching() {
        let lines = PostgresUpgrade.script(from: "postgresql@14", to: "postgresql@18")
        let backup = lines.firstIndex { $0.contains("pg_dumpall") }!
        let stop = lines.firstIndex { $0.contains("brew services stop") }!
        let uninstall = lines.firstIndex { $0.contains("brew uninstall") }!
        XCTAssertLessThan(backup, stop)
        XCTAssertLessThan(stop, uninstall)
        XCTAssertTrue(lines[uninstall - 1].hasPrefix("ask "), "asks before removing the old version")
    }
}

final class TerminalScriptTests: XCTestCase {
    /// zsh -n parses a script without running anything.
    private func assertValidZsh(_ script: String, _ label: String, file: StaticString = #filePath, line: UInt = #line) throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("devsweep-\(UUID().uuidString).zsh")
        try script.write(to: url, atomically: true, encoding: .utf8)
        defer { try? FileManager.default.removeItem(at: url) }
        let r = Shell.run(["/bin/zsh", "-n", url.path])
        XCTAssertTrue(r.ok, "\(label): \(r.stderr)", file: file, line: line)
    }

    func testGuidedPostgresScriptParses() throws {
        let step = RuntimeStep(kind: .guided, title: "Upgrade PostgreSQL 14 to 18", detail: "",
                               commands: PostgresUpgrade.script(from: "postgresql@14", to: "postgresql@18"))
        try assertValidZsh(step.terminalScript, "postgres")
        XCTAssertTrue(step.terminalScript.contains("read \"?Press Return"), "waits before running")
    }

    func testEveryRemoveAndInstallCommandParses() throws {
        let sources: [InstallSource] = [.homebrew, .nvm, .fnm, .volta, .asdf, .mise, .pyenv, .uv, .rbenv, .rvm, .sdkman,
                                        .pythonOrg, .nodejsOrg, .goOrg, .jdkFolder]
        for tool in ["node", "python", "java", "go", "ruby", "postgres"] {
            for source in sources {
                let i = Installation(version: "3.12.2", cycle: "3.12", path: "/Users/test/it's here", source: source, sourceDetail: "thing@1")
                for cmds in [Commands.remove(i, tool: tool), Commands.install(tool: tool, cycle: "24", via: source)].compactMap({ $0 }) {
                    let step = RuntimeStep(kind: .remove, title: "Test \(tool) \(source)", detail: "", commands: cmds)
                    try assertValidZsh(step.terminalScript, "\(tool)/\(source)")
                }
            }
        }
    }
}
