#if DEBUG
import AppKit
import DevSweepCore
import SwiftUI

/// Regenerates the README screenshots. Debug builds only:
///
///   DEVSWEEP_SHOTS=docs/screenshots build/.../DevSweep.app/Contents/MacOS/DevSweep
///
/// Walks the app through each screen in light and dark mode and saves PNGs
/// of its own windows (an app may capture its own windows without screen
/// recording permission).
@MainActor
enum ScreenshotTour {
    static func run(model: AppModel, to dir: URL) async {
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        if let hidden = NSApp.windows.first(where: { !$0.isVisible && $0.frame.width > 600 }) {
            hidden.deminiaturize(nil); hidden.makeKeyAndOrderFront(nil); await pause(1)
        }
        guard let window = NSApp.windows.first(where: { $0.isVisible && $0.frame.width > 600 }) else {
            print("shots: no main window", NSApp.windows.map { "\($0.title) vis=\($0.isVisible) \($0.frame)" }); exit(1)
        }
        window.setContentSize(NSSize(width: 1280, height: 800))
        window.center()
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)

        model.checkSecurity()
        for _ in 0..<120 where model.versions == nil || model.isCheckingVersions || model.isCheckingSecurity || model.isLoadingProjects {
            await pause(0.5)
        }
        if ProcessInfo.processInfo.environment["DEVSWEEP_STATES"] != nil {
            model.lastScan = nil; model.hasCheckedSecurity = false; model.hasLoadedPorts = false
            model.githubAccounts = []; model.githubStatus = .notSignedIn; model.projects = []; model.hasLoadedProjects = true
            AppearanceMode.light.apply()
            for (name, item) in [("home", SidebarItem.home), ("cleanup", .cleanUp), ("projects", .projects), ("health", .health)] {
                model.selection = item
                model.githubAccounts = []; model.githubStatus = .notSignedIn; model.projects = []
                await pause(1.5)
                await saveStable(window, "state-\(name)", dir)
            }
            model.openSettings()
            await pause(2)
            await saveStable(window, "state-settings", dir)
            exit(0)
        }
        // Security and Ports would show where your own secret files are, so
        // the README shows made-up examples instead. Nothing touches disk.
        model.security = SampleData.security
        model.ports = SampleData.ports
        model.hasLoadedPorts = true
        model.hasCheckedSecurity = true
        model.projects = SampleData.projects
        model.githubAccounts = [GitHubAccount(login: "sample-dev", isActive: true), GitHubAccount(login: "sample-team", isActive: false)]
        model.projectsState = SampleData.projectsState
        model.hasLoadedProjects = true
        // Show scheduled scans as switched on, in memory only.
        model.schedule = ScanSchedule(enabled: true, frequency: .weekly, weekday: 2, hour: 9)
        model.lastScheduledRun = Date()
        model.useSampleData = true
        let panelItem = model.findings.first { $0.ruleID == "chrome-cache" } ?? model.findings.first
        let reviewIDs = pickReviewItems(model.findings)

