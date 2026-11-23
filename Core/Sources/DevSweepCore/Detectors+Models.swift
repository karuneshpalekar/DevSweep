import Foundation

extension Detectors {
    // MARK: - Ollama

    /// One item per Ollama model and tag. Ollama stores a small manifest per
    /// tag and shares large blob files between tags, so an item holds its
    /// manifest and only the blobs no other model uses.
    static func ollamaModels(_ rule: Rule, _ ctx: ScanContext) -> [Candidate] {
        guard let root = rule.detector.paths?.first.map({ FS.expand($0, home: ctx.home) }) else { return [] }
        let manifestsDir = root.appendingPathComponent("manifests")
        let blobsDir = root.appendingPathComponent("blobs")
        struct Model { var name: String; var manifest: URL; var digests: [String]; var date: Date? }
        var models: [Model] = []
        let fm = FileManager.default
        if let e = fm.enumerator(at: manifestsDir, includingPropertiesForKeys: [.isRegularFileKey]) {
            for case let url as URL in e {
                guard (try? url.resourceValues(forKeys: [.isRegularFileKey]))?.isRegularFile == true,
                      let data = try? Data(contentsOf: url),
                      let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { continue }
                var digests: [String] = []
                if let c = json["config"] as? [String: Any], let d = c["digest"] as? String { digests.append(d) }
                for layer in json["layers"] as? [[String: Any]] ?? [] {
                    if let d = layer["digest"] as? String { digests.append(d) }
                }
                // manifests/<host>/<namespace>/<model>/<tag>
                // Compare resolved paths: /var and /private/var are the same folder.
                let base = manifestsDir.resolvingSymlinksInPath().pathComponents
                let full = url.resolvingSymlinksInPath().pathComponents
                guard full.count > base.count, Array(full.prefix(base.count)) == base else { continue }
                let rel = Array(full.suffix(from: base.count))
                guard rel.count >= 2 else { continue }
                let tag = rel[rel.count - 1], model = rel[rel.count - 2]
                var prefix = rel.dropLast(2)
                if prefix.first == "registry.ollama.ai" { prefix = prefix.dropFirst() }
                if prefix.first == "library" { prefix = prefix.dropFirst() }
                let name = (prefix + [model]).joined(separator: "/") + ":" + tag
                models.append(Model(name: name, manifest: url, digests: digests, date: FS.modificationDate(url)))
            }
        }
        var users: [String: Int] = [:]
        for m in models { for d in Set(m.digests) { users[d, default: 0] += 1 } }
        return models.sorted { $0.name < $1.name }.map { m in
            let blobs = Set(m.digests).filter { users[$0] == 1 }
                .map { blobsDir.appendingPathComponent($0.replacingOccurrences(of: ":", with: "-")) }
                .filter { fm.fileExists(atPath: $0.path) }
            let shared = Set(m.digests).count - blobs.count
            let ago = m.date.map { RelativeDateTimeFormatter().localizedString(for: $0, relativeTo: Date()) } ?? "a while ago"
            var checks: [Check] = [.passed("Only this model's files")]
            if shared > 0 { checks.append(.passed("\(shared) file\(shared == 1 ? "" : "s") another model also uses \(shared == 1 ? "is" : "are") kept")) }
            return Candidate(
                key: m.name, paths: [m.manifest] + blobs.sorted { $0.path < $1.path },
                title: "Ollama · \(m.name)",
                subtitle: "Downloaded \(ago)",
                facts: ["name": "Ollama · \(m.name)", "model": m.name, "ago": ago],
                checks: checks
            )
        }
    }

    // MARK: - LM Studio

    /// One item per model folder: models/<publisher>/<model>.
    static func lmStudioModels(_ rule: Rule, _ ctx: ScanContext) -> [Candidate] {
        var out: [Candidate] = []
        var seen = Set<String>()
        for root in FS.uniqueDirectories(rule.detector.paths ?? [], home: ctx.home) {
            for publisher in FS.children(root) where FS.isDirectory(publisher) && !publisher.lastPathComponent.hasPrefix(".") {
                for model in FS.children(publisher) where FS.isDirectory(model) && !model.lastPathComponent.hasPrefix(".") {
                    let slug = publisher.lastPathComponent + "/" + model.lastPathComponent
                    guard seen.insert(slug).inserted else { continue }
                    let date = FS.modificationDate(model)
                    let ago = date.map { RelativeDateTimeFormatter().localizedString(for: $0, relativeTo: Date()) } ?? "a while ago"
                    out.append(Candidate(
                        key: slug, paths: [model],
                        title: "LM Studio · \(model.lastPathComponent)",
                        subtitle: "\(publisher.lastPathComponent) · downloaded \(ago)",
                        facts: ["name": "LM Studio · \(model.lastPathComponent)", "model": slug, "ago": ago],
                        checks: [.passed("Only this model's folder")]
                    ))
                }
            }
        }
        return out
    }
}
