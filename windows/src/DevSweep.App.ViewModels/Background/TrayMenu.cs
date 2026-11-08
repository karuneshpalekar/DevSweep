using DevSweep.App.ViewModels.Services;
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
}

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

    /// <summary>Free space on the system volume, as "412 GB free on Local Disk (C:)"; null when unknown.</summary>
    public string? FreeSpaceText
        => _backend.Volumes().FirstOrDefault(v => v.IsSystem) is { } v ? $"{SizeLabels.Size(v.FreeBytes)} free on {v.Name}" : null;

    /// <summary>The menu, in order: free space, active alerts, recent projects (when Projects is on), Open, Scan now, Quit.</summary>
    public IReadOnlyList<TrayItem> Items()
    {
        var items = new List<TrayItem> { TrayItem.Heading(FreeSpaceText ?? "Free space unknown") };

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
        items.Add(TrayItem.Separator);
        items.Add(new TrayItem("Quit DevSweep", TrayCommand.Quit));
        return items;
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
            case TrayCommand.Quit:
                QuitRequested?.Invoke(this, EventArgs.Empty);
                break;
            default:
                break;
        }
    }
}
