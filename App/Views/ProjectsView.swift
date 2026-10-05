import DevSweepCore
import SwiftUI

/// Projects, with RepoShelf's other tabs: Cleanup, Accounts and Activity.
@MainActor
struct ProjectsView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        VStack(spacing: 0) {
            ZStack {
                Group {
                    switch model.projectsTab {
                    case .projects: ProjectListTab()
                    case .cleanup: CleanupTab()
                    case .accounts: AccountsTab()
                    case .activity: ActivityTab()
                    }
                }
                .modifier(FadeIn())
                .id(model.projectsTab)
            }
        }
        .navigationTitle("Projects")
        .navigationSubtitle(subtitle)
        .sheet(item: $model.projectSheet) { sheet in
            switch sheet {
            case .clone(let id): if let p = model.projects.first(where: { $0.id == id }) { CloneSheet(project: p) }
            case .publish: PublishSheet()
            case .addByURL: AddByURLSheet()
            case .addAccount: AddAccountSheet()
            }
        }
        .task { if !model.hasLoadedProjects { model.refreshProjects() } }
    }

    private func label(_ tab: ProjectsTab) -> String {
        let n: Int
        switch tab {
        case .projects: n = model.projects.filter(\.onDisk).count
        case .cleanup: n = model.idleProjects.count
        case .accounts: n = model.githubAccounts.count
        case .activity: n = 0
        }
        return n > 0 ? "\(tab.title) · \(n)" : tab.title
    }

    private var subtitle: String {
        if !model.hasLoadedProjects { return model.isLoadingProjects ? "Looking…" : "Not loaded yet" }
        let onMac = model.projects.filter(\.onDisk)
        if onMac.isEmpty { return "No projects on this Mac" + (model.isLoadingProjects ? " · refreshing" : "") }
        let size = onMac.reduce(0) { $0 + $1.localSize }
        return "\(onMac.count) on this Mac · \(SizeFormat.string(size))" + (model.isLoadingProjects ? " · refreshing" : "")
    }
}

/// The project list: every repo on this Mac and on GitHub.
@MainActor
struct ProjectListTab: View {
    @Environment(AppModel.self) private var model
    @State private var search = ""

    private var items: [Project] {
        model.visibleProjects.filter {
            search.isEmpty || $0.nameWithOwner.localizedCaseInsensitiveContains(search)
                || $0.description.localizedCaseInsensitiveContains(search)
        }
    }

    var body: some View {
        VStack(spacing: 0) {
            controls
            Divider()
            SidePanelLayout(selected: model.selectedProjectID, width: 360) {
                listArea
            } panel: { id in
                if let p = model.projects.first(where: { $0.id == id }) { ProjectDetail(project: p) }
            }
        }
    }

    private var controls: some View {
        @Bindable var model = model
        return HStack(spacing: 12) {
            Picker("Show", selection: $model.projectFilter) {
                ForEach(ProjectFilter.allCases) { Text(label($0)).tag($0) }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .fixedSize()

            if model.githubAccounts.count > 1 {
                Picker("Account", selection: $model.accountFilter) {
                    Text("All accounts").tag(String?.none)
                    ForEach(model.githubAccounts) { Text($0.login).tag(String?.some($0.login)) }
                }
                .labelsHidden()
                .fixedSize()
            }
            Spacer()
            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                TextField("Search", text: $search).textFieldStyle(.plain)
            }
            .padding(.horizontal, 8).frame(width: 170, height: 26)
            .background(.quaternary.opacity(0.6), in: RoundedRectangle(cornerRadius: 6))

            Menu {
                Button("Add a project by URL…") { model.projectSheet = .addByURL }
                Button("Publish a folder to GitHub…") { model.projectSheet = .publish }
                Divider()
                Button("Refresh") { model.refreshProjects() }
            } label: { Label("Get a project", systemImage: "plus") }
            .fixedSize()
        }
        .padding(.horizontal, 16).padding(.vertical, 8)
    }

    private func label(_ f: ProjectFilter) -> String {
        let n: Int
        switch f {
        case .onMac: n = model.projects.filter(\.onDisk).count
        case .onGitHub: n = model.projects.filter { !$0.onDisk }.count
        case .all: n = model.projects.count
        }
        return n > 0 ? "\(f.title) · \(n)" : f.title
    }

