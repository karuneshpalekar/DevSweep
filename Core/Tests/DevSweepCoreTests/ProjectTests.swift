import XCTest
@testable import DevSweepCore

final class ProjectTests: XCTestCase {
    var root: URL!
    let gitEnv = ["GIT_AUTHOR_NAME": "t", "GIT_AUTHOR_EMAIL": "t@example.com",
                  "GIT_COMMITTER_NAME": "t", "GIT_COMMITTER_EMAIL": "t@example.com"]

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent("devsweep-proj-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: root) }

    @discardableResult
    private func git(_ dir: URL, _ args: String...) -> ShellResult {
        Shell.run(["git", "-C", dir.path] + args, env: gitEnv, timeout: 30)
    }

    private func commit(_ dir: URL, _ file: String, _ text: String = "x") throws {
        try text.write(to: dir.appendingPathComponent(file), atomically: true, encoding: .utf8)
        git(dir, "add", "-A")
        XCTAssertTrue(git(dir, "commit", "-q", "-m", "add \(file)").ok)
    }

    /// A clone whose origin is a local bare repo, already pushed.
    private func pushedClone(_ name: String = "work") throws -> URL {
        let bare = root.appendingPathComponent("remote-\(name).git")
        XCTAssertTrue(Shell.run(["git", "init", "-q", "--bare", "-b", "main", bare.path], timeout: 30).ok)
        let work = root.appendingPathComponent(name)
        XCTAssertTrue(Shell.run(["git", "init", "-q", "-b", "main", work.path], timeout: 30).ok)
        git(work, "remote", "add", "origin", bare.path)
        try commit(work, "a.txt")
        XCTAssertTrue(git(work, "push", "-q", "-u", "origin", "main").ok)
        return work
    }

    // MARK: - Safety

    func testSafetyNoticesEverythingThatWouldBeLost() throws {
        let work = try pushedClone()
        XCTAssertEqual(ProjectScanner.safety(of: work), GitSafety(unpushedCommits: 0, changedFiles: 0, stashes: 0, hasRemote: true))
        XCTAssertTrue(ProjectScanner.safety(of: work).isSafeToRemove)

        try commit(work, "b.txt")
        var s = ProjectScanner.safety(of: work)
        XCTAssertEqual(s.unpushedCommits, 1)
        XCTAssertFalse(s.isSafeToRemove)

        // Commits on another local branch count too.
        git(work, "checkout", "-q", "-b", "feature")
        try commit(work, "c.txt")
        git(work, "checkout", "-q", "main")
        XCTAssertEqual(ProjectScanner.safety(of: work).unpushedCommits, 2)

        try "changed".write(to: work.appendingPathComponent("a.txt"), atomically: true, encoding: .utf8)
        try "new".write(to: work.appendingPathComponent("untracked.txt"), atomically: true, encoding: .utf8)
        s = ProjectScanner.safety(of: work)
        XCTAssertEqual(s.changedFiles, 1, "only files git already tracks count as changed")
        XCTAssertEqual(s.untrackedFiles, 1)
        XCTAssertEqual(s.untrackedSample, ["untracked.txt"])
        XCTAssertFalse(s.isSafeToRemove)

        git(work, "stash", "-q", "--include-untracked")
        s = ProjectScanner.safety(of: work)
        XCTAssertEqual(s.stashes, 1)
        XCTAssertEqual(s.changedFiles, 0)
    }

    func testPushingMakesItSafeAndNoRemoteIsNeverSafe() throws {
        let work = try pushedClone()
        try commit(work, "b.txt")
        XCTAssertFalse(ProjectScanner.safety(of: work).isSafeToRemove)
        XCTAssertTrue(git(work, "push", "-q").ok)
        XCTAssertTrue(ProjectScanner.safety(of: work).isSafeToRemove)

        let lonely = root.appendingPathComponent("lonely")
        XCTAssertTrue(Shell.run(["git", "init", "-q", lonely.path], timeout: 30).ok)
        try commit(lonely, "a.txt")
        let s = ProjectScanner.safety(of: lonely)
        XCTAssertFalse(s.hasRemote)
        XCTAssertFalse(s.isSafeToRemove, "a folder with no remote exists nowhere else")
    }

    // MARK: - Finding and joining

    func testScanMapsClonesByOriginNotByFolderName() throws {
        func repo(_ rel: String, origin: String?) throws {
            let dir = root.appendingPathComponent(rel)
            XCTAssertTrue(Shell.run(["git", "init", "-q", dir.path], timeout: 30).ok)
            if let origin { git(dir, "remote", "add", "origin", origin) }
            try commit(dir, "f.txt")
        }
        try repo("Code/whatever-folder", origin: "https://github.com/Octo/Widget.git")
        try repo("Code/sub/ssh-clone", origin: "git@github.com:octo/gadget.git")
        try repo("Code/no-origin", origin: nil)
        try repo("Code/elsewhere", origin: "https://gitlab.com/x/y.git")
        try FileManager.default.createDirectory(at: root.appendingPathComponent("Code/node_modules/dep/.git"), withIntermediateDirectories: true)

        let found = ProjectScanner.scanLocal(roots: [root.appendingPathComponent("Code")])
        let bySlug = Dictionary(uniqueKeysWithValues: found.map { ($0.slug ?? $0.path.lastPathComponent, $0) })
        XCTAssertEqual(Set(bySlug.keys), ["Octo/Widget", "octo/gadget", "no-origin", "elsewhere"], "node_modules is skipped")
        XCTAssertEqual(bySlug["Octo/Widget"]?.path.lastPathComponent, "whatever-folder")
        XCTAssertFalse(bySlug["no-origin"]!.hasRemote)
        XCTAssertNil(bySlug["elsewhere"]?.slug, "only GitHub origins are matched to GitHub repos")
    }

    func testMergeJoinsLocalRemoteAndRemovedRepos() throws {
        let remote = [
            RemoteRepo(name: "Widget", nameWithOwner: "Octo/Widget", description: "d", isPrivate: true, pushedAt: nil,
                       url: "", diskUsageKB: 4000, account: "octo"),
            RemoteRepo(name: "other", nameWithOwner: "octo/other", description: "", isPrivate: false, pushedAt: nil,
                       url: "", diskUsageKB: 100, account: "octo"),
        ]
        let clone = LocalClone(path: URL(fileURLWithPath: "/x/widget"), size: 123, slug: "octo/widget", hasRemote: true,
                               lastCommit: Date(), safety: GitSafety(unpushedCommits: 2))
        let orgClone = LocalClone(path: URL(fileURLWithPath: "/x/team"), size: 5, slug: "someorg/team", hasRemote: true,
                                  lastCommit: nil, safety: GitSafety())
        let loose = LocalClone(path: URL(fileURLWithPath: "/x/scratch"), size: 1, slug: nil, hasRemote: false,
                               lastCommit: nil, safety: GitSafety(hasRemote: false))
        var state = ProjectsState()
        state.known = [.init(nameWithOwner: "octo/removed", account: "octo")]
        state.lastOpened["Octo/Widget"] = Date(timeIntervalSince1970: 1_000)

        let projects = ProjectScanner.merge(local: [clone, orgClone, loose], remote: remote, state: state, home: root)
        func p(_ id: String) -> Project? { projects.first { $0.nameWithOwner == id } }

        XCTAssertEqual(projects.count, 5)
        XCTAssertEqual(ProjectScanner.merge(local: [clone], remote: remote, state: {
            var st = ProjectsState()
            st.known = [.init(nameWithOwner: "oldorg/widget", account: "oldorg")]
            return st
        }(), home: root).filter { $0.name.lowercased() == "widget" }.count, 1, "a stale entry under an old owner is dropped when the repo is on this Mac")
        XCTAssertEqual(p("Octo/Widget")?.localPath, "/x/widget", "case-insensitive match to the repo list")
        XCTAssertEqual(p("Octo/Widget")?.status.text, "2 commits not pushed")
        XCTAssertEqual(p("Octo/Widget")?.lastOpened, Date(timeIntervalSince1970: 1_000))
        XCTAssertEqual(p("octo/other")?.status.text, "Only on GitHub")
        XCTAssertEqual(p("octo/removed")?.onDisk, false, "a removed repo stays in the list with a Download option")
        XCTAssertEqual(p("someorg/team")?.onDisk, true, "clones from accounts you're not signed in to still show")
        XCTAssertEqual(p("scratch")?.status.text, "Only on this Mac")
        XCTAssertEqual(projects.prefix(3).allSatisfy(\.onDisk), true, "on-disk projects are listed first")
    }

    func testDestinationReusesWhereItLivedBefore() {
        var state = ProjectsState()
        state.workspaceRoot = "~/Code"
        let home = URL(fileURLWithPath: "/Users/me")
        let p = Project(id: "a/b", name: "b", nameWithOwner: "a/b", owner: "a", description: "", isPrivate: nil, onGitHub: true, account: "acct")
        XCTAssertEqual(ProjectScanner.destination(for: p, state: state, home: home).path, "/Users/me/Code/acct/b")
        state.known = [.init(nameWithOwner: "a/b", account: "acct", lastParent: "~/Documents/GitHub")]
        XCTAssertEqual(ProjectScanner.destination(for: p, state: state, home: home).path, "/Users/me/Documents/GitHub/b")
    }

    // MARK: - GitHub helpers

    func testParsesSlugsAndAccounts() {
        XCTAssertEqual(GitHubClient.parseSlug("https://github.com/Octo/Widget.git"), "Octo/Widget")
        XCTAssertEqual(GitHubClient.parseSlug("git@github.com:octo/gadget.git"), "octo/gadget")
        XCTAssertEqual(GitHubClient.parseSlug("github.com/octo/gadget/"), "octo/gadget")
        XCTAssertEqual(GitHubClient.parseSlug(" octo/gadget "), "octo/gadget")
        XCTAssertEqual(GitHubClient.parseSlug("https://ghp_secret@github.com/octo/gadget.git"), "octo/gadget", "credentials are stripped")
        XCTAssertNil(GitHubClient.parseSlug("octo"))
        XCTAssertNil(GitHubClient.parseSlug("a/b/c"))

        let status = """
        github.com
          ✓ Logged in to github.com account karuneshpalekar (keyring)
          - Active account: true
          - Git operations protocol: https

          ✓ Logged in to github.com account karunesh-growfitter (keyring)
          - Active account: false
        """
        let accounts = GitHubClient.parseAccounts(status)
        XCTAssertEqual(accounts, [GitHubAccount(login: "karuneshpalekar", isActive: true),
                                  GitHubAccount(login: "karunesh-growfitter", isActive: false)])
    }

    func testCloneStrategiesBuildTheRightCommand() {
        let dest = URL(fileURLWithPath: "/tmp/x/repo")
        XCTAssertEqual(GitHubClient.cloneArguments(slug: "a/b", destination: dest, strategy: .blobless),
                       ["gh", "repo", "clone", "a/b", "/tmp/x/repo", "--", "--filter=blob:none"])
        XCTAssertEqual(GitHubClient.cloneArguments(slug: "a/b", destination: dest, strategy: .shallow).suffix(2), ["--", "--depth=1"])
        XCTAssertEqual(GitHubClient.cloneArguments(slug: "a/b", destination: dest, strategy: .full).last, "/tmp/x/repo")
    }

    func testIdentityIsWrittenToTheCloneNotTheGlobalConfig() throws {
        let work = try pushedClone("ident")
        GitHubClient.applyIdentity(GitIdentity(name: "Someone", email: "someone@example.com"), to: work)
        XCTAssertEqual(git(work, "config", "--local", "user.name").stdout.trimmingCharacters(in: .whitespacesAndNewlines), "Someone")
        XCTAssertEqual(git(work, "config", "--local", "user.email").stdout.trimmingCharacters(in: .whitespacesAndNewlines), "someone@example.com")
    }

    // MARK: - Moving over from RepoShelf

    func testImportsRepoShelfState() throws {
        let json = """
        {"workspaceRootPath": "/Users/me/Code", "identities": {"me": {"name": "Me", "email": "me@example.com"}},
         "cloneStrategies": {"me/app": "shallow", "me/odd": "bogus"}, "lastOpened": {"me/app": 812044380.25},
         "knownRepos": [{"nameWithOwner": "org/site", "login": "org", "cloneURL": "x", "lastParentPath": "~/Documents/GitHub"}],
         "addedRepos": [{"nameWithOwner": "org/site", "login": "org", "cloneURL": "x"}, {"nameWithOwner": "x/y", "login": "me", "cloneURL": "x"}]}
        """
        let file = root.appendingPathComponent("state.json")
        try json.write(to: file, atomically: true, encoding: .utf8)
        let s = try XCTUnwrap(ProjectStore.importRepoShelf(file))
        XCTAssertEqual(s.identities["me"], GitIdentity(name: "Me", email: "me@example.com"))
        XCTAssertEqual(s.strategies, ["me/app": .shallow], "unknown strategies are dropped")
        XCTAssertEqual(s.lastOpened["me/app"], Date(timeIntervalSinceReferenceDate: 812_044_380.25))
        XCTAssertEqual(Set(s.known.map(\.nameWithOwner)), ["org/site", "x/y"], "no duplicates")
        XCTAssertEqual(s.known.first { $0.nameWithOwner == "org/site" }?.lastParent, "~/Documents/GitHub")

        let store = ProjectStore(url: root.appendingPathComponent("projects.json"), importFrom: file)
        XCTAssertEqual(store.state.workspaceRoot, "/Users/me/Code")
        let reopened = ProjectStore(url: root.appendingPathComponent("projects.json"), importFrom: nil)
        XCTAssertEqual(reopened.state.identities["me"]?.name, "Me", "the import was saved")
    }
}

final class ProjectActivityTests: XCTestCase {
    var dir: URL!

