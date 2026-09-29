import Foundation

enum FS {
    static let fm = FileManager.default

    static func expand(_ path: String, home: URL) -> URL {
        if path == "~" { return home }
        if path.hasPrefix("~/") { return home.appendingPathComponent(String(path.dropFirst(2))) }
        if path.hasPrefix("$ANDROID_HOME"), let env = ProcessInfo.processInfo.environment["ANDROID_HOME"] {
            return URL(fileURLWithPath: env + path.dropFirst("$ANDROID_HOME".count))
        }
        return URL(fileURLWithPath: path)
    }

    /// Minimal glob: `*`, `?` and `[...]` inside any path component.
    static func glob(_ pattern: String, home: URL) -> [URL] {
        let url = expand(pattern, home: home)
        let parts = url.pathComponents
        var current: [URL] = [URL(fileURLWithPath: "/")]
        for part in parts.dropFirst() {
            let isPattern = part.contains("*") || part.contains("?") || part.contains("[")
            var next: [URL] = []
            for base in current {
                if isPattern {
                    let children = (try? fm.contentsOfDirectory(atPath: base.path)) ?? []
                    for child in children.sorted() where fnmatch(part, child, 0) == 0 {
                        next.append(base.appendingPathComponent(child))
                    }
                } else {
                    let candidate = base.appendingPathComponent(part)
                    if exists(candidate) { next.append(candidate) }
                }
            }
            current = next
            if current.isEmpty { break }
        }
        return current
    }

    /// True for files, folders and symlinks (even dangling ones).
    static func exists(_ url: URL) -> Bool {
        (try? url.resourceValues(forKeys: [.isSymbolicLinkKey])) != nil || fm.fileExists(atPath: url.path)
    }

    static func isDirectory(_ url: URL) -> Bool {
        var isDir: ObjCBool = false
        return fm.fileExists(atPath: url.path, isDirectory: &isDir) && isDir.boolValue
    }

    static func children(_ url: URL) -> [URL] {
        ((try? fm.contentsOfDirectory(at: url, includingPropertiesForKeys: nil)) ?? [])
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
    }

    /// Space the item actually takes on disk. Does not follow symlinks.
    static func allocatedSize(_ url: URL) -> Int64 {
        let keys: Set<URLResourceKey> = [.totalFileAllocatedSizeKey, .fileAllocatedSizeKey, .isSymbolicLinkKey, .isDirectoryKey]
        guard let values = try? url.resourceValues(forKeys: keys) else { return 0 }
        if values.isSymbolicLink == true { return 0 }
        if values.isDirectory != true {
            return Int64(values.totalFileAllocatedSize ?? values.fileAllocatedSize ?? 0)
        }
        var total: Int64 = 0
        guard let e = fm.enumerator(at: url, includingPropertiesForKeys: Array(keys), options: [], errorHandler: { _, _ in true }) else { return 0 }
        for case let child as URL in e {
            guard let v = try? child.resourceValues(forKeys: keys), v.isSymbolicLink != true else { continue }
            total += Int64(v.totalFileAllocatedSize ?? v.fileAllocatedSize ?? 0)
        }
        return total
    }

    static func modificationDate(_ url: URL) -> Date? {
        (try? url.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate
    }

    static func abbreviate(_ path: String, home: URL) -> String {
        let h = home.path
        if path == h { return "~" }
        if path.hasPrefix(h + "/") { return "~" + path.dropFirst(h.count) }
        return path
    }
}

enum Version {
    /// Numeric pieces of a version string: "2024.2", "1.9.23", "30.0.3".
    static func parts(_ s: String) -> [Int] {
        s.split(whereSeparator: { !$0.isNumber }).compactMap { Int($0) }
    }

    static func less(_ a: String, _ b: String) -> Bool {
        let pa = parts(a), pb = parts(b)
        for i in 0..<max(pa.count, pb.count) {
            let x = i < pa.count ? pa[i] : 0
            let y = i < pb.count ? pb[i] : 0
            if x != y { return x < y }
        }
        return false
    }
}

enum ListFormat {
    /// "a", "a and b", "a, b and c"
    static func join(_ items: [String]) -> String {
        switch items.count {
        case 0: return ""
        case 1: return items[0]
        default: return items.dropLast().joined(separator: ", ") + " and " + items.last!
        }
    }
}