    @ViewBuilder
    private var listArea: some View {
        VStack(spacing: 0) {
            if model.githubStatus != nil || model.isLoadingProjects, !githubReady {
                GitHubSetupCard { model.projectSheet = .addAccount }.padding(14)
                Divider()
            }
            if let message = model.projectsMessage {
                HStack(spacing: 10) {
                    Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                    Text(message).font(.callout).fixedSize(horizontal: false, vertical: true)
                    Spacer()
                    Button("Try again") { model.refreshProjects() }.controlSize(.small)
                }
                .padding(.horizontal, 16).padding(.vertical, 8)
                .background(Color.orange.opacity(0.1))
            }
            if items.isEmpty {
                ContentUnavailableView(emptyTitle, systemImage: "folder", description: Text(emptyDetail))
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List { ForEach(items) { row($0) } }
                    .listStyle(.inset)
            }
        }
    }

    /// Signed in to at least one account.
    private var githubReady: Bool { if case .signedIn = model.githubStatus { return true } else { return false } }

    private var emptyTitle: String {
        if !model.hasLoadedProjects || model.isLoadingProjects && model.projects.isEmpty { return "Looking…" }
        if model.githubStatus == nil, model.projectFilter != .onMac { return "Looking…" }
        if !githubReady, model.projectFilter != .onMac { return "GitHub isn't connected" }
        switch model.projectFilter {
        case .onMac: return "No projects found"
        case .onGitHub: return "Everything on GitHub is here"
        case .all: return "No projects yet"
        }
    }

    private var emptyDetail: String {
        if model.githubStatus == nil, model.projectFilter != .onMac { return "Checking your GitHub accounts." }
        if !githubReady, model.projectFilter != .onMac { return "Connect GitHub above to list your repositories and download them." }
        switch model.projectFilter {
        case .onMac: return "DevSweep looks in the project folders listed in Settings."
        case .onGitHub: return "Repos you haven't downloaded appear here, ready to download."
        case .all: return "Sign in to GitHub in Accounts, or download or publish a project."
        }
    }

    private func row(_ p: Project) -> some View {
        let selected = model.selectedProjectID == p.id
        let status = p.status
        return HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(p.name).fontWeight(.medium).lineLimit(1)
                    if p.isPrivate == true { Image(systemName: "lock.fill").font(.caption2).foregroundStyle(.secondary) }
                }
                Text(p.onDisk ? FS_abbreviate(p.localPath ?? "") : [p.owner, p.description].filter { !$0.isEmpty }.joined(separator: " · "))
                    .font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
            }
            Spacer(minLength: 8)
            if model.busyProjectID == p.id || model.busyProjectID == "all" {
                ProgressView().controlSize(.small)
            } else if !p.onDisk {
                Button("Download") { model.projectSheet = .clone(p.id) }.controlSize(.small)
            } else {
                StatusTag(text: status.text, color: status.level == .ok ? .green : (status.level == .attention ? .orange : .secondary))
            }
            Text(p.onDisk ? SizeFormat.string(p.localSize) : (p.remoteKB > 0 ? SizeFormat.string(Int64(p.remoteKB) * 1024) : ""))
                .foregroundStyle(.secondary).monospacedDigit().frame(width: 70, alignment: .trailing)
            Text(p.lastUsed.map { $0.formatted(.relative(presentation: .numeric, unitsStyle: .abbreviated)) } ?? "")
                .font(.caption).foregroundStyle(.secondary).frame(width: 70, alignment: .trailing)
            Image(systemName: selected ? "info.circle.fill" : "info.circle")
                .font(.title3).foregroundStyle(selected ? Color.accentColor : .secondary)
        }
        .modifier(SelectableRow(selected: selected) { model.toggleProject(p) })
    }
}

// MARK: - Panel

