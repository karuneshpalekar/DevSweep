using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevSweep.App.ViewModels.Settings;
using DevSweep.App.ViewModels.Text;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Scanning;

namespace DevSweep.App.ViewModels;

/// <summary>One row in Clean up, and everything the ⓘ inspector shows about it.</summary>
public sealed partial class FindingViewModel : ObservableObject
{
    public FindingViewModel(Finding finding, string? profile)
    {
        ArgumentNullException.ThrowIfNull(finding);
        Finding = finding;
        Profile = profile;
        Kind = CleanupKinds.Of(finding);
        ChosenAction = finding.Actions.Count > 0 ? finding.Actions[0] : new CleanAction(CleanActionKind.Manual);
    }

    public Finding Finding { get; }

    public string? Profile { get; }

    public string Id => Finding.Id;

    public string Title => Finding.Title;

    public string Subtitle => Finding.Subtitle.Length > 0 ? Finding.Subtitle : Finding.Paths.Count > 0 ? Words.Path(Finding.Paths[0], Profile) : string.Empty;

    public CleanupKind Kind { get; }

    public string SizeText => SizeLabels.Size(Finding);

    public IReadOnlyList<SizeBadge> Badges => SizeLabels.Badges(Finding);

    public string SizeTooltip => SizeLabels.Tooltip(Finding);

    public string RiskTitle => RiskLabels.Title(Finding.Risk);

    public string RiskSummary => RiskLabels.Summary(Finding.Risk);

    public Tone RiskTone => RiskLabels.Tone(Finding.Risk);

    /// <summary>Manual-only items (admin commands) can't be selected; the inspector shows their commands.</summary>
    public bool CanSelect => Finding.Actions.Any(a => a.Kind != CleanActionKind.Manual);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial CleanAction ChosenAction { get; set; }

    public IReadOnlyList<CleanAction> Actions => Finding.Actions;

    public string What => Finding.Explanation?.What ?? string.Empty;

    public string Why => Finding.Explanation?.Why ?? string.Empty;

    public string IfDeleted => Finding.Explanation?.IfDeleted ?? string.Empty;

    public string? WontLose => Finding.Explanation?.WontLose;

    public string? Before => Finding.Explanation?.Before;

    public string? Undo => Finding.Explanation?.Undo;

    public string? Better => Finding.Explanation?.Better;

    public IReadOnlyList<Check> Checks => Finding.Checks;

    public IReadOnlyList<string> BlockingApps => Finding.BlockingApps;

    public IReadOnlyList<string> Locations => [.. Finding.Paths.Select((p, i) =>
        Finding.PathSizes.Count == Finding.Paths.Count ? $"{Words.Path(p, Profile)}  ·  {SizeLabels.Size(Finding.PathSizes[i])}" : Words.Path(p, Profile))];

    /// <summary>The command lines of a manual action, for the inspector's copy box.</summary>
    public string? ManualCommand => ChosenAction.Kind == CleanActionKind.Manual && ChosenAction.Command is { Count: > 0 } c
        ? Core.Review.CommandLineText.Join(c) : null;

    public static string Detail(CleanAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.Detail ?? action.Kind switch
        {
            CleanActionKind.Delete => "Removed for good right away. It rebuilds on its own.",
            CleanActionKind.Trash => "Goes to the Recycle Bin (or DevSweep's quarantine when the bin wouldn't keep it). Restore it from History.",
            CleanActionKind.Command => "Runs the tool's own command. Its output is kept in History.",
            _ => "Shows the commands. You run them; DevSweep doesn't change anything.",
        };
    }

    partial void OnChosenActionChanged(CleanAction value) => OnPropertyChanged(nameof(ManualCommand));
}

/// <summary>Findings of one risk, as one section of the list.</summary>
public sealed record RiskGroup(Risk Risk, IReadOnlyList<FindingViewModel> Items)
{
    public string Title => RiskLabels.Title(Risk);

    public string Summary => RiskLabels.Summary(Risk);

    public Tone Tone => RiskLabels.Tone(Risk);

    public string Detail => $"{Words.Items(Items.Count)} · {SizeLabels.Size(Items.Sum(i => i.Finding.Size))}";

    public bool CanSelectAll => Risk != Risk.NeedsAdmin && Items.Any(i => i.CanSelect);
}

/// <summary>A filter chip.</summary>
public sealed record KindFilter(CleanupKind? Kind, string Title, string Detail);

/// <summary>Clean up: filter by kind, grouped by risk, selection, the ⓘ inspector and Review.</summary>
public sealed partial class CleanUpViewModel : ObservableObject
{
    private readonly ScanViewModel _scan;
    private readonly AppSettingsStore _settings;
    private readonly string? _profile;
    private List<FindingViewModel> _all = [];

