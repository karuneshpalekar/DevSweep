using DevSweep.App.ViewModels.Services;
using DevSweep.App.ViewModels.Settings;
using DevSweep.App.ViewModels.Text;
using DevSweep.Core.Projects;

namespace DevSweep.App.ViewModels.Background;

public enum TrayCommand
{
    /// <summary>A line of information (free space, a heading).</summary>
    None,
    OpenWindow,
    ScanNow,
    Quit,

    /// <summary>Open the screen an alert is about (<see cref="TrayItem.Argument"/> is the <see cref="ToastTarget"/>).</summary>
    OpenTarget,

    /// <summary>Show a project's folder in File Explorer (<see cref="TrayItem.Argument"/> is the path).</summary>
    RevealProject,

    /// <summary>Open a project in the chosen editor (<see cref="TrayItem.Argument"/> is the folder).</summary>
    OpenProject,

    /// <summary>Open Settings.</summary>
    OpenSettings,

    /// <summary>Open Clean up (the flyout's Review button).</summary>
    OpenCleanUp,
}

/// <summary>What the headline of the tray flyout says.</summary>
public enum FlyoutState
{
    NotScanned,
    Scanning,
    Clean,
    HasCleanable,
}

/// <summary>An alert line in the flyout; critical ones (secrets) get the red mark, the rest the amber one.</summary>
public sealed record FlyoutAlert(TrayItem Item, bool Critical);

/// <summary>A recent project line in the flyout: its name, a "3 not pushed" note, and the folder its Open button opens.</summary>
public sealed record FlyoutProject(string Name, string? Note, string Path);

/// <summary>Everything the tray flyout shows (the Mac's menu bar popover): free space with its bar, what can be cleaned, alerts, recent projects, the buttons and when the last scan was.</summary>
public sealed record TrayFlyoutContent(
    string FreeText,
    string Headline,
    string SubLine,
    FlyoutState State,
    double UsedFraction,
    double CleanableFraction,
    IReadOnlyList<FlyoutAlert> Alerts,
    IReadOnlyList<FlyoutProject> Recent,
    bool CanScan,
    string? LastScanText);

/// <summary>One line of the tray menu.</summary>
public sealed record TrayItem(string Text, TrayCommand Command = TrayCommand.None, string? Argument = null, bool Enabled = true, bool IsSeparator = false)
{
    public static TrayItem Separator { get; } = new(string.Empty, IsSeparator: true);

    public static TrayItem Heading(string text) => new(text, Enabled: false);
}

/// <summary>The tray icon's menu and tooltip, built from what the app knows right now.</summary>
public sealed class TrayViewModel
{
    /// <summary>Alerts and projects listed in the menu are capped, so it stays a menu.</summary>
    public const int MaxListed = 5;

    private readonly IDevSweepBackend _backend;
    private readonly ScanViewModel _scan;
    private readonly ProjectsViewModel _projects;
    private readonly AlertCoordinator _alerts;
    private readonly ReviewViewModel _review;

    public TrayViewModel(IDevSweepBackend backend, ScanViewModel scan, ProjectsViewModel projects, AlertCoordinator alerts, ReviewViewModel review)
    {
        ArgumentNullException.ThrowIfNull(review);
        _review = review;
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(alerts);
        _backend = backend;
        _scan = scan;
        _projects = projects;
        _alerts = alerts;
    }

    /// <summary>Show the main window (and go to a screen when <see cref="ToastTarget"/> is given).</summary>
    public event EventHandler<ToastTarget?>? ShowRequested;

    public event EventHandler? QuitRequested;

    public event EventHandler<string>? RevealRequested;

    /// <summary>Alerts listed in the flyout (the Mac lists two) and recent projects (three).</summary>
    public const int FlyoutAlerts = 2;

    public const int FlyoutProjects = 3;