@MainActor
struct ProjectDetail: View {
    @Environment(AppModel.self) private var model
    let project: Project
    @State private var confirmRemove = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                HStack(alignment: .top) {
                    VStack(alignment: .leading, spacing: 3) {
                        Text(project.name).font(.title3.weight(.semibold))
                        Text([project.owner.isEmpty ? nil : project.owner, project.isPrivate.map { $0 ? "private" : "public" },
                              project.strategy.map { "\($0.title.lowercased()) clone" }].compactMap { $0 }.joined(separator: " · "))
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                    CloseButton { model.selectedProjectID = nil }
                }
                if !project.description.isEmpty { Text(project.description).font(.callout).foregroundStyle(.secondary) }

                FlowLayout(spacing: 8) {
                    if project.onDisk {
                        Button("Open in editor") { model.openInEditor(project) }
                        Button("Show in Finder") { model.reveal(project.localPath ?? "") }
                    } else {
                        Button("Download…") { model.projectSheet = .clone(project.id) }.buttonStyle(.borderedProminent)
                    }
                    if project.onGitHub { Button("Open on GitHub") { model.openOnGitHub(project) } }
                }

                if project.onDisk { safetySection } else {
                    Text("This project isn't on your Mac. Download it when you need it, and remove it again when you're done.")
                        .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
            }
            .padding(20)
        }
        .confirmationDialog("Move \(project.name) to the Trash?", isPresented: $confirmRemove) {
            Button("Move to Trash", role: .destructive) { model.removeFromMac(project) }
        } message: {
            Text("It's fully pushed to GitHub. You can download it again, or restore it from History.")
        }
    }

    @ViewBuilder
    private var safetySection: some View {
        let s = project.safety ?? GitSafety(hasRemote: false)
        VStack(alignment: .leading, spacing: 8) {
            PanelHeading("Is it safe to remove from this Mac?")
            if !s.hasRemote {
                CheckRow(check: .warning("It has no remote, so it exists only here"))
            } else {
                CheckRow(check: s.unpushedCommits == 0 ? .passed("Everything is pushed to GitHub")
                         : .warning("\(s.unpushedCommits) commit\(s.unpushedCommits == 1 ? "" : "s") aren't on GitHub yet"))
                CheckRow(check: s.changedFiles == 0 ? .passed("No uncommitted changes")
                         : .warning("\(s.changedFiles) file\(s.changedFiles == 1 ? " has" : "s have") changes that aren't committed"))
                CheckRow(check: s.untrackedFiles == 0 ? .passed("No files outside git")
                         : .warning("\(s.untrackedFiles) file\(s.untrackedFiles == 1 ? " isn't" : "s aren't") in git: "
                                    + s.untrackedSample.joined(separator: ", ") + (s.untrackedFiles > s.untrackedSample.count ? "…" : "")))
                CheckRow(check: s.stashes == 0 ? .passed("No stashes")
                         : .warning("\(s.stashes) stash\(s.stashes == 1 ? "" : "es") would be lost"))
                Text("Compared with GitHub as of this Mac's last fetch.").font(.caption).foregroundStyle(.secondary)
            }
        }

        if s.isSafeToRemove {
            callout("Safe to remove. It stays in this list with a Download button.", color: .green)
        } else if !s.hasRemote {
            callout("Nothing on GitHub keeps a copy of this folder. Publish it first, or remove it anyway after DevSweep warns you.", color: .orange)
        } else {
            callout("Not everything is saved on GitHub. You can still remove it, and DevSweep warns you first about what would be lost. Pushing your work avoids that.", color: .orange)
        }

        FlowLayout(spacing: 8) {
            if !s.hasRemote {
                Button("Publish to GitHub…") { model.projectSheet = .publish }.buttonStyle(.borderedProminent)
            } else if s.unpushedCommits > 0 {
                Button("Push \(s.unpushedCommits) commit\(s.unpushedCommits == 1 ? "" : "s")") { model.push(project) }.buttonStyle(.borderedProminent)
            }
            Button("Remove from Mac…", role: .destructive) {
                if s.isSafeToRemove { confirmRemove = true } else { model.pendingRiskyRemoval = project }
            }
            .buttonStyle(DestructiveButtonStyle())
            .disabled(model.busyProjectID != nil)
        }
        Text("Removing moves the folder to the Trash. If anything in it isn't on GitHub, DevSweep warns you first.")
            .font(.caption).foregroundStyle(.secondary)
    }

    private func callout(_ text: String, color: Color) -> some View {
        Text(text).font(.callout).fixedSize(horizontal: false, vertical: true)
            .padding(10).frame(maxWidth: .infinity, alignment: .leading)
            .background(color.opacity(0.12), in: RoundedRectangle(cornerRadius: 8))
    }
}
