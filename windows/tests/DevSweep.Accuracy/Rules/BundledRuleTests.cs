using DevSweep.Core.Cleaning;
using DevSweep.Core.Inventory.Collectors;
using DevSweep.Core.Platform;
using DevSweep.Core.Rules;
using DevSweep.Core.Scanning;
using Microsoft.Win32;
using Xunit;

namespace DevSweep.Accuracy.Rules;

/// <summary>Every bundled rule against a fixture machine on real NTFS: found where its location says, measured exactly, explained.</summary>
public sealed class BundledRuleTests(ITestOutputHelper output) : IDisposable
{
    private const long Mb = 1024 * 1024;
    private readonly MachineFixture _machine = new("rules");

    public void Dispose() => _machine.Dispose();

    [Fact]
    public async Task Npm_cache_is_found_at_the_default_location()
    {
        string cache = Path.Join(_machine.Local, "npm-cache", "_cacache");
        MachineFixture.Allocate(Path.Join(cache, "content-v2", "sha512", "aa", "blob"), 60 * Mb);

        Finding finding = Only(await _machine.ScanAsync([Category.PackageCaches]), "npm-cache");

        Assert.Equal([cache], finding.Paths);
        AssertExact(finding, 60 * Mb);
        Assert.Contains(finding.Checks, c => c.Text.StartsWith("npm cache: ", StringComparison.Ordinal) && c.Text.Contains("the default", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Npm_cache_follows_the_user_npmrc()
    {
        string moved = Path.Join(_machine.Root, "D", "npm");
        MachineFixture.Write(Path.Join(_machine.Profile, ".npmrc"), $"cache={moved}\n");
        MachineFixture.Allocate(Path.Join(moved, "_cacache", "index-v5", "x"), 55 * Mb);
        MachineFixture.Allocate(Path.Join(_machine.Local, "npm-cache", "_cacache", "stale"), 55 * Mb);

        Finding finding = Only(await _machine.ScanAsync([Category.PackageCaches]), "npm-cache");

        Assert.Equal([Path.Join(moved, "_cacache")], finding.Paths);
        Assert.Contains(finding.Checks, c => c.Text.Contains($"cache in {Path.Join(_machine.Profile, ".npmrc")}", StringComparison.Ordinal));
        Assert.Contains(moved, finding.Explanation!.Why, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pip_cache_and_its_threshold()
    {
        string cache = Path.Join(_machine.Local, "pip", "Cache");
        MachineFixture.Allocate(Path.Join(cache, "http-v2", "a"), 40 * Mb);

        Assert.DoesNotContain((await _machine.ScanAsync([Category.PackageCaches])).Findings, f => f.RuleId == "pip-cache");

        MachineFixture.Allocate(Path.Join(cache, "wheels", "b"), 20 * Mb);
        Finding finding = Only(await _machine.ScanAsync([Category.PackageCaches]), "pip-cache");
        AssertExact(finding, 60 * Mb);
        Assert.Equal(["Delete", "Run pip cache purge"], finding.Actions.Select(a => a.DisplayLabel));
    }

    [Fact]
    public async Task Gradle_caches_follow_gradle_user_home()
    {
        string home = Path.Join(_machine.Root, "gradle-home");
        _machine.Environment.Variables["GRADLE_USER_HOME"] = home;
        MachineFixture.Allocate(Path.Join(home, "caches", "modules-2", "files"), 520 * Mb);

        Finding finding = Only(await _machine.ScanAsync([Category.PackageCaches]), "gradle-caches");

        Assert.Equal([Path.Join(home, "caches")], finding.Paths);
        AssertExact(finding, 520 * Mb);
        Assert.Contains(finding.Checks, c => c.Text.Contains("from the GRADLE_USER_HOME environment variable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NuGet_http_cache_is_a_dotnet_finding_with_dotnets_command()
    {
        MachineFixture.Allocate(Path.Join(_machine.Local, "NuGet", "v3-cache", "abc", "list.dat"), 51 * Mb);

        Finding finding = Only(await _machine.ScanAsync([Category.Dotnet]), "nuget-http-cache");

        Assert.Equal(Category.Dotnet, finding.Category);
        Assert.Equal(["dotnet", "nuget", "locals", "http-cache", "--clear"], finding.Actions[0].Command);
        AssertExact(finding, 51 * Mb);
    }

    [Fact]
    public async Task Temp_items_older_than_seven_days_and_only_those()
    {
        DateTime old = DateTime.UtcNow.AddDays(-10);
        string oldFile = MachineFixture.Allocate(Path.Join(_machine.Temp, "setup-old.msi"), 30 * Mb);
        string oldFolder = Path.Join(_machine.Temp, "old-extract");
        MachineFixture.Allocate(Path.Join(oldFolder, "inner", "payload.bin"), 30 * Mb);
        string touchedFolder = Path.Join(_machine.Temp, "old-but-touched");
        MachineFixture.Allocate(Path.Join(touchedFolder, "old.bin"), 30 * Mb);
        MachineFixture.Allocate(Path.Join(_machine.Temp, "new.bin"), 30 * Mb);
        MachineFixture.Age(oldFile, old);
        MachineFixture.Age(oldFolder, old);
        MachineFixture.Age(touchedFolder, old);
        MachineFixture.Write(Path.Join(touchedFolder, "written-today.log"), "x");
        Directory.SetLastWriteTimeUtc(touchedFolder, old); // the folder itself looks old; its new file does not

        Finding finding = Only(await _machine.ScanAsync([Category.Windows]), "temp-old-files");

        Assert.Equal([oldFile, oldFolder], finding.Paths.Order(StringComparer.OrdinalIgnoreCase));
        AssertExact(finding, 60 * Mb);
        Assert.Contains(finding.Checks, c => c.Text.Contains("2 newer items left alone", StringComparison.Ordinal));
        Assert.Contains("7 days", finding.Explanation!.Why, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JetBrains_keeps_the_newest_version_of_each_ide()
    {
        string roaming = Path.Join(_machine.Roaming, "JetBrains");
        string local = Path.Join(_machine.Local, "JetBrains");
        MachineFixture.Allocate(Path.Join(roaming, "IntelliJIdea2024.1", "options", "a.xml"), 12 * Mb);
        MachineFixture.Allocate(Path.Join(local, "IntelliJIdea2024.1", "caches", "c"), 12 * Mb);
        MachineFixture.Allocate(Path.Join(roaming, "IntelliJIdea2024.10", "options", "a.xml"), 12 * Mb);
        MachineFixture.Allocate(Path.Join(roaming, "IntelliJIdea2024.9", "options", "a.xml"), 12 * Mb);
        MachineFixture.Allocate(Path.Join(roaming, "Rider2024.3", "options", "a.xml"), 12 * Mb);
        MachineFixture.Allocate(Path.Join(roaming, "consentOptions", "accepted"), 12 * Mb);

        ScanResult result = await _machine.ScanAsync([Category.IdeVersions]);

        Assert.Equal(["IntelliJIdea 2024.1 settings and caches", "IntelliJIdea 2024.9 settings and caches"], result.Findings.Select(f => f.Title).Order(StringComparer.Ordinal));
        Finding older = result.Findings.Single(f => f.Title.Contains("2024.1 ", StringComparison.Ordinal));
        Assert.Equal(2, older.Paths.Count);
        AssertExact(older, 24 * Mb);
        Assert.Contains("IntelliJIdea 2024.10 is newer", older.Explanation!.Why, StringComparison.Ordinal);
        Assert.Contains(older.Checks, c => c.Text.Contains("IntelliJIdea 2024.10 stays", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stale_project_artifacts_judge_activity_by_git_and_source_never_by_the_artifacts()
    {
        string src = Path.Join(_machine.Root, "src");
        DateTime old = DateTime.UtcNow.AddDays(-60);

        string staleNode = Project(src, "old-web", "package.json", ("node_modules", 60 * Mb), old);
        MachineFixture.Write(Path.Join(staleNode, ".git", "HEAD"), "ref: refs/heads/main");
        MachineFixture.Age(Path.Join(staleNode, ".git"), old);
        MachineFixture.Allocate(Path.Join(staleNode, "node_modules", ".package-lock.json"), 1024); // npm install today: not activity

        Project(src, "fresh-web", "package.json", ("node_modules", 60 * Mb), DateTime.UtcNow);
        string committed = Project(src, "old-but-committed", "package.json", ("node_modules", 60 * Mb), old);
        MachineFixture.Write(Path.Join(committed, ".git", "index"), "today");

        string dotnet = Project(src, Path.Join("group", "OldLib"), "OldLib.csproj", ("bin", 30 * Mb), old);
        MachineFixture.Allocate(Path.Join(dotnet, "obj", "project.assets.json"), 30 * Mb);
        MachineFixture.Age(dotnet, old);

        string scripts = Path.Join(src, "scripts");
        MachineFixture.Allocate(Path.Join(scripts, "bin", "tool.ps1"), 60 * Mb);
        MachineFixture.Age(scripts, old);

        string linked = Path.Join(src, "linked-web");
        MachineFixture.Write(Path.Join(linked, "package.json"), "{}");
        Directory.CreateDirectory(Path.Join(_machine.Root, "elsewhere-modules"));
        MachineFixture.Allocate(Path.Join(_machine.Root, "elsewhere-modules", "big.bin"), 60 * Mb);
        Fixtures.Ntfs.CreateJunction(Path.Join(linked, "node_modules"), Path.Join(_machine.Root, "elsewhere-modules"));
        MachineFixture.Age(linked, old);

        ScanResult result = await _machine.ScanAsync([Category.Projects], projectRoots: [src]);
        foreach (Finding f in result.Findings)
        {
            output.WriteLine($"{f.Title}: {f.Size:N0} {string.Join(" | ", f.Checks.Select(c => c.Text))}");
        }

        Assert.Equal(["bin and obj in OldLib", "node_modules in old-web"], result.Findings.Select(f => f.Title).Order(StringComparer.Ordinal));
        Finding node = result.Findings.Single(f => f.Title == "node_modules in old-web");
        Assert.Equal([Path.Join(staleNode, "node_modules")], node.Paths);
        Assert.Equal("npm install", node.Facts["reinstall"]);
        Assert.Contains(node.Checks, c => c.Text.Contains("Last commit or checkout", StringComparison.Ordinal));
        Finding net = result.Findings.Single(f => f.Title == "bin and obj in OldLib");
        Assert.Equal(2, net.Paths.Count);
        Assert.Equal("dotnet build", net.Facts["reinstall"]);
        AssertExact(net, 60 * Mb);
    }

    [Fact]
    public async Task Orphaned_app_data_is_flagged_only_for_apps_no_source_lists()
    {
        MachineFixture.Allocate(Path.Join(_machine.Roaming, "Postman", "storage", "data.db"), 3 * Mb);
        MachineFixture.Allocate(Path.Join(_machine.Roaming, "Slack", "Cache", "data"), 3 * Mb);
        _machine.Registry.Set(RegistryHive.CurrentUser, RegistryView.Registry64, UninstallCollector.KeyPath + @"\slack",
            ("DisplayName", "Slack"), ("InstallLocation", Path.Join(_machine.Local, "slack")));

        ScanResult result = await _machine.ScanAsync([Category.Leftovers]);

        Finding postman = Assert.Single(result.Findings);
        Assert.Equal("Postman", postman.Title);
        Assert.Equal(Risk.Leftover, postman.Risk);
        Assert.Equal([CleanActionKind.Trash], postman.Actions.Select(a => a.Kind));
        Assert.Equal(12, postman.Checks.Count(c => c.Text.StartsWith("Not in ", StringComparison.Ordinal)));
        Assert.Contains(postman.Checks, c => c.Status == CheckStatus.Warning && c.Text.Contains("collections", StringComparison.Ordinal));
        Assert.Contains("12 places apps are installed from", postman.Explanation!.Why, StringComparison.Ordinal);
        Assert.Equal(12, result.Report.Inventory.Count);
    }

    private static string Project(string root, string name, string manifest, (string Folder, long Bytes) artifact, DateTime when)
    {
        string project = Path.Join(root, name);
        MachineFixture.Write(Path.Join(project, manifest), "{}");
        MachineFixture.Write(Path.Join(project, "src", "index.code"), "code");
        string folder = Path.Join(project, artifact.Folder);
        MachineFixture.Allocate(Path.Join(folder, artifact.Folder == "bin" ? "Debug" : "pkg", "payload.bin"), artifact.Bytes);
        MachineFixture.Age(project, when);
        return project;
    }

    private static Finding Only(ScanResult result, string ruleId) => Assert.Single(result.Findings, f => f.RuleId == ruleId);

    /// <summary>The walker's size is the allocation: the bytes, rounded up to clusters, plus the small write.</summary>
    private void AssertExact(Finding finding, long allocated)
    {
        output.WriteLine($"{finding.Id}: {finding.Size:N0} bytes, {finding.Files} files, {finding.Confidence}, {finding.Measurement}");
        Assert.InRange(finding.Size, allocated, allocated + (64 * 1024 * finding.Paths.Count) + (64 * 1024));
        Assert.Equal(SizeConfidence.Exact, finding.Confidence);
        Assert.Equal(SizeMeasurement.Exact, finding.Measurement);
        Assert.Equal(finding.Size, finding.FreeableBytes);
        Assert.DoesNotContain("{{", finding.Explanation!.What + finding.Explanation.Why + finding.Explanation.IfDeleted, StringComparison.Ordinal);
    }
}
