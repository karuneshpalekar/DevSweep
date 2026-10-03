import Foundation

/// A TCP port something on this Mac is listening on.
public struct ListeningPort: Codable, Hashable, Sendable, Identifiable {
    public var id: String { "\(pid):\(port)" }
    public var port: Int
    public var pid: Int32
    /// Process name as lsof reports it.
    public var command: String
    /// Full command line.
    public var arguments: String
    /// Friendly name, e.g. "PostgreSQL 14 (Homebrew)" or "Node.js dev server".
    public var label: String
    /// Working directory, when the process runs from a project folder.
    public var folder: String?
    public var started: Date?
    /// Listening on all interfaces, so other devices on the network can connect.
    public var reachableFromNetwork: Bool
    /// A developer tool or project server rather than an app or macOS service.
    public var isDevelopment: Bool

    public init(port: Int, pid: Int32, command: String, arguments: String, label: String, folder: String?,
                started: Date?, reachableFromNetwork: Bool, isDevelopment: Bool) {
        self.port = port; self.pid = pid; self.command = command; self.arguments = arguments; self.label = label
        self.folder = folder; self.started = started; self.reachableFromNetwork = reachableFromNetwork
        self.isDevelopment = isDevelopment
    }
}

public enum PortScanner {
    /// Ports owned by your user. macOS hides other users' processes without sudo,
    /// which keeps this to the things you started.
    public static func scan(home: URL = FileManager.default.homeDirectoryForCurrentUser,
                            installs: [Installation] = []) -> [ListeningPort] {
        let out = Shell.run(["lsof", "-nP", "-iTCP", "-sTCP:LISTEN", "-F", "pcn"], timeout: 15).stdout
        let entries = parse(out)
        guard !entries.isEmpty else { return [] }
        let pids = Array(Set(entries.map(\.pid)))
        let info = processInfo(pids)
        let cwds = workingDirectories(pids)

        var ports: [ListeningPort] = []
        for e in entries {
            let p = info[e.pid]
            let args = p?.args ?? e.command
            var folder = cwds[e.pid]
            if folder == "/" || folder == home.path { folder = nil }
            let (label, dev) = describe(command: e.command, args: args, folder: folder, installs: installs, home: home)
            ports.append(ListeningPort(
                port: e.port, pid: e.pid, command: e.command, arguments: args, label: label,
                folder: folder.map { FS.abbreviate($0, home: home) }, started: p?.started,
                reachableFromNetwork: e.network, isDevelopment: dev))
        }
        return ports.sorted { ($0.isDevelopment ? 0 : 1, $0.port) < ($1.isDevelopment ? 0 : 1, $1.port) }
    }

    struct Entry { var pid: Int32; var command: String; var port: Int; var network: Bool }

    /// Parses `lsof -F pcn`: p<pid>, c<command>, n<address:port> lines.
    /// IPv4 and IPv6 listeners for the same port become one entry.
    static func parse(_ text: String) -> [Entry] {
        var out: [String: Entry] = [:]
        var pid: Int32 = 0
        var command = ""
        for line in text.split(separator: "\n") {
            guard let tag = line.first else { continue }
            let value = String(line.dropFirst())
            switch tag {
            case "p": pid = Int32(value) ?? 0
            case "c": command = value
            case "n":
                guard let colon = value.lastIndex(of: ":"), let port = Int(value[value.index(after: colon)...]) else { continue }
                let host = String(value[..<colon])
                let network = host == "*" || host == "0.0.0.0" || host == "[::]"
                let key = "\(pid):\(port)"
                if var existing = out[key] {
                    existing.network = existing.network || network
                    out[key] = existing
                } else {
                    out[key] = Entry(pid: pid, command: command, port: port, network: network)
                }
            default: break
            }
        }
        return out.values.sorted { $0.port < $1.port }
    }

