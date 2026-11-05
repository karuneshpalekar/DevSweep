using DevSweep.Core.Projects.Git;
using DevSweep.Fixtures;
using Xunit;

namespace DevSweep.Accuracy.Projects;

/// <summary>
/// The removal-safety check against real repositories (the Mac ProjectTests, ported, plus the
/// Windows cases): every condition that would lose work is caught, a fully pushed repository
/// is safe, and nothing that could not be checked is ever called safe.
/// </summary>
public sealed class SafetyCheckTests(ITestOutputHelper output) : IDisposable
{
    private readonly GitFixture _git = new("proj-safety");

    public void Dispose() => _git.Dispose();

    private async Task<RemovalSafety> Check(string folder, SafetyCheckOptions? options = null)
    {
        RemovalSafety s = await GitFixture.Check(folder, options);
        output.WriteLine($"{Path.GetFileName(folder)}: {s.State} | {s.Headline}");
        foreach (string b in s.Blockers)
        {
            output.WriteLine("   blocker: " + b);
        }

        foreach (string p in s.Problems.Concat(s.Notes))
        {
            output.WriteLine("   note: " + p);
        }

        return s;
    }

    [Fact]
    public async Task A_fully_pushed_repository_is_safe()
    {
        string work = _git.Repo("pushed");
        RemovalSafety s = await Check(work);
        Assert.Equal(SafetyState.Safe, s.State);
        Assert.True(s.IsSafeToRemove);
        Assert.True(s.HasRemote);
        Assert.Empty(s.Blockers);
        Assert.Empty(s.Problems);
        Assert.True(s.RemotesAreLocalFolders, "the fixture's origin is a folder on this PC, and the check says so");
    }

    [Fact]
    public async Task Everything_that_would_be_lost_is_noticed()
    {
        string work = _git.Repo("work");
        GitFixture.Commit(work, "b.txt");
        RemovalSafety s = await Check(work);
        Assert.Equal(SafetyState.NotSafe, s.State);
        Assert.Equal(1, s.UnpushedCommits);
        Assert.Equal("main", Assert.Single(s.UnpushedByRef).Ref);

        // Commits on another local branch count too.
        GitFixture.Git(work, "checkout", "-q", "-b", "feature");
        GitFixture.Commit(work, "c.txt");
        GitFixture.Git(work, "checkout", "-q", "main");
        s = await Check(work);
        Assert.Equal(2, s.UnpushedCommits);
        Assert.Equal(["feature", "main"], s.UnpushedByRef.Select(r => r.Ref).Order(StringComparer.Ordinal));

        File.WriteAllText(Path.Join(work, "a.txt"), "changed\n");
        File.WriteAllText(Path.Join(work, "untracked.txt"), "new\n");
        s = await Check(work);
        Assert.Equal(1, s.ChangedFiles);
        Assert.Equal(1, s.UntrackedFiles);
        Assert.Equal(["untracked.txt"], s.UntrackedSample);
        Assert.Equal(3, s.Blockers.Count);

        GitFixture.Git(work, "stash", "-q", "--include-untracked");
        s = await Check(work);
        Assert.Equal(1, s.Stashes);
        Assert.Equal(0, s.ChangedFiles);
        Assert.Equal(0, s.UntrackedFiles);
        Assert.Equal(SafetyState.NotSafe, s.State);

        GitFixture.Git(work, "stash", "drop", "-q");
        GitFixture.Git(work, "push", "-q", "origin", "main", "feature");
        s = await Check(work);
        Assert.Equal(SafetyState.Safe, s.State);
    }

    [Fact]
    public async Task No_remote_is_never_safe_and_a_detached_head_counts()
    {
        string lonely = _git.Repo("lonely", pushed: false);
        RemovalSafety s = await Check(lonely);
        Assert.False(s.HasRemote);
        Assert.Equal(SafetyState.NotSafe, s.State);
        Assert.Contains("only copy", s.Blockers[0], StringComparison.Ordinal);

        string work = _git.Repo("detached");
        GitFixture.Git(work, "checkout", "-q", "--detach");
        GitFixture.Commit(work, "d.txt");
        s = await Check(work);
        Assert.Equal(1, s.UnpushedCommits);
        Assert.Contains(s.UnpushedByRef, r => r.Ref == "detached HEAD");
    }

    [Fact]
    public async Task Ignored_files_do_not_block_but_are_named_and_artifacts_are_not()
    {
        string work = _git.Repo("ignored");
        File.WriteAllText(Path.Join(work, ".gitignore"), "node_modules/\n.env\n*.log\n");
        GitFixture.Git(work, "add", "-A");
        GitFixture.Git(work, "commit", "-q", "-m", "ignore");
        GitFixture.Git(work, "push", "-q");
        Directory.CreateDirectory(Path.Join(work, "node_modules", "dep"));
        File.WriteAllText(Path.Join(work, "node_modules", "dep", "index.js"), "x");
        File.WriteAllText(Path.Join(work, ".env"), "TOKEN=1");
        File.WriteAllText(Path.Join(work, "build.log"), "log");
        RemovalSafety s = await Check(work);
        Assert.Equal(SafetyState.Safe, s.State);
        Assert.Equal(0, s.UntrackedFiles);
        Assert.Equal([".env"], s.IgnoredSample);
    }