        for mode in [AppearanceMode.light, .dark] {
            mode.apply()
            model.inspectedID = nil
            model.selection = .home
            await pause(1.5)
            await saveStable(window, "home-\(mode.rawValue)", dir)

            model.showWelcome = true
            await pause(1.2)
            saveWithSheet(window, "welcome-\(mode.rawValue)", dir)
            model.showWelcome = false
            await pause(0.8)

            model.selection = .cleanUp
            await pause(1)
            model.inspectedID = panelItem?.id
            await pause(1.2)
            await saveStable(window, "cleanup-\(mode.rawValue)", dir)

            model.inspectedID = nil
            model.selection = .projects
            model.projectFilter = .onMac
            await pause(0.8)
            model.selectedProjectID = model.projects.first { ($0.safety?.unpushedCommits ?? 0) > 0 }?.id
            await pause(1.4)
            await saveStable(window, "projects-\(mode.rawValue)", dir)
            model.selectedProjectID = nil

            for (tab, name) in [(ProjectsTab.cleanup, "idle"), (.accounts, "accounts"), (.activity, "activity")] {
                model.projectsTab = tab
                await pause(1.0)
                if tab == .cleanup {
                    model.selectedProjectID = model.idleProjects.first { $0.safety?.isSafeToRemove != true }?.id
                    await pause(1.2)
                }
                await saveStable(window, "\(name)-\(mode.rawValue)", dir)
                model.selectedProjectID = nil
            }
            model.projectsTab = .projects

            model.selection = .health
            model.healthTab = .security
            await pause(0.8)
            model.selectedSecurityID = model.visibleSecurity.first { $0.level == .critical }?.id
            await pause(1.4)
            await saveStable(window, "security-\(mode.rawValue)", dir)
            model.selectedSecurityID = nil

            model.healthTab = .ports
            await pause(0.8)
            model.selectedPortID = model.ports.first { $0.isDevelopment && $0.reachableFromNetwork }?.id
            await pause(1.4)
            await saveStable(window, "ports-\(mode.rawValue)", dir)
            model.selectedPortID = nil

            model.healthTab = .tools
            await pause(0.8)
            model.selectedRuntimeID = model.versions?.runtimes.first { $0.steps.contains { $0.kind == .guided } }?.id
                ?? model.versions?.runtimes.first?.id
            await pause(1.4)
            await saveStable(window, "health-\(mode.rawValue)", dir)
            model.selectedRuntimeID = nil

            model.selection = .history
            await pause(1)
            await saveStable(window, "history-\(mode.rawValue)", dir)

            model.selection = .cleanUp
            model.checked = reviewIDs
            model.outcomes = nil
            model.showReview = true
            await pause(1.5)
            saveWithSheet(window, "review-\(mode.rawValue)", dir)
            model.showReview = false
            model.checked = []
            await pause(0.8)

            await saveMenuBar(model, mode, dir)
            await saveSettings(mode, dir, model: model, window: window)
        }
        AppearanceMode.current.apply()
        print("SHOTS OK")
        exit(0)
    }

    /// Settings, inline: General, then Scans and alerts.
    private static func saveSettings(_ mode: AppearanceMode, _ dir: URL, model: AppModel, window: NSWindow) async {
        model.openSettings()
        await pause(1.2)
        await saveStable(window, "settings-\(mode.rawValue)", dir)
        model.openSettings()
        model.selection = .home
        await pause(0.5)
    }

    private static func pickReviewItems(_ findings: [Finding]) -> Set<String> {
        var picked: [Finding] = []
        for kind in [CleanAction.Kind.command, .delete, .trash] {
            picked += findings.filter { $0.defaultAction?.kind == kind && $0.risk != .needsAdmin }.prefix(2)
        }
        if let chrome = findings.first(where: { $0.ruleID == "chrome-cache" }) { picked.append(chrome) }
        return Set(picked.map(\.id))
    }

    private static func pause(_ seconds: Double) async {
        try? await Task.sleep(for: .milliseconds(Int(seconds * 1000)))
    }

    private static func image(of window: NSWindow) -> CGImage? {
        CGWindowListCreateImage(.null, .optionIncludingWindow, CGWindowID(window.windowNumber),
                                [.boundsIgnoreFraming, .bestResolution])
    }

    private static func write(_ image: CGImage, _ name: String, _ dir: URL) {
        let rep = NSBitmapImageRep(cgImage: image)
        guard let data = rep.representation(using: .png, properties: [:]) else { return }
        try? data.write(to: dir.appendingPathComponent("\(name).png"))
        print("shots: \(name).png \(image.width)x\(image.height)")
    }

    /// A window caught mid-redraw is half drawn and changes between captures. Keep
    /// capturing until two in a row are identical, so only a finished window is saved.
    private static func saveStable(_ window: NSWindow, _ name: String, _ dir: URL) async {
        var previous: Data?
        var last: CGImage?
        for _ in 0..<8 {
            guard let img = image(of: window), let data = NSBitmapImageRep(cgImage: img).representation(using: .png, properties: [:]) else {
                await pause(0.6); continue
            }
            last = img
            if data == previous { break }
            previous = data
            await pause(0.6)
        }
        if let last { write(last, name, dir) } else { print("shots: failed \(name)") }
    }

    private static func save(_ window: NSWindow, _ name: String, _ dir: URL) {
        guard let img = image(of: window) else { print("shots: failed \(name)"); return }
        write(img, name, dir)
    }

    /// The main window with its sheet drawn in place.
    private static func saveWithSheet(_ window: NSWindow, _ name: String, _ dir: URL) {
        guard let base = image(of: window) else { return }
        guard let sheet = window.attachedSheet, let sheetImg = image(of: sheet) else { write(base, name, dir); return }
        let scale = CGFloat(base.width) / window.frame.width
        guard let ctx = CGContext(data: nil, width: base.width, height: base.height, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return }
        ctx.draw(base, in: CGRect(x: 0, y: 0, width: base.width, height: base.height))
        let f = sheet.frame, w = window.frame
        let rect = CGRect(x: (f.minX - w.minX) * scale, y: (f.minY - w.minY) * scale,
                          width: CGFloat(sheetImg.width), height: CGFloat(sheetImg.height))
        ctx.setShadow(offset: CGSize(width: 0, height: -8 * scale), blur: 30 * scale,
                      color: NSColor.black.withAlphaComponent(0.35).cgColor)
        ctx.draw(sheetImg, in: rect)
        if let out = ctx.makeImage() { write(out, name, dir) }
    }

    /// The menu bar popover, shown briefly in a real window so it renders
    /// exactly as on screen (offscreen drawing loses text and materials).
    private static func saveMenuBar(_ model: AppModel, _ mode: AppearanceMode, _ dir: URL) async {
        let content = MenuBarView()
            .environment(model)
            .background(.regularMaterial)
            .clipShape(RoundedRectangle(cornerRadius: 12))
        let host = NSHostingView(rootView: content)
        host.frame.size = host.fittingSize
        let win = NSWindow(contentRect: host.frame, styleMask: [.borderless], backing: .buffered, defer: false)
        win.isOpaque = false
        win.backgroundColor = .clear
        win.hasShadow = false
        win.appearance = NSAppearance(named: mode == .dark ? .darkAqua : .aqua)
        win.contentView = host
        win.center()
        win.orderFrontRegardless()
        await pause(0.8)
        if let img = image(of: win) { write(img, "menubar-\(mode.rawValue)", dir) }
        win.orderOut(nil)
    }
}
/// Made-up findings for README screenshots.
enum SampleData {
    static let home = NSHomeDirectory()

