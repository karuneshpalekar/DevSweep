using DevSweep.Core.Cleaning;
using DevSweep.Core.Health.Runtimes;
using DevSweep.Core.Health.Security;
using DevSweep.Core.Inventory.Registry;
using DevSweep.Core.Locations;
using DevSweep.Fixtures;
using Xunit;

namespace DevSweep.Accuracy.Health;

/// <summary>
/// Security scan on real NTFS with synthetic fake secrets and real git repositories: git statuses come
/// from git itself; junctions are not followed; cloud-only placeholders are skipped and never hydrated.
/// </summary>
public sealed class SecurityAccuracyTests(ITestOutputHelper output) : IDisposable
{
    private const string FakeSecret = "fake-secret-value-do-not-use";
    private readonly TempFixture _fixture = new("security");
    private CloudSyncRoot? _sync;

    public void Dispose()
    {
        _sync?.Dispose();
        _fixture.Dispose();
    }

    private static async Task Git(string git, string directory, params string[] arguments)
    {
        CommandResult result = await CommandRunner.RunAsync([git, "-C", directory, .. arguments], TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, $"git {string.Join(' ', arguments)}: {result.StandardError}");
    }

    [Fact]
    public async Task Env_files_get_their_real_git_status()
    {
        var real = new ProbeContext(WindowsEnvironment.Instance, WindowsRegistryReader.Instance);
        WindowsGitRunner? git = WindowsGitRunner.Find(real, out string? whyNot);
        if (git is null)
        {
            Assert.Skip($"git is not usable here: {whyNot}");
        }

        output.WriteLine($"git: {git.Git}");
        var environment = FixedEnvironment.UnderRoot(_fixture.Root);
        string root = _fixture.CreateDirectory(@"Users\Dev\source");
        string committed = _fixture.CreateDirectory(@"Users\Dev\source\committed");
        string ignored = _fixture.CreateDirectory(@"Users\Dev\source\ignored");
        string open = _fixture.CreateDirectory(@"Users\Dev\source\open");
        string plain = _fixture.CreateDirectory(@"Users\Dev\source\plain");
        foreach (string repo in new[] { committed, ignored, open })
        {
            await Git(git.Git, repo, "init", "-q");
            await Git(git.Git, repo, "config", "user.email", "test@example.invalid");
            await Git(git.Git, repo, "config", "user.name", "DevSweep test");
            await Git(git.Git, repo, "config", "core.autocrlf", "false");
        }

        foreach (string folder in new[] { committed, ignored, open, plain })
        {
            await File.WriteAllTextAsync(Path.Join(folder, ".env"), $"API_KEY={FakeSecret}\nPORT=1\n", TestContext.Current.CancellationToken);
        }

        await Git(git.Git, committed, "add", ".env");
        await Git(git.Git, committed, "commit", "-q", "-m", "oops");
        await File.WriteAllTextAsync(Path.Join(ignored, ".gitignore"), ".env\n", TestContext.Current.CancellationToken);

        SecurityResult result = SecurityScanner.Scan(environment, [root], git);
        foreach (SecurityFinding f in result.Findings)
        {
            output.WriteLine($"{f.Level} {f.Status}: {f.Title} ({f.Path})");
        }

        Assert.Equal("Committed to git", Assert.Single(result.Findings, f => f.Path.StartsWith(committed, StringComparison.OrdinalIgnoreCase)).Status);
        Assert.Equal("Kept out of git", Assert.Single(result.Findings, f => f.Path.StartsWith(ignored, StringComparison.OrdinalIgnoreCase)).Status);
        Assert.Equal("Not ignored", Assert.Single(result.Findings, f => f.Path.StartsWith(open, StringComparison.OrdinalIgnoreCase)).Status);
        Assert.Equal("Not in git", Assert.Single(result.Findings, f => f.Path.StartsWith(plain, StringComparison.OrdinalIgnoreCase)).Status);
        Assert.DoesNotContain(result.Findings, f => (f.Title + f.Why + f.WhatToDo + string.Join(' ', f.Fix ?? [])).Contains(FakeSecret, StringComparison.Ordinal));
    }

    [Fact]
    public void Junctions_are_not_followed_and_cloud_only_files_are_never_opened()
    {
        var environment = FixedEnvironment.UnderRoot(_fixture.Root);
        string downloads = environment.KnownFolder("Downloads")!;
        Directory.CreateDirectory(downloads);
        string outside = _fixture.CreateDirectory("outside");
        File.WriteAllText(Path.Join(outside, "backup-codes.txt"), "11111-22222");
        Ntfs.CreateJunction(Path.Join(downloads, "linked"), outside);
        File.WriteAllText(Path.Join(downloads, "recovery-codes.txt"), "33333-44444");

        SecurityResult local = SecurityScanner.Scan(environment, [], null);
        Assert.Equal(Path.Join(downloads, "recovery-codes.txt"), Assert.Single(local.Findings).Path);

        _sync = CloudSyncRoot.TryCreate(_fixture.PathOf("OneDrive"), out string reason);
        if (_sync is null)
        {
            Assert.Skip($"{reason} ({Elevation.Describe()})");
        }

        _sync.CreateFilePlaceholders(string.Empty, [("github-recovery-codes.txt", 4096), ("id_rsa", 4096), (".env", 4096)]);
        environment.Folders["Documents"] = _sync.Root;
        SecurityResult cloud = SecurityScanner.Scan(environment, [_sync.Root], null);
        output.WriteLine($"cloud-only files skipped: {cloud.CloudFilesSkipped}; findings: {cloud.Findings.Count}");
        Assert.True(cloud.CloudFilesSkipped >= 3);
        Assert.DoesNotContain(cloud.Findings, f => f.Path.StartsWith(_sync.Root, StringComparison.OrdinalIgnoreCase));
        foreach (string name in new[] { "github-recovery-codes.txt", "id_rsa", ".env" })
        {
            PlaceholderInfo info = CloudSyncRoot.GetInfo(Path.Join(_sync.Root, name));
            output.WriteLine($"{name}: on-disk data {info.OnDiskDataSize}");
            Assert.Equal(0, info.OnDiskDataSize);
        }
    }
}