    /// <summary>Free space on the system volume, as "412 GB free on Local Disk (C:)"; null when unknown.</summary>
    public string? FreeSpaceText
        => _backend.Volumes().FirstOrDefault(v => v.IsSystem) is { } v ? $"{SizeLabels.Size(v.FreeBytes)} free on {v.Name}" : null;

    /// <summary>The menu, in order: free space, active alerts, recent projects (when Projects is on), Open, Scan now, Quit.</summary>
    public IReadOnlyList<TrayItem> Items()
    {
        var items = new List<TrayItem> { TrayItem.Heading(FreeSpaceText ?? "Free space unknown") };
        items.Add(TrayItem.Heading(CleanableLine));

        IReadOnlyList<Core.Health.Alerts.AlertEvent> active = _alerts.Active;
        if (active.Count > 0)
        {
            items.Add(TrayItem.Separator);
            items.Add(TrayItem.Heading(active.Count == 1 ? "Needs attention" : $"Needs attention ({active.Count})"));
            foreach (Core.Health.Alerts.AlertEvent alert in active.Take(MaxListed))
            {
                items.Add(new TrayItem(alert.Title, TrayCommand.OpenTarget, AlertToasts.TargetOf(alert.Target).ToString()));
            }
        }

        if (_backend.Projects is not null && _projects.Report is { } report)
        {
            List<Project> recent = [.. report.Snapshot.Projects
                .Where(p => p.OnDisk && !string.IsNullOrEmpty(p.LocalPath))
                .OrderByDescending(p => p.LastOpened ?? p.LastActivity ?? DateTimeOffset.MinValue)
                .Take(MaxListed)];
            if (recent.Count > 0)
            {
                items.Add(TrayItem.Separator);
                items.Add(TrayItem.Heading("Recent projects"));
                items.AddRange(recent.Select(p => new TrayItem(p.Name, TrayCommand.RevealProject, p.LocalPath)));
            }
        }

        items.Add(TrayItem.Separator);
        items.Add(new TrayItem("Open DevSweep", TrayCommand.OpenWindow));
        // A scan while Review is open would replace the list being reviewed, so it waits.
        items.Add(_scan.IsScanning ? new TrayItem("Scanning…", TrayCommand.ScanNow, Enabled: false) : new TrayItem("Scan now", TrayCommand.ScanNow, Enabled: !_review.IsOpen));
        items.Add(new TrayItem("Settings", TrayCommand.OpenSettings));
        items.Add(TrayItem.Separator);
        if (LastScanText is { } last)
        {
            items.Add(TrayItem.Heading(last));
        }

        items.Add(new TrayItem("Quit DevSweep", TrayCommand.Quit));
        return items;
    }

    /// <summary>"17.09 GB can be cleaned", "Nothing to clean", "Scanning…" or "Not scanned yet".</summary>
    public string CleanableLine => _scan.IsScanning && !_scan.HasScanned ? "Scanning…"
        : !_scan.HasScanned ? "Not scanned yet"
        : _scan.Findings.Count == 0 ? "Nothing to clean"
        : SizeLabels.Size(_scan.TotalBytes) + " can be cleaned";

    /// <summary>"Last scan 5 minutes ago"; null before the first scan.</summary>
    public string? LastScanText => _scan.LastScan is { } last ? "Last scan " + Words.Relative(last, _backend.Now) : null;

