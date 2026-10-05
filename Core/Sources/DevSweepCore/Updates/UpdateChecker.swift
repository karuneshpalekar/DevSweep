import Foundation

/// A dotted version like 0.8.2, with an optional leading "v".
public struct AppVersion: Comparable, Hashable, Sendable, CustomStringConvertible {
    public let parts: [Int]

    public init?(_ text: String) {
        var s = text.trimmingCharacters(in: .whitespaces)
        if s.hasPrefix("v") || s.hasPrefix("V") { s.removeFirst() }
        // Drop a pre-release or build suffix: 1.2.3-beta.1 -> 1.2.3
        s = String(s.prefix { $0.isNumber || $0 == "." })
        let nums = s.split(separator: ".", omittingEmptySubsequences: false).map { Int($0) }
        guard !nums.isEmpty, !nums.contains(nil) else { return nil }
        parts = nums.compactMap { $0 }
    }

    public static func < (a: AppVersion, b: AppVersion) -> Bool {
        let n = max(a.parts.count, b.parts.count)
        for i in 0..<n {
            let x = i < a.parts.count ? a.parts[i] : 0
            let y = i < b.parts.count ? b.parts[i] : 0
            if x != y { return x < y }
        }
        return false
    }

    public static func == (a: AppVersion, b: AppVersion) -> Bool { !(a < b) && !(b < a) }

    public var description: String { parts.map(String.init).joined(separator: ".") }
}

/// A published release that is newer than the running app.
public struct UpdateInfo: Equatable, Sendable {
    public var version: String
    public var pageURL: URL
    public var downloadURL: URL?
    public var notes: String
    public var publishedAt: Date?

    public init(version: String, pageURL: URL, downloadURL: URL?, notes: String, publishedAt: Date?) {
        self.version = version; self.pageURL = pageURL; self.downloadURL = downloadURL
        self.notes = notes; self.publishedAt = publishedAt
    }
}

/// Asks GitHub for the latest DevSweep release. One unauthenticated request,
/// nothing about this Mac is sent.
public enum UpdateChecker {
    public static let latestReleaseURL = URL(string: "https://api.github.com/repos/karuneshpalekar/DevSweep/releases/latest")!

    public enum Outcome: Equatable, Sendable {
        case upToDate(latest: String)
        case available(UpdateInfo)
        case failed(String)
    }

    private struct Release: Decodable {
        var tag_name: String
        var html_url: URL
        var body: String?
        var draft: Bool?
        var prerelease: Bool?
        var published_at: Date?
        var assets: [Asset]?
        struct Asset: Decodable { var name: String; var browser_download_url: URL }
    }

    /// Compares the JSON GitHub returns with the running version.
    public static func evaluate(_ data: Data, current: String) -> Outcome {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        guard let release = try? decoder.decode(Release.self, from: data) else {
            return .failed("GitHub's answer wasn't what DevSweep expected.")
        }
        if release.draft == true || release.prerelease == true { return .upToDate(latest: current) }
        guard let latest = AppVersion(release.tag_name), let running = AppVersion(current) else {
            return .failed("Couldn't read the version number.")
        }
        guard running < latest else { return .upToDate(latest: latest.description) }
        let dmg = release.assets?.first { $0.name.hasSuffix(".dmg") }?.browser_download_url
        return .available(UpdateInfo(version: latest.description, pageURL: release.html_url, downloadURL: dmg,
                                     notes: release.body ?? "", publishedAt: release.published_at))
    }

    public static func check(current: String, session: URLSession = .shared) async -> Outcome {
        var request = URLRequest(url: latestReleaseURL, timeoutInterval: 15)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.setValue("DevSweep/\(current)", forHTTPHeaderField: "User-Agent")
        do {
            let (data, response) = try await session.data(for: request)
            if let http = response as? HTTPURLResponse, http.statusCode != 200 {
                return .failed(http.statusCode == 403 ? "GitHub is limiting requests right now. Try again later."
                                                      : "GitHub answered with status \(http.statusCode).")
            }
            return evaluate(data, current: current)
        } catch {
            return .failed("Couldn't reach GitHub. Check your connection.")
        }
    }
}
