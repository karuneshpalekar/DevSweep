using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Places;
using DevSweep.App.ViewModels.Sample;
using DevSweep.App.ViewModels.Settings;
using DevSweep.Core.Health;
using DevSweep.Core.Health.Packages;
using DevSweep.Core.Health.Ports;
using DevSweep.Core.Health.Runtimes;
using DevSweep.Core.Health.Security;
using DevSweep.Core.Modules;
using Xunit;

namespace DevSweep.App.Tests;

/// <summary>A Health module that answers from sample data and records what the view model asked of it.</summary>
internal sealed class FakeHealth : IHealthSource
{
    public HealthFeatures Features { get; } = new(true, true, true);

    public List<string> Calls { get; } = [];

    public StopOutcome FirstStop { get; set; } = StopOutcome.StillRunning;

    public HealthReport Report { get; set; } = SampleData.Health;

    public Task<HealthReport> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Report);

    public void OpenFix(RuntimeStep fix) => Calls.Add("run:" + fix.Id);

    public Task<PackagesResult?> PackagesAsync(VersionsResult? versions, CancellationToken cancellationToken) => Task.FromResult<PackagesResult?>(SampleData.Packages);

    public Task<PortsResult?> PortsAsync(CancellationToken cancellationToken)
    {
        Calls.Add("ports");
        return Task.FromResult(Report.Ports);
    }

    public StopResult? StopRefusal(ListeningPort port) => port.OwnedByYou == false ? new StopResult(StopOutcome.NotYours, "Windows runs it.") : null;

    public Task<StopResult> StopAsync(ListeningPort port, bool force, CancellationToken cancellationToken)
    {
        Calls.Add((force ? "end:" : "ask:") + port.Port);
        if (force)
        {
            Report = Report with { Ports = Report.Ports! with { Ports = [.. Report.Ports.Ports.Where(p => p.Id != port.Id)] } };
            return Task.FromResult(new StopResult(StopOutcome.Stopped, "node was ended."));
        }

        return Task.FromResult(new StopResult(FirstStop, FirstStop == StopOutcome.StillRunning ? "node is still running." : "node stopped."));
    }

    public string? TrashBlockedReason(SecurityFinding finding) => finding.Path.Contains("OneDrive", StringComparison.Ordinal) ? "Online-only." : null;

    public Task<HealthActionResult> TrashAsync(SecurityFinding finding, CancellationToken cancellationToken)
    {
        Calls.Add("trash:" + finding.Title);
        return Task.FromResult(new HealthActionResult(true, "Moved."));
    }
}

public sealed class HealthViewModelTests
{
    private static async Task<(HealthViewModel Vm, FakeHealth Source, AppSettingsStore Settings, List<string> Flags)> Ready()
    {
        var source = new FakeHealth();
        var settings = new AppSettingsStore(null);
        var flags = new List<string>();
        var vm = new HealthViewModel(source, settings, (id, on) => flags.Add($"{id}={on}"), () => SampleData.Now);
        await vm.LoadAsync();
        await vm.PackagesTask;
        return (vm, source, settings, flags);
    }

    [Fact]
    public async Task The_tools_list_has_filters_counts_and_a_summary()
    {
        (HealthViewModel vm, _, _, _) = await Ready();
        Assert.Equal(vm.AllToolRows.Count, vm.FilterCount(ToolsFilter.All));
        Assert.Equal(vm.FilterCount(ToolsFilter.All), vm.FilterCount(ToolsFilter.NeedsAttention) + vm.FilterCount(ToolsFilter.UpToDate));
        Assert.Contains(vm.AllToolRows, r => r.Id == "packages" && r.Kind == ToolKind.Packages && r.NeedsAttention);
        Assert.Contains(vm.AllToolRows, r => r.Id == "projects" && r.Alerts == 2);
        Assert.Contains(vm.AllToolRows, r => r.Id == "go" && !r.NeedsAttention);
        Assert.Equal($"{vm.AllToolRows.Count} tools · {vm.FilterCount(ToolsFilter.NeedsAttention)} need attention", vm.ToolsSummary);

        vm.ToolsFilter = ToolsFilter.NeedsAttention;
        Assert.All(vm.ToolRows, r => Assert.True(r.NeedsAttention));
        vm.ToolsFilter = ToolsFilter.UpToDate;
        Assert.DoesNotContain(vm.ToolRows, r => r.NeedsAttention);
        Assert.Contains(vm.ToolRows, r => r.Id == "go");
    }

    [Fact]
    public async Task Selecting_a_tool_toggles_it_and_a_step_run_is_requested_not_executed_here()
    {
        (HealthViewModel vm, FakeHealth source, _, _) = await Ready();
        vm.SelectTool("node");
        Assert.Equal("node", vm.SelectedToolRow?.Id);
        vm.SelectTool("node");
        Assert.Null(vm.SelectedToolRow);

        RuntimeStep step = vm.AllToolRows.Single(r => r.Id == "node").Report!.Steps[0];
        vm.OpenFix(step);
        Assert.Equal([step.Id], vm.RunRequests);
        Assert.Equal(["run:" + step.Id], source.Calls);
    }

