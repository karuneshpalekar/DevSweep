import DevSweepCore
import SwiftUI

@MainActor
struct HistoryView: View {
    @Environment(AppModel.self) private var model

    private var byDay: [(Date, [HistoryEntry])] {
        Dictionary(grouping: model.historyEntries) { Calendar.current.startOfDay(for: $0.date) }
            .sorted { $0.key > $1.key }
    }

    var body: some View {
        Group {
            if model.historyEntries.isEmpty {
                ContentUnavailableView("Nothing cleaned yet", systemImage: "clock",
                                       description: Text("Everything DevSweep changes shows up here, with a way to undo it."))
            } else {
                List {
                    ForEach(byDay, id: \.0) { day, entries in
                        Section(day.formatted(date: .complete, time: .omitted)) {
                            ForEach(entries) { HistoryRow(entry: $0) }
                        }
                    }
                    Section {
                        Text("Items stay restorable until you empty the Trash. DevSweep never empties it for you.")
                            .font(.callout).foregroundStyle(.secondary)
                    }
                }
                .listStyle(.inset)
            }
        }
        .navigationTitle("History")
        .navigationSubtitle("\(SizeFormat.string(model.historyEntries.reduce(0) { $0 + $1.size })) freed in total")
    }
}

@MainActor
struct HistoryRow: View {
    @Environment(AppModel.self) private var model
    let entry: HistoryEntry
    @State private var expanded = false

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 12) {
                Text(entry.date.formatted(date: .omitted, time: .shortened))
                    .font(.caption).foregroundStyle(.secondary).monospacedDigit().frame(width: 60, alignment: .leading)
                VStack(alignment: .leading, spacing: 2) {
                    Text(entry.title).fontWeight(.medium)
                    Text(detail).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                }
                Spacer()
                SizeText(bytes: entry.size)
                status
            }
            if expanded {
                ForEach(entry.items, id: \.original) { item in
                    HStack {
                        Text(item.original.replacingOccurrences(of: NSHomeDirectory(), with: "~"))
                            .font(.caption).lineLimit(1).truncationMode(.middle)
                        Spacer()
                        if let t = item.trashed, FileManager.default.fileExists(atPath: t) {
                            Text("In Trash").font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    .padding(.leading, 72)
                }
            }
        }
        .padding(.vertical, 4)
        .contentShape(Rectangle())
        .onTapGesture { if !entry.items.isEmpty { withAnimation(Motion.swap) { expanded.toggle() } } }
    }

    private var detail: String {
        if let cmd = entry.command { return "Ran \(cmd.joined(separator: " "))" }
        var s = "\(entry.actionLabel) · \(entry.items.count) item\(entry.items.count == 1 ? "" : "s")"
        if !entry.canRestore, entry.restoredAt == nil, let note = entry.note { s += " · \(note)" }
        return s
    }

    @ViewBuilder
    private var status: some View {
        if entry.restoredAt != nil {
            tag("Restored", .green)
        } else if entry.canRestore {
            Button("Restore") { model.restore(entry) }.controlSize(.small)
        } else if entry.actionKind == .delete || entry.actionKind == .command {
            tag("Rebuilds itself", .secondary)
        } else {
            tag("No longer in the Trash", .secondary)
        }
    }

    private func tag(_ s: String, _ color: Color) -> some View {
        Text(s).font(.caption).padding(.horizontal, 7).padding(.vertical, 2)
            .foregroundStyle(color)
            .background(color.opacity(0.12), in: RoundedRectangle(cornerRadius: 5))
    }
}
