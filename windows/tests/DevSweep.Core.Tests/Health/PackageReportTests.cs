using DevSweep.Core.Cleaning;
using DevSweep.Core.Health.Packages;
using DevSweep.Core.Health.Runtimes;
using Xunit;

namespace DevSweep.Core.Tests.Health;

public sealed class PackageReportTests
{
    // Recorded from `winget list --upgrade-available --disable-interactivity` (English), spinner frames included.
    private const string WingetEnglish = "   - \r   \\ \r   | \rName                       Id                          Version   Available Source\r\n"
        + "---------------------------------------------------------------------------------\r\n"
        + "Git                        Git.Git                     2.46.0    2.47.1    winget\r\n"
        + "Microsoft Visual Studio C… Microsoft.VisualStudioCode  1.94.2    1.95.3    winget\r\n"
        + "Node.js                    OpenJS.NodeJS.LTS           < 20.11.0 22.11.0   winget\r\n"
        + "3 upgrades available.\r\n";

    // The same table in German: other headings, other footer. Positions are what matter.
    private const string WingetGerman = "Name        Id              Version Verfügbar Quelle\r\n"
        + "----------------------------------------------------\r\n"
        + "Git         Git.Git         2.46.0  2.47.1    winget\r\n"
        + "3 Aktualisierungen verfügbar.\r\n";

    [Fact]
    public void Winget_table_is_read_by_column_position_whatever_the_language()
    {
        ParsedPackages english = PackageParsers.Winget(WingetEnglish, 0);
        Assert.True(english.Understood);
        Assert.Equal(["Git.Git", "Microsoft.VisualStudioCode", "OpenJS.NodeJS.LTS"], english.Outdated.Select(p => p.Id));
        Assert.Equal(("20.11.0", "22.11.0"), (english.Outdated[2].Installed, english.Outdated[2].Latest));
        Assert.Equal("Microsoft Visual Studio C", english.Outdated[1].Name);

        ParsedPackages german = PackageParsers.Winget(WingetGerman, 0);
        Assert.True(german.Understood);
        Assert.Equal(("2.46.0", "2.47.1"), (german.Outdated.Single().Installed, german.Outdated.Single().Latest));
    }

    [Fact]
    public void Winget_with_nothing_to_upgrade_is_told_by_its_exit_code_not_its_words()
    {
        Assert.Equal(new ParsedPackages([], true), PackageParsers.Winget("Es wurde kein installiertes Paket gefunden.\r\n", PackageParsers.WingetNothingFound));
        ParsedPackages unknown = PackageParsers.Winget("Something we have never seen\r\n", 0);
        Assert.False(unknown.Understood);
        Assert.False(PackageParsers.Winget("Name Id\r\n-----\r\nGit Git.Git\r\n", 0).Understood); // too few columns
    }

    [Fact]
    public void Chocolatey_output_is_pipe_separated_and_warnings_are_ignored()
    {
        ParsedPackages parsed = PackageParsers.Chocolatey("Chocolatey v2.3.0\r\ngit|2.46.0|2.47.1|false\r\nnodejs|20.11.0|22.11.0|true\r\n", 0);
        Assert.True(parsed.Understood);
        Assert.Equal([("git", false), ("nodejs", true)], parsed.Outdated.Select(p => (p.Id, p.Pinned)));
        Assert.True(PackageParsers.Chocolatey(string.Empty, 0).Understood);
        Assert.False(PackageParsers.Chocolatey("Access denied", 1).Understood);
    }

    [Fact]
    public void Scoop_files_are_read_with_their_quirks()
    {
        Assert.Equal("1.2.3", PackageParsers.ScoopManifestVersion("{ \"version\": \"1.2.3\", /* c */ }"));
        Assert.Null(PackageParsers.ScoopManifestVersion("{ \"version\": \"nightly\" }"));
        Assert.Null(PackageParsers.ScoopManifestVersion("not json"));
        Assert.Equal(("main", true), PackageParsers.ScoopInstall("{\"bucket\":\"main\",\"hold\":true}"));
        Assert.Equal((null, false), PackageParsers.ScoopInstall("{\"url\":\"x\"}"));
    }