    static let security: [SecurityFinding] = [
        SecurityFinding(kind: .recoveryCodes, level: .critical, title: "GitHub recovery codes",
                        path: home + "/Downloads/github-recovery-codes.txt", status: "Plain text",
                        why: "Anyone who gets this file can sign in to your GitHub account without your password or phone.",
                        whatToDo: "Save the codes in your password manager, then delete the file. GitHub can also generate new codes, which makes these useless.",
                        checks: [.passed("Recognised from the file name"), .passed("The codes themselves were never read into DevSweep")]),
        SecurityFinding(kind: .serviceAccountKey, level: .critical, title: "Google Cloud service-account key",
                        path: home + "/Downloads/my-project-4f2a.json", status: "Plain text",
                        why: "It gives full access to whatever that service account can reach, with no password and no expiry.",
                        whatToDo: "If you still need it, keep it outside Downloads or in a secrets manager. If you don't, delete it and revoke the key.",
                        checks: [.passed("Recognised from the file's structure (a service-account key)")]),
        SecurityFinding(kind: .envFile, level: .critical, title: ".env with 3 secrets is committed in demo-api",
                        path: home + "/Code/demo-api/.env", status: "Committed to git",
                        why: "Everyone with access to this repository, now or later, can read these secrets, including in its history.",
                        whatToDo: "Stop tracking the file and ignore it, then rotate the secrets.",
                        checks: [.warning("git tracks this file"), .passed("Only key names were checked; values were never kept")],
                        fix: ["cd ~/Code/demo-api", "git rm --cached .env", "echo .env >> .gitignore", "git commit -m \"Stop tracking .env\""]),
        SecurityFinding(kind: .envFile, level: .ok, title: ".env.local with 2 secrets in web-app",
                        path: home + "/Code/web-app/.env.local", status: "Kept out of git",
                        why: "This is the right way to keep local secrets: git ignores the file.",
                        whatToDo: "Nothing to do.", checks: [.passed("git ignores this file")]),
    ]

