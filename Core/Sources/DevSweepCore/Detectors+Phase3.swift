import CoreServices
import Foundation

extension Detectors {
    // MARK: - Docker

    /// Docker Desktop puts its CLI in /usr/local/bin or ~/.docker/bin.
    static func dockerCLI(_ ctx: ScanContext) -> String? {
        if let p = Shell.locate("docker") { return p }
        let own = ctx.home.appendingPathComponent(".docker/bin/docker").path
        return FileManager.default.isExecutableFile(atPath: own) ? own : nil
    }

    /// Parses Docker's sizes: "1.2GB", "512.3MB (45%)", "0B". Docker uses powers of 1000.
    static func dockerBytes(_ text: String) -> Int64 {
        guard let r = text.range(of: #"^\s*([0-9.]+)\s*([kKMGT]?B)"#, options: .regularExpression) else { return 0 }
        let part = String(text[r]).trimmingCharacters(in: .whitespaces)
        let digits = part.prefix { $0.isNumber || $0 == "." }
        let unit = part.dropFirst(digits.count).uppercased()
        let scale: Double = ["B": 1, "KB": 1e3, "MB": 1e6, "GB": 1e9, "TB": 1e12][unit] ?? 1
        return Int64((Double(digits) ?? 0) * scale)
    }

    /// `docker system df --format '{{json .}}'`, one JSON object per line, keyed by Type.
    static func parseDockerDF(_ text: String) -> [String: [String: String]] {
        var out: [String: [String: String]] = [:]
        for line in text.split(separator: "\n") {
            guard let data = line.data(using: .utf8),
                  let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let type = obj["Type"] as? String else { continue }
            out[type] = obj.mapValues { "\($0)" }
        }
        return out
    }

    static func docker(_ rule: Rule, _ ctx: ScanContext) -> [Candidate] {
        guard let type = rule.detector.names?.first, let cli = dockerCLI(ctx) else { return [] }
        // Fails quietly when Docker isn't running; there's nothing to prune then.
        let r = Shell.run([cli, "system", "df", "--format", "{{json .}}"], timeout: 30)
        guard r.ok, let row = parseDockerDF(r.stdout)[type] else { return [] }
        let reclaimable = dockerBytes(row["Reclaimable"] ?? "")
        guard reclaimable > 0 else { return [] }
        let total = row["TotalCount"] ?? "?"
        let active = row["Active"] ?? "0"
        return [Candidate(
            key: type, paths: [], subtitle: "\(total) total · \(active) in use",
            size: reclaimable,
            facts: ["total": total, "active": active, "docker": cli],
            checks: [.passed("Measured by Docker itself (docker system df)"),
                     .passed("Only things no container uses are counted")]
        )]
    }

    // MARK: - Large old files

    /// When the file was last opened, from Spotlight, falling back to its
    /// modification date. Whichever is later counts.
    static func lastUsed(_ url: URL) -> Date? {
        var dates: [Date] = []
        if let item = MDItemCreateWithURL(nil, url as CFURL),
           let d = MDItemCopyAttribute(item, kMDItemLastUsedDate) as? Date { dates.append(d) }
        if let m = FS.modificationDate(url) { dates.append(m) }
        return dates.max()
    }

    static func largeOldFiles(_ rule: Rule, _ ctx: ScanContext) -> [Candidate] {
        let spec = rule.detector
        let minBytes = Int64(spec.fileMinMB ?? 200) * 1_000_000
        let cutoff = Date().addingTimeInterval(-Double(spec.unusedDays ?? 120) * 86_400)
        let exts = spec.extensions.map { Set($0.map { $0.lowercased() }) }
        let keys: Set<URLResourceKey> = [.isRegularFileKey, .isPackageKey, .isDirectoryKey, .totalFileAllocatedSizeKey, .isHiddenKey]
        var out: [Candidate] = []

        for root in FS.uniqueDirectories(spec.roots ?? [], home: ctx.home) {
            guard let e = FileManager.default.enumerator(at: root, includingPropertiesForKeys: Array(keys),
                                                         options: [.skipsHiddenFiles, .skipsPackageDescendants]) else { continue }
            for case let url as URL in e {
                if e.level > 4 { e.skipDescendants(); continue }
                guard let v = try? url.resourceValues(forKeys: keys) else { continue }
                if v.isDirectory == true {
                    if ["node_modules", ".git", "Library"].contains(url.lastPathComponent) || v.isPackage == true { e.skipDescendants() }
                    continue
                }
                guard v.isRegularFile == true, Int64(v.totalFileAllocatedSize ?? 0) >= minBytes else { continue }
                if let exts, !exts.contains(url.pathExtension.lowercased()) { continue }
                guard let used = lastUsed(url), used < cutoff else { continue }
                let ago = RelativeDateTimeFormatter().localizedString(for: used, relativeTo: Date())
                out.append(Candidate(
                    key: url.path, paths: [url], title: url.lastPathComponent,
                    subtitle: "\(url.deletingLastPathComponent().lastPathComponent) · last opened \(ago)",
                    facts: ["name": url.lastPathComponent, "ago": ago, "folder": FS.abbreviate(url.deletingLastPathComponent().path, home: ctx.home)],
                    checks: [.passed("Last opened \(ago), according to Spotlight and the file's dates")]
                ))
            }
        }
        return out
    }

    // MARK: - iPhone and iPad backups

    struct DeviceBackup { var dir: URL; var device: String; var product: String?; var date: Date?; var udid: String }

    static func readBackup(_ dir: URL) -> DeviceBackup? {
        guard let data = try? Data(contentsOf: dir.appendingPathComponent("Info.plist")),
              let info = try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any] else { return nil }
        return DeviceBackup(dir: dir, device: info["Device Name"] as? String ?? "iPhone or iPad",
                            product: (info["Product Name"] as? String) ?? (info["Product Type"] as? String),
                            date: info["Last Backup Date"] as? Date,
                            udid: (info["Target Identifier"] as? String) ?? dir.lastPathComponent)
    }

