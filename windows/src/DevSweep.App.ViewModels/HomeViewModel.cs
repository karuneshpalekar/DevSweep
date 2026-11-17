using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DevSweep.Core.Health.Ports;
using DevSweep.Core.Health.Runtimes;
using DevSweep.Core.Health.Security;
using DevSweep.App.ViewModels.Services;
using DevSweep.App.ViewModels.Text;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Health.Alerts;
using DevSweep.Core.Scanning;
using DevSweep.App.ViewModels.Background;
using DevSweep.App.ViewModels.Settings;

namespace DevSweep.App.ViewModels;

/// <summary>One volume's free space bar.</summary>
public sealed record VolumeRow(VolumeSpace Volume, long Cleanable)
{
    public string Name => Volume.Name;

    public string FreeText => SizeLabels.Size(Volume.FreeBytes);

    public string Detail => $"{SizeLabels.Size(Volume.FreeBytes)} free of {SizeLabels.Size(Volume.TotalBytes)}";

    public double UsedFraction => Volume.TotalBytes <= 0 ? 0 : 1 - ((double)Volume.FreeBytes / Volume.TotalBytes);

    /// <summary>The part of the used space that can be cleaned (shown on the system volume).</summary>
    public double CleanableFraction => Volume.TotalBytes <= 0 ? 0 : Math.Min(UsedFraction, (double)Cleanable / Volume.TotalBytes);
}

/// <summary>A "Grew", "New" or "Cleaned" line.</summary>
public sealed record ChangeRow(string Tag, Tone Tone, string Title, string Value);

/// <summary>Something that needs attention.</summary>
/// <param name="Item">For Health rows: the item it is about ("tool:node", "security:&lt;id&gt;", "port:&lt;id&gt;"), so a click opens its panel.</param>
public sealed record AttentionRow(string Tag, Tone Tone, string Text, string Place, string? Item = null);

/// <summary>What the headline card says: nothing scanned yet, a scan running, or a finished scan.</summary>
public enum HomeState
{
    NotScanned,
    Scanning,
    Scanned,
}