    [Fact]
    public async Task Extra_worktrees_are_not_safe_from_either_side()
    {
        string work = _git.Repo("main-tree");
        string linked = Path.Join(_git.Root, "linked-tree");
        GitFixture.Git(work, "worktree", "add", "-q", "-b", "side", linked);
        RemovalSafety main = await Check(work);
        RemovalSafety side = await Check(linked);
        Assert.Equal(SafetyState.NotSafe, main.State);
        Assert.True(SafetyChecker.SamePath(linked, Assert.Single(main.OtherWorktrees)), string.Join(", ", main.OtherWorktrees));
        Assert.Equal(SafetyState.NotSafe, side.State);
        Assert.NotNull(side.LinkedWorktreeOf);
        Assert.True(SafetyChecker.SamePath(work, side.LinkedWorktreeOf!), side.LinkedWorktreeOf);

        GitFixture.Git(work, "worktree", "remove", linked);
        GitFixture.Git(work, "branch", "-q", "-D", "side");
        Assert.Equal(SafetyState.Safe, (await Check(work)).State);
    }

    [Fact]
    public async Task A_submodule_with_unpushed_work_is_caught()
    {
        string lib = _git.Repo("lib");
        string libBare = Path.Join(_git.Root, "remote-lib.git");
        string app = _git.Repo("app");
        GitFixture.Git(app, "-c", "protocol.file.allow=always", "submodule", "add", "-q", libBare, "vendor/lib");
        GitFixture.Git(app, "commit", "-q", "-m", "submodule");
        GitFixture.Git(app, "push", "-q");
        Assert.Equal(SafetyState.Safe, (await Check(app)).State);

        string inner = Path.Join(app, "vendor", "lib");
        GitFixture.Git(inner, "config", "core.autocrlf", "false");
        GitFixture.Git(inner, "checkout", "-q", "-b", "work");
        GitFixture.Commit(inner, "inner.txt");
        RemovalSafety s = await Check(app);
        Assert.Equal(SafetyState.NotSafe, s.State);
        SubmoduleIssue issue = Assert.Single(s.Submodules);
        Assert.Equal("vendor/lib", issue.Path);
        Assert.Contains("commit", issue.Reason, StringComparison.Ordinal);
        Assert.True(Directory.Exists(lib));
    }

    [Fact]
    public async Task Git_lfs_is_unknown_never_safe()
    {
        string work = _git.Repo("lfs");
        File.WriteAllText(Path.Join(work, ".gitattributes"), "*.psd filter=lfs diff=lfs merge=lfs -text\n");
        GitFixture.Git(work, "add", "-A");
        GitFixture.Git(work, "commit", "-q", "-m", "lfs");
        GitFixture.Git(work, "push", "-q");
        RemovalSafety s = await Check(work);
        Assert.True(s.UsesLfs);
        Assert.Equal(SafetyState.Unknown, s.State);
        Assert.False(s.IsSafeToRemove);
    }

    [Fact]
    public async Task Autocrlf_churn_git_calls_clean_is_clean_and_line_ending_only_changes_are_named()
    {
        string work = _git.Repo("crlf");
        GitFixture.Git(work, "config", "core.autocrlf", "true");
        File.Delete(Path.Join(work, "a.txt"));
        GitFixture.Git(work, "checkout", "--", "a.txt");
        Assert.Equal("first\r\n", File.ReadAllText(Path.Join(work, "a.txt"))); // checked out with CRLF
        File.SetLastWriteTimeUtc(Path.Join(work, "a.txt"), DateTime.UtcNow.AddMinutes(1)); // stat-dirty
        RemovalSafety clean = await Check(work);
        Assert.Equal(SafetyState.Safe, clean.State);

        GitFixture.Git(work, "config", "core.autocrlf", "false");
        GitFixture.Commit(work, "unix.txt", "one\ntwo\n");
        GitFixture.Git(work, "push", "-q");
        File.WriteAllText(Path.Join(work, "unix.txt"), "one\r\ntwo\r\n");
        RemovalSafety eol = await Check(work);
        output.WriteLine($"changed {eol.ChangedFiles}, line endings only {eol.LineEndingOnlyFiles}");
        Assert.Equal(SafetyState.NotSafe, eol.State); // git says modified: never hidden
        Assert.Equal(1, eol.LineEndingOnlyFiles);
        Assert.Contains("differ only in line endings", eol.Blockers[0], StringComparison.Ordinal);

        File.WriteAllText(Path.Join(work, "unix.txt"), "one\r\nTWO\r\n");
        RemovalSafety real = await Check(work);
        Assert.Equal(0, real.LineEndingOnlyFiles);
    }

