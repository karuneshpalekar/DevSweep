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
public sealed record AttentionRow(string Tag, Tone Tone, string Text, string Place);

/// <summary>Home: free space per volume, what can be cleaned, what changed, and what needs attention.</summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly IDevSweepBackend _backend;
    private readonly ScanViewModel _scan;
    private readonly HealthViewModel _health;

    public HomeViewModel(IDevSweepBackend backend, ScanViewModel scan, HealthViewModel health)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(health);
        _backend = backend;
        _scan = scan;
        _health = health;
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
        Subtitle = _scan.IsScanning ? "Scanning · " + _scan.Status
            : _scan.LastScan is { } last ? "Scanned " + Words.Relative(last, _backend.Now)
            : "Not scanned yet";
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
        if (_health.Report is { } health)
        {
            foreach (SecurityFinding item in _health.NeedsAction.OrderBy(f => f.Level).Take(2))
            {
                Attention.Add(new AttentionRow("Security", item.Level == SecurityLevel.Critical ? Tone.Critical : Tone.Caution, $"{item.Title}: {item.Status}. {item.WhatToDo}", "health"));
            }

            foreach (RuntimeReport tool in (health.Versions?.Runtimes ?? []).Where(t => t.NeedsAttention).Take(2))
            {
                RuntimeIssue issue = tool.Issues.OrderBy(i => i.Level).First();
                Attention.Add(new AttentionRow("Health", issue.Level == IssueLevel.Critical ? Tone.Critical : Tone.Caution, $"{tool.Name}: {issue.Text}", "health"));
            }

            foreach (ListeningPort port in _health.DevelopmentPorts.Where(p => p.ReachableFromNetwork).Take(1))
            {
                Attention.Add(new AttentionRow("Ports", Tone.Caution, $"{port.Label} on port {port.Port} listens on every network interface, so other devices may reach it.", "health"));
            }
        }

        if (_scan.Report is { } report)
        {
            if (report.TimedOut.Count > 0)
            {
                Attention.Add(new AttentionRow("Scan", Tone.Caution,
                    $"{Words.Count(report.TimedOut.Count, "check")} took too long and {(report.TimedOut.Count == 1 ? "was" : "were")} skipped: {string.Join(", ", report.TimedOut.Take(3).Select(c => c.Title))}.", "cleanup"));
            }

            if (report.AccessDenied.Count > 0)
            {
                Attention.Add(new AttentionRow("Scan", Tone.Neutral,
                    $"{Words.Count(report.AccessDenied.Count, "folder")} couldn't be read, so anything there wasn't counted. DevSweep never changes permissions to look.", "cleanup"));
            }

            if (report.RuleErrors.Count > 0)
            {
                Attention.Add(new AttentionRow("Rules", Tone.Critical, $"Some rule packs couldn't be read: {report.RuleErrors[0]}", "settings"));
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
