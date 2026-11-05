using DevSweep.Core.Projects;
using DevSweep.Core.Projects.Actions;
using DevSweep.Core.Projects.Git;
using DevSweep.Core.Projects.GitHub;
using Xunit;

namespace DevSweep.Core.Tests.Projects;

/// <summary>Slugs, gh output, git status, strategies, credentials and terminal scripts (the Mac GitHubClient tests, ported, plus Windows).</summary>
public sealed class ParsingTests
{
    [Theory]
    [InlineData("https://github.com/Octo/Widget.git", "Octo/Widget")]
    [InlineData("git@github.com:octo/gadget.git", "octo/gadget")]
    [InlineData("github.com/octo/gadget/", "octo/gadget")]
    [InlineData(" octo/gadget ", "octo/gadget")]
    [InlineData("https://ghp_secret@github.com/octo/gadget.git", "octo/gadget")]
    [InlineData("https://user:pass@github.com/octo/gadget", "octo/gadget")]
    [InlineData("ssh://git@github.com/octo/gadget.git", "octo/gadget")]
    [InlineData("octo", null)]
    [InlineData("a/b/c", null)]
    [InlineData("https://gitlab.com/x/y.git", null)]
    public void Slugs_are_read_from_urls_and_pasted_text(string input, string? slug)
        => Assert.Equal(slug, RemoteUrls.ParseSlug(input));

    [Fact]
    public void Only_github_origins_are_github_and_ssh_aliases_count()
    {
        IReadOnlySet<string> aliases = RemoteUrls.SshAliases("Host github-work\n  HostName github.com\n  IdentityFile ~/.ssh/work\nHost other *.example\n  HostName example.com\n");
        Assert.Equal(["github-work"], aliases);
        Assert.Equal("acme/app", RemoteUrls.GitHubSlug("git@github-work:acme/app.git", aliases));
        Assert.Null(RemoteUrls.GitHubSlug("git@github-work:acme/app.git"));
        Assert.Null(RemoteUrls.GitHubSlug("git@gitlab.com:x/y.git", aliases));
        Assert.True(RemoteUrls.Parse(@"C:\remotes\app.git").IsLocalFolder);
        Assert.True(RemoteUrls.Parse(@"\\server\share\app.git").IsLocalFolder);
        Assert.True(RemoteUrls.Parse("file:///C:/remotes/app.git").IsLocalFolder);
        Assert.False(RemoteUrls.Parse("https://github.com/a/b").IsLocalFolder);
    }

    [Fact]
    public void Gh_auth_status_gives_every_account_and_the_active_one()
    {
        const string status = """
            github.com
              ✓ Logged in to github.com account karuneshpalekar (keyring)
              - Active account: true
              - Git operations protocol: https
              - Token: gho_************************************

              ✓ Logged in to github.com account karunesh-growfitter (keyring)
              - Active account: false
            """;
        Assert.Equal(
            [new GitHubAccount("karuneshpalekar", true), new GitHubAccount("karunesh-growfitter", false)],
            GitHubCli.ParseAuthStatus(status));
        Assert.Empty(GitHubCli.ParseAuthStatus("You are not logged into any GitHub hosts. To log in, run: gh auth login"));
    }

    [Fact]
    public void Hosts_yml_lists_accounts_without_reading_tokens()
    {
        const string multi = """
            github.com:
                users:
                    alice:
                        oauth_token: gho_secret
                    bob:
                git_protocol: https
                user: bob
                oauth_token: gho_secret
            """;
        Assert.Equal(
            [new GitHubAccount("alice", false, "github.com", "hosts.yml"), new GitHubAccount("bob", true, "github.com", "hosts.yml")],
            GitHubCli.ParseHostsFile(multi));
        Assert.Equal([new GitHubAccount("old", true, "github.com", "hosts.yml")], GitHubCli.ParseHostsFile("github.com:\n    user: old\n    oauth_token: x\n"));
        Assert.Empty(GitHubCli.ParseHostsFile("not: [valid"));
        Assert.Empty(GitHubCli.ParseHostsFile("github.com:\n    user: --evil\n"));
    }

    [Theory]
    [InlineData("octo", true)]
    [InlineData("karunesh-growfitter", true)]
    [InlineData("-x", false)]
    [InlineData("--user", false)]
    [InlineData("a--b", false)]
    [InlineData("has space", false)]
    public void Logins_can_never_be_read_as_options(string login, bool valid) => Assert.Equal(valid, GitHubCli.IsLogin(login));

    [Fact]
    public void Gh_runs_without_inherited_tokens_and_without_prompts()
    {
        IReadOnlyDictionary<string, string?> env = GitHubCli.QuietEnvironment;
        Assert.Null(env["GH_TOKEN"]);
        Assert.Null(env["GITHUB_TOKEN"]);
        Assert.Equal("1", env["GH_PROMPT_DISABLED"]);
    }

    [Fact]
    public void A_token_never_shows_itself()
    {
        var token = new GitHubToken("octo", "gho_abcdef");
        Assert.DoesNotContain("gho_", token.ToString(), StringComparison.Ordinal);
        Assert.Equal("gho_abcdef", token.Value);
    }

