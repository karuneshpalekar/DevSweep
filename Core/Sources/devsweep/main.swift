import DevSweepCore
import Foundation

let usage = """
devsweep: find what's safe to clean on a developer Mac

USAGE
  devsweep scan [--json] [--explain]   Scan (read-only) and list findings
  devsweep rules                       List loaded rules
  devsweep history                     Show what DevSweep has changed
  devsweep restore <history-id>        Move an entry's items back from the Trash

Scanning never changes anything. Cleaning happens in the DevSweep app,
where every item is explained before you confirm.
"""

let args = Array(CommandLine.arguments.dropFirst())

func scan(json: Bool, explain: Bool) async {
    let (rules, errors) = RuleLoader.loadAll()
    errors.forEach { FileHandle.standardError.write(Data("rule error: \($0)\n".utf8)) }
    let result = await Scanner(rules: rules).scan(ignored: IgnoreStore().ids)

    if json {
        let e = JSONEncoder()
        e.outputFormatting = [.prettyPrinted, .sortedKeys]
        print(String(decoding: try! e.encode(result.findings), as: UTF8.self))
        return
    }

    for risk in Risk.allCases {
        let group = result.findings.filter { $0.risk == risk }
        guard !group.isEmpty else { continue }
        let total = group.reduce(0) { $0 + $1.size }
        print("\n\(risk.title.uppercased()) · \(group.count) items · \(SizeFormat.string(total))")
        for f in group {
            let size = SizeFormat.string(f.size).padding(toLength: 10, withPad: " ", startingAt: 0)
            print("  \(size) \(f.title)")
            print("             \(f.subtitle)  [\(f.defaultAction?.displayLabel ?? "-")]")
            if explain {
                print("             why: \(f.explanation.why)")
                print("             if deleted: \(f.explanation.ifDeleted)")
                for c in f.checks { print("             \(c.status == .passed ? "✓" : "!") \(c.text)") }
            }
        }
    }
    print("\n\(result.findings.count) items · \(SizeFormat.string(result.totalSize)) can be cleaned")
    if let disk = result.disk {
        print("Disk: \(SizeFormat.string(disk.free)) free of \(SizeFormat.string(disk.total))")
    }
}

switch args.first {
case "scan":
    await scan(json: args.contains("--json"), explain: args.contains("--explain"))
case "runtimes":
    let result = await RuntimeScanner().scan()
    if args.contains("--json") {
        let e = JSONEncoder()
        e.outputFormatting = [.prettyPrinted, .sortedKeys]
        e.dateEncodingStrategy = .iso8601
        print(String(decoding: try! e.encode(result.runtimes), as: UTF8.self))
        break
    }
    let marks: [SupportStatus: String] = [.endOfLife: "✗", .endingSoon: "!", .supported: "✓", .unknown: "?"]
    for r in result.runtimes + [result.macOS].compactMap({ $0 }) {
        print("\n\(r.name.uppercased())\(r.recommended.map { "   recommended: \($0)" } ?? "")")
        for i in r.installs {
            var line = "  \(marks[i.support]!) \(i.version.padding(toLength: 12, withPad: " ", startingAt: 0)) \(i.source.title)"
            if let d = i.sourceDetail { line += " (\(d))" }
            if i.isDefault { line += "  ← your shell uses this" }
            if let running = i.isRunning { line += running ? "  [running]" : "  [not running]" }
            if let ends = i.supportEnds { line += "  support ends \(ends.formatted(date: .abbreviated, time: .omitted))" }
            print(line)
        }
        for issue in r.issues { print("    \(issue.level == .info ? "·" : "▲") \(issue.text)") }
        for s in r.steps { print("    → \(s.title)"); s.commands.prefix(3).forEach { print("        $ \($0)") } }
    }
    if let b = result.homebrew {
        print("\nHOMEBREW  \(b.outdated.count) outdated, \(b.deprecated.count) deprecated")
        for issue in b.issues { print("    \(issue.level == .info ? "·" : "▲") \(issue.text)") }
    }
    if result.eolOffline { print("\n(Support dates may be out of date: couldn't reach endoflife.date.)") }
case "rules":
    let (rules, errors) = RuleLoader.loadAll()
    for r in rules { print("\(r.id.padding(toLength: 28, withPad: " ", startingAt: 0)) \(r.category.title) · \(r.risk.title)") }
    errors.forEach { print("error: \($0)") }
case "history":
    let df = DateFormatter()
    df.dateStyle = .medium; df.timeStyle = .short
    for e in HistoryStore().entries {
        let state = e.restoredAt != nil ? "restored" : (e.canRestore ? "restorable" : "")
        print("\(df.string(from: e.date))  \(SizeFormat.string(e.size).padding(toLength: 9, withPad: " ", startingAt: 0)) \(e.actionLabel): \(e.title)  \(state)  [\(e.id.uuidString)]")
    }
case "restore":
    guard args.count > 1, let id = UUID(uuidString: args[1]) else { print(usage); exit(1) }
    do {
        let n = try HistoryStore().restore(id)
        print("Restored \(n) item\(n == 1 ? "" : "s").")
    } catch {
        print(error.localizedDescription); exit(1)
    }
default:
    print(usage)
}
