using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DevSweep.App.ViewModels.Background;
using DevSweep.App.ViewModels.Text;

namespace DevSweep.App.ViewModels;

/// <summary>What the sidebar shows besides the places: the free-space card, the numbers on the places, the attention cards and the status line.</summary>
public sealed partial class SidebarViewModel : ObservableObject
{
    /// <summary>The attention cards in the pane footer (the Mac shows two).</summary>
    public const int MaxCards = 2;

    private readonly MainViewModel _main;

    public SidebarViewModel(MainViewModel main)
    {
        ArgumentNullException.ThrowIfNull(main);
        _main = main;
        main.Scan.PropertyChanged += (_, _) => Changed();
        main.Health.PropertyChanged += (_, _) => Changed();
        main.Projects.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProjectsViewModel.Report) or nameof(ProjectsViewModel.State))
            {
                Changed();
            }
        };
        main.Home.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(HomeViewModel.Subtitle) or nameof(HomeViewModel.CleanableText))
            {
                Changed();
            }
        };
        main.Settings.Changed += (_, _) => Changed();
    }

    /// <summary>The system volume (where the cleanable part is).</summary>
    public VolumeRow? Volume => _main.Home.Volumes.FirstOrDefault(v => v.Volume.IsSystem) ?? _main.Home.Volumes.FirstOrDefault();

    /// <summary>"85.5 GB"; "-" when unknown.</summary>
    public string FreeText => Volume?.FreeText ?? "-";

    public bool HasVolume => Volume is not null;

    /// <summary>"17.09 GB can be cleaned", "Scanning…", "Not scanned yet" or "Nothing to clean".</summary>
    public string CleanableLine
    {
        get
        {
            ScanViewModel scan = _main.Scan;
            return scan.IsScanning && !scan.HasScanned ? "Scanning…"
                : !scan.HasScanned ? "Not scanned yet"
                : scan.Findings.Count == 0 ? "Nothing to clean"
                : SizeLabels.Size(scan.TotalBytes) + " can be cleaned";
        }
    }

    public bool HasCleanable => _main.Scan.HasScanned && _main.Scan.Findings.Count > 0;

    /// <summary>The number or size shown beside a place; empty when there is nothing to say.</summary>
    public string CountFor(string placeId) => placeId switch
    {
        "cleanup" => HasCleanable ? SizeLabels.Size(_main.Scan.TotalBytes) : string.Empty,
        "projects" => Nonzero(ProjectsNeedingPush),
        "health" => _main.Health.Report is null ? string.Empty : Nonzero(HealthAttentionCount),
        _ => string.Empty,
    };

    /// <summary>Projects on this PC with commits that are not on a remote (the Mac's "needing push").</summary>
    public int ProjectsNeedingPush => _main.Projects.All.Count(p => p.OnDisk && p.Safety is { HasRemote: true, UnpushedCommits: > 0 });

    /// <summary>Tools that need attention plus security findings that need action (what Home's attention list draws from).</summary>
    public int HealthAttentionCount => _main.Health.Report is null ? 0
        : (_main.Health.Report.Versions is null ? 0 : _main.Health.FilterCount(ToolsFilter.NeedsAttention)) + _main.Health.NeedsAction.Count;

    /// <summary>Up to two of the most important things, as the cards under "Needs your attention".</summary>
    public IReadOnlyList<AttentionRow> Cards => [.. _main.Home.Attention.Where(a => a.Place == "health").Take(MaxCards)];

    /// <summary>"Last scan 5 minutes ago · next Monday, 9:00 AM".</summary>
    public string StatusLine
        => ScheduleText.StatusLine(_main.Scan.IsScanning, _main.Scan.LastScan, _main.Settings.Current.Schedule, _main.Background.Alerts.LastScheduledRun, _main.Backend.Now);

    private static string Nonzero(int n) => n > 0 ? n.ToString(CultureInfo.InvariantCulture) : string.Empty;

    private void Changed() => OnPropertyChanged(string.Empty);
}
