import AppKit
import DevSweepCore
import SwiftUI

@MainActor
struct SheetFrame<Content: View>: View {
    let title: String
    let subtitle: String
    @ViewBuilder let content: () -> Content

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            VStack(alignment: .leading, spacing: 4) {
                Text(title).font(.title2.weight(.semibold))
                Text(subtitle).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            content()
        }
        .padding(24)
        .frame(width: 520)
    }
}

// MARK: - Download

@MainActor
struct CloneSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let project: Project

    @State private var strategy: CloneStrategy = .blobless
    @State private var destination: URL?
    @State private var error: String?
    @State private var working = false

    private var target: URL {
        destination ?? ProjectScanner.destination(for: project, state: model.projectsState, home: FileManager.default.homeDirectoryForCurrentUser)
    }

    private var identity: GitIdentity? {
        guard let a = model.credentialAccount(for: project) else { return nil }
        let i = model.identity(for: a)
        return (i?.isBlank ?? true) ? nil : i
    }

    var body: some View {
        SheetFrame(title: "Download \(project.name)", subtitle: "\(project.nameWithOwner)\(project.isPrivate == true ? " · private" : "")") {
            VStack(spacing: 8) {
                ForEach(CloneStrategy.allCases) { s in option(s) }
            }
            VStack(alignment: .leading, spacing: 6) {
                PanelHeading("Where it goes")
                HStack {
                    Text(FS_abbreviate(target.path)).font(.callout.monospaced()).lineLimit(1).truncationMode(.middle)
                    Spacer()
                    Button("Choose…") { choose() }.controlSize(.small)
                }
            }
            identityNote
            if let error { Text(error).font(.callout).foregroundStyle(.red).fixedSize(horizontal: false, vertical: true) }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction).disabled(working)
                Button {
                    working = true
                    error = nil
                    model.download(project, strategy: strategy, to: target) { err in
                        working = false
                        if let err { error = err } else { dismiss() }
                    }
                } label: {
                    if working { HStack(spacing: 6) { ProgressView().controlSize(.small); Text("Downloading…") } } else { Text("Download") }
                }
                .buttonStyle(.borderedProminent)
                .keyboardShortcut(.defaultAction)
                .disabled(working)
            }
        }
        .onAppear { strategy = model.projectsState.strategies[project.nameWithOwner] ?? .blobless }
    }

    private func option(_ s: CloneStrategy) -> some View {
        let selected = strategy == s
        let estimate = project.remoteKB > 0 ? "about " + SizeFormat.string(Int64(Double(project.remoteKB) * 1024 * s.sizeFactor)) : nil
        return Button { strategy = s } label: {
            HStack(alignment: .top, spacing: 10) {
                Image(systemName: selected ? "largecircle.fill.circle" : "circle").foregroundStyle(selected ? Color.accentColor : .secondary)
                VStack(alignment: .leading, spacing: 2) {
                    HStack {
                        Text(s.title).fontWeight(.semibold)
                        if let estimate { Text(estimate).font(.caption).foregroundStyle(.secondary) }
                    }
                    Text(s.summary).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 0)
            }
            .padding(10).contentShape(Rectangle())
            .background(selected ? Color.accentColor.opacity(0.07) : .clear, in: RoundedRectangle(cornerRadius: 8))
            .overlay(RoundedRectangle(cornerRadius: 8).stroke(selected ? Color.accentColor : Color.secondary.opacity(0.25)))
        }
        .buttonStyle(.plain)
    }

    @ViewBuilder
    private var identityNote: some View {
        if let i = identity {
            Label("Commits here will be made as \(i.name) <\(i.email)>.", systemImage: "person.crop.circle.badge.checkmark")
                .font(.callout).foregroundStyle(.secondary)
        } else {
            HStack(alignment: .top, spacing: 8) {
                Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                VStack(alignment: .leading, spacing: 4) {
                    Text("No commit identity saved for \(model.credentialAccount(for: project) ?? "this account"), so commits here will use your global Git identity.")
                        .font(.callout).fixedSize(horizontal: false, vertical: true)
                    Button("Set one in Settings, GitHub") { model.openSettings() }.buttonStyle(.link).font(.callout)
                }
            }
            .padding(10).background(Color.orange.opacity(0.1), in: RoundedRectangle(cornerRadius: 8))
        }
    }

    private func choose() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.prompt = "Choose"
        panel.message = "Pick the folder to put \(project.name) in."
        if panel.runModal() == .OK, let url = panel.url { destination = url.appendingPathComponent(project.name) }
    }
}

// MARK: - Publish