    static func processInfo(_ pids: [Int32]) -> [Int32: (args: String, started: Date?)] {
        let list = pids.map(String.init).joined(separator: ",")
        let out = Shell.run(["ps", "-o", "pid=,lstart=,args=", "-p", list], timeout: 10).stdout
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "EEE MMM d HH:mm:ss yyyy"
        var result: [Int32: (String, Date?)] = [:]
        for line in out.split(separator: "\n") {
            let parts = line.split(separator: " ", omittingEmptySubsequences: true)
            // pid, then lstart is 5 words ("Sat Oct  3 18:20:01 2026"), then the command line.
            guard parts.count >= 7, let pid = Int32(parts[0]) else { continue }
            let start = f.date(from: parts[1...5].joined(separator: " "))
            result[pid] = (parts[6...].joined(separator: " "), start)
        }
        return result
    }

    static func workingDirectories(_ pids: [Int32]) -> [Int32: String] {
        let out = Shell.run(["lsof", "-a", "-d", "cwd", "-Fn", "-p", pids.map(String.init).joined(separator: ",")], timeout: 10).stdout
        var result: [Int32: String] = [:]
        var pid: Int32 = 0
        for line in out.split(separator: "\n") {
            if line.hasPrefix("p") { pid = Int32(line.dropFirst()) ?? 0 }
            if line.hasPrefix("n") { result[pid] = String(line.dropFirst()) }
        }
        return result
    }

    /// Turns a process into something a person recognises.
    static func describe(command: String, args: String, folder: String?, installs: [Installation], home: URL) -> (String, Bool) {
        let project = folder.map { URL(fileURLWithPath: $0).lastPathComponent }
        let inProject = project.map { " in \($0)" } ?? ""
        let lower = command.lowercased()
        if lower.hasPrefix("postgres") {
            let match = installs.first { args.contains($0.path) || ($0.sourceDetail.map { args.contains("/\($0)/") } ?? false) }
            if let m = match { return ("PostgreSQL \(m.cycle) (\(m.source.title))", true) }
            // Homebrew paths name the version: …/postgresql@14/bin/postgres
            if let r = args.range(of: #"postgresql@\d+"#, options: .regularExpression) {
                return ("PostgreSQL \(args[r].dropFirst("postgresql@".count)) (Homebrew)", true)
            }
            return ("PostgreSQL", true)
        }
        let known: [(String, String)] = [
            ("node", "Node.js"), ("bun", "Bun"), ("deno", "Deno"), ("python", "Python"), ("ruby", "Ruby"),
            ("puma", "Rails (Puma)"), ("java", "Java"), ("php", "PHP"), ("redis-server", "Redis"), ("mysqld", "MySQL"),
            ("mongod", "MongoDB"), ("com.docker", "Docker"), ("vpnkit", "Docker"), ("ollama", "Ollama"), ("nginx", "nginx"),
            ("caddy", "Caddy"), ("hugo", "Hugo"), ("dotnet", ".NET"), ("go", "Go"), ("cloudflared", "Cloudflare Tunnel"),
            ("ngrok", "ngrok"),
        ]
        if let k = known.first(where: { lower.hasPrefix($0.0) }) {
            var label = k.1
            if args.contains("GradleDaemon") { label = "Gradle daemon" }
            else if args.contains("vite") { label = "Vite dev server" }
            else if args.contains("next") { label = "Next.js dev server" }
            else if args.contains("astro") { label = "Astro dev server" }
            else if args.contains("webpack") { label = "webpack dev server" }
            else if args.contains("manage.py") { label = "Django dev server" }
            else if args.contains("uvicorn") || args.contains("gunicorn") { label = "Python web server" }
            return (label + inProject, true)
        }
        let system: [String: String] = ["ControlCenter": "macOS AirPlay Receiver", "rapportd": "macOS Handoff and Continuity",
                                        "sharingd": "macOS Sharing", "identityservicesd": "macOS iMessage and FaceTime"]
        if let s = system[command] { return (s, false) }
        // Anything started from a project folder counts as development.
        return (command + inProject, folder.map { $0.hasPrefix(home.path + "/") } ?? false)
    }

    /// Asks a process to stop (SIGTERM), the same as Ctrl-C in its terminal.
    @discardableResult
    public static func stop(_ pid: Int32) -> Bool { kill(pid, SIGTERM) == 0 }
}
