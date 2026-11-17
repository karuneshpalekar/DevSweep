using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevSweep.App.ViewModels.Places;
using DevSweep.App.ViewModels.Settings;
using DevSweep.App.ViewModels.Text;
using DevSweep.Core.Health.Packages;
using DevSweep.Core.Health.Ports;
using DevSweep.Core.Health.Runtimes;
using DevSweep.Core.Health.Security;

namespace DevSweep.App.ViewModels;

/// <summary>The "Show" chips of Tools and versions.</summary>
public enum ToolsFilter
{
    All,
    NeedsAttention,
    UpToDate,
}

/// <summary>What a row of the tools list is.</summary>
public enum ToolKind
{
    Runtime,
    Windows,
    Packages,
    Projects,
}

/// <summary>One row of the tools list (the Mac's runtime row).</summary>
/// <param name="Id">"node", "windows", "packages", "projects": also the side panel's key.</param>
/// <param name="Alerts">Things to look at (Info notes don't count).</param>
/// <param name="Critical">At least one of them is critical.</param>
public sealed record ToolRow(string Id, string Title, ToolKind Kind, string Summary, int Alerts, bool Critical, RuntimeReport? Report = null)
{
    public bool NeedsAttention => Alerts > 0;
}

/// <summary>Where a Stop is.</summary>
public enum StopPhase
{
    None,

    /// <summary>The program is being asked to stop (Ctrl+C or close its window).</summary>
    Asking,

    /// <summary>It ignored the request: the user decides whether to end it.</summary>
    ConfirmEnd,
}

/// <summary>What an action on a security finding did.</summary>
public sealed record HealthActionResult(bool Succeeded, string Message);

/// <summary>
/// Health: Tools and versions (master list, side panel, package-manager report), Security (needs action / looks fine, panel with
/// actions) and Ports (development vs others, Stop with a confirm). Binds to M3b's result types through <see cref="IHealthSource"/>.
/// </summary>
/// <param name="source">The Health module; null when it is off or not in this build.</param>
/// <param name="settings">Where ignored items are kept (Settings, Ignored items).</param>
/// <param name="setModule">Turns a flag on or off for the next start (<c>features.json</c>).</param>
public sealed partial class HealthViewModel(IHealthSource? source, AppSettingsStore? settings = null, Action<string, bool>? setModule = null) : ObservableObject
{
    private int _portsBusy;

    [ObservableProperty]
    public partial PlaceState State { get; set; } = source is null ? PlaceState.Unavailable : PlaceState.NotChecked;

    [ObservableProperty]
    public partial HealthTab Tab { get; set; }