    override func setUpWithError() throws {
        dir = FileManager.default.temporaryDirectory.appendingPathComponent("devsweep-act-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: dir) }

    func testAnOlderFileWithoutActivityStillLoadsAndKeepsIdentities() throws {
        // What version 0.6 wrote: no "activity" fields at all.
        let old = """
        {"workspaceRoot": "~/Code", "identities": {"me": {"name": "Me", "email": "me@example.com"}},
         "strategies": {"me/app": "shallow"}, "lastOpened": {}, "known": [{"nameWithOwner": "me/app", "account": "me"}]}
        """
        let file = dir.appendingPathComponent("projects.json")
        try old.write(to: file, atomically: true, encoding: .utf8)
        let store = ProjectStore(url: file, importFrom: nil)
        XCTAssertEqual(store.state.identities["me"]?.name, "Me", "saved identities survive the upgrade")
        XCTAssertEqual(store.state.known.count, 1)
        XCTAssertEqual(store.state.activity, [])
    }

    func testRepoShelfActivityIsImportedOnceAndSortedNewestFirst() throws {
        let shelf = """
        {"activity": [
          {"id": "263684D8-9112-4077-B66E-8947DA6C1512", "kind": "remove", "subject": "app", "detail": "freed 4.6 MB", "date": 812735643.2},
          {"id": "0218E0F3-7984-4970-9EE2-66E534646535", "kind": "clone", "subject": "site", "detail": "full · 3.2 MB", "date": 812732657.3},
          {"kind": "switchAccount", "subject": "x", "detail": "", "date": 812732000.0},
          {"kind": "publish", "subject": "new", "detail": "private", "date": 812740000.0}]}
        """
        let shelfFile = dir.appendingPathComponent("state.json")
        try shelf.write(to: shelfFile, atomically: true, encoding: .utf8)
        let projects = dir.appendingPathComponent("projects.json")
        try #"{"identities": {"me": {"name": "Me", "email": "m@e.com"}}}"#.write(to: projects, atomically: true, encoding: .utf8)

        let store = ProjectStore(url: projects, importFrom: shelfFile)
        XCTAssertEqual(store.state.activity.map(\.kind), [.publish, .remove, .download], "account switches aren't carried over")
        XCTAssertEqual(store.state.activity.first?.subject, "new")
        XCTAssertEqual(store.state.identities["me"]?.email, "m@e.com", "existing data is untouched")

        let again = ProjectStore(url: projects, importFrom: shelfFile)
        XCTAssertEqual(again.state.activity.count, 3, "imported only once")
    }

    func testLoggingKeepsNewestFirstAndCapsTheTrail() {
        let store = ProjectStore(url: dir.appendingPathComponent("p.json"), importFrom: nil)
        for i in 0..<505 { store.log(.download, "repo\(i)", "detail") }
        XCTAssertEqual(store.state.activity.count, 500)
        XCTAssertEqual(store.state.activity.first?.subject, "repo504")
        let reopened = ProjectStore(url: dir.appendingPathComponent("p.json"), importFrom: nil)
        XCTAssertEqual(reopened.state.activity.count, 500, "the trail is saved")
    }
}
