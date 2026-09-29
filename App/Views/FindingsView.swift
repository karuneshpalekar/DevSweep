import DevSweepCore
import SwiftUI

@MainActor
struct FindingsView: View {
    @Environment(AppModel.self) private var model
    let item: SidebarItem

    @State private var search = ""
    @State private var riskFilter: Risk?

    private var title: String {
        if case .category(let c) = item { return c.title }
        return "All items"
    }

    private var visible: [Finding] {
        model.findings(for: item).filter { f in
            (riskFilter == nil || f.risk == riskFilter)
                && (search.isEmpty || f.title.localizedCaseInsensitiveContains(search)
                    || f.subtitle.localizedCaseInsensitiveContains(search))
        }
    }

    var body: some View {
        @Bindable var model = model
        let items = visible
        // A plain side panel instead of SwiftUI's .inspector: on macOS 14,
        // .inspector inside a NavigationSplitView crashes in NSToolbar
        // (registerSeparator) when it opens or the toolbar changes.
        //
        // The panel stays in the view tree and animates its width, so the list
        // resizes in step with it instead of jumping. It keeps showing the
        // last item while it closes, rather than going blank mid-slide.
        let isOpen = model.inspected != nil
        HStack(spacing: 0) {
            listColumn(items)
            Divider().opacity(isOpen ? 1 : 0)
            ZStack(alignment: .topLeading) {
                if let f = panelFinding {
                    InspectorView(finding: f)
                        .id(f.id)
                        .transition(.opacity)
                }
            }
            .frame(width: Self.panelWidth, alignment: .leading)
            .frame(width: isOpen ? Self.panelWidth : 0, alignment: .leading)
            .clipped()
            .background(.background.secondary)
            .opacity(isOpen ? 1 : 0)
        }
        .animation(Motion.panel, value: isOpen)
        .animation(Motion.swap, value: panelFinding?.id)
        .onChange(of: model.inspectedID, initial: true) { _, _ in
            if let f = model.inspected { panelFinding = f }
        }
        .navigationTitle(title)
        .navigationSubtitle(subtitle)
    }

    private static let panelWidth: CGFloat = 344
    @State private var panelFinding: Finding?

    private func listColumn(_ items: [Finding]) -> some View {
        @Bindable var model = model
        return VStack(spacing: 0) {
            filterBar
            Divider()
            if model.isScanning && model.findings.isEmpty {
                ProgressView("Scanning · \(model.scanStatus)").frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if items.isEmpty {
                ContentUnavailableView("Nothing to clean here", systemImage: "checkmark.seal",
                                       description: Text("DevSweep didn't find anything in this section."))
            } else {
                List {
                    ForEach(Risk.allCases, id: \.self) { risk in
                        let group = items.filter { $0.risk == risk }
                        if !group.isEmpty {
                            Section {
                                ForEach(group) { f in
                                    FindingRow(finding: f)
                                        .listRowBackground(
                                            RoundedRectangle(cornerRadius: 6)
                                                .fill(model.inspectedID == f.id ? Color.accentColor.opacity(0.14) : .clear)
                                                .padding(.horizontal, 4)
                                        )
                                }
                            } header: {
                                groupHeader(risk, group)
                            }
                        }
                    }
                }
                .listStyle(.inset)
            }
        }
        .safeAreaInset(edge: .bottom) { selectionBar }
    }

    private var subtitle: String {
        let list = model.findings(for: item)
        let size = list.reduce(0) { $0 + $1.size }
        var s = "\(list.count) items · \(SizeFormat.string(size)) can be cleaned"
        if model.isScanning { s += " · scanning" }
        return s
    }

    private var filterBar: some View {
        HStack(spacing: 12) {
            // Chips scroll sideways when the window is narrow, so they never
            // push into the search field.
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 6) {
                    chip("All", selected: riskFilter == nil) { withAnimation(Motion.swap) { riskFilter = nil } }
                    ForEach(Risk.allCases, id: \.self) { r in
                        if model.findings(for: item).contains(where: { $0.risk == r }) {
                            chip(r.title, selected: riskFilter == r) { withAnimation(Motion.swap) { riskFilter = r } }
                        }
                    }
                }
            }
            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                TextField("Search items", text: $search)
                    .textFieldStyle(.plain)
                if !search.isEmpty {
                    Button { search = "" } label: { Image(systemName: "xmark.circle.fill") }
                        .buttonStyle(.plain)
                        .foregroundStyle(.secondary)
                        .accessibilityLabel("Clear search")
                }
            }
            .padding(.horizontal, 8)
            .frame(width: 200, height: 26)
            .background(.quaternary.opacity(0.6), in: RoundedRectangle(cornerRadius: 6))
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 8)
    }

    private func chip(_ title: String, selected: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title).font(.callout)
                .padding(.horizontal, 10).padding(.vertical, 3)
                .foregroundStyle(selected ? Color(nsColor: .windowBackgroundColor) : .primary)
                .background(selected ? Color.primary : Color.secondary.opacity(0.12), in: Capsule())
        }
        .buttonStyle(.plain)
    }

    private func groupHeader(_ risk: Risk, _ group: [Finding]) -> some View {
        let allChecked = group.allSatisfy { model.checked.contains($0.id) }
        return HStack(spacing: 10) {
            RiskBadge(risk: risk)
            Text("\(group.count) item\(group.count == 1 ? "" : "s") · \(SizeFormat.string(group.reduce(0) { $0 + $1.size }))")
                .foregroundStyle(.secondary)
            Spacer()
            if risk != .needsAdmin {
                Button(allChecked ? "Deselect all" : "Select all") {
                    let ids = group.map(\.id)
                    if allChecked { model.checked.subtract(ids) } else { model.checked.formUnion(ids) }
                }
                .buttonStyle(.link)
                .font(.callout)
            }
        }
        .padding(.vertical, 4)
    }

    private var selectionBar: some View {
        HStack(spacing: 12) {
            if model.checked.isEmpty {
                Text("Select items to clean. Nothing changes until you review and confirm.")
                    .foregroundStyle(.secondary)
            } else {
                Text("\(model.checked.count) item\(model.checked.count == 1 ? "" : "s") selected").fontWeight(.semibold)
                    + Text(" · \(SizeFormat.string(model.checkedSize))").foregroundColor(.secondary)
            }
            Spacer()
            if !model.checked.isEmpty {
                Button("Clear selection") { model.checked.removeAll() }
            }
            Button("Review \(model.checked.count) item\(model.checked.count == 1 ? "" : "s")") {
                model.outcomes = nil
                model.showReview = true
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
            .disabled(model.checked.isEmpty)
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 10)
        .background(.bar)
        .overlay(alignment: .top) { Divider() }
    }
}

