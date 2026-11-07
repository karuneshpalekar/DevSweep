using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevSweep.App.ViewModels.Services;
using DevSweep.App.ViewModels.Text;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Platform.Quarantine;

namespace DevSweep.App.ViewModels;

/// <summary>Where one item of an entry is now.</summary>
public sealed record HistoryItemRow(string Path, string Where);

/// <summary>One History line.</summary>
public sealed record HistoryRow(HistoryEntry Entry, bool CanRestore, string? Profile)
{
    public string Time => Words.Time(Entry.Date);

    public string Title => Entry.Title;

    public string SizeText => SizeLabels.Size(Entry.Size);

    public string Detail
    {
        get
        {
            if (Entry.Command is { Count: > 0 } command)
            {
                return "Ran " + Core.Review.CommandLineText.Join(command);
            }

            string text = $"{Entry.ActionLabel} · {Words.Items(Entry.Items.Count)}";
            int quarantined = Entry.Items.Count(i => i.Destination == ItemDestination.Quarantine);
            if (quarantined > 0)
            {
                text += " · in DevSweep's quarantine (the Recycle Bin wouldn't keep it)";
            }

            if (Entry.Reconciliation is { } r && Entry.Items.Any(i => i.Destination == ItemDestination.Deleted))
            {
                text += $" · drive freed {SizeLabels.Size(Math.Max(0, r.FreedBytes))}";
            }

            return text;
        }
    }

    /// <summary>"Restored", "Rebuilds itself", "No longer in the Recycle Bin", or empty when Restore is offered.</summary>
    public string Status => Entry.RestoredAt is not null ? "Restored"
        : CanRestore ? string.Empty
        : Entry.ActionKind == CleanActionKind.Manual ? string.Empty
        : Entry.ActionKind is CleanActionKind.Delete or CleanActionKind.Command ? "Rebuilds itself"
        : Entry.Items.Any(i => i.Destination == ItemDestination.Quarantine) ? "No longer in the quarantine"
        : "No longer in the Recycle Bin";

    public IReadOnlyList<HistoryItemRow> Items => [.. Entry.Items.Select(i => new HistoryItemRow(Words.Path(i.Original, Profile), i.Destination switch
    {
        ItemDestination.RecycleBin => "Recycle Bin",
        ItemDestination.Quarantine => "Quarantine",
        _ => "Deleted",
    }))];
}

public sealed record HistoryDay(string Title, IReadOnlyList<HistoryRow> Rows);

/// <summary>History with Restore (Recycle Bin and quarantine), and the quarantine's size and Empty.</summary>
public sealed partial class HistoryViewModel(IDevSweepBackend backend) : ObservableObject
{
    public ObservableCollection<HistoryDay> Days { get; } = [];

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string QuarantineText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasQuarantine { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    public bool IsEmpty => Days.Count == 0;

    [RelayCommand]
    public void Refresh()
    {
        IReadOnlyList<HistoryEntry> entries = backend.History();
        Days.Clear();
        foreach (IGrouping<DateTime, HistoryEntry> day in entries.OrderByDescending(e => e.Date).GroupBy(e => e.Date.ToLocalTime().Date))
        {
            Days.Add(new HistoryDay(Words.Day(day.First().Date), [.. day.Select(e => new HistoryRow(e, backend.CanRestore(e), backend.ProfilePath))]));
        }

        Subtitle = $"{SizeLabels.Size(entries.Where(e => e.RestoredAt is null).Sum(e => e.Size))} removed in total";
        IReadOnlyList<QuarantineEntry> quarantine = backend.Quarantine();
        HasQuarantine = quarantine.Count > 0;
        QuarantineText = quarantine.Count == 0
            ? "DevSweep's quarantine is empty."
            : $"DevSweep's quarantine holds {Words.Items(quarantine.Count)}, {SizeLabels.Size(quarantine.Sum(q => q.Bytes))} on disk. They can be restored until you empty it.";
        OnPropertyChanged(nameof(IsEmpty));
    }

    public async Task RestoreAsync(HistoryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        HistoryRestoreResult result = await backend.RestoreAsync(row.Entry.Id);
        Message = string.Join(Environment.NewLine, result.Messages);
        Refresh();
    }

    /// <summary>Deletes everything in the quarantine for good (the view confirms first).</summary>
    public async Task EmptyQuarantineAsync()
    {
        long freed = await backend.EmptyQuarantineAsync();
        Message = $"Emptied the quarantine: {SizeLabels.Size(freed)} deleted for good.";
        Refresh();
    }
}