    [ObservableProperty]
    public partial HealthReport? Report { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>The package-manager report; null until it has been read (it is slower than the rest).</summary>
    [ObservableProperty]
    public partial PackagesResult? Packages { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingPackages { get; set; }

    [ObservableProperty]
    public partial ToolsFilter ToolsFilter { get; set; }

    /// <summary>The tool (or "packages", "projects") whose panel is open.</summary>
    [ObservableProperty]
    public partial string? SelectedTool { get; set; }

    [ObservableProperty]
    public partial string? SelectedSecurity { get; set; }

    [ObservableProperty]
    public partial string? SelectedPort { get; set; }

    /// <summary>The last thing an action did ("Moved to the Recycle Bin"), shown in the panel.</summary>
    [ObservableProperty]
    public partial string? Notice { get; set; }

    [ObservableProperty]
    public partial StopPhase Stop { get; set; }

    [ObservableProperty]
    public partial string? StopMessage { get; set; }

    /// <summary>"Health will be on from the next start": set after the one-click enable.</summary>
    [ObservableProperty]
    public partial bool RestartNeeded { get; set; }

    [ObservableProperty]
    public partial bool IsRefreshingPorts { get; set; }

    public IHealthSource? Source => source;

    /// <summary>The last package-report load (tests and the screenshot tour wait on it).</summary>
    public Task PackagesTask { get; private set; } = Task.CompletedTask;

    /// <summary>The steps the user asked to run, in order (the fake source in tests also records them).</summary>
    public List<string> RunRequests { get; } = [];

    /// <summary>The tabs whose flags are on.</summary>
    public IReadOnlyList<HealthTab> Tabs => source is null ? [HealthTab.Tools, HealthTab.Security, HealthTab.Ports]
        : [.. ((HealthTab?[])[source.Features.Runtimes ? HealthTab.Tools : null, source.Features.Security ? HealthTab.Security : null, source.Features.Ports ? HealthTab.Ports : null]).OfType<HealthTab>()];

    public static string TabTitle(HealthTab tab) => tab switch
    {
        HealthTab.Tools => "Tools and versions",
        HealthTab.Security => "Security",
        _ => "Ports",
    };

    public static string UnavailableText => "Health is turned off. It shows every install of your languages and databases, which one a new terminal runs and whether it's still supported, files that look like secrets, and what's listening on your ports.";

    public bool CanEnable => setModule is not null;

    /// <summary>Turns Health and its three tabs on in <c>features.json</c>. They apply the next time DevSweep starts.</summary>
    public void Enable()
    {
        if (setModule is null)
        {
            return;
        }

        setModule("health", true);
        foreach (string tab in (ReadOnlySpan<string>)["health.runtimes", "health.security", "health.ports"])
        {
            setModule(tab, true);
        }

        RestartNeeded = true;
    }

    // ---- Tools and versions -------------------------------------------------------------------------------------------------

    private IReadOnlyList<string> IgnoredIds => settings is null ? [] : [.. settings.Current.Ignored.Select(i => i.Id)];

    /// <summary>Every row of the tools list, filter aside.</summary>
    public IReadOnlyList<ToolRow> AllToolRows
    {
        get
        {
            var rows = new List<ToolRow>();
            if (Report?.Versions is not { } versions)
            {
                return rows;
            }

            foreach (RuntimeReport tool in versions.Runtimes.Where(r => r.Installs.Count > 0 || r.NotInstalls.Count > 0))
            {
                rows.Add(Row(tool, ToolKind.Runtime));
            }

            if (Packages is { Managers.Count: > 0 } packages)
            {
                int alerts = packages.AttentionCount;
                rows.Add(new ToolRow("packages", "Package managers", ToolKind.Packages, PackagesSummary(packages), alerts, packages.Deprecated > 0));
            }

            if (versions.Windows is { } windows)
            {
                rows.Add(Row(windows, ToolKind.Windows));
            }

            if (versions.Projects.Count > 0)
            {
                int problems = versions.Projects.Count(p => p.Status != RequirementStatus.Ok);
                int names = versions.Projects.Select(p => p.Project).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                rows.Add(new ToolRow("projects", "Your projects", ToolKind.Projects,
                    $"{Words.Count(names, "project")} · {Words.Count(versions.Projects.Count, "requirement")}", problems, versions.Projects.Any(p => p.Status == RequirementStatus.EndOfLife)));
            }

            return rows;
        }
    }

    public IReadOnlyList<ToolRow> ToolRows => [.. AllToolRows.Where(r => ToolsFilter switch
    {
        ToolsFilter.NeedsAttention => r.NeedsAttention,
        ToolsFilter.UpToDate => !r.NeedsAttention,
        _ => true,
    })];

    public int FilterCount(ToolsFilter filter) => AllToolRows.Count(r => filter switch
    {
        ToolsFilter.NeedsAttention => r.NeedsAttention,
        ToolsFilter.UpToDate => !r.NeedsAttention,
        _ => true,
    });

    /// <summary>"9 tools · 3 need attention".</summary>
    public string ToolsSummary
    {
        get
        {
            int all = FilterCount(ToolsFilter.All);
            int need = FilterCount(ToolsFilter.NeedsAttention);
            string tools = Words.Count(all, "tool");
            return need == 0 ? tools + " · all up to date" : $"{tools} · {need} need{(need == 1 ? "s" : string.Empty)} attention";
        }
    }

    /// <summary>"Checked 12:38 PM · support dates from endoflife.date", or the offline note.</summary>
    public string CheckedText
    {
        get
        {
            if (State == PlaceState.Loading)
            {
                return "Checking…";
            }

            if (Report is null)
            {
                return "Not checked yet";
            }

            DateTimeOffset when = Report.Versions?.Date ?? Report.Security?.Date ?? Report.Ports?.Date ?? DateTimeOffset.Now;
            string time = when.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
            return Report.Versions is { EolOffline: true } ? $"Checked {time} · support dates may be out of date (offline)" : Tab == HealthTab.Tools ? $"Checked {time} · support dates from endoflife.date" : $"Checked {time}";
        }
    }

    public ToolRow? SelectedToolRow => SelectedTool is null ? null : AllToolRows.FirstOrDefault(r => r.Id == SelectedTool);

    private static ToolRow Row(RuntimeReport tool, ToolKind kind)
    {
        RuntimeIssue[] alerts = [.. tool.Issues.Where(i => i.Level != IssueLevel.Info)];
        string summary = kind == ToolKind.Windows && tool.Installs.Count > 0
            ? "This PC · " + tool.Installs[0].Version
            : tool.Installs.Any(i => i.IsRunning is not null)
                ? $"{tool.Installs.Count} installed · {tool.Installs.Count(i => i.IsRunning == true)} running"
                : $"{tool.Installs.Count} installed";
        return new ToolRow(tool.Id, tool.Name, kind, summary, alerts.Length, alerts.Any(i => i.Level == IssueLevel.Critical), tool);
    }

    private static string PackagesSummary(PackagesResult packages)
    {
        string names = string.Join(", ", packages.Managers.Select(m => m.Name));
        return $"{names} · {packages.Outdated} outdated · {packages.Deprecated} no longer offered";
    }

    public void SelectTool(string? id) => SelectedTool = SelectedTool == id ? null : id;

    /// <summary>Runs a step the user chose: writes the script and opens it in a visible PowerShell window that waits for Enter.</summary>
    public void OpenFix(RuntimeStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        RunRequests.Add(step.Id);
        try
        {
            source?.OpenFix(step);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Error = "Couldn't open the fix window: " + ex.Message;
        }
    }

    // ---- Security -----------------------------------------------------------------------------------------------------------

    public IReadOnlyList<SecurityFinding> NeedsAction => Report?.Security is { } s ? [.. s.Findings.Where(f => f.Level != SecurityLevel.Ok && !IgnoredIds.Contains(f.Id))] : [];

    public IReadOnlyList<SecurityFinding> LooksFine => Report?.Security is { } s ? [.. s.Findings.Where(f => f.Level == SecurityLevel.Ok && !IgnoredIds.Contains(f.Id))] : [];

    public SecurityFinding? SelectedFinding => SelectedSecurity is null ? null : Report?.Security?.Findings.FirstOrDefault(f => f.Id == SelectedSecurity);

    public int IgnoredFindings => Report?.Security is { } s ? s.Findings.Count(f => IgnoredIds.Contains(f.Id)) : 0;

    /// <summary>Why "Move to Recycle Bin" is not offered or not possible for this finding, or null when it is.</summary>
    public string? TrashBlockedReason(SecurityFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (finding.Kind == SecurityKind.EnvFile)
        {
            return "A .env file belongs to its project, so DevSweep doesn't remove it. Use the steps above to keep it out of git.";
        }

        return source?.TrashBlockedReason(finding);
    }

    /// <summary>Moves the file to the Recycle Bin through the cleaner (logged in History); leaves it out of the list when it worked.</summary>
    public async Task TrashAsync(SecurityFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (TrashBlockedReason(finding) is { } reason)
        {
            Notice = reason;
            return;
        }

        if (source is null)
        {
            return;
        }

        HealthActionResult result = await source.TrashAsync(finding, CancellationToken.None).ConfigureAwait(true);
        Notice = result.Message;
        if (result.Succeeded && Report?.Security is { } security)
        {
            SelectedSecurity = null;
            Report = Report with { Security = security with { Findings = [.. security.Findings.Where(f => f.Id != finding.Id)] } };
        }
    }

    /// <summary>Leaves the finding out from now on (Settings, Ignored items brings it back).</summary>
    public void Ignore(SecurityFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        settings?.Update(s => s with { Ignored = [.. s.Ignored.Where(i => i.Id != finding.Id), new IgnoredItem(finding.Id, finding.Title)] });
        if (SelectedSecurity == finding.Id)
        {
            SelectedSecurity = null;
        }

        OnPropertyChanged(nameof(NeedsAction));
        OnPropertyChanged(nameof(LooksFine));
        OnPropertyChanged(nameof(IgnoredFindings));
    }

    // ---- Ports --------------------------------------------------------------------------------------------------------------

    public IReadOnlyList<ListeningPort> DevelopmentPorts => Report?.Ports is { } p ? [.. p.Ports.Where(x => x.IsDevelopment)] : [];

    public IReadOnlyList<ListeningPort> OtherPorts => Report?.Ports is { } p ? [.. p.Ports.Where(x => !x.IsDevelopment)] : [];

    public ListeningPort? SelectedPortInfo => SelectedPort is null ? null : Report?.Ports?.Ports.FirstOrDefault(p => p.Id == SelectedPort);

    /// <summary>Why DevSweep won't stop this process (Windows' own, another user's), or null when it may.</summary>
    public string? StopRefusal(ListeningPort port)
    {
        ArgumentNullException.ThrowIfNull(port);
        return source?.StopRefusal(port)?.Message;
    }

    /// <summary>Asks the program to stop the way Ctrl+C does; when it ignores that, waits for the user to confirm ending it.</summary>
    public async Task StopAsync(ListeningPort port)
    {
        ArgumentNullException.ThrowIfNull(port);
        if (source is null || Stop == StopPhase.Asking)
        {
            return;
        }

        Stop = StopPhase.Asking;
        StopMessage = $"Asking {port.ProcessName} to stop…";
        StopResult result = await source.StopAsync(port, force: false, CancellationToken.None).ConfigureAwait(true);
        await Finish(result).ConfigureAwait(true);
    }

    /// <summary>Ends the program for good; only after <see cref="StopPhase.ConfirmEnd"/> and the user's confirmation.</summary>
    public async Task EndAsync(ListeningPort port)
    {
        ArgumentNullException.ThrowIfNull(port);
        if (source is null || Stop != StopPhase.ConfirmEnd)
        {
            return;
        }

        Stop = StopPhase.Asking;
        StopMessage = $"Ending {port.ProcessName}…";
        StopResult result = await source.StopAsync(port, force: true, CancellationToken.None).ConfigureAwait(true);
        await Finish(result).ConfigureAwait(true);
    }

    public void CancelStop()
    {
        Stop = StopPhase.None;
        StopMessage = null;
    }

    private async Task Finish(StopResult result)
    {
        StopMessage = result.Message;
        if (result.Outcome == StopOutcome.StillRunning)
        {
            Stop = StopPhase.ConfirmEnd;
            return;
        }

        Stop = StopPhase.None;
        if (result.Outcome is StopOutcome.Stopped or StopOutcome.NotFound)
        {
            await RefreshPortsAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Reads the listeners again (the Ports tab does this every few seconds while it is visible).</summary>
    public async Task RefreshPortsAsync()
    {
        if (source is null || Report is null || !source.Features.Ports || Interlocked.Exchange(ref _portsBusy, 1) == 1)
        {
            return;
        }

        IsRefreshingPorts = true;
        try
        {
            PortsResult? ports = await source.PortsAsync(CancellationToken.None).ConfigureAwait(true);
            if (ports is not null && Report is { } current)
            {
                Report = current with { Ports = ports };
                if (SelectedPort is { } id && ports.Ports.All(p => p.Id != id) && Stop != StopPhase.ConfirmEnd)
                {
                    SelectedPort = null;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsRefreshingPorts = false;
            _ = Interlocked.Exchange(ref _portsBusy, 0);
        }
    }

    // ---- The tab bar --------------------------------------------------------------------------------------------------------

    /// <summary>The number on a tab ("Tools and versions · 9"); empty when there is nothing to look at.</summary>
    public string Count(HealthTab tab) => Report is null ? string.Empty : tab switch
    {
        HealthTab.Tools => Nonzero(Report.Versions?.AttentionCount ?? 0),
        HealthTab.Security => Nonzero(NeedsAction.Count),
        _ => Nonzero(DevelopmentPorts.Count),
    };

    private static string Nonzero(int n) => n > 0 ? n.ToString(CultureInfo.InvariantCulture) : string.Empty;

    partial void OnReportChanged(HealthReport? value)
    {
        OnPropertyChanged(nameof(NeedsAction));
        OnPropertyChanged(nameof(LooksFine));
        OnPropertyChanged(nameof(DevelopmentPorts));
        OnPropertyChanged(nameof(OtherPorts));
        OnPropertyChanged(nameof(AllToolRows));
        OnPropertyChanged(nameof(ToolRows));
        OnPropertyChanged(nameof(ToolsSummary));
        OnPropertyChanged(nameof(CheckedText));
    }

    partial void OnPackagesChanged(PackagesResult? value)
    {
        OnPropertyChanged(nameof(AllToolRows));
        OnPropertyChanged(nameof(ToolRows));
        OnPropertyChanged(nameof(ToolsSummary));
    }

    partial void OnToolsFilterChanged(ToolsFilter value) => OnPropertyChanged(nameof(ToolRows));

    partial void OnStateChanged(PlaceState value) => OnPropertyChanged(nameof(CheckedText));

    partial void OnTabChanged(HealthTab value)
    {
        OnPropertyChanged(nameof(CheckedText));
        CancelStop();
        Notice = null;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (source is null || State == PlaceState.Loading)
        {
            return;
        }

        if (Tabs.Count > 0 && !Tabs.Contains(Tab))
        {
            Tab = Tabs[0];
        }

        State = PlaceState.Loading;
        Error = null;
        try
        {
            Report = await source.LoadAsync(CancellationToken.None).ConfigureAwait(true);
            State = PlaceState.Ready;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            Error = ex.Message;
            State = PlaceState.Failed;
            return;
        }

        PackagesTask = LoadPackagesAsync();
    }

    private async Task LoadPackagesAsync()
    {
        if (source is null || !source.Features.Runtimes)
        {
            return;
        }

        IsLoadingPackages = true;
        try
        {
            Packages = await source.PackagesAsync(Report?.Versions, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            Packages = null;
            Notice = "The package managers couldn't be checked: " + ex.Message;
        }
        finally
        {
            IsLoadingPackages = false;
        }
    }

    /// <summary>The address "Open in browser" uses for a development port that serves web pages; null for databases and the like.</summary>
    public static string? UrlFor(ListeningPort port)
    {
        ArgumentNullException.ThrowIfNull(port);
        if (!port.IsDevelopment || port.Protocol != PortProtocol.Tcp)
        {
            return null;
        }

        string name = port.ProcessName.ToLowerInvariant();
        string[] databases = ["postgres", "mysqld", "mongod", "redis-server", "sqlservr", "docker", "com.docker.backend", "wslrelay", "ollama"];
        int[] databasePorts = [1433, 1521, 3306, 5432, 5672, 6379, 9042, 11211, 27017];
        if (databases.Contains(name) || databasePorts.Contains(port.Port))
        {
            return null;
        }

        string scheme = port.Port is 443 or 8443 or 5001 or 7000 or 7001 ? "https" : "http";
        return string.Create(CultureInfo.InvariantCulture, $"{scheme}://localhost:{port.Port}");
    }
}
