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
        // Rows come from the checked items themselves, so an item with every
        // part unticked stays listed and can be ticked again.
        let rows = model.checkedFindings.map { PlannedAction(finding: $0, action: model.action(for: $0)) }
        let deleted = rows.filter { $0.action.kind == .delete || $0.action.kind == .command }
        let trashed = rows.filter { $0.action.kind == .trash }
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
                SizeText(bytes: items.reduce(0) { $0 + chosenSize($1.finding) }).foregroundStyle(.secondary)
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
                    SizeText(bytes: chosenSize(item.finding))
                }
                if let parts = item.finding.parts, !parts.isEmpty {
                    PartPicker(finding: item.finding, parts: parts)
                }
            }
        }
    }

    /// The size that will actually be freed for an item, after unticked parts.
    private func chosenSize(_ f: Finding) -> Int64 {
        f.restricted(excluding: model.excludedParts[f.id] ?? [])?.size ?? 0
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


/// Ticks for each part of an item (each browser profile), all on by default.
@MainActor
struct PartPicker: View {
    @Environment(AppModel.self) private var model
    let finding: Finding
    let parts: [FindingPart]
    @State private var expanded = true

    var body: some View {
        let chosen = parts.filter { model.isPartIncluded($0, in: finding) }
        let kind = parts.first?.kind ?? "parts"
        DisclosureGroup(isExpanded: $expanded) {
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 12) {
                    Button("Select all") { model.setAllParts(of: finding, included: true) }
                        .disabled(chosen.count == parts.count)
                    Button("Select none") { model.setAllParts(of: finding, included: false) }
                        .disabled(chosen.isEmpty)
                }
                .buttonStyle(.link).font(.callout)
                .padding(.bottom, 4)
                ForEach(parts) { part in
                    Toggle(isOn: Binding(get: { model.isPartIncluded(part, in: finding) },
                                         set: { model.setPart(part, of: finding, included: $0) })) {
                        HStack {
                            VStack(alignment: .leading, spacing: 1) {
                                Text(part.title)
                                if !part.subtitle.isEmpty {
                                    Text(part.subtitle).font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                                }
                            }
                            Spacer()
                            SizeText(bytes: part.size).foregroundStyle(.secondary)
                        }
                    }
                    .toggleStyle(.checkbox)
                    .padding(.vertical, 2)
                }
            }
            .padding(.leading, 4)
            .padding(.top, 4)
        } label: {
            Text(chosen.count == parts.count ? "All \(parts.count) \(kind) selected"
                 : chosen.isEmpty ? "No \(kind) selected, so nothing here is cleaned"
                 : "\(chosen.count) of \(parts.count) \(kind) selected")
                .font(.callout)
                .foregroundStyle(chosen.isEmpty ? Color.orange : Color.secondary)
        }
    }
}
