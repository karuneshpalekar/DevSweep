using System.Text;
using DevSweep.Core.Health.Runtimes;
using Xunit;

namespace DevSweep.Core.Tests.Health;

public sealed class RuntimeReportTests
{
    private static Installation Node(string version, InstallSource source, bool isDefault = false, string? path = null, string? detail = null)
        => new()
        {
            Version = version,
            Cycle = new NodeProbe().Cycle(version),
            Path = path ?? $@"C:\node\{source}\{version}",
            Source = source,
            SourceDetail = detail,
            IsDefault = isDefault,
            Product = "nodejs",
        };

    private static RuntimeReport Build(RuntimeProbe probe, IReadOnlyList<Installation> installs, string product, string json)
        => RuntimeReporter.Build(new ReportInput
        {
            Probe = probe,
            Installs = installs,
            Eol = new Dictionary<string, EolData> { [product] = EolFixtures.Data(json) },
            Profile = @"C:\Users\Dev",
            Today = EolFixtures.Today,
        });

    [Fact]
    public void Support_dates_flags_and_fixes_follow_the_owning_tool()
    {
        RuntimeReport report = Build(new NodeProbe(),
            [Node("20.11.0", InstallSource.Nvm, isDefault: true), Node("16.20.2", InstallSource.Nvm), Node("22.3.0", InstallSource.Fnm), Node("18.19.0", InstallSource.Volta, path: @"C:\Users\Dev\AppData\Local\Volta\tools\image\node\18.19.0")],
            "nodejs", EolFixtures.NodeJs);

        Installation def = report.Installs[0];
        Assert.True(def.IsDefault);
        Assert.Equal(SupportStatus.EndOfLife, def.Support);
        Assert.Equal(new DateOnly(2026, 4, 30), def.SupportEnds);
        Assert.Equal("20.19.5", def.LatestInCycle);
        Assert.Equal(SupportStatus.Supported, report.Installs.Single(i => i.Version == "22.3.0").Support);
        Assert.Equal("Node.js 24 LTS, supported until 30 Apr 2028", report.Recommended);

        Assert.Contains(report.Issues, i => i.Level == IssueLevel.Critical && i.Text.StartsWith("Node.js 20.11.0 (nvm-windows) reached end of life on 30 Apr 2026", StringComparison.Ordinal) && i.Text.EndsWith("It's the one a new terminal runs.", StringComparison.Ordinal));
        Assert.Contains(report.Issues, i => i.Text.Contains("22.3.0 → 22.21.1 (fnm)", StringComparison.Ordinal));
        Assert.Contains(report.Issues, i => i.Text.StartsWith("Installed by 3 different tools (fnm, nvm-windows and Volta)", StringComparison.Ordinal));
        Assert.Contains(report.Issues, i => i.Text.Contains("even though 22.3.0 from fnm is installed", StringComparison.Ordinal));

        RuntimeStep switchStep = Assert.Single(report.Steps, s => s.Kind == StepKind.SwitchDefault);
        Assert.Equal("Switch to Node.js 24 LTS", switchStep.Title);
        Assert.Equal(["nvm install '24'", "nvm use '24'"], switchStep.Commands);
        Assert.Contains(report.Steps, s => s.Kind == StepKind.Remove && s.Commands.SequenceEqual(["nvm uninstall '16.20.2'"]));
        RuntimeStep volta = Assert.Single(report.Steps, s => s.Title.Contains("Volta", StringComparison.Ordinal));
        Assert.Contains("SendToRecycleBin", volta.Commands[0], StringComparison.Ordinal);
        Assert.False(volta.NeedsAdmin);
        Assert.Equal(["fnm install '22'", "fnm default '22'"], Assert.Single(report.Steps, s => s.Kind == StepKind.Upgrade).Commands);
        Assert.Equal(report.Steps.Count, report.Steps.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Ending_within_120_days_is_a_warning_and_older_than_every_line_is_end_of_life()
    {
        static Installation Go(string v) => new() { Version = v, Cycle = VersionTextCycle(v), Path = $@"C:\go{v}", Source = InstallSource.GoOrg, Product = "go" };
        RuntimeReport report = Build(new GoProbe(), [Go("1.24.2"), Go("1.25.3"), Go("1.23.4"), Go("1.19.13")], "go", EolFixtures.Go);

        Assert.Equal(SupportStatus.EndingSoon, report.Installs.Single(i => i.Version == "1.24.2").Support);
        Assert.Contains(report.Issues, i => i.Level == IssueLevel.Warning && i.Text.Contains("support ends 15 Jan 2027 (in 72 days)", StringComparison.Ordinal));
        Assert.Equal(SupportStatus.Supported, report.Installs.Single(i => i.Version == "1.25.3").Support);
        Assert.Equal(SupportStatus.EndOfLife, report.Installs.Single(i => i.Version == "1.23.4").Support);
        Assert.Equal(SupportStatus.EndOfLife, report.Installs.Single(i => i.Version == "1.19.13").Support);
        Assert.Contains(report.Steps, s => s.Commands.Contains("Start-Process 'ms-settings:appsfeatures'"));
    }

    private static string VersionTextCycle(string v) => DevSweep.Core.Health.Common.VersionText.MajorMinor(v);

    [Fact]
    public void The_same_version_from_two_managers_is_a_duplicate()
    {
        RuntimeReport report = Build(new NodeProbe(), [Node("20.11.0", InstallSource.Nvm), Node("20.11.0", InstallSource.Scoop, detail: "nodejs-lts")], "nodejs", EolFixtures.NodeJs);

        Assert.Contains(report.Issues, i => i.Text == "Node.js 20.11.0 is installed more than once (nvm-windows and Scoop).");
        Assert.Contains(report.Steps, s => s.Commands.SequenceEqual(["scoop cleanup 'nodejs-lts'"]));
    }

    [Fact]
    public void Removal_commands_per_owner_and_admin_only_where_machine_wide()
    {
        static FixCommand? Remove(InstallSource source, string? detail = null, string path = @"C:\Users\Dev\x", string? code = null, string? winget = null)
            => FixCommands.Remove(new Installation { Version = "3.8.10", Cycle = "3.8", Path = path, Source = source, SourceDetail = detail, ProductCode = code, WingetId = winget }, "python", @"C:\Users\Dev");

        Assert.Equal(["pyenv uninstall '3.8.10'"], Remove(InstallSource.Pyenv)!.Commands);
        Assert.Equal(["uv python uninstall '3.8.10'"], Remove(InstallSource.Uv)!.Commands);
        Assert.Equal(["py uninstall '3.8'"], Remove(InstallSource.PythonInstallManager)!.Commands);
        Assert.Equal(["Get-AppxPackage -Name 'PythonSoftwareFoundation.Python.3.8' | Remove-AppxPackage"], Remove(InstallSource.StorePython, "PythonSoftwareFoundation.Python.3.8_qbz5n2kfra8p0")!.Commands);
        Assert.Equal(["conda env remove --prefix 'C:\\Users\\Dev\\x' --yes"], Remove(InstallSource.Conda, "env ml")!.Commands);
        Assert.Null(Remove(InstallSource.Conda, "base"));
        FixCommand choco = Remove(InstallSource.Chocolatey, "python38")!;
        Assert.Equal(["choco uninstall 'python38' -y"], choco.Commands);
        Assert.True(choco.NeedsAdmin);
        Assert.Equal(["winget uninstall --id 'Python.Python.3.8' --exact"], Remove(InstallSource.PythonOrg, winget: "Python.Python.3.8")!.Commands);
        FixCommand msi = Remove(InstallSource.PythonOrg, path: @"C:\Program Files\Python38", code: "11111111-2222-3333-4444-555555555555")!;
        Assert.True(msi.NeedsAdmin);
        Assert.Contains("'{11111111-2222-3333-4444-555555555555}'", msi.Commands[0], StringComparison.Ordinal);
        Assert.Null(Remove(InstallSource.IdeBundled));
        Assert.Null(Remove(InstallSource.ToolCache));
        Assert.True(Remove(InstallSource.SdkFolder, path: @"D:\sdks\x")!.NeedsAdmin);

        Assert.Equal(["winget install --id 'Python.Python.3.13' --exact"], FixCommands.Install("python", "3.13", InstallSource.PythonOrg)!.Commands);
        Assert.Equal(["choco install 'python313' -y"], FixCommands.Install("python", "3.13", InstallSource.Chocolatey)!.Commands);
        Assert.Equal(["rustup update stable", "rustup default stable"], FixCommands.Install("rust", "1.87", InstallSource.Rustup)!.Commands);
        Assert.Equal("Microsoft.OpenJDK.21", FixCommands.WingetId("java", "21", new Installation { Version = "21.0.2", Cycle = "21", Path = "x", Source = InstallSource.JdkInstaller, Product = "microsoft-build-of-openjdk" }));
    }

    [Fact]
    public void The_fix_window_lists_the_commands_and_waits_for_enter_before_running_anything()
    {
        var step = new RuntimeStep
        {
            Id = "node:remove:1",
            Kind = StepKind.Remove,
            Title = "Remove Node.js 16.20.2 (nvm-windows)",
            Detail = "It's out of support.",
            Commands = ["nvm uninstall '16.20.2'", "Write-Output 'it''s done'"],
        };

        string script = TerminalScript.Build(step);
        int listed = script.IndexOf("Write-Host '  > nvm uninstall ''16.20.2'''", StringComparison.Ordinal);
        int wait = script.IndexOf("Read-Host 'Press Enter to run them", StringComparison.Ordinal);
        int run = script.IndexOf("\n    nvm uninstall '16.20.2'", StringComparison.Ordinal);

        Assert.True(listed > 0 && wait > listed && run > wait, script);
        Assert.Contains("if ($LASTEXITCODE -ne 0) { throw", script, StringComparison.Ordinal);
        Assert.DoesNotContain("RunAs", script, StringComparison.Ordinal);
        Assert.Contains("GetEnvironmentVariable('Path', 'Machine')", script, StringComparison.Ordinal);

        string admin = TerminalScript.Build(step with { NeedsAdmin = true });
        int explain = admin.IndexOf("need an administrator", StringComparison.Ordinal);
        Assert.True(explain > 0 && explain < admin.IndexOf("Read-Host 'Press Enter", StringComparison.Ordinal));
        Assert.Contains("-Verb RunAs", admin, StringComparison.Ordinal);
        Assert.Contains("if ($answer -eq 'A')", admin, StringComparison.Ordinal);

        string folder = Path.Join(Path.GetTempPath(), "devsweep-script-unit-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            string path = TerminalScript.Write(step, folder);
            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
            Assert.EndsWith("node-remove-1.ps1", path, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Release_cycles_parse_dates_booleans_and_numbers()
    {
        IReadOnlyList<ReleaseCycle> go = EolFixtures.Cycles(EolFixtures.Go);
        Assert.Equal(["1.25", "1.24", "1.23"], go.Select(c => c.Cycle));
        Assert.Equal(SupportStatus.Supported, go[0].Status(EolFixtures.Today));
        Assert.Equal(SupportStatus.EndOfLife, go[2].Status(EolFixtures.Today));

        IReadOnlyList<ReleaseCycle> node = EolFixtures.Cycles(EolFixtures.NodeJs);
        Assert.False(node[0].IsLts(EolFixtures.Today));
        Assert.True(node[1].IsLts(EolFixtures.Today));
        Assert.False(node[1].IsLts(new DateOnly(2025, 10, 1)));
        Assert.Null(ReleaseCycle.ParseList("not json"u8));
        Assert.Null(ReleaseCycle.ParseList("{}"u8));
    }

    [Fact]
    public async Task Support_data_is_cached_for_a_day_and_used_offline_when_the_network_fails()
    {
        string cache = Path.Join(Path.GetTempPath(), "devsweep-eol-unit-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            var recorded = new EolFixtures.Recorded();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var first = new EndOfLifeClient(cache, recorded, () => now);
            EolData fetched = await first.CyclesAsync("nodejs", TestContext.Current.CancellationToken);
            Assert.Equal(6, fetched.Cycles.Count);
            Assert.False(fetched.Stale);
            Assert.True(File.Exists(Path.Join(cache, "nodejs.json")));

            var again = new EndOfLifeClient(cache, recorded, () => now.AddHours(2));
            Assert.Equal(6, (await again.CyclesAsync("nodejs", TestContext.Current.CancellationToken)).Cycles.Count);
            Assert.Single(recorded.Requests); // fresh cache: no download

            var offline = new EndOfLifeClient(cache, OfflineEolFetcher.Instance, () => now.AddDays(3));
            EolData stale = await offline.CyclesAsync("nodejs", TestContext.Current.CancellationToken);
            Assert.Equal(6, stale.Cycles.Count);
            Assert.True(stale.Stale);
            Assert.True(offline.UsedStaleData);

            EolData none = await offline.CyclesAsync("python", TestContext.Current.CancellationToken);
            Assert.Empty(none.Cycles);
            Assert.True(none.Stale);

            Assert.Empty((await first.CyclesAsync("../etc/passwd", TestContext.Current.CancellationToken)).Cycles);
            Assert.DoesNotContain("../etc/passwd", recorded.Requests);

            File.WriteAllText(Path.Join(cache, "go.json"), "garbage", Encoding.UTF8);
            File.SetLastWriteTimeUtc(Path.Join(cache, "go.json"), now.UtcDateTime);
            Assert.Equal(3, (await new EndOfLifeClient(cache, recorded, () => now).CyclesAsync("go", TestContext.Current.CancellationToken)).Cycles.Count);
        }
        finally
        {
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_whole_scan_works_offline_and_with_recorded_support_data()
    {
        using var machine = new HealthMachine();
        machine.File(Path.Join(machine.RoamingAppData, "nvm", "v16.20.2", "node.exe"));
        string projects = Path.Join(machine.Profile, "source");
        machine.File(Path.Join(projects, "web", ".nvmrc"), "16\n");
        machine.File(Path.Join(projects, "api", "package.json"), """{"engines":{"node":">=22"}}""");

        VersionsResult offline = await new RuntimeHealth().ScanAsync(machine.Context(), new EndOfLifeClient(null, OfflineEolFetcher.Instance), [projects], new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(offline.EolOffline);
        Assert.Equal(SupportStatus.Unknown, Assert.Single(offline.Runtimes).Installs[0].Support);

        VersionsResult online = await new RuntimeHealth().ScanAsync(machine.Context(), new EndOfLifeClient(null, new EolFixtures.Recorded()), [projects], new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(SupportStatus.EndOfLife, Assert.Single(online.Runtimes).Installs[0].Support);
        Assert.Equal(2, online.Projects.Count);
        ProjectRequirement web = Assert.Single(online.Projects, p => p.Project == "web");
        Assert.Equal(RequirementStatus.EndOfLife, web.Status);
        ProjectRequirement api = Assert.Single(online.Projects, p => p.Project == "api");
        Assert.Equal(RequirementStatus.NotInstalled, api.Status);
        Assert.Equal(["nvm install '22'", "nvm use '22'"], api.Fix);
    }
}