    [Fact]
    public async Task The_package_report_arrives_after_the_rest_and_shows_as_a_row()
    {
        var source = new FakeHealth();
        var vm = new HealthViewModel(source);
        await vm.LoadAsync();
        await vm.PackagesTask;
        Assert.NotNull(vm.Packages);
        Assert.False(vm.IsLoadingPackages);
        Assert.Equal(SampleData.Packages.AttentionCount, vm.AllToolRows.Single(r => r.Id == "packages").Alerts);
    }

    [Fact]
    public async Task Security_ignore_hides_the_finding_and_Settings_can_bring_it_back()
    {
        (HealthViewModel vm, _, AppSettingsStore settings, _) = await Ready();
        SecurityFinding aws = vm.NeedsAction.Single(f => f.Title == "AWS access keys");
        int before = vm.NeedsAction.Count;
        vm.SelectedSecurity = aws.Id;
        vm.Ignore(aws);
        Assert.Equal(before - 1, vm.NeedsAction.Count);
        Assert.Null(vm.SelectedSecurity);
        Assert.Contains(settings.Current.Ignored, i => i.Id == aws.Id);
        Assert.Equal(1, vm.IgnoredFindings);
        settings.Update(s => s with { Ignored = [] });
        Assert.Contains(vm.NeedsAction, f => f.Id == aws.Id);
    }

    [Fact]
    public async Task Move_to_the_Recycle_Bin_goes_through_the_source_and_is_blocked_with_a_reason_for_cloud_files_and_env_files()
    {
        (HealthViewModel vm, FakeHealth source, _, _) = await Ready();
        SecurityFinding cloud = vm.NeedsAction.Single(f => f.Path.Contains("OneDrive", StringComparison.Ordinal));
        Assert.Equal("Online-only.", vm.TrashBlockedReason(cloud));
        await vm.TrashAsync(cloud);
        Assert.DoesNotContain(source.Calls, c => c.StartsWith("trash:", StringComparison.Ordinal));
        Assert.Equal("Online-only.", vm.Notice);

        SecurityFinding env = vm.NeedsAction.First(f => f.Kind == SecurityKind.EnvFile);
        Assert.Contains(".env", vm.TrashBlockedReason(env), StringComparison.Ordinal);

        SecurityFinding aws = vm.NeedsAction.Single(f => f.Title == "AWS access keys");
        await vm.TrashAsync(aws);
        Assert.Contains("trash:AWS access keys", source.Calls);
        Assert.DoesNotContain(vm.Report!.Security!.Findings, f => f.Id == aws.Id);
    }

    [Fact]
    public async Task Stop_asks_first_then_waits_for_a_confirmation_before_ending()
    {
        (HealthViewModel vm, FakeHealth source, _, _) = await Ready();
        ListeningPort port = vm.DevelopmentPorts.First(p => p.Port == 5173);
        await vm.EndAsync(port);
        Assert.DoesNotContain(source.Calls, c => c.StartsWith("end:", StringComparison.Ordinal)); // never ends without having asked

        await vm.StopAsync(port);
        Assert.Equal(StopPhase.ConfirmEnd, vm.Stop);
        Assert.Contains("ask:5173", source.Calls);
        Assert.DoesNotContain("end:5173", source.Calls);

        vm.CancelStop();
        Assert.Equal(StopPhase.None, vm.Stop);
        await vm.StopAsync(port);
        await vm.EndAsync(port);
        Assert.Contains("end:5173", source.Calls);
        Assert.Equal(StopPhase.None, vm.Stop);
        Assert.DoesNotContain(vm.DevelopmentPorts, p => p.Id == port.Id); // re-read after it stopped
    }

    [Fact]
    public async Task A_polite_stop_that_works_needs_no_confirmation_and_system_processes_are_refused_with_a_reason()
    {
        (HealthViewModel vm, FakeHealth source, _, _) = await Ready();
        source.FirstStop = StopOutcome.Stopped;
        await vm.StopAsync(vm.DevelopmentPorts.First(p => p.Port == 3000));
        Assert.Equal(StopPhase.None, vm.Stop);
        Assert.Equal("node stopped.", vm.StopMessage);

        ListeningPort system = vm.OtherPorts.First(p => p.OwnedByYou == false);
        Assert.Equal("Windows runs it.", vm.StopRefusal(system));
        Assert.Null(vm.StopRefusal(vm.DevelopmentPorts[0]));
    }

