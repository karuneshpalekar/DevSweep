import DevSweepCore
import SwiftUI

/// The ⓘ panel: everything DevSweep knows about one item, in plain words.
@MainActor
struct InspectorView: View {
    @Environment(AppModel.self) private var model
    let finding: Finding

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                header
                section("What it is", finding.explanation.what)
                section("Why it's flagged", finding.explanation.why)
                if !finding.checks.isEmpty {
                    VStack(alignment: .leading, spacing: 6) {
                        heading("What was checked")
                        ForEach(finding.checks, id: \.self) { CheckRow(check: $0) }
                    }
                }
                section("If you clean it", finding.explanation.ifDeleted)
                if let s = finding.explanation.wontLose { section("You won't lose", s) }
                if let s = finding.explanation.before { section("Before you do", s) }
                if !finding.blockingApps.isEmpty {
                    HStack {
                        ForEach(finding.blockingApps, id: \.self) { app in
                            Button("Quit \(app)") { model.quit(app) }
                        }
                    }
                }
                if let s = finding.explanation.better {
                    VStack(alignment: .leading, spacing: 4) {
                        heading("Better option").foregroundStyle(Color.accentColor)
                        Text(s).fixedSize(horizontal: false, vertical: true)
                    }
                    .padding(12)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(Color.accentColor.opacity(0.1), in: RoundedRectangle(cornerRadius: 8))
                }
                if let s = finding.explanation.undo { section("How to undo", s) }
                actions
                paths
            }
            .padding(20)
        }
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                RiskBadge(risk: finding.risk)
                Spacer()
                Button { model.closeExplanation() } label: {
                    Image(systemName: "xmark")
                        .font(.system(size: 11, weight: .bold))
                        .foregroundStyle(.secondary)
                        .frame(width: 22, height: 22)
                        .background(.quaternary, in: Circle())
                }
                .buttonStyle(.plain)
                .keyboardShortcut(.cancelAction)
                .help("Close (Esc)")
                .accessibilityLabel("Close explanation")
            }
            SizeText(bytes: finding.size).font(.title2.weight(.semibold))
            Text(finding.title).font(.title3.weight(.semibold))
            Text(finding.subtitle).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
        }
    }

    private var actions: some View {
        VStack(alignment: .leading, spacing: 8) {
            heading("What to do")
            if finding.risk == .needsAdmin, let a = finding.defaultAction, let lines = a.command {
                Text(a.displayDetail).font(.callout).foregroundStyle(.secondary)
                Text(lines.joined(separator: "\n"))
                    .font(.system(.caption, design: .monospaced))
                    .textSelection(.enabled)
                    .padding(8)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 6))
                Button("Copy commands") {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(lines.joined(separator: "\n"), forType: .string)
                }
            } else {
                ForEach(finding.actions) { a in
                    let selected = model.action(for: finding).id == a.id
                    Button {
                        model.setAction(a, for: finding)
                        model.checked.insert(finding.id)
                    } label: {
                        HStack(alignment: .top, spacing: 10) {
                            Image(systemName: selected ? "largecircle.fill.circle" : "circle")
                                .foregroundStyle(selected ? Color.accentColor : .secondary)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(a.displayLabel).fontWeight(.medium)
                                Text(a.displayDetail).font(.caption).foregroundStyle(.secondary)
                                    .fixedSize(horizontal: false, vertical: true)
                            }
                            Spacer(minLength: 0)
                        }
                        .padding(8)
                        .contentShape(Rectangle())
                        .background(selected ? Color.accentColor.opacity(0.08) : .clear, in: RoundedRectangle(cornerRadius: 8))
                        .overlay(RoundedRectangle(cornerRadius: 8).stroke(selected ? Color.accentColor : .clear))
                    }
                    .buttonStyle(.plain)
                }
                Button("Always ignore this") { model.ignore(finding) }
                    .buttonStyle(.link)
            }
        }
    }

    @ViewBuilder
    private var paths: some View {
        if !finding.paths.isEmpty {
            DisclosureGroup("\(finding.paths.count) location\(finding.paths.count == 1 ? "" : "s")") {
                VStack(alignment: .leading, spacing: 4) {
                    ForEach(finding.paths, id: \.self) { p in
                        HStack {
                            Text(p.replacingOccurrences(of: NSHomeDirectory(), with: "~"))
                                .font(.caption).lineLimit(1).truncationMode(.middle).textSelection(.enabled)
                            Spacer()
                            Button { model.reveal(p) } label: { Image(systemName: "magnifyingglass") }
                                .buttonStyle(.borderless)
                                .help("Show in Finder")
                        }
                    }
                }
                .padding(.top, 4)
            }
            .font(.callout)
        }
    }

    private func heading(_ s: String) -> some View {
        Text(s).font(.caption.weight(.semibold)).foregroundStyle(.secondary)
    }

    private func section(_ title: String, _ body: String) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            heading(title)
            Text(body).fixedSize(horizontal: false, vertical: true)
        }
    }
}