    static var projects: [Project] {
        let now = Date()
        let h = NSHomeDirectory()
        func ago(_ days: Double) -> Date { now.addingTimeInterval(-days * 86_400) }
        return [
            Project(id: "sample-dev/web-app", name: "web-app", nameWithOwner: "sample-dev/web-app", owner: "sample-dev",
                    description: "Marketing site", isPrivate: true, localPath: h + "/Code/sample-dev/web-app", localSize: 412_000_000,
                    remoteKB: 90_000, onGitHub: true, account: "sample-dev", safety: GitSafety(), lastOpened: now.addingTimeInterval(-7_200),
                    lastActivity: ago(1), strategy: .blobless),
            Project(id: "sample-dev/demo-api", name: "demo-api", nameWithOwner: "sample-dev/demo-api", owner: "sample-dev",
                    description: "REST API", isPrivate: true, localPath: h + "/Code/sample-dev/demo-api", localSize: 1_130_000_000,
                    remoteKB: 220_000, onGitHub: true, account: "sample-dev",
                    safety: GitSafety(unpushedCommits: 3, changedFiles: 2, stashes: 0), lastOpened: ago(1), lastActivity: ago(1), strategy: .blobless),
            Project(id: "sample-dev/design-system", name: "design-system", nameWithOwner: "sample-dev/design-system", owner: "sample-dev",
                    description: "", isPrivate: false, localPath: h + "/Code/sample-dev/design-system", localSize: 88_000_000,
                    remoteKB: 40_000, onGitHub: true, account: "sample-dev", safety: GitSafety(), lastOpened: ago(9), lastActivity: ago(9), strategy: .shallow),
            Project(id: "sample-dev/old-experiments", name: "old-experiments", nameWithOwner: "sample-dev/old-experiments", owner: "sample-dev",
                    description: "", isPrivate: true, localPath: h + "/Code/sample-dev/old-experiments", localSize: 640_000_000,
                    remoteKB: 150_000, onGitHub: true, account: "sample-dev", safety: GitSafety(), lastOpened: ago(75), lastActivity: ago(80), strategy: .full),
            Project(id: "local:scratch-notes", name: "scratch-notes", nameWithOwner: "scratch-notes", owner: "", description: "",
                    isPrivate: nil, localPath: h + "/Code/scratch-notes", localSize: 2_000_000, onGitHub: false,
                    safety: GitSafety(unpushedCommits: 0, changedFiles: 0, stashes: 0, hasRemote: false), lastActivity: ago(3)),
            Project(id: "sample-dev/prototype", name: "prototype", nameWithOwner: "sample-dev/prototype", owner: "sample-dev",
                    description: "", isPrivate: true, localPath: h + "/Code/sample-dev/prototype", localSize: 310_000_000,
                    remoteKB: 70_000, onGitHub: true, account: "sample-dev",
                    safety: GitSafety(unpushedCommits: 2, changedFiles: 0, stashes: 0), lastOpened: ago(60), lastActivity: ago(62), strategy: .blobless),
            Project(id: "sample-team/team-site", name: "team-site", nameWithOwner: "sample-team/team-site", owner: "sample-team",
                    description: "", isPrivate: true, localPath: h + "/Code/sample-team/team-site", localSize: 205_000_000,
                    remoteKB: 60_000, onGitHub: true, account: "sample-team", safety: GitSafety(), lastOpened: ago(3), lastActivity: ago(4), strategy: .blobless),
            Project(id: "client-org/landing-page", name: "landing-page", nameWithOwner: "client-org/landing-page", owner: "client-org",
                    description: "", isPrivate: nil, localPath: h + "/Code/client-org/landing-page", localSize: 150_000_000,
                    onGitHub: true, safety: GitSafety(), lastOpened: ago(48), lastActivity: ago(50)),
            Project(id: "sample-dev/mobile-app", name: "mobile-app", nameWithOwner: "sample-dev/mobile-app", owner: "sample-dev",
                    description: "iOS and Android client", isPrivate: true, remoteKB: 520_000, onGitHub: true, account: "sample-dev", lastActivity: ago(14)),
            Project(id: "sample-dev/docs-site", name: "docs-site", nameWithOwner: "sample-dev/docs-site", owner: "sample-dev",
                    description: "Documentation", isPrivate: false, remoteKB: 31_000, onGitHub: true, account: "sample-dev", lastActivity: ago(40)),
        ]
    }