    static func iosBackups(_ rule: Rule, _ ctx: ScanContext) -> [Candidate] {
        let root = ctx.home.appendingPathComponent("Library/Application Support/MobileSync/Backup")
        let backups = FS.children(root).filter(FS.isDirectory).compactMap(readBackup)
        let cutoff = Date().addingTimeInterval(-Double(rule.detector.unusedDays ?? 90) * 86_400)
        let byDevice = Dictionary(grouping: backups, by: \.udid)
        return backups.compactMap { b in
            let siblings = byDevice[b.udid] ?? [b]
            let newest = siblings.max { ($0.date ?? .distantPast) < ($1.date ?? .distantPast) }
            let isNewest = newest?.dir == b.dir
            let old = (b.date ?? .distantPast) < cutoff
            guard old || !isNewest else { return nil }
            let when = b.date.map { $0.formatted(date: .abbreviated, time: .omitted) } ?? "an unknown date"
            var checks: [Check] = []
            if isNewest {
                checks.append(.warning("It's the only or newest backup of this device on this Mac"))
            } else {
                checks.append(.passed("A newer backup of this device is kept (\(newest?.date?.formatted(date: .abbreviated, time: .omitted) ?? "newer"))"))
            }
            return Candidate(
                key: b.dir.lastPathComponent, paths: [b.dir], title: "\(b.device) backup from \(when)",
                subtitle: [b.product, "MobileSync"].compactMap { $0 }.joined(separator: " · "),
                facts: ["device": b.device, "when": when],
                checks: checks
            )
        }
    }

    // MARK: - Time Machine local snapshots

    /// Names like "com.apple.TimeMachine.2026-09-30-123456.local". macOS
    /// update snapshots (com.apple.os.update-…) are left alone.
    static func timeMachineSnapshotNames(_ text: String) -> [String] {
        text.split(separator: "\n").map(String.init).filter { $0.hasPrefix("com.apple.TimeMachine.") }
    }

    static func timeMachineSnapshots(_ rule: Rule, _ ctx: ScanContext) -> [Candidate] {
        let names = timeMachineSnapshotNames(Shell.run(["tmutil", "listlocalsnapshots", "/"], timeout: 20).stdout)
        guard !names.isEmpty else { return [] }
        let dates = names.compactMap { $0.components(separatedBy: ".").dropFirst(3).first }.sorted()
        return [Candidate(
            key: "all", paths: [], title: "\(names.count) Time Machine snapshot\(names.count == 1 ? "" : "s") on this disk",
            subtitle: dates.first.map { "Oldest from \($0.prefix(10))" } ?? "",
            size: 0,
            facts: ["count": String(names.count)],
            checks: [.passed("Only Time Machine snapshots are counted, not macOS update snapshots"),
                     .warning("macOS doesn't report how much space they use")]
        )]
    }

    // MARK: - Chrome profiles

    static func chromeProfiles(_ rule: Rule, _ ctx: ScanContext) -> [Candidate] {
        let base = ctx.home.appendingPathComponent("Library/Application Support/Google/Chrome")
        guard let data = try? Data(contentsOf: base.appendingPathComponent("Local State")),
              let state = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let profile = state["profile"] as? [String: Any],
              let cache = profile["info_cache"] as? [String: [String: Any]] else { return [] }
        let lastUsed = profile["last_used"] as? String
        let cutoff = Date().addingTimeInterval(-Double(rule.detector.unusedDays ?? 180) * 86_400)
        return cache.sorted { $0.key < $1.key }.compactMap { dir, info in
            guard dir != lastUsed else { return nil }
            let active = (info["active_time"] as? Double).map { Date(timeIntervalSince1970: $0) }
            guard let active, active < cutoff else { return nil }
            let name = info["name"] as? String ?? dir
            let account = (info["user_name"] as? String).flatMap { $0.isEmpty ? nil : $0 }
            let ago = RelativeDateTimeFormatter().localizedString(for: active, relativeTo: Date())
            var checks: [Check] = [.passed("Last used \(ago)"), .passed("It isn't the profile Chrome opens with")]
            if let account { checks.append(.passed("Signed in as \(account), so synced data is also in that Google account")) }
            else { checks.append(.warning("Not signed in: its bookmarks and passwords exist only on this Mac")) }
            return Candidate(
                key: dir, paths: [base.appendingPathComponent(dir)],
                title: "Chrome profile “\(name)”",
                subtitle: [account, "last used \(ago)"].compactMap { $0 }.joined(separator: " · "),
                facts: ["profile": name, "ago": ago],
                checks: checks
            )
        }
    }
}
