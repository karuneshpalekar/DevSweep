import DevSweepCore
import SwiftUI

/// Last stop before anything changes: groups the selection by what will
/// actually happen and shows the exact commands.
@MainActor
struct ReviewSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if let outcomes = model.outcomes {
                results(outcomes).transition(.opacity)
            } else {
                review.transition(.opacity)
            }
        }
        .animation(Motion.swap, value: model.outcomes == nil)
        .frame(width: 640)
        .frame(minHeight: 360, maxHeight: 720)
    }

    // MARK: - Review

    private var review: some View {
        let plan = model.plan
        let deleted = plan.filter { $0.action.kind == .delete || $0.action.kind == .command }
        let trashed = plan.filter { $0.action.kind == .trash }
        let blocked = plan.flatMap(\.finding.blockingApps)

        return VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 4) {
                Text("Review \(plan.count) item\(plan.count == 1 ? "" : "s")").font(.title2.weight(.semibold))
                Text("\(SizeFormat.string(model.checkedSize)) will be freed. Here's exactly what happens to each item.")
                    .foregroundStyle(.secondary)
            }
            .padding([.horizontal, .top], 24)
            .padding(.bottom, 12)

            ScrollView {
                VStack(alignment: .leading, spacing: 20) {
                    if !deleted.isEmpty {
                        group("Deleted right away", deleted,
                              note: "These rebuild on their own, so there's nothing to restore.")
                    }
                    if !trashed.isEmpty {
                        group("Moved to the Trash", trashed,
                              note: "You can restore these from History until you empty the Trash.")
                    }
                    DisclosureGroup("Commands that will run") {
                        Text(commands(plan))
                            .font(.system(.caption, design: .monospaced))
                            .textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(10)
                            .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 6))
                            .padding(.top, 6)
                    }
                }
                .padding(.horizontal, 24)
                .padding(.bottom, 16)
            }

            Divider()
            HStack(spacing: 10) {
                Image(systemName: "arrow.uturn.backward").foregroundStyle(.secondary)
                Text(blocked.isEmpty ? "Every change is logged in History." : "Quit \(ListFormatter.localizedString(byJoining: Array(Set(blocked)))) to clean everything.")
                    .font(.callout).foregroundStyle(blocked.isEmpty ? Color.secondary : Color.orange)
                Spacer()
                Button("Cancel") { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button {
                    model.clean()
                } label: {
                    if model.isCleaning {
                        HStack(spacing: 6) { ProgressView().controlSize(.small); Text(model.cleanStatus).lineLimit(1) }
                    } else {
                        Text("Clean \(SizeFormat.string(model.checkedSize))")
                    }
                }
                .buttonStyle(.borderedProminent)
                .keyboardShortcut(.defaultAction)
                .disabled(model.isCleaning || plan.isEmpty)
            }
            .padding(.horizontal, 24)
            .padding(.vertical, 14)
        }
    }

    private func group(_ title: String, _ items: [PlannedAction], note: String) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .firstTextBaseline) {
                Text(title).font(.headline)
                SizeText(bytes: items.reduce(0) { $0 + $1.finding.size }).foregroundStyle(.secondary)
            }
            Text(note).font(.callout).foregroundStyle(.secondary)
            ForEach(items, id: \.finding.id) { item in
                Divider()
                HStack(alignment: .center) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(item.finding.title)
                        if !item.finding.blockingApps.isEmpty {
                            Text("\(item.finding.blockingApps.joined(separator: ", ")) is open. It has to quit first.")
                                .font(.caption).foregroundStyle(.orange)
                        } else if item.action.kind == .command, let cmd = item.action.command {
                            Text("Runs \(cmd.joined(separator: " "))").font(.caption.monospaced()).foregroundStyle(.secondary)
                        }
                    }
                    Spacer()
                    ForEach(item.finding.blockingApps, id: \.self) { app in
                        Button("Quit \(app)") { model.quit(app) }.controlSize(.small)
                    }
                    SizeText(bytes: item.finding.size)
                }
            }
        }
    }

    private func commands(_ plan: [PlannedAction]) -> String {
        let home = NSHomeDirectory()
        func q(_ p: String) -> String { "\"" + p.replacingOccurrences(of: home, with: "~") + "\"" }
        return plan.flatMap { item -> [String] in
            switch item.action.kind {
            case .command: return [item.action.command?.joined(separator: " ") ?? ""]
            case .delete: return item.finding.paths.map { "rm -rf \(q($0))" }
            case .trash: return item.finding.paths.map { "move to Trash \(q($0))" }
            case .manual: return item.action.command ?? []
            }
        }.joined(separator: "\n")
    }

    // MARK: - Results

    private func results(_ outcomes: [ActionOutcome]) -> some View {
        let freed = outcomes.reduce(0) { $0 + $1.freed }
        let failed = outcomes.filter { !$0.succeeded }
        return VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 4) {
                Text("Freed \(SizeFormat.string(freed))").font(.title2.weight(.semibold))
                Text(failed.isEmpty ? "Everything went as planned." : "\(failed.count) item\(failed.count == 1 ? "" : "s") couldn't be cleaned.")
                    .foregroundStyle(.secondary)
            }
            .padding(24)
            List(outcomes) { o in
                HStack(alignment: .top) {
                    Image(systemName: o.succeeded ? "checkmark.circle.fill" : "xmark.octagon.fill")
                        .foregroundStyle(o.succeeded ? .green : .red)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(o.finding.title)
                        Text(o.message).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                    }
                    Spacer()
                    SizeText(bytes: o.freed).foregroundStyle(.secondary)
                }
            }
            .listStyle(.inset)
            Divider()
            HStack {
                Spacer()
                Button("Show History") { dismiss(); model.selection = .history }
                Button("Done") { dismiss() }.buttonStyle(.borderedProminent).keyboardShortcut(.defaultAction)
            }
            .padding(.horizontal, 24)
            .padding(.vertical, 14)
        }
    }
}