/// <summary>Home: free space per volume, what can be cleaned, what changed, and what needs attention.</summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly IDevSweepBackend _backend;
    private readonly ScanViewModel _scan;
    private readonly HealthViewModel _health;
    private readonly AppSettingsStore? _settings;

    /// <summary>How many Health items Home lists (the Mac shows the 4 most important); the rest are in Health.</summary>
    public const int MaxHealthItems = 4;

    public HomeViewModel(IDevSweepBackend backend, ScanViewModel scan, HealthViewModel health, AppSettingsStore? settings = null)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(health);
        _backend = backend;
        _scan = scan;
        _health = health;
        _settings = settings;
        if (settings is not null)
        {
            settings.Changed += (_, _) => Refresh();
        }

        scan.PropertyChanged += (_, _) => Refresh();
        health.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HealthViewModel.Report))
            {
                Refresh();
            }
        };
    }

    public ScanViewModel Scan => _scan;

    /// <summary>When the last scheduled scan ran (the host connects it to the alert memory); used for "next scan".</summary>
    public Func<DateTimeOffset?>? LastScheduledRun { get; set; }

    public HomeState State => _scan.IsScanning && !_scan.HasScanned ? HomeState.Scanning : _scan.HasScanned ? HomeState.Scanned : HomeState.NotScanned;

    /// <summary>"Monday, 9:00 AM" while scheduled scans are on; null when they are off.</summary>
    public string? NextScanText => _settings is null ? null : ScheduleText.Next(_settings.Current.Schedule, LastScheduledRun?.Invoke(), _backend.Now);

    /// <summary>The banner above the headline when Windows blocked folders in the last scan (Controlled folder access or permissions); null otherwise.</summary>
    [ObservableProperty]
    public partial string? BlockedFoldersText { get; set; }

    /// <summary>The banner for rule packs that could not be read (those packs were skipped); null when all loaded.</summary>
    [ObservableProperty]
    public partial string? RuleErrorsText { get; set; }

    /// <summary>"Showing the 4 most important. Health has the rest." when Health has more than Home lists.</summary>
    [ObservableProperty]
    public partial string? AttentionCaption { get; set; }

    public ObservableCollection<VolumeRow> Volumes { get; } = [];

    public ObservableCollection<ChangeRow> Changes { get; } = [];

    public ObservableCollection<AttentionRow> Attention { get; } = [];

    [ObservableProperty]
    public partial string CleanableText { get; set; } = "Not scanned yet";

    [ObservableProperty]
    public partial string Subtitle { get; set; } = "Not scanned yet";

    [ObservableProperty]
    public partial string ChangesTitle { get; set; } = "Since last week";

    [ObservableProperty]
    public partial string? ChangesEmpty { get; set; }

    [ObservableProperty]
    public partial string? AttentionEmpty { get; set; }

    [ObservableProperty]
    public partial string ReviewLabel { get; set; } = string.Empty;

    public void Refresh()
    {
        long cleanable = _scan.HasScanned ? _scan.TotalBytes : 0;
        Volumes.Clear();
        foreach (VolumeSpace volume in _backend.Volumes())
        {
            Volumes.Add(new VolumeRow(volume, volume.IsSystem ? cleanable : 0));
        }

        CleanableText = !_scan.HasScanned
            ? _scan.IsScanning ? "Scanning · " + _scan.Status : "Not scanned yet"
            : _scan.Findings.Count == 0 ? "Nothing to clean" : $"{SizeLabels.Size(_scan.TotalBytes)} can be cleaned";
        ReviewLabel = _scan.HasScanned && _scan.Findings.Count > 0 ? $"Review {Words.Items(_scan.Findings.Count)}" : string.Empty;
        string? next = NextScanText;
        Subtitle = _scan.IsScanning ? "Scanning · " + _scan.Status
            : _scan.LastScan is { } last ? "Scanned " + Words.Relative(last, _backend.Now) + (next is null ? string.Empty : " · next scan " + next)
            : "Not scanned yet";
        BlockedFoldersText = _scan.Report is { AccessDenied.Count: > 0 } denied
            ? denied.AccessDenied.Count == 1 ? "One folder can't be read, so a few leftovers can be missed." : $"{denied.AccessDenied.Count} folders can't be read, so a few leftovers can be missed."
            : null;
        RuleErrorsText = _scan.Report is { RuleErrors.Count: > 0 } errors
            ? "Some rule packs couldn't be read, so their checks were skipped: " + string.Join("; ", errors.RuleErrors.Take(2).Select(e => e.Message)) + (errors.RuleErrors.Count > 2 ? $" (and {errors.RuleErrors.Count - 2} more)" : string.Empty)
            : null;
        OnPropertyChanged(nameof(State));
        RefreshChanges();
        RefreshAttention();
    }

    private void RefreshChanges()
    {
        Changes.Clear();
        ScanChanges? changes = _scan.Changes;
        if (changes is not null)
        {
            foreach (ScanChange grew in changes.Grew.Take(3))
            {
                Changes.Add(new ChangeRow("Grew", Tone.Caution, grew.Title, "+" + SizeLabels.Size(grew.Bytes)));
            }

            foreach (ScanChange added in changes.New.Take(3))
            {
                Changes.Add(new ChangeRow("New", Tone.Accent, added.Title, SizeLabels.Size(added.Bytes)));
            }
        }

        DateTimeOffset weekAgo = _backend.Now.AddDays(-7);
        List<HistoryEntry> cleaned = [.. _backend.History().Where(e => e.Date >= weekAgo && e.RestoredAt is null && e.Size > 0)];
        if (cleaned.Count > 0)
        {
            Changes.Add(new ChangeRow("Cleaned", Tone.Good, $"You cleaned {Words.Items(cleaned.Count)}", SizeLabels.Size(cleaned.Sum(e => e.Size))));
        }

        ChangesTitle = changes is null ? "Since last week" : "Since " + changes.Since.ToLocalTime().ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture);
        ChangesEmpty = Changes.Count > 0 ? null
            : changes is null ? "This is the first scan. From next week, this shows what grew and what's new."
            : "Nothing changed much since " + changes.Since.ToLocalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture) + ".";
    }

    private void RefreshAttention()
    {
        Attention.Clear();
        AttentionCaption = null;
        var healthRows = new List<AttentionRow>();
        if (_health.Report is { } health)
        {
            foreach (SecurityFinding item in _health.NeedsAction.OrderBy(f => f.Level))
            {
                healthRows.Add(new AttentionRow("Security", item.Level == SecurityLevel.Critical ? Tone.Critical : Tone.Caution, $"{item.Title}: {item.Status}. {item.WhatToDo}", "health", "security:" + item.Id));
            }

            foreach (RuntimeReport tool in (health.Versions?.Runtimes ?? []).Where(t => t.NeedsAttention))
            {
                RuntimeIssue issue = tool.Issues.OrderBy(i => i.Level).First();
                healthRows.Add(new AttentionRow("Health", issue.Level == IssueLevel.Critical ? Tone.Critical : Tone.Caution, $"{tool.Name}: {issue.Text}", "health", "tool:" + tool.Id));
            }

            foreach (ListeningPort port in _health.DevelopmentPorts.Where(p => p.ReachableFromNetwork))
            {
                healthRows.Add(new AttentionRow("Ports", Tone.Caution, $"{port.Label} on port {port.Port} listens on every network interface, so other devices may reach it.", "health", "port:" + port.Id));
            }
        }

        // The most important few, one kind not crowding out the others: up to 2 security findings, 1 exposed port, then tools to fill.
        List<AttentionRow> chosen = [.. healthRows.Where(r => r.Tag == "Security").Take(2), .. healthRows.Where(r => r.Tag == "Ports").Take(1)];
        chosen.AddRange(healthRows.Where(r => r.Tag == "Health").Take(MaxHealthItems - chosen.Count));
        foreach (AttentionRow row in chosen.OrderBy(r => healthRows.IndexOf(r)))
        {
            Attention.Add(row);
        }

        if (healthRows.Count > MaxHealthItems)
        {
            AttentionCaption = $"Showing the {MaxHealthItems} most important. Health has the rest.";
        }

        if (_scan.Report is { } report)
        {
            if (report.TimedOut.Count > 0)
            {
                Attention.Add(new AttentionRow("Scan", Tone.Caution,
                    $"{Words.Count(report.TimedOut.Count, "check")} took too long and {(report.TimedOut.Count == 1 ? "was" : "were")} skipped: {string.Join(", ", report.TimedOut.Take(3).Select(c => c.Title))}.", "cleanup"));
            }

            foreach (Finding blocked in _scan.Findings.Where(f => f.BlockingApps.Count > 0).Take(1))
            {
                Attention.Add(new AttentionRow("Clean up", Tone.Neutral, $"{blocked.Title}: close {string.Join(", ", blocked.BlockingApps)} before cleaning it.", "cleanup"));
            }
        }

        AttentionEmpty = Attention.Count > 0 ? null
            : _scan.HasScanned || _health.Report is not null ? "Nothing needs attention right now."
            : "Not checked yet";
    }
}