    [Fact]
    public void Porcelain_v2_status_is_parsed_with_renames_submodules_and_spaces()
    {
        string output = string.Join('\0',
            "# branch.oid abc",
            "1 .M N... 100644 100644 100644 aaa aaa src/a file.txt",
            "2 R. N... 100644 100644 100644 bbb bbb R100 new name.txt",
            "old name.txt",
            "1 .M S.M. 160000 160000 160000 ccc ccc vendor/lib",
            "u UU N... 100644 100644 100644 100644 d1 d2 d3 conflict.txt",
            "? untracked dir/x.txt",
            "! ignored.log",
            string.Empty);
        IReadOnlyList<StatusEntry> entries = StatusParser.Parse(output);
        Assert.Equal(6, entries.Count);
        Assert.Equal(new StatusEntry(StatusKind.Changed, "src/a file.txt", ".M"), entries[0]);
        Assert.True(entries[0].UnstagedOnly);
        Assert.Equal("old name.txt", entries[1].OriginalPath);
        Assert.Equal("new name.txt", entries[1].Path);
        Assert.True(entries[2].IsSubmodule);
        Assert.True(entries[2].SubmoduleHasWork);
        Assert.Equal(StatusKind.Unmerged, entries[3].Kind);
        Assert.Equal("untracked dir/x.txt", entries[4].Path);
        Assert.Equal(StatusKind.Ignored, entries[5].Kind);
    }

    [Fact]
    public void Strategies_give_the_right_clone_arguments_and_estimates()
    {
        Assert.Equal(["--filter=blob:none"], CloneStrategy.Blobless.GitArguments());
        Assert.Equal(["--depth=1"], CloneStrategy.Shallow.GitArguments());
        Assert.Empty(CloneStrategy.Full.GitArguments());
        Assert.Equal(358_400, CloneStrategy.Blobless.EstimateBytes(1000));
        Assert.Null(CloneStrategy.Full.EstimateBytes(0));
        Assert.Equal(CloneStrategy.Shallow, CloneStrategies.Parse("SHALLOW"));
        Assert.Null(CloneStrategies.Parse("bogus"));

        DownloadPlan plan = Downloader.Plan(new DownloadRequest { NameWithOwner = "a/b", Destination = Path.Join(Path.GetTempPath(), "dl-" + Guid.NewGuid().ToString("N"), "b"), Strategy = CloneStrategy.Shallow, RemoteKB = 2000 });
        Assert.Null(plan.Refusal);
        Assert.Equal("https://github.com/a/b.git", plan.Url);
        Assert.Equal(["clone", "--config", "core.longpaths=true", "--depth=1", "--", "https://github.com/a/b.git", plan.Destination], plan.Arguments);
        Assert.Equal(245_760, plan.EstimateBytes);
        Assert.NotNull(Downloader.Plan(new DownloadRequest { NameWithOwner = "a/b", Destination = "relative\\b" }).Refusal);
    }

    [Fact]
    public void Credentials_are_per_command_and_the_token_is_only_in_the_environment()
    {
        var token = new GitHubToken("work", "gho_worktoken");
        GitCall call = GitCredentials.For(@"C:\Program Files\GitHub CLI\gh.exe", token, TimeSpan.FromMinutes(1));
        Assert.Equal(["credential.helper=", "credential.helper=!'C:/Program Files/GitHub CLI/gh.exe' auth git-credential", "credential.useHttpPath=false"], call.Config);
        Assert.Equal("gho_worktoken", call.Environment["GH_TOKEN"]);
        Assert.True(call.AllowNetwork);
        IReadOnlyList<string> argv = GitCli.Arguments("C:\\x", ["push"], call);
        Assert.DoesNotContain(argv, a => a.Contains("gho_", StringComparison.Ordinal));
        Assert.DoesNotContain(argv, a => a.Contains("--global", StringComparison.Ordinal));
        Assert.Equal(["-c", "core.fsmonitor=false", "-c", "core.longpaths=true"], argv.Take(4));
        Assert.Equal(["-C", "C:\\x", "push"], argv.TakeLast(3));
    }

    [Fact]
    public void Terminal_scripts_show_every_command_wait_for_enter_and_never_hold_a_token()
    {
        TerminalJob push = TerminalLauncher.Push(@"C:\Code\it's here", "app", @"C:\Git\cmd\git.exe", @"C:\gh\gh.exe", "work", 2);
        string script = TerminalLauncher.Script(push);
        Assert.Contains("Read-Host 'Press Enter", script, StringComparison.Ordinal);
        Assert.Contains("Set-Location -LiteralPath 'C:\\Code\\it''s here'", script, StringComparison.Ordinal);
        Assert.Contains("auth token --hostname github.com --user 'work'", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Item Env:GH_TOKEN", script, StringComparison.Ordinal);
        Assert.DoesNotContain("gho_", script, StringComparison.Ordinal);
        foreach (TerminalCommand c in push.Commands)
        {
            Assert.Contains("Write-Host '  > " + c.Shown.Replace("'", "''", StringComparison.Ordinal), script, StringComparison.Ordinal);
        }

        TerminalJob signIn = TerminalLauncher.SignIn(@"C:\gh\gh.exe", "alice");
        Assert.Equal(2, signIn.Commands.Count);
        Assert.Contains("auth switch --hostname github.com --user 'alice'", signIn.Commands[1].PowerShell, StringComparison.Ordinal);
        Assert.Single(TerminalLauncher.SignIn(@"C:\gh\gh.exe", null).Commands);

        TerminalLaunch wt = TerminalLauncher.Plan(@"C:\t\s.ps1", @"C:\wt.exe", @"C:\Windows");
        Assert.True(wt.WindowsTerminal);
        Assert.Equal([@"C:\wt.exe", "new-tab", "--title", "DevSweep", @"C:\Windows" + Path.DirectorySeparatorChar + Path.Join("System32", "WindowsPowerShell", "v1.0", "powershell.exe"), "-NoProfile", "-ExecutionPolicy", "Bypass", "-NoExit", "-File", @"C:\t\s.ps1"], wt.Argv);
        Assert.False(TerminalLauncher.Plan(@"C:\t\s.ps1", null, @"C:\Windows").WindowsTerminal);
    }
}