    public CleanUpViewModel(ScanViewModel scan, AppSettingsStore settings, string? profile)
    {
        ArgumentNullException.ThrowIfNull(scan);
        _scan = scan;
        _settings = settings;
        _profile = profile;
        scan.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ScanViewModel.Findings))
            {
                Reload();
            }
            else
            {
                OnPropertyChanged(nameof(Subtitle));
            }
        };
        Reload();
    }

    public ScanViewModel Scan => _scan;

    public ObservableCollection<RiskGroup> Groups { get; } = [];

    public ObservableCollection<KindFilter> Filters { get; } = [];

    [ObservableProperty]
    public partial CleanupKind? SelectedKind { get; set; }

    [ObservableProperty]
    public partial string Search { get; set; } = string.Empty;

    [ObservableProperty]
    public partial FindingViewModel? Inspected { get; set; }

    [ObservableProperty]
    public partial string SelectionText { get; set; } = "Select items to clean. Nothing changes until you review and confirm.";

    [ObservableProperty]
    public partial int SelectedCount { get; set; }

    /// <summary>Asks the shell to open Review with these items.</summary>
    public event EventHandler<IReadOnlyList<PlannedAction>>? ReviewRequested;

    public IReadOnlyList<FindingViewModel> All => _all;

    public string Subtitle
    {
        get
        {
            if (!_scan.HasScanned)
            {
                return _scan.IsScanning ? "Scanning · " + _scan.Status : "Not scanned yet";
            }

            return _all.Count == 0 ? "Nothing to clean" : $"{Words.Items(_all.Count)} · {SizeLabels.Size(_scan.TotalBytes)} can be cleaned";
        }
    }

    public IReadOnlyList<FindingViewModel> Selected => [.. _all.Where(f => f.IsSelected)];

    partial void OnSelectedKindChanged(CleanupKind? value) => Regroup();

    partial void OnSearchChanged(string value) => Regroup();

    public void Reload()
    {
        HashSet<string> wasSelected = _all.Where(f => f.IsSelected).Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        foreach (FindingViewModel old in _all)
        {
            old.PropertyChanged -= OnItemChanged;
        }

        _all = [.. _scan.Findings.Select(f => new FindingViewModel(f, _profile) { IsSelected = wasSelected.Contains(f.Id) })];
        foreach (FindingViewModel item in _all)
        {
            item.PropertyChanged += OnItemChanged;
        }

        Filters.Clear();
        Filters.Add(new KindFilter(null, "All", _all.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        foreach (CleanupKind kind in CleanupKinds.All.Where(k => _all.Any(f => f.Kind == k)))
        {
            Filters.Add(new KindFilter(kind, CleanupKinds.Title(kind), SizeLabels.Size(_all.Where(f => f.Kind == kind).Sum(f => f.Finding.Size))));
        }

        if (SelectedKind is { } selected && _all.TrueForAll(f => f.Kind != selected))
        {
            SelectedKind = null;
        }

        if (Inspected is { } inspected)
        {
            Inspected = _all.Find(f => f.Id == inspected.Id);
        }

        Regroup();
        OnPropertyChanged(nameof(Subtitle));
        UpdateSelection();
    }

    private void Regroup()
    {
        Groups.Clear();
        IEnumerable<FindingViewModel> visible = _all.Where(f =>
            (SelectedKind is null || f.Kind == SelectedKind)
            && (Search.Length == 0
                || f.Title.Contains(Search, StringComparison.OrdinalIgnoreCase)
                || f.Subtitle.Contains(Search, StringComparison.OrdinalIgnoreCase)));
        foreach (IGrouping<Risk, FindingViewModel> group in visible.GroupBy(f => f.Finding.Risk).OrderBy(g => g.Key))
        {
            Groups.Add(new RiskGroup(group.Key, [.. group.OrderByDescending(f => f.Finding.Size)]));
        }
    }

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FindingViewModel.IsSelected))
        {
            UpdateSelection();
        }
    }

    private void UpdateSelection()
    {
        IReadOnlyList<FindingViewModel> selected = Selected;
        SelectedCount = selected.Count;
        SelectionText = selected.Count == 0
            ? "Select items to clean. Nothing changes until you review and confirm."
            : $"{Words.Count(selected.Count, "item")} selected · {SizeLabels.Size(selected.Sum(f => f.Finding.Size))}";
        ReviewCommand.NotifyCanExecuteChanged();
    }

    public static void ToggleGroup(RiskGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        bool all = group.Items.Where(i => i.CanSelect).All(i => i.IsSelected);
        foreach (FindingViewModel item in group.Items.Where(i => i.CanSelect))
        {
            item.IsSelected = !all;
        }
    }

    public void Inspect(FindingViewModel? item) => Inspected = item is not null && Inspected?.Id == item.Id ? null : item;

    [RelayCommand]
    public void ClearSelection()
    {
        foreach (FindingViewModel item in _all)
        {
            item.IsSelected = false;
        }
    }

    /// <summary>Leaves the item out of every scan from now on (Settings, Ignored items can bring it back).</summary>
    public void Ignore(FindingViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _settings.Update(s => s with { Ignored = [.. s.Ignored.Where(i => i.Id != item.Id), new IgnoredItem(item.Id, item.Title)] });
        if (Inspected?.Id == item.Id)
        {
            Inspected = null;
        }

        _scan.Remove([item.Id]);
    }

    private bool CanReview() => SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanReview))]
    public void Review()
        => ReviewRequested?.Invoke(this, [.. Selected.Where(f => f.CanSelect).Select(f => new PlannedAction(f.Finding, f.ChosenAction))]);
}
