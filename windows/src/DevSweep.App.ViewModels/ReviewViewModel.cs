using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevSweep.App.ViewModels.Services;
using DevSweep.App.ViewModels.Text;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Review;

namespace DevSweep.App.ViewModels;

public enum ReviewStage
{
    Closed,

    /// <summary>Measuring every selected item exactly (streams on).</summary>
    Measuring,

    /// <summary>Showing what will happen.</summary>
    Ready,

    Cleaning,

    /// <summary>Showing outcomes.</summary>
    Done,
}

/// <summary>One item in a Review group.</summary>
public sealed record ReviewRow(ReviewItem Item)
{
    public string Title => Item.Finding.Title;

    public string SizeText => SizeLabels.Size(Item.Finding);

    public IReadOnlyList<SizeBadge> Badges => SizeLabels.Badges(Item.Finding);

    public string SizeTooltip => SizeLabels.Tooltip(Item.Finding);

    public string Summary => Item.Summary;

    public IReadOnlyList<string> Blockers => Item.Blockers;

    public IReadOnlyList<string> Notes => Item.Notes;

    public string? Command => Item.Outcome is ReviewOutcome.RunCommand or ReviewOutcome.Manual && Item.Commands.Count > 0 ? "Runs " + Item.Commands[0] : null;
}

/// <summary>Items that will have the same fate.</summary>
public sealed record ReviewGroup(ReviewOutcome Outcome, IReadOnlyList<ReviewRow> Rows)
{
    public string Title => Outcome switch
    {
        ReviewOutcome.RecycleBin => "Moved to the Recycle Bin",
        ReviewOutcome.Quarantine => "Moved to DevSweep's quarantine",
        ReviewOutcome.DeleteForGood => "Deleted right away",
        ReviewOutcome.RunCommand => "Cleaned by the tool's own command",
        ReviewOutcome.Manual => "You run these yourself",
        _ => "Can't be cleaned right now",
    };

    public string Note => Outcome switch
    {
        ReviewOutcome.RecycleBin => "You can restore these from History until you empty the Recycle Bin.",
        ReviewOutcome.Quarantine => "The Recycle Bin wouldn't keep these (too big for its limit, turned off, or a path too long for it), so they move to a quarantine folder on the same drive. Restore them from History; they take space until you empty the quarantine.",
        ReviewOutcome.DeleteForGood => "These rebuild on their own, so there's nothing to restore. This can't be undone.",
        ReviewOutcome.RunCommand => "DevSweep runs the command below and keeps its output in History. This can't be undone.",
        ReviewOutcome.Manual => "DevSweep doesn't change anything here. Copy the commands and run them in a terminal; Windows asks for an administrator (UAC) only if a command needs one.",
        _ => "Nothing is changed for these. Fix what's listed and review again.",
    };

    public Tone Tone => Outcome switch
    {
        ReviewOutcome.DeleteForGood or ReviewOutcome.RunCommand => Tone.Caution,
        ReviewOutcome.Blocked => Tone.Critical,
        ReviewOutcome.Manual => Tone.Neutral,
        _ => Tone.Good,
    };

    public string SizeText => SizeLabels.Size(Rows.Sum(r => r.Item.Finding.Size));
}

/// <summary>What happened to one item.</summary>
public sealed record OutcomeRow(ActionOutcome Outcome)
{
    public string Title => Outcome.Finding.Title;

    public bool Succeeded => Outcome.Succeeded;

    public string Message => Outcome.Message;

    public string FreedText => SizeLabels.Size(Outcome.Freed);

    public string Reconciliation => ReconciliationText.Describe(Outcome);
}

/// <summary>
/// The Review sheet: re-measures every selected item exactly, asks Core what will actually
/// happen to each (Recycle Bin, quarantine, delete, command, manual, or blocked and why),
/// shows the exact operations, then runs the cleaner and shows outcomes with freed-space
/// reconciliation.
/// </summary>
public sealed partial class ReviewViewModel(IDevSweepBackend backend) : ObservableObject, IDisposable
{
    private CancellationTokenSource? _cancel;
    private ReviewPlan? _plan;