    [Fact]
    public async Task Winget_is_queried_read_only_with_a_time_limit_and_no_prompts()
    {
        using var machine = new HealthMachine();
        machine.Alias(Path.Join(machine.LocalAppData, "Microsoft", "WindowsApps", "winget.exe"), new AliasInfo("Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe!winget", "winget.exe", AliasKind.OtherPackage));
        var asked = new List<(IReadOnlyList<string> Argv, TimeSpan Limit)>();
        var reporter = new PackageReporter(machine.Environment, (argv, limit, _) =>
        {
            asked.Add((argv, limit));
            return Task.FromResult(new CommandResult(0, WingetEnglish, string.Empty, false, TimeSpan.FromSeconds(3)));
        }, _ => null);

        PackagesResult result = await reporter.ReportAsync(null, null, TestContext.Current.CancellationToken);
        PackageManagerReport winget = Assert.Single(result.Managers);
        Assert.Equal(3, winget.Outdated.Count);
        Assert.Equal(["list", "--upgrade-available", "--disable-interactivity", "--accept-source-agreements"], asked.Single().Argv.Skip(1));
        Assert.True(asked.Single().Limit <= TimeSpan.FromSeconds(120));
        RuntimeStep step = Assert.Single(winget.Steps);
        Assert.Equal(["winget upgrade --all --disable-interactivity"], step.Commands);
        Assert.False(step.NeedsAdmin);
        Assert.Equal(3, result.AttentionCount);
    }

