import XCTest
@testable import DevSweepCore

final class AIToolTests: XCTestCase {
    private func tempHome() throws -> URL {
        let home = FileManager.default.temporaryDirectory.appendingPathComponent("devsweep-ai-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: home) }
        return home
    }

    private func write(_ home: URL, _ path: String, _ data: Data) throws {
        let url = home.appendingPathComponent(path)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try data.write(to: url)
    }

    private func rule(_ kind: DetectorSpec.Kind, paths: [String]) -> Rule {
        var spec = DetectorSpec(kind: kind)
        spec.paths = paths
        return Rule(id: "t", title: "{{name}}", category: .aiModels, risk: .holdsData, detector: spec,
                    explain: Explanation(what: "", why: "", ifDeleted: ""), actions: [CleanAction(kind: .trash)])
    }

    func testOllamaModelsKeepBlobsAnotherModelUses() throws {
        let home = try tempHome()
        let m = ".ollama/models"
        func manifest(_ digests: [String]) -> Data {
            let layers = digests.dropFirst().map { "{\"digest\":\"\($0)\",\"size\":1}" }.joined(separator: ",")
            return Data("{\"config\":{\"digest\":\"\(digests[0])\"},\"layers\":[\(layers)]}".utf8)
        }
        try write(home, "\(m)/manifests/registry.ollama.ai/library/llama3/8b", manifest(["sha256:cfg1", "sha256:shared", "sha256:w1"]))
        try write(home, "\(m)/manifests/registry.ollama.ai/library/llama3/latest", manifest(["sha256:cfg2", "sha256:shared", "sha256:w2"]))
        try write(home, "\(m)/manifests/registry.ollama.ai/someone/coder/7b", manifest(["sha256:cfg3", "sha256:w3"]))
        for b in ["cfg1", "cfg2", "cfg3", "shared", "w1", "w2", "w3"] { try write(home, "\(m)/blobs/sha256-\(b)", Data([1])) }

        let found = Detectors.ollamaModels(rule(.ollamaModels, paths: ["~/.ollama/models"]), ScanContext(home: home))
        let byKey = Dictionary(uniqueKeysWithValues: found.map { ($0.key, $0) })
        XCTAssertEqual(Set(byKey.keys), ["llama3:8b", "llama3:latest", "someone/coder:7b"], "library models drop the registry and namespace")
        let llama = try XCTUnwrap(byKey["llama3:8b"])
        let names = Set(llama.paths.map(\.lastPathComponent))
        XCTAssertEqual(names, ["8b", "sha256-cfg1", "sha256-w1"], "its manifest and its own blobs, not the shared one")
        XCTAssertEqual(llama.title, "Ollama · llama3:8b")
        XCTAssertTrue(llama.checks.contains { $0.text.contains("another model also uses") })
        XCTAssertEqual(byKey["someone/coder:7b"]?.paths.count, 3)
    }

    func testLMStudioListsOneItemPerModelFolder() throws {
        let home = try tempHome()
        try write(home, ".lmstudio/models/lmstudio-community/Qwen2.5-7B-GGUF/model.gguf", Data([1]))
        try write(home, ".lmstudio/models/bartowski/Llama-3-8B-GGUF/model.gguf", Data([1]))
        try write(home, ".cache/lm-studio/models/lmstudio-community/Qwen2.5-7B-GGUF/model.gguf", Data([1]))
        let found = Detectors.lmStudioModels(rule(.lmStudioModels, paths: ["~/.lmstudio/models", "~/.cache/lm-studio/models"]), ScanContext(home: home))
        XCTAssertEqual(Set(found.map(\.key)), ["lmstudio-community/Qwen2.5-7B-GGUF", "bartowski/Llama-3-8B-GGUF"],
                       "the same model in the old location isn't listed twice")
        XCTAssertTrue(found.contains { $0.title == "LM Studio · Llama-3-8B-GGUF" })
    }

    func testEditorCachesOnlyWhileTheAppIsInstalled() throws {
        let home = try tempHome()
        try write(home, "Library/Application Support/Zap/Cache/data", Data([1]))
        var spec = DetectorSpec(kind: .paths)
        spec.paths = ["~/Library/Application Support/Zap/Cache"]
        spec.requireApps = ["Zap-not-installed-\(UUID().uuidString).app"]
        let r = Rule(id: "zap", title: "Zap caches", category: .ideVersions, risk: .rebuilds, detector: spec,
                     explain: Explanation(what: "", why: "", ifDeleted: ""), actions: [CleanAction(kind: .delete)])
        XCTAssertTrue(Detectors.paths(r, ScanContext(home: home)).isEmpty, "an uninstalled app's folders are left to the leftovers check")
        try FileManager.default.createDirectory(at: home.appendingPathComponent("Applications/Zap.app"), withIntermediateDirectories: true)
        var installed = r
        installed.detector.requireApps = ["Zap.app"]
        XCTAssertEqual(Detectors.paths(installed, ScanContext(home: home)).count, 1, "found in ~/Applications")
    }

    func testNewRulesLoad() {
        let (rules, errors) = RuleLoader.loadAll()
        XCTAssertTrue(errors.isEmpty, "\(errors)")
        let ids = Set(rules.map(\.id))
        for id in ["cursor-caches", "windsurf-caches", "antigravity-caches", "kiro-caches", "trae-caches", "void-caches",
                   "vscode-insiders-caches", "claude-desktop-caches", "ollama-models", "lmstudio-models"] {
            XCTAssertTrue(ids.contains(id), id)
        }
        let claude = rules.first { $0.id == "claude-desktop-caches" }
        XCTAssertFalse(claude?.detector.paths?.contains { $0.contains("vm_bundles") } ?? true, "the code-running VM is never offered")
        XCTAssertEqual(claude?.detector.requireApps, ["Claude.app"])
    }
}