    [ObservableProperty]
    public partial ReviewStage Stage { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double Progress { get; set; }

    public ObservableCollection<ReviewGroup> Groups { get; } = [];

    public ObservableCollection<OutcomeRow> Outcomes { get; } = [];

    [ObservableProperty]
    public partial string Heading { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subheading { get; set; } = string.Empty;

    /// <summary>"Some of these can't be undone…", or null.</summary>
    [ObservableProperty]
    public partial string? Warning { get; set; }

    [ObservableProperty]
    public partial string Footer { get; set; } = "Every change is logged in History.";

    [ObservableProperty]
    public partial bool FooterIsWarning { get; set; }

    [ObservableProperty]
    public partial string CommandsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CleanLabel { get; set; } = "Clean";

    /// <summary>Raised after cleaning with the ids of findings that are gone.</summary>
    public event EventHandler<IReadOnlyCollection<string>>? Cleaned;

    public bool IsOpen => Stage != ReviewStage.Closed;

    partial void OnStageChanged(ReviewStage value)
    {
        OnPropertyChanged(nameof(IsOpen));
        CleanCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Opens Review for the selected items: exact re-measure first, then the plan.</summary>
    public async Task OpenAsync(IReadOnlyList<PlannedAction> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        _cancel?.Cancel();
        _cancel?.Dispose();
        _cancel = new CancellationTokenSource();
        CancellationToken token = _cancel.Token;
        Groups.Clear();
        Outcomes.Clear();
        Warning = null;
        CommandsText = string.Empty;
        Stage = ReviewStage.Measuring;
        Heading = $"Review {Words.Items(selection.Count)}";
        Subheading = "Measuring each item exactly before anything changes…";
        try
        {
            var measured = new List<PlannedAction>(selection.Count);
            for (int i = 0; i < selection.Count; i++)
            {
                PlannedAction item = selection[i];
                Status = $"Measuring exactly · {i + 1} of {selection.Count} · {item.Finding.Title}";
                Progress = (double)i / Math.Max(1, selection.Count);
                Finding exact = await backend.RemeasureAsync(item.Finding, token);
                measured.Add(item with { Finding = exact });
            }

            Status = "Checking what will happen to each item…";
            Progress = 1;
            ReviewPlan plan = await backend.PrepareAsync(measured, token);
            ShowPlan(plan);
        }
        catch (OperationCanceledException)
        {
            Stage = ReviewStage.Closed;
        }
    }

    /// <summary>Shows a prepared plan (tests and screenshots call this directly).</summary>
    public void ShowPlan(ReviewPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _plan = plan;
        Groups.Clear();
        ReviewOutcome[] order = [ReviewOutcome.Blocked, ReviewOutcome.DeleteForGood, ReviewOutcome.RunCommand, ReviewOutcome.RecycleBin, ReviewOutcome.Quarantine, ReviewOutcome.Manual];
        foreach (ReviewOutcome outcome in order)
        {
            IReadOnlyList<ReviewItem> items = plan.In(outcome);
            if (items.Count > 0)
            {
                Groups.Add(new ReviewGroup(outcome, [.. items.Select(i => new ReviewRow(i))]));
            }
        }

        IReadOnlyList<PlannedAction> runnable = plan.Runnable;
        long freedNow = plan.Items.Where(i => i.Outcome == ReviewOutcome.DeleteForGood).Sum(i => i.Finding.FreeableBytes);
        long moved = plan.Items.Where(i => i.CanBeUndone).Sum(i => i.Finding.Size);
        long commands = plan.Items.Where(i => i.Outcome == ReviewOutcome.RunCommand).Sum(i => i.Finding.Size);
        Heading = $"Review {Words.Items(plan.Items.Count)}";
        var parts = new List<string>();
        if (freedNow > 0)
        {
            parts.Add($"{SizeLabels.Size(freedNow)} is freed right away");
        }

        if (commands > 0)
        {
            parts.Add($"about {SizeLabels.Size(commands)} by the tools' own commands");
        }

        if (moved > 0)
        {
            parts.Add($"{SizeLabels.Size(moved)} moves where you can restore it (space comes free when you empty it)");
        }

        Subheading = (parts.Count == 0 ? "Nothing here can be cleaned automatically." : string.Join("; ", parts) + ".")
            + " Sizes were measured exactly just now. Here's exactly what happens to each item.";
        Warning = plan.HasPermanentChanges
            ? "Some of these can't be undone: items deleted right away and changes made by commands are not kept anywhere."
            : null;
        CommandsText = string.Join(Environment.NewLine, plan.Items.SelectMany(i => i.Commands.Select(c => i.Outcome == ReviewOutcome.Manual ? "(you run) " + c : c)));
        List<string> blockers = [.. plan.Items.SelectMany(i => i.Blockers).Distinct(StringComparer.Ordinal)];
        FooterIsWarning = blockers.Count > 0;
        Footer = blockers.Count > 0 ? string.Join(" ", blockers) + " Then review again to clean everything." : "Every change is logged in History.";
        CleanLabel = runnable.Count == 0 ? "Nothing to clean" : $"Clean {Words.Items(runnable.Count)}";
        Stage = ReviewStage.Ready;
        Status = string.Empty;
    }

    private bool CanClean() => Stage == ReviewStage.Ready && _plan is { Runnable.Count: > 0 };

    [RelayCommand(CanExecute = nameof(CanClean))]
    public async Task CleanAsync()
    {
        if (_plan is null)
        {
            return;
        }

        IReadOnlyList<PlannedAction> runnable = _plan.Runnable;
        Stage = ReviewStage.Cleaning;
        int done = 0;
        var progress = new Progress<string>(title =>
        {
            done++;
            Status = $"Cleaning {done} of {runnable.Count} · {title}";
            Progress = (double)done / Math.Max(1, runnable.Count);
        });
        IReadOnlyList<ActionOutcome> outcomes = await backend.CleanAsync(runnable, progress, CancellationToken.None);
        ShowOutcomes(outcomes);
    }

    public void ShowOutcomes(IReadOnlyList<ActionOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        Outcomes.Clear();
        foreach (ActionOutcome outcome in outcomes)
        {
            Outcomes.Add(new OutcomeRow(outcome));
        }

        long freed = outcomes.Sum(o => o.Freed);
        int failed = outcomes.Count(o => !o.Succeeded);
        long volumeFreed = outcomes.Where(o => o.Reconciliation is not null).Sum(o => Math.Max(0, o.Reconciliation!.FreedBytes));
        Heading = $"Removed {SizeLabels.Size(freed)}";
        Subheading = (failed == 0 ? "Everything went as planned." : $"{Words.Items(failed)} couldn't be cleaned.")
            + $" The drive reports {SizeLabels.Size(volumeFreed)} actually freed; anything in the Recycle Bin or quarantine frees space when you empty it.";
        Stage = ReviewStage.Done;
        Cleaned?.Invoke(this, outcomes.Where(o => o.Succeeded).Select(o => o.Finding.Id).ToHashSet(StringComparer.Ordinal));
    }

    public void Dispose() => _cancel?.Dispose();

    [RelayCommand]
    public void Close()
    {
        _cancel?.Cancel();
        Stage = ReviewStage.Closed;
    }
}
