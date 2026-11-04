using DevSweep.Core.Inventory;
using DevSweep.Core.Locations;
using DevSweep.Core.Modules;
using DevSweep.Fixtures;
using Xunit;

namespace DevSweep.Accuracy;

/// <summary>The real inventory of the machine the suite runs on (the CI runner, as administrator and as a standard user).</summary>
public sealed class InventorySmokeTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempFixture _fixture = new("inventory");

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Every_source_reports_and_the_machine_wide_registry_is_read()
    {
        DevSweepEngine engine = DevSweepEngine.CreateDefault();

        AppInventory inventory = engine.BuildInventory(cancellationToken: TestContext.Current.CancellationToken);

        foreach (SourceReport source in inventory.Sources)
        {
            output.WriteLine($"{source.Source,-20} {source.Count,5} apps {source.Milliseconds,8:0} ms  {(source.Complete ? "complete" : "INCOMPLETE: " + string.Join("; ", source.Problems))}  [{source.Where}]");
        }

        output.WriteLine($"{inventory.Apps.Count} apps merged from {inventory.Records.Count} records");
        Assert.Equal(12, inventory.Sources.Count);
        SourceReport machine = inventory.Sources.Single(s => s.Source == InventorySource.UninstallMachine64);
        Assert.True(machine.Complete, string.Join("; ", machine.Problems));
        Assert.True(machine.Count > 0);
        Assert.True(inventory.Sources.Single(s => s.Source == InventorySource.RunningProcesses).Count > 0);
        Assert.True(inventory.Apps.Count > 10);
    }

    [Fact]
    public void Git_on_the_runner_is_installed_and_a_made_up_app_is_not()
    {
        AppInventory inventory = DevSweepEngine.CreateDefault().BuildInventory(cancellationToken: TestContext.Current.CancellationToken);

        InstallMatch git = inventory.IsInstalled(new AppHint { Name = "Git", Names = ["Git"], Exes = ["git.exe"] });
        output.WriteLine($"Git: {git.State}: {string.Join("; ", git.Evidence.Select(e => $"{e.Source} {e.MatchedBy}"))}");
        Assert.Equal(InstallState.Installed, git.State);

        InstallMatch nothing = inventory.IsInstalled(new AppHint { Name = "Nothing", Names = ["DevSweep Imaginary App 9000"] });
        output.WriteLine($"Imaginary: {nothing.State} {nothing.UnknownBecause}");
        Assert.NotEqual(InstallState.Installed, nothing.State);
    }

    [Fact]
    public void Shortcuts_windows_writes_are_read_without_com()
    {
        string target = Path.Join(Environment.SystemDirectory, "notepad.exe");
        string link = _fixture.PathOf("Notepad shortcut.lnk");
        string script = $"$s = (New-Object -ComObject WScript.Shell).CreateShortcut('{link}'); $s.TargetPath = '{target}'; $s.Arguments = '/A x.txt'; $s.Save()";
        (int exit, string text) = Ntfs.Run("powershell.exe", $"-NoProfile -NonInteractive -Command \"{script}\"");
        Assert.True(exit == 0, text);

        ShellLinkInfo? info = ShellLink.Read(link);

        Assert.Equal(target, info?.Target, ignoreCase: true);
        Assert.False(info!.IsAdvertised);
    }

    [Fact]
    public void Real_known_folders_and_tool_locations_resolve()
    {
        LocationRegistry locations = DevSweepEngine.CreateDefault().CreateLocations();

        foreach (ResolvedLocation location in locations.ResolveAll())
        {
            output.WriteLine(location.Describe());
        }

        Assert.Equal(LocationSource.KnownFolder, locations.Resolve("LocalAppData").Source);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), locations.Resolve("LocalAppData").Path, ignoreCase: true);
        Assert.NotNull(locations.Resolve("ProgramData").Path);
        Assert.NotNull(locations.Resolve("npmCache").Path);
    }
}