    /// Accounts, commit identity and a short activity trail for the Accounts and Activity tabs.
    static var projectsState: ProjectsState {
        var s = ProjectsState()
        s.identities["sample-dev"] = GitIdentity(name: "Sample Dev", email: "dev@example.com")
        let now = Date()
        func at(_ hours: Double) -> Date { now.addingTimeInterval(-hours * 3600) }
        s.activity = [
            ProjectActivity(date: at(1), kind: .download, subject: "sample-dev/web-app", detail: "Blobless clone · 92 MB · ~/Code/sample-dev/web-app"),
            ProjectActivity(date: at(3), kind: .push, subject: "sample-dev/demo-api", detail: "Started pushing 3 commits"),
            ProjectActivity(date: at(5), kind: .remove, subject: "sample-dev/old-demo", detail: "Freed 480 MB. In the Trash; restore from History."),
            ProjectActivity(date: at(26), kind: .publish, subject: "sample-dev/scratch-notes", detail: "Private repository"),
            ProjectActivity(date: at(27), kind: .identity, subject: "sample-dev", detail: "Commits are now made as Sample Dev <dev@example.com>"),
            ProjectActivity(date: at(29), kind: .addAccount, subject: "sample-dev", detail: "Started signing in"),
            ProjectActivity(date: at(52), kind: .addRepo, subject: "client-org/landing-page", detail: "Added by URL"),
            ProjectActivity(date: at(53), kind: .download, subject: "client-org/landing-page", detail: "Shallow clone · 18 MB · ~/Code/client-org/landing-page"),
        ]
        return s
    }

    static let ports: [ListeningPort] = [
        ListeningPort(port: 3000, pid: 41230, command: "node", arguments: "node ~/Code/web-app/node_modules/.bin/next dev",
                      label: "Next.js dev server in web-app", folder: "~/Code/web-app",
                      started: Date().addingTimeInterval(-3 * 3600), reachableFromNetwork: false, isDevelopment: true),
        ListeningPort(port: 5432, pid: 812, command: "postgres", arguments: "/opt/homebrew/opt/postgresql@17/bin/postgres -D /opt/homebrew/var/postgresql@17",
                      label: "PostgreSQL 17 (Homebrew)", folder: nil, started: Date().addingTimeInterval(-86_400 * 2),
                      reachableFromNetwork: false, isDevelopment: true),
        ListeningPort(port: 8000, pid: 41388, command: "Python", arguments: "python manage.py runserver 0.0.0.0:8000",
                      label: "Django dev server in demo-api", folder: "~/Code/demo-api",
                      started: Date().addingTimeInterval(-1800), reachableFromNetwork: true, isDevelopment: true),
        ListeningPort(port: 5000, pid: 630, command: "ControlCenter", arguments: "/System/Library/CoreServices/ControlCenter.app/Contents/MacOS/ControlCenter",
                      label: "macOS AirPlay Receiver", folder: nil, started: nil, reachableFromNetwork: true, isDevelopment: false),
    ]
}
#endif