    [Fact]
    public async Task Ports_refresh_reads_only_the_ports_and_drops_a_selection_that_went_away()
    {
        (HealthViewModel vm, FakeHealth source, _, _) = await Ready();
        ListeningPort port = vm.DevelopmentPorts[0];
        vm.SelectedPort = port.Id;
        source.Report = source.Report with { Ports = source.Report.Ports! with { Ports = [.. source.Report.Ports.Ports.Where(p => p.Id != port.Id)] } };
        await vm.RefreshPortsAsync();
        Assert.Contains("ports", source.Calls);
        Assert.Null(vm.SelectedPort);
        Assert.DoesNotContain(vm.DevelopmentPorts, p => p.Id == port.Id);
    }

    [Fact]
    public void Only_development_web_ports_get_an_Open_in_browser_address()
    {
        ListeningPort Make(int port, string process, bool dev) => SampleData.Health.Ports!.Ports[0] with { Port = port, ProcessName = process, IsDevelopment = dev };
        Assert.Equal("http://localhost:5173", HealthViewModel.UrlFor(Make(5173, "node", true)));
        Assert.Equal("https://localhost:5001", HealthViewModel.UrlFor(Make(5001, "dotnet", true)));
        Assert.Null(HealthViewModel.UrlFor(Make(5432, "postgres", true)));
        Assert.Null(HealthViewModel.UrlFor(Make(6379, "redis-server", true)));
        Assert.Null(HealthViewModel.UrlFor(Make(5040, "svchost", false)));
    }

    [Fact]
    public async Task The_off_state_can_be_enabled_in_one_click_and_says_a_restart_applies_it()
    {
        var flags = new List<string>();
        var vm = new HealthViewModel(null, new AppSettingsStore(null), (id, on) => flags.Add($"{id}={on}"));
        Assert.Equal(PlaceState.Unavailable, vm.State);
        Assert.True(vm.CanEnable);
        vm.Enable();
        Assert.Equal(["health=True", "health.runtimes=True", "health.security=True", "health.ports=True"], flags);
        Assert.True(vm.RestartNeeded);
        await vm.LoadAsync();
        Assert.Equal(PlaceState.Unavailable, vm.State);
    }

    [Fact]
    public async Task Tab_counts_and_the_checked_text_follow_the_data()
    {
        (HealthViewModel vm, _, _, _) = await Ready();
        Assert.NotEqual(string.Empty, vm.Count(HealthTab.Security));
        Assert.StartsWith("Checked ", vm.CheckedText, StringComparison.Ordinal);
        Assert.EndsWith("support dates from endoflife.date", vm.CheckedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Home_attention_row_opens_the_item_it_is_about()
    {
        var backend = new SampleBackend();
        var model = new MainViewModel(backend, new AppSettingsStore(null));
        await model.Health.LoadAsync();
        model.Home.Refresh();
        AttentionRow security = model.Home.Attention.First(a => a.Tag == "Security");
        Assert.StartsWith("security:", security.Item, StringComparison.Ordinal);
        model.OpenAttention(security);
        Assert.Equal("health", model.Current);
        Assert.Equal(HealthTab.Security, model.Health.Tab);
        Assert.Equal(security.Item!["security:".Length..], model.Health.SelectedSecurity);

        AttentionRow tool = model.Home.Attention.First(a => a.Tag == "Health");
        model.OpenAttention(tool);
        Assert.Equal(HealthTab.Tools, model.Health.Tab);
        Assert.Equal(tool.Item!["tool:".Length..], model.Health.SelectedTool);

        AttentionRow port = model.Home.Attention.First(a => a.Tag == "Ports");
        model.OpenAttention(port);
        Assert.Equal(HealthTab.Ports, model.Health.Tab);
        Assert.NotNull(model.Health.SelectedPortInfo);
    }
}

public sealed class RealStopTests
{
    [Fact]
    [Trait("Category", "Windows")]
    public async Task A_dummy_process_is_ended_through_the_real_source_but_only_when_it_is_the_program_that_was_listed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var engine = DevSweepEngine.CreateDefault(FeatureFlags.Default);
        CoreHealthSource source = CoreHealthSource.From(engine, () => null)!;
        using System.Diagnostics.Process dummy = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ping.exe", "-n 120 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            ListeningPort port = SampleData.Health.Ports!.Ports[0] with { Pid = dummy.Id, ProcessName = "PING", Port = 49999, OwnedByYou = true };
            ListeningPort wrongName = port with { ProcessName = "notping" };

            StopResult stale = await source.StopAsync(wrongName, force: true, TestContext.Current.CancellationToken);
            Assert.Equal(StopOutcome.NotFound, stale.Outcome);
            Assert.False(dummy.HasExited);

            StopResult ended = await source.StopAsync(port, force: true, TestContext.Current.CancellationToken);
            Assert.Equal(StopOutcome.Stopped, ended.Outcome);
            Assert.True(dummy.WaitForExit(5000));
        }
        finally
        {
            if (!dummy.HasExited)
            {
                dummy.Kill();
            }
        }
    }
}
