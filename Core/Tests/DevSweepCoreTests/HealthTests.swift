import XCTest
@testable import DevSweepCore

final class HealthTests: XCTestCase {
    var home: URL!

    override func setUpWithError() throws {
        home = FileManager.default.temporaryDirectory.appendingPathComponent("devsweep-health-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: home)
    }

    @discardableResult
    private func write(_ rel: String, _ text: String, executable: Bool = false) throws -> URL {
        let url = home.appendingPathComponent(rel)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try text.write(to: url, atomically: true, encoding: .utf8)
        if executable { try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: url.path) }
        return url
    }

    private func git(_ dir: String, _ args: String...) {
        Shell.run(["git", "-C", home.appendingPathComponent(dir).path] + args)
    }

    // MARK: - Security

    func testFindsSecretsByNameAndShapeWithoutKeepingThem() throws {
        try write("Downloads/github-recovery-codes.txt", "abcde-12345\nfghij-67890\n")
        try write("Downloads/old/deploy.pem", "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----\n")
        let account = #"{"type": "service_account", "private_key": "-----BEGIN PRIVATE KEY-----x", "client_email": "a@b.iam"}"#
        try write("Downloads/firebase-adminsdk.json", account)
        try write("Downloads/creds.b64", Data(account.utf8).base64EncodedString())
        try write("Desktop/admin_accessKeys.csv", "Access key ID,Secret access key\nAKIA,xyz\n")
        try write("Documents/Chrome Passwords.csv", "name,url,username,password\n")
        try write("Downloads/notes.txt", "nothing secret here")

        let found = SecurityScanner.scan(home: home, projectRoots: [])
        let kinds = Dictionary(grouping: found, by: \.kind)
        XCTAssertEqual(kinds[.recoveryCodes]?.first?.title, "GitHub recovery codes")
        XCTAssertEqual(kinds[.privateKey]?.count, 1)
        XCTAssertEqual(kinds[.serviceAccountKey]?.count, 2, "plain and base64-encoded")
        XCTAssertEqual(kinds[.cloudAccessKeys]?.count, 1)
        XCTAssertEqual(kinds[.passwordExport]?.count, 1)
        XCTAssertFalse(found.contains { $0.path.hasSuffix("notes.txt") })

        let encoded = String(decoding: try JSONEncoder().encode(found), as: UTF8.self)
        for secret in ["abcde-12345", "AKIA", "BEGIN PRIVATE KEY-----x"] {
            XCTAssertFalse(encoded.contains(secret), "findings must never contain the secret itself")
        }
    }

    func testEnvFilesAreJudgedByGitStatus() throws {
        for repo in ["tracked", "ignored", "loose"] {
            try write("Code/\(repo)/.env", "API_KEY=sk-123\nDEBUG=true\n")
            try write("Code/\(repo)/.env.example", "API_KEY=\n")
            git("Code/\(repo)", "init", "-q")
        }
        try write("Code/ignored/.gitignore", ".env\n")
        git("Code/tracked", "add", ".env")
        try write("Code/nogit/.env", "SECRET_TOKEN=abc\n")

        let found = SecurityScanner.scan(home: home, projectRoots: ["~/Code", "~/code"])
        func status(_ project: String) -> String? { found.first { $0.path.contains("/\(project)/.env") && !$0.path.hasSuffix("example") }?.status }
        XCTAssertEqual(status("tracked"), "Committed to git")
        XCTAssertEqual(status("ignored"), "Kept out of git")
        XCTAssertEqual(status("loose"), "Not ignored")
        XCTAssertEqual(status("nogit"), "Not in git")
        XCTAssertFalse(found.contains { $0.path.hasSuffix(".env.example") }, "example files aren't secrets")
        XCTAssertEqual(found.filter { $0.path.contains("/tracked/") }.count, 1, "~/Code and ~/code are one folder")
        XCTAssertEqual(found.first { $0.status == "Committed to git" }?.level, .critical)
    }

    // MARK: - Project requirements