    [Fact]
    public async Task A_rename_by_letter_case_only_is_caught_where_git_ignores_case()
    {
        string work = _git.Repo("case");
        GitFixture.Commit(work, "Readme.md", "hello\n");
        GitFixture.Git(work, "push", "-q");
        bool ignoreCase = GitFixture.Run(work, true, "config", "--get", "core.ignorecase").Output.Trim() == "true";
        if (!ignoreCase)
        {
            Assert.Skip("The file system is case-sensitive here (core.ignorecase is off); git sees case renames itself.");
        }

        File.Move(Path.Join(work, "Readme.md"), Path.Join(work, "README.md"));
        Assert.Equal(string.Empty, GitFixture.Git(work, "status", "--porcelain").Trim()); // git sees nothing
        RemovalSafety s = await Check(work);
        Assert.Equal(SafetyState.NotSafe, s.State);
        Assert.Equal("Readme.md is now README.md", Assert.Single(s.CaseOnlyRenames));
    }

    [Fact]
    public async Task A_repository_with_paths_over_260_characters_is_checked()
    {
        string work = _git.Repo("long");
        string deep = string.Join('/', Enumerable.Range(0, 12).Select(i => $"folder-number-{i:00}-abcdefgh"));
        GitFixture.Commit(work, deep + "/deep-file.txt", "deep\n");
        GitFixture.Git(work, "push", "-q");
        Assert.True(Path.Join(work, deep, "deep-file.txt").Length > 260);
        RemovalSafety s = await Check(work);
        Assert.Equal(SafetyState.Safe, s.State);
        Assert.Equal(1, s.LongPathFiles);

        File.AppendAllText(Path.GetFullPath(Path.Join(work, deep, "deep-file.txt")), "more\n");
        s = await Check(work);
        Assert.Equal(1, s.ChangedFiles); // the long path is read, not skipped
    }

    [Fact]
    public async Task A_file_held_open_by_a_program_blocks_removal()
    {
        string work = _git.Repo("locked");
        string file = Path.Join(work, "a.txt");
        using (FileHolder.Start(file))
        {
            RemovalSafety s = await Check(work, new SafetyCheckOptions { CheckLocks = true });
            output.WriteLine("lock: " + s.LockMessage);
            Assert.NotNull(s.LockMessage);
            Assert.Equal(SafetyState.NotSafe, s.State);
        }

        Assert.Equal(SafetyState.Safe, (await Check(work, new SafetyCheckOptions { CheckLocks = true })).State);
    }

    [Fact]
    public async Task An_unfinished_merge_and_a_folder_git_cannot_read_are_not_safe()
    {
        string work = _git.Repo("merge");
        GitFixture.Git(work, "checkout", "-q", "-b", "other");
        GitFixture.Commit(work, "a.txt", "other\n");
        GitFixture.Git(work, "push", "-q", "-u", "origin", "other");
        GitFixture.Git(work, "checkout", "-q", "main");
        GitFixture.Commit(work, "a.txt", "main\n");
        GitFixture.Git(work, "push", "-q");
        GitFixture.Run(work, true, "merge", "other");
        RemovalSafety s = await Check(work);
        Assert.Equal(SafetyState.NotSafe, s.State);
        Assert.Equal("merge", s.OperationInProgress);
        Assert.Equal(1, s.ConflictedFiles);

        string notRepo = _git.Temp.CreateDirectory("plain");
        RemovalSafety plain = await Check(notRepo);
        Assert.Equal(SafetyState.Unknown, plain.State);
        Assert.False(plain.IsSafeToRemove);
    }

    [Fact]
    public async Task Commits_only_on_a_tag_are_unknown()
    {
        string work = _git.Repo("tagged");
        GitFixture.Git(work, "checkout", "-q", "--detach");
        GitFixture.Commit(work, "release.txt");
        GitFixture.Git(work, "tag", "v1");
        GitFixture.Git(work, "checkout", "-q", "main");
        RemovalSafety s = await Check(work);
        Assert.Equal(SafetyState.Unknown, s.State);
        Assert.Contains("tags", s.Problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_brings_the_remote_tracking_refs_up_to_date()
    {
        string work = _git.Repo("fetch");
        string bare = Path.Join(_git.Root, "remote-fetch.git");
        GitFixture.Git(work, "checkout", "-q", "-b", "topic");
        GitFixture.Commit(work, "topic.txt");
        GitFixture.Git(work, "push", "-q", "-u", "origin", "topic");
        GitFixture.Git(work, "checkout", "-q", "main");

        // Someone deletes the branch on the server; this clone still has origin/topic.
        string other = Path.Join(_git.Root, "other-clone");
        GitFixture.Git(null, "clone", "-q", bare, other);
        GitFixture.Git(other, "push", "-q", "origin", "--delete", "topic");

        RemovalSafety stale = await Check(work);
        Assert.Equal(SafetyState.Safe, stale.State); // measured against the last fetch, and the check says so
        Assert.False(stale.Fetched);
        RemovalSafety fetched = await Check(work, new SafetyCheckOptions { Fetch = true });
        Assert.True(fetched.Fetched);
        Assert.NotNull(fetched.LastFetchUtc);
        Assert.Equal(SafetyState.NotSafe, fetched.State); // pruned: the commit is only here now
        Assert.Equal("topic", Assert.Single(fetched.UnpushedByRef).Ref);
    }
}