    /// <summary>The content of the left-click flyout, built from what the app knows right now.</summary>
    public TrayFlyoutContent Flyout()
    {
        IReadOnlyList<VolumeSpace> volumes = _backend.Volumes();
        VolumeSpace? system = volumes.FirstOrDefault(v => v.IsSystem) ?? (volumes.Count > 0 ? volumes[0] : null);
        var row = system is null ? null : new VolumeRow(system, _scan.HasScanned ? _scan.TotalBytes : 0);
        FlyoutState state = _scan.HasScanned ? (_scan.Findings.Count > 0 ? FlyoutState.HasCleanable : FlyoutState.Clean)
            : _scan.IsScanning ? FlyoutState.Scanning : FlyoutState.NotScanned;
        (string headline, string sub) = state switch
        {
            FlyoutState.HasCleanable => (SizeLabels.Size(_scan.TotalBytes), _scan.IsScanning ? "scanning again…" : "can be cleaned"),
            FlyoutState.Clean => ("Nothing to clean", _scan.IsScanning ? "scanning again…" : "all clear"),
            FlyoutState.Scanning => ("Scanning…", _scan.Status),
            _ => ("Not scanned yet", "Scan to see what can be cleaned"),
        };
        List<FlyoutAlert> alerts = [.. _alerts.Active.Take(FlyoutAlerts).Select(a => new FlyoutAlert(
            new TrayItem(a.Title, TrayCommand.OpenTarget, AlertToasts.TargetOf(a.Target).ToString()),
            a.Target == Core.Health.Alerts.AlertTarget.HealthSecurity))];
        List<FlyoutProject> recent = [];
        if (_backend.Projects is not null && _projects.Report is { } report)
        {
            recent = [.. report.Snapshot.Projects
                .Where(p => p.OnDisk && !string.IsNullOrEmpty(p.LocalPath))
                .OrderByDescending(p => p.LastOpened ?? p.LastActivity ?? DateTimeOffset.MinValue)
                .Take(FlyoutProjects)
                .Select(p => new FlyoutProject(p.Name, p.Safety is { HasRemote: true, UnpushedCommits: > 0 } s ? $"{s.UnpushedCommits} not pushed" : null, p.LocalPath!))];
        }

        return new TrayFlyoutContent(system is null ? string.Empty : SizeLabels.Size(system.FreeBytes) + " free", headline, sub, state,
            row?.UsedFraction ?? 0, row?.CleanableFraction ?? 0, alerts, recent, !_scan.IsScanning && !_review.IsOpen, LastScanText);
    }

    /// <summary>The hover text (Windows shows at most 127 characters).</summary>
    public string Tooltip()
    {
        string text = "DevSweep";
        if (FreeSpaceText is { } free)
        {
            text += " · " + free;
        }

        if (_alerts.Active.Count > 0)
        {
            text += $" · {_alerts.Active.Count} to look at";
        }

        return text.Length <= 127 ? text : text[..126] + "…";
    }

    /// <summary>Does what the clicked item says. Nothing here cleans, moves or deletes anything.</summary>
    public void Execute(TrayItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.Enabled)
        {
            return;
        }

        switch (item.Command)
        {
            case TrayCommand.OpenWindow:
                ShowRequested?.Invoke(this, null);
                break;
            case TrayCommand.OpenTarget:
                ShowRequested?.Invoke(this, Enum.TryParse(item.Argument, out ToastTarget target) && Enum.IsDefined(target) ? target : ToastTarget.Home);
                break;
            case TrayCommand.ScanNow:
                _ = _scan.ScanAsync();
                break;
            case TrayCommand.RevealProject when item.Argument is { Length: > 0 } path:
                RevealRequested?.Invoke(this, path);
                break;
            case TrayCommand.OpenProject when item.Argument is { Length: > 0 } folder:
                if (_projects.All.FirstOrDefault(p => string.Equals(p.LocalPath, folder, StringComparison.OrdinalIgnoreCase)) is { } project)
                {
                    _projects.OpenInEditor(project);
                }
                else
                {
                    RevealRequested?.Invoke(this, folder);
                }

                break;
            case TrayCommand.OpenSettings:
                ShowRequested?.Invoke(this, ToastTarget.Settings);
                break;
            case TrayCommand.OpenCleanUp:
                ShowRequested?.Invoke(this, ToastTarget.CleanUp);
                break;
            case TrayCommand.Quit:
                QuitRequested?.Invoke(this, EventArgs.Empty);
                break;
            default:
                break;
        }
    }
}