    [Fact]
    public async Task A_result_that_cannot_be_read_says_so_instead_of_guessing()
    {
        using var machine = new HealthMachine();
        machine.Alias(Path.Join(machine.LocalAppData, "Microsoft", "WindowsApps", "winget.exe"), new AliasInfo("Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe!winget", "winget.exe", AliasKind.OtherPackage));
        PackagesResult garbled = await new PackageReporter(machine.Environment, (_, _, _) => Task.FromResult(new CommandResult(0, "???", string.Empty, false, TimeSpan.Zero)), _ => null).ReportAsync(null, null, TestContext.Current.CancellationToken);
        Assert.Contains("couldn't read the result", garbled.Managers.Single().Problem, StringComparison.Ordinal);
        Assert.Empty(garbled.Managers.Single().Outdated);

        PackagesResult slow = await new PackageReporter(machine.Environment, (_, _, _) => Task.FromResult(new CommandResult(-1, string.Empty, string.Empty, true, TimeSpan.FromSeconds(90))), _ => null).ReportAsync(null, null, TestContext.Current.CancellationToken);
        Assert.Contains("in time", slow.Managers.Single().Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Managers_that_are_not_installed_are_not_reported_and_nothing_is_run()
    {
        using var machine = new HealthMachine();
        int ran = 0;
        PackagesResult result = await new PackageReporter(machine.Environment, (_, _, _) =>
        {
            ran++;
            return Task.FromResult(new CommandResult(0, string.Empty, string.Empty, false, TimeSpan.Zero));
        }, _ => null).ReportAsync(null, null, TestContext.Current.CancellationToken);
        Assert.Empty(result.Managers);
        Assert.Equal(0, ran);
    }

    [Fact]
    public async Task Scoop_is_answered_from_its_files_with_outdated_removed_held_and_orphans()
    {
        using var machine = new HealthMachine();
        string scoop = Path.Join(machine.Profile, "scoop");
        void App(string name, string version, string? bucket, string? latest, bool hold = false)
        {
            machine.File(Path.Join(scoop, "apps", name, version, "install.json"), bucket is null ? "{}" : $"{{\"bucket\":\"{bucket}\",\"hold\":{(hold ? "true" : "false")}}}");
            Directory.CreateDirectory(Path.Join(scoop, "apps", name, "current"));
            if (latest is not null)
            {
                machine.File(Path.Join(scoop, "buckets", bucket!, "bucket", name + ".json"), $"{{\"version\":\"{latest}\"}}");
            }
        }

        App("git", "2.46.0", "main", "2.47.1");
        App("ripgrep", "14.1.0", "main", "14.1.0");
        App("pinned", "1.0.0", "main", "2.0.0", hold: true);
        App("oldtool", "0.9.0", "extras", null);
        Directory.CreateDirectory(Path.Join(scoop, "buckets", "extras", "bucket"));
        Directory.CreateDirectory(Path.Join(scoop, "apps", "ghost"));
        machine.File(Path.Join(scoop, "cache", "git#2.46.0#abc.7z"), new string('x', 4096));

        PackageManagerReport report = (await new PackageReporter(machine.Environment, (_, _, _) => throw new InvalidOperationException("Scoop is read from files"), p => (4096, true)).ReportAsync(null, null, TestContext.Current.CancellationToken)).Managers.Single();
        Assert.Equal(PackageManager.Scoop, report.Manager);
        Assert.Equal(["git", "pinned"], report.Outdated.Select(p => p.Id));
        Assert.True(report.Outdated[1].Pinned);
        Assert.Equal("oldtool", report.Deprecated.Single().Name);
        Assert.Equal("ghost", Path.GetFileName(report.Orphans.Single().Path));
        Assert.Equal(4, report.InstalledCount);
        Assert.Equal(4096, report.Caches.Single().Bytes);
        Assert.Contains(report.Steps, s => s.Id == "scoop:upgrade-all" && s.Commands.SequenceEqual(["scoop update", "scoop update *"]));
        Assert.Contains(report.Steps, s => s.Id == "scoop:removed" && s.Commands.Single() == "scoop uninstall 'oldtool'");
        Assert.Contains(report.Steps, s => s.Id == "scoop:orphans" && s.Commands.Single().Contains("SendToRecycleBin", StringComparison.Ordinal));
        Assert.All(report.Steps, s => Assert.False(s.NeedsAdmin));
    }

    [Fact]
    public async Task Chocolatey_upgrade_needs_an_administrator_and_empty_package_folders_are_orphans()
    {
        using var machine = new HealthMachine();
        string choco = Path.Join(machine.Environment.KnownFolder("ProgramData")!, "chocolatey");
        machine.File(Path.Join(choco, "bin", "choco.exe"), "MZ");
        Directory.CreateDirectory(Path.Join(choco, "lib", "emptypkg"));
        machine.File(Path.Join(choco, "lib", "git", "git.nuspec"));
        PackageManagerReport report = (await new PackageReporter(machine.Environment, (argv, _, _) =>
            Task.FromResult(new CommandResult(0, "git|2.46.0|2.47.1|false\r\n", string.Empty, false, TimeSpan.Zero)), _ => null).ReportAsync(null, null, TestContext.Current.CancellationToken)).Managers.Single();
        Assert.Equal(PackageManager.Chocolatey, report.Manager);
        Assert.Single(report.Outdated);
        Assert.Equal("emptypkg", Path.GetFileName(report.Orphans.Single().Path));
        Assert.True(report.Steps.Single(s => s.Id == "choco:upgrade-all").NeedsAdmin);
    }

    [Fact]
    public void The_same_release_line_from_two_owners_is_a_duplicate_but_ides_and_ci_caches_are_not()
    {
        Installation Install(string cycle, InstallSource source) => new() { Version = cycle + ".1", Cycle = cycle, Path = @"C:\x\" + source, Source = source };
        var node = new RuntimeReport { Id = "node", Name = "Node.js", Installs = [Install("20", InstallSource.Scoop), Install("20", InstallSource.NodejsOrg), Install("22", InstallSource.Nvm), Install("22", InstallSource.IdeBundled)] };
        DuplicateInstall duplicate = Assert.Single(PackageReporter.Duplicates([node]));
        Assert.Equal(("node", "20", ["Scoop", "nodejs.org installer"]), (duplicate.Tool, duplicate.Cycle, duplicate.Owners.ToArray()));
        Assert.Contains("Node.js 20 is installed more than once", duplicate.Text, StringComparison.Ordinal);
    }
}
