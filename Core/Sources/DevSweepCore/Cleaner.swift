import Foundation

public struct PlannedAction: Sendable {
    public let finding: Finding
    public let action: CleanAction

    public init(finding: Finding, action: CleanAction) {
        self.finding = finding
        self.action = action
    }
}

public struct ActionOutcome: Sendable, Identifiable {
    public var id: String { finding.id }
    public let finding: Finding
    public let action: CleanAction
    public let succeeded: Bool
    public let message: String
    public let freed: Int64
}

/// Carries out a reviewed plan and records every change in History.
public final class Cleaner {
    let history: HistoryStore
    let home: URL
    let fm = FileManager.default

    public init(history: HistoryStore, home: URL = FileManager.default.homeDirectoryForCurrentUser) {
        self.history = history
        self.home = home
    }

    public func run(_ plan: [PlannedAction], context: ScanContext? = nil,
                    progress: ((String) -> Void)? = nil) -> [ActionOutcome] {
        let ctx = context ?? ScanContext(home: home)
        return plan.map { item in
            progress?(item.finding.title)
            let (outcome, moved) = perform(item, context: ctx)
            if outcome.succeeded || !moved.isEmpty { recordHistory(item, outcome, moved) }
            return outcome
        }
    }

    private func perform(_ item: PlannedAction, context: ScanContext) -> (ActionOutcome, [MovedItem]) {
        let f = item.finding
        func result(_ ok: Bool, _ message: String, freed: Int64 = 0, _ moved: [MovedItem] = []) -> (ActionOutcome, [MovedItem]) {
            (ActionOutcome(finding: f, action: item.action, succeeded: ok, message: message, freed: freed), moved)
        }

        // Re-check blockers now; the user may have reopened the app.
        let blocking = context.running(f.blockers)
        if !blocking.isEmpty { return result(false, "Quit \(ListFormat.join(blocking)) first.") }

        switch item.action.kind {
        case .manual:
            return result(false, "Run the commands yourself; DevSweep didn't change anything.")

        case .command:
            guard let cmd = item.action.command, !cmd.isEmpty else { return result(false, "No command to run.") }
            let res = Shell.run(cmd, timeout: 600)
            guard res.ok else {
                let err = res.stderr.trimmingCharacters(in: .whitespacesAndNewlines)
                return result(false, err.isEmpty ? "\(cmd.joined(separator: " ")) failed (exit \(res.status))." : err)
            }
            return result(true, "Ran \(cmd.joined(separator: " "))", freed: f.size)

        case .delete, .trash:
            var moved: [MovedItem] = []
            var errors: [String] = []
            var freed: Int64 = 0
            for path in f.paths {
                let url = URL(fileURLWithPath: path)
                guard FS.exists(url) else { continue }
                do {
                    try Safety.check(url, home: home)
                    let size = FS.allocatedSize(url)
                    stopLaunchAgentIfNeeded(url)
                    if item.action.kind == .trash {
                        var landed: NSURL?
                        try fm.trashItem(at: url, resultingItemURL: &landed)
                        moved.append(MovedItem(original: path, trashed: (landed as URL?)?.path))
                    } else {
                        try fm.removeItem(at: url)
                        moved.append(MovedItem(original: path, trashed: nil))
                    }
                    freed += size
                } catch {
                    errors.append("\(FS.abbreviate(path, home: home)): \(error.localizedDescription)")
                }
            }
            if errors.isEmpty {
                return result(true, item.action.kind == .trash ? "Moved to Trash" : "Deleted", freed: freed, moved)
            }
            return result(false, errors.joined(separator: "\n"), freed: freed, moved)
        }
    }

    private func stopLaunchAgentIfNeeded(_ url: URL) {
        guard url.pathExtension == "plist",
              url.deletingLastPathComponent().path == home.appendingPathComponent("Library/LaunchAgents").path else { return }
        Shell.run(["launchctl", "bootout", "gui/\(getuid())", url.path], timeout: 15)
    }

    private func recordHistory(_ item: PlannedAction, _ outcome: ActionOutcome, _ moved: [MovedItem]) {
        history.append(HistoryEntry(
            id: UUID(), date: Date(), title: item.finding.title, findingID: item.finding.id,
            ruleID: item.finding.ruleID, actionKind: item.action.kind, actionLabel: item.action.displayLabel,
            size: outcome.freed, items: moved,
            command: item.action.kind == .command ? item.action.command : nil,
            note: item.finding.explanation.undo, restoredAt: nil
        ))
    }
}