    func testReadsRequirementsFromCommonFiles() throws {
        try write("Code/web/package.json", #"{"name":"web","engines":{"node":">=18.0.0"}}"#)
        try write("Code/web/.nvmrc", "v20.11.0\n")
        try write("Code/api/pyproject.toml", "[project]\nrequires-python = \">=3.10\"\n")
        try write("Code/svc/go.mod", "module x\n\ngo 1.21\n")
        try write("Code/app/build.gradle.kts", "kotlin {\n    jvmToolchain(17)\n}\n")
        try write("Code/tools/.tool-versions", "nodejs 18.19.0\nruby 3.2.2\n")
        try write("Code/tools/.git/HEAD", "ref: refs/heads/main\n")

        let projects = ProjectRequirementScanner.projects(in: ["~/Code"], home: home)
        let raw = projects.flatMap(ProjectRequirementScanner.requirements)
        func spec(_ project: String, _ tool: String, _ file: String) -> String? {
            raw.first { $0.project.lastPathComponent == project && $0.tool == tool && $0.file == file }?.spec
        }
        XCTAssertEqual(spec("web", "node", ".nvmrc"), "v20.11.0")
        XCTAssertEqual(spec("web", "node", "package.json"), ">=18.0.0")
        XCTAssertEqual(spec("api", "python", "pyproject.toml"), ">=3.10")
        XCTAssertEqual(spec("svc", "go", "go.mod"), ">=1.21")
        XCTAssertEqual(spec("app", "java", "build.gradle.kts"), "17")
        XCTAssertEqual(spec("tools", "ruby", ".tool-versions"), "3.2.2")
    }

    func testEvaluatesRequirementsAgainstInstalls() {
        func inst(_ v: String, _ cycle: String, isDefault: Bool = false) -> Installation {
            var i = Installation(version: v, cycle: cycle, path: "/x/\(v)", source: .homebrew, sourceDetail: "node")
            i.isDefault = isDefault
            return i
        }
        let node = RuntimeReport(id: "node", name: "Node.js", installs: [inst("23.2.0", "23", isDefault: true)],
                                 issues: [], steps: [], recommended: nil, dataSource: nil, checkedAt: nil, note: nil)
        let past = Date().addingTimeInterval(-86_400)
        let cycles = ["node": [ReleaseCycle(cycle: "16", releaseDate: nil, eolDate: past, ended: true, latest: "16.20.2", isLTS: true)]]
        let p = URL(fileURLWithPath: "/tmp/p")
        let raw = [
            ProjectRequirementScanner.Raw(project: p.appendingPathComponent("ok"), tool: "node", spec: ">=18", file: "package.json"),
            ProjectRequirementScanner.Raw(project: p.appendingPathComponent("missing"), tool: "node", spec: "20.11.0", file: ".nvmrc"),
            ProjectRequirementScanner.Raw(project: p.appendingPathComponent("old"), tool: "node", spec: "16", file: ".nvmrc"),
        ]
        let result = ProjectRequirementScanner.evaluate(raw, reports: [node], cycles: cycles, toolNames: ["node": "Node.js"])
        func status(_ name: String) -> ProjectRequirement.Status? { result.first { $0.project == name }?.status }
        XCTAssertEqual(status("ok"), .ok)
        XCTAssertEqual(status("missing"), .notInstalled)
        XCTAssertEqual(result.first { $0.project == "missing" }?.fix?.first, "brew install node@20")
        XCTAssertEqual(status("old"), .endOfLife)
        XCTAssertEqual(result.first?.status, .notInstalled, "problems are listed before projects that are fine")
    }

    // MARK: - Ports

    func testParsesLsofAndMergesIPv4AndIPv6() {
        let sample = """
        p836
        cpostgres
        f7
        n127.0.0.1:5432
        f8
        n[::1]:5432
        p14856
        cnode
        f20
        n*:4321
        """
        let entries = PortScanner.parse(sample)
        XCTAssertEqual(entries.map(\.port), [4321, 5432])
        XCTAssertEqual(entries.first { $0.port == 5432 }?.network, false)
        XCTAssertEqual(entries.first { $0.port == 4321 }?.network, true, "* means every interface")
        let (label, dev) = PortScanner.describe(command: "postgres", args: "/opt/homebrew/opt/postgresql@14/bin/postgres -D x",
                                                folder: nil, installs: [], home: home)
        XCTAssertEqual(label, "PostgreSQL 14 (Homebrew)")
        XCTAssertTrue(dev)
    }

    // MARK: - More tools

    func testDetectsRustDotnetFlutterAndConda() throws {
        try write(".rustup/toolchains/stable-aarch64-apple-darwin/bin/rustc", "#!/bin/sh\necho 'rustc 1.75.0 (82e1608df 2023-12-21)'\n", executable: true)
        try write(".dotnet/sdk/8.0.100/dotnet.dll", "")
        try write(".dotnet/sdk/6.0.400/dotnet.dll", "")
        try write("development/flutter/bin/cache/flutter.version.json", #"{"frameworkVersion":"3.24.3"}"#)
        try write("miniconda3/bin/python3", "#!/bin/sh\necho 'Python 3.12.4'\n", executable: true)
        try write("miniconda3/envs/ml/bin/python3", "#!/bin/sh\necho 'Python 3.10.14'\n", executable: true)

        let probe = RuntimeProbe(home: home, env: ShellEnvironment(path: [], javaHome: nil), processArgs: [])
        XCTAssertEqual(probe.rust().map(\.version), ["1.75.0"])
        XCTAssertEqual(probe.rust().first?.cycle, "1.75")
        XCTAssertEqual(Set(probe.dotnet().map(\.cycle)), ["8", "6"])
        XCTAssertEqual(probe.flutter().first?.version, "3.24.3")
        let conda = probe.conda()
        XCTAssertEqual(Set(conda.map(\.sourceDetail)), ["base", "env ml"])
        let env = conda.first { $0.sourceDetail == "env ml" }!
        XCTAssertEqual(Commands.remove(env, tool: "python"), ["conda env remove -n ml"])
    }
}