@MainActor
struct PublishSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss

    @State private var folder: URL?
    @State private var name = ""
    @State private var account = ""
    @State private var summary = ""
    @State private var isPrivate = true
    @State private var secrets: [SecurityFinding] = []
    @State private var acknowledged = false
    @State private var error: String?
    @State private var working = false

    var body: some View {
        SheetFrame(title: "Publish a folder to GitHub", subtitle: "Creates a new repository from a folder on this Mac, with its first commit.") {
            VStack(alignment: .leading, spacing: 6) {
                PanelHeading("Folder")
                HStack {
                    Text(folder.map { FS_abbreviate($0.path) } ?? "No folder chosen").font(.callout.monospaced())
                        .foregroundStyle(folder == nil ? .secondary : .primary).lineLimit(1).truncationMode(.middle)
                    Spacer()
                    Button("Choose…") { choose() }.controlSize(.small)
                }
            }
            if folder != nil {
                Form {
                    TextField("Name", text: $name)
                    Picker("Account", selection: $account) {
                        ForEach(model.githubAccounts) { Text($0.login).tag($0.login) }
                    }
                    TextField("Description", text: $summary)
                    Picker("Visibility", selection: $isPrivate) {
                        Text("Private").tag(true)
                        Text("Public").tag(false)
                    }
                    .pickerStyle(.segmented)
                }
                .formStyle(.columns)
                if !isPrivate {
                    Text("Public means anyone can see the code and its history.").font(.callout).foregroundStyle(.secondary)
                }
                secretsWarning
            }
            if let error { Text(error).font(.callout).foregroundStyle(.red).fixedSize(horizontal: false, vertical: true) }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction).disabled(working)
                Button {
                    guard let folder else { return }
                    working = true
                    error = nil
                    model.publish(folder: folder, name: name, account: account, description: summary, isPrivate: isPrivate) { err in
                        working = false
                        if let err { error = err } else { dismiss() }
                    }
                } label: {
                    if working { HStack(spacing: 6) { ProgressView().controlSize(.small); Text("Publishing…") } } else { Text("Publish") }
                }
                .buttonStyle(.borderedProminent)
                .keyboardShortcut(.defaultAction)
                .disabled(folder == nil || name.isEmpty || account.isEmpty || working || (!secrets.isEmpty && !acknowledged))
            }
        }
        .onAppear { account = model.githubAccounts.first(where: \.isActive)?.login ?? model.githubAccounts.first?.login ?? "" }
    }

    @ViewBuilder
    private var secretsWarning: some View {
        if !secrets.isEmpty {
            VStack(alignment: .leading, spacing: 6) {
                Label("\(secrets.count) file\(secrets.count == 1 ? "" : "s") in this folder look like secrets", systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.orange).fontWeight(.semibold)
                ForEach(secrets.prefix(4), id: \.id) { f in
                    Text("• " + URL(fileURLWithPath: f.path).lastPathComponent + ", " + f.title).font(.callout)
                }
                Text("Everything in the folder is committed, and a public or private repo keeps its history. Add these to .gitignore or move them out first.")
                    .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                Toggle("Publish anyway", isOn: $acknowledged).toggleStyle(.checkbox)
            }
            .padding(10).background(Color.orange.opacity(0.1), in: RoundedRectangle(cornerRadius: 8))
        }
    }

    private func choose() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.prompt = "Choose"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        folder = url
        name = url.lastPathComponent.replacingOccurrences(of: " ", with: "-")
        acknowledged = false
        secrets = []
        Task { secrets = await Task.detached { SecurityScanner.scanFolder(url) }.value }
    }
}

// MARK: - Add by URL

@MainActor
struct AddByURLSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var text = ""
    @State private var error: String?
    @State private var working = false

    var body: some View {
        SheetFrame(title: "Add a project by URL", subtitle: "For a repository that isn't in your own lists, such as a fork or a team repo. It appears under Only on GitHub, ready to download.") {
            TextField("github.com/owner/name or owner/name", text: $text).textFieldStyle(.roundedBorder)
            if let error { Text(error).font(.callout).foregroundStyle(.red).fixedSize(horizontal: false, vertical: true) }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button {
                    working = true
                    error = nil
                    model.addByURL(text) { err in
                        working = false
                        if let err { error = err } else { dismiss() }
                    }
                } label: {
                    if working { ProgressView().controlSize(.small) } else { Text("Add") }
                }
                .buttonStyle(.borderedProminent)
                .keyboardShortcut(.defaultAction)
                .disabled(text.isEmpty || working)
            }
        }
    }
}
