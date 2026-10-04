import DevSweepCore
import SwiftUI

// MARK: - Cleanup

/// Local copies you haven't opened in a while, and what removing them would free.
@MainActor
struct CleanupTab: View {
    @Environment(AppModel.self) private var model
    @State private var confirmAll = false

    private static let choices: [(days: Int, title: String)] = [
        (7, "1 week"), (14, "2 weeks"), (21, "3 weeks"), (30, "1 month"), (60, "2 months"), (90, "3 months"),
    ]

    var body: some View {
        let idle = model.idleProjects
        let safe = model.safeIdleProjects
        VStack(spacing: 0) {
            HStack(spacing: 8) {
                Text("Not opened or changed for")
                Picker("", selection: Binding(get: { model.idleDays }, set: { model.setIdleDays($0) })) {
                    ForEach(Self.choices, id: \.days) { Text($0.title).tag($0.days) }
                }
                .labelsHidden().fixedSize()
                Spacer()
                if !safe.isEmpty {
                    Button("Remove \(safe.count) safe project\(safe.count == 1 ? "" : "s") · free \(SizeFormat.string(safe.reduce(0) { $0 + $1.localSize }))…") {
                        confirmAll = true
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled(model.busyProjectID != nil)
                }
            }
            .padding(.horizontal, 16).padding(.vertical, 10)
            Divider()
            SidePanelLayout(selected: model.selectedProjectID, width: 360) {
                if idle.isEmpty {
                    ContentUnavailableView("Disk is tidy", systemImage: "checkmark.seal",
                                           description: Text("Nothing on this Mac has sat untouched for \(Self.choices.first { $0.days == model.idleDays }?.title ?? "\(model.idleDays) days")."))
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                } else {
                    // The summary lives inside the list. Wrapping text beside the list changes height
                    // as the side panel opens, which leaves the whole window blank.
                    List {
                        Section {
                            ForEach(idle) { row($0) }
                        } header: {
                            Text("\(idle.count) project\(idle.count == 1 ? "" : "s") · \(SizeFormat.string(idle.reduce(0) { $0 + $1.localSize })) · \(safe.count) fully on GitHub and safe to remove")
                                .textCase(nil).font(.callout).foregroundStyle(.secondary)
                        }
                    }
                    .listStyle(.inset)
                }
            } panel: { id in
                if let p = model.projects.first(where: { $0.id == id }) { ProjectDetail(project: p) }
            }
        }
        .confirmationDialog("Remove \(safe.count) project\(safe.count == 1 ? "" : "s") from this Mac?", isPresented: $confirmAll) {
            Button("Move to Trash", role: .destructive) { model.removeAllSafeIdle() }
        } message: {
            Text("Each one is fully pushed to GitHub, with no uncommitted changes or stashes. They go to the Trash, and you can download them again.")
        }
    }

    private func row(_ p: Project) -> some View {
        let selected = model.selectedProjectID == p.id
        let s = p.safety ?? GitSafety(hasRemote: false)
        return HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text(p.nameWithOwner).fontWeight(.medium).lineLimit(1)
                Text("Last opened \(p.lastUsed.map { $0.formatted(.relative(presentation: .named)) } ?? "a long time ago") · \(FS_abbreviate(p.localPath ?? ""))")
                    .font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
            }
            Spacer(minLength: 8)
            if model.busyProjectID == p.id || model.busyProjectID == "all" {
                ProgressView().controlSize(.small)
            } else if s.isSafeToRemove {
                StatusTag(text: "Safe to remove", color: .green)
                Button("Remove") { model.removeFromMac(p) }.controlSize(.small)
            } else {
                StatusTag(text: p.status.text, color: .orange)
            }
            Text(SizeFormat.string(p.localSize)).foregroundStyle(.secondary).monospacedDigit().frame(width: 70, alignment: .trailing)
            Image(systemName: selected ? "info.circle.fill" : "info.circle")
                .font(.title3).foregroundStyle(selected ? Color.accentColor : .secondary)
        }
        .modifier(SelectableRow(selected: selected) { model.toggleProject(p) })
    }
}

// MARK: - Accounts

@MainActor
struct AccountsTab: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                Text("Each account's name and email are written into every project you download with it, so commits are attributed correctly whatever your global Git identity is. DevSweep uses each account's own token, so the account active in your terminal never changes.")
                    .foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                if model.githubAccounts.isEmpty {
                    if model.githubStatus == nil && !model.isLoadingProjects {
                        Text("Not checked yet.").foregroundStyle(.secondary)
                    } else {
                        GitHubSetupCard { model.projectSheet = .addAccount }
                    }
                }
                let summaries = Dictionary(uniqueKeysWithValues: model.accountSummaries.map { ($0.login, $0) })
                ForEach(model.githubAccounts) { a in AccountCard(account: a, summary: summaries[a.login]) }

                let outside = model.outsideOwners
                if !outside.isEmpty {
                    VStack(alignment: .leading, spacing: 8) {
                        PanelHeading("Clones from accounts you're not signed in to")
                        ForEach(outside) { o in
                            HStack {
                                Text(o.owner).fontWeight(.medium)
                                Spacer()
                                Text("\(o.projects) project\(o.projects == 1 ? "" : "s") · \(SizeFormat.string(o.bytes))")
                                    .foregroundStyle(.secondary).monospacedDigit()
                            }
                            .padding(.vertical, 4)
                            Divider()
                        }
                        Text("They show in Projects and can be removed safely, but to download more from \(outside.count == 1 ? "this owner" : "these owners") you'd need to sign in with an account that can read them.")
                            .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    }
                }

                HStack {
                    Button("Add account…") { model.projectSheet = .addAccount }.buttonStyle(.borderedProminent)
                    Button("Refresh") { model.refreshProjects() }.disabled(model.isLoadingProjects)
                    Spacer()
                    SettingsLink { Text("Downloads and folders in Settings") }
                        .buttonStyle(.link)
                        .simultaneousGesture(TapGesture().onEnded { UserDefaults.standard.set("github", forKey: "settingsTab") })
                }
            }
            .padding(20)
        }
    }
}