@MainActor
struct FindingRow: View {
    @Environment(AppModel.self) private var model
    let finding: Finding

    var body: some View {
        HStack(spacing: 12) {
            if finding.risk != .needsAdmin {
                Toggle("", isOn: Binding(
                    get: { model.checked.contains(finding.id) },
                    set: { if $0 { model.checked.insert(finding.id) } else { model.checked.remove(finding.id) } }
                ))
                .toggleStyle(.checkbox)
                .labelsHidden()
                .accessibilityLabel("Select \(finding.title)")
            } else {
                Image(systemName: "lock").foregroundStyle(.secondary).frame(width: 14)
            }
            VStack(alignment: .leading, spacing: 2) {
                Text(finding.title).fontWeight(.medium).lineLimit(1)
                Text(finding.subtitle).font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
            }
            Spacer(minLength: 8)
            SizeText(bytes: finding.size).frame(width: 80, alignment: .trailing)
            if finding.actions.count > 1 {
                Picker("Action", selection: Binding(
                    get: { model.action(for: finding).id },
                    set: { id in if let a = finding.actions.first(where: { $0.id == id }) { model.setAction(a, for: finding) } }
                )) {
                    ForEach(finding.actions) { a in Text(a.displayLabel).tag(a.id) }
                }
                .labelsHidden()
                .pickerStyle(.menu)
                .frame(width: 140)
            } else {
                // One option only: plain text, not a greyed-out menu.
                Text(model.action(for: finding).displayLabel)
                    .foregroundStyle(.secondary)
                    .padding(.leading, 8)
                    .frame(width: 140, alignment: .leading)
            }
            Button {
                model.toggleExplanation(for: finding)
            } label: {
                Image(systemName: model.inspectedID == finding.id ? "info.circle.fill" : "info.circle")
                    .font(.title3)
            }
            .buttonStyle(.plain)
            .foregroundStyle(model.inspectedID == finding.id ? Color.accentColor : .secondary)
            .help("What happens if I clean this?")
            .accessibilityLabel("Explain \(finding.title)")
        }
        .padding(.vertical, 3)
        // Clicking anywhere on the row (outside its controls) opens or closes
        // the explanation, same as the ⓘ button.
        .contentShape(Rectangle())
        .onTapGesture { model.toggleExplanation(for: finding) }
        .contextMenu {
            Button("Show in Finder") { if let p = finding.paths.first { model.reveal(p) } }
                .disabled(finding.paths.isEmpty)
            Button("Always ignore") { model.ignore(finding) }
        }
    }
}