/// One signed-in account: what it has here, and its commit identity.
@MainActor
struct AccountCard: View {
    @Environment(AppModel.self) private var model
    let account: GitHubAccount
    var summary: AccountSummary?
    @State private var name = ""
    @State private var email = ""
    @State private var saved = GitIdentity()

    private var changed: Bool { GitIdentity(name: name, email: email) != saved }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                Image(systemName: "person.crop.circle.fill").font(.title2).foregroundStyle(.secondary)
                VStack(alignment: .leading, spacing: 1) {
                    HStack(spacing: 6) {
                        Text(account.login).font(.headline)
                        if account.isActive { StatusTag(text: "Active in Terminal", color: .secondary) }
                    }
                    if let s = summary {
                        Text("\(s.repos) repositories · \(s.onDisk) on this Mac · \(SizeFormat.string(s.bytes))")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }
                Spacer()
            }
            HStack {
                TextField("Name for commits", text: $name).textFieldStyle(.roundedBorder)
                TextField("Email for commits", text: $email).textFieldStyle(.roundedBorder)
                Button("Save") {
                    model.saveIdentity(GitIdentity(name: name, email: email), for: account.login)
                    saved = GitIdentity(name: name, email: email)
                }
                .disabled(!changed)
            }
            if name.isEmpty && email.isEmpty {
                Label("Not set. Projects downloaded with this account use your global Git identity.", systemImage: "exclamationmark.triangle.fill")
                    .font(.caption).foregroundStyle(.orange)
            } else if !changed {
                Label("Commits in projects downloaded with this account are made as \(name) <\(email)>.", systemImage: "checkmark.circle.fill")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .padding(14)
        .background(.background, in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(.separator))
        .onAppear {
            let i = model.identity(for: account.login) ?? GitIdentity()
            name = i.name; email = i.email; saved = i
        }
    }
}

@MainActor
struct AddAccountSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var username = ""
    @State private var name = ""
    @State private var email = ""

    var body: some View {
        SheetFrame(title: "Add a GitHub account", subtitle: "Opens GitHub's sign-in in Terminal. The commit identity below is saved for the account, so projects you download with it are attributed correctly from the start.") {
            Form {
                TextField("GitHub username", text: $username, prompt: Text("octocat"))
                TextField("Name for commits", text: $name, prompt: Text("Octo Cat"))
                TextField("Email for commits", text: $email, prompt: Text("octo@example.com"))
            }
            .formStyle(.columns)
            Text("When you've finished signing in in Terminal, press Refresh on the Accounts tab and the account appears.")
                .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Open sign-in in Terminal") {
                    model.startSignIn(username: username, identity: GitIdentity(name: name, email: email))
                    dismiss()
                }
                .buttonStyle(.borderedProminent)
                .keyboardShortcut(.defaultAction)
            }
        }
    }
}

// MARK: - Activity

/// A running trail of downloads, removals, publishes and account changes.
@MainActor
struct ActivityTab: View {
    @Environment(AppModel.self) private var model

    private var byDay: [(Date, [ProjectActivity])] {
        Dictionary(grouping: model.activity) { Calendar.current.startOfDay(for: $0.date) }.sorted { $0.key > $1.key }
    }

    var body: some View {
        if model.activity.isEmpty {
            ContentUnavailableView("Nothing yet", systemImage: "clock",
                                   description: Text("Downloads, removals, publishes and account changes are listed here."))
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else {
            List {
                ForEach(byDay, id: \.0) { day, events in
                    Section(day.formatted(date: .complete, time: .omitted)) {
                        ForEach(events) { row($0) }
                    }
                }
            }
            .listStyle(.inset)
        }
    }

    private func row(_ e: ProjectActivity) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Text(e.date.formatted(date: .omitted, time: .shortened)).font(.caption).foregroundStyle(.secondary)
                .monospacedDigit().frame(width: 62, alignment: .leading).padding(.top, 2)
            Image(systemName: Self.icon(e.kind)).foregroundStyle(Self.color(e.kind)).frame(width: 20).padding(.top, 1)
            VStack(alignment: .leading, spacing: 2) {
                Text(Self.verb(e.kind) + " " + e.subject).fontWeight(.medium)
                if !e.detail.isEmpty { Text(e.detail).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true) }
            }
            Spacer()
        }
        .padding(.vertical, 3)
    }

    static func verb(_ k: ProjectActivity.Kind) -> String {
        switch k {
        case .download: return "Downloaded"
        case .remove: return "Removed"
        case .publish: return "Published"
        case .push: return "Pushed"
        case .addRepo: return "Added"
        case .addAccount: return "Signed in to"
        case .identity: return "Commit identity for"
        }
    }

    static func icon(_ k: ProjectActivity.Kind) -> String {
        switch k {
        case .download: return "arrow.down.circle"
        case .remove: return "trash"
        case .publish: return "arrow.up.circle"
        case .push: return "arrow.up.forward.circle"
        case .addRepo: return "plus.circle"
        case .addAccount: return "person.crop.circle.badge.plus"
        case .identity: return "person.text.rectangle"
        }
    }

    static func color(_ k: ProjectActivity.Kind) -> Color {
        switch k {
        case .download, .addRepo: return .blue
        case .remove: return .orange
        case .publish, .push: return .green
        case .addAccount, .identity: return .secondary
        }
    }
}
