using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevSweep.App.ViewModels.Services;
using DevSweep.App.ViewModels.Text;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Review;
using DevSweep.Core.Scanning;

namespace DevSweep.App.ViewModels;

/// <summary>A check as the scan progress panel shows it.</summary>
public sealed partial class CheckProgressViewModel(string id, string title) : ObservableObject
{
    public string Id { get; } = id;

    public string Title { get; } = title;

    [ObservableProperty]
    public partial bool IsRunning { get; set; } = true;

    [ObservableProperty]
    public partial int Found { get; set; }

    public string State => IsRunning ? "Checking…" : Found > 0 ? Words.Count(Found, "item") + " found" : "Done";

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(State));

    partial void OnFoundChanged(int value) => OnPropertyChanged(nameof(State));
}

/// <summary>
/// Runs scans: per-check progress (what is running, what was found), cancel, and the
/// scan report afterwards. Holds the current findings for every place.
/// </summary>
public sealed partial class ScanViewModel(IDevSweepBackend backend) : ObservableObject, IDisposable
{
    private CancellationTokenSource? _cancel;

    public ObservableCollection<CheckProgressViewModel> Checks { get; } = [];

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "Not scanned yet";

    /// <summary>0 to 1.</summary>
    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<Finding> Findings { get; set; } = [];

    [ObservableProperty]
    public partial ScanReport? Report { get; set; }

    [ObservableProperty]
    public partial ScanChanges? Changes { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? LastScan { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public bool HasScanned => Report is not null;

    public long TotalBytes => Report?.TotalBytes ?? Findings.Sum(f => f.Size);

    /// <summary>Raised when a scan finished (not when it was cancelled).</summary>
    public event EventHandler? Finished;

    partial void OnReportChanged(ScanReport? value) => OnPropertyChanged(nameof(HasScanned));

    partial void OnFindingsChanged(IReadOnlyList<Finding> value) => OnPropertyChanged(nameof(TotalBytes));

    /// <summary>Shows a finished scan (sample data and tests use this too).</summary>
    public void Show(ScanResult result, ScanChanges? changes, DateTimeOffset when)
    {
        ArgumentNullException.ThrowIfNull(result);
        Findings = result.Findings;
        Report = result.Report;
        Changes = changes;
        LastScan = when;
        Status = result.Report.Summary;
    }

    /// <summary>Drops findings that were cleaned (their ids).</summary>
    public void Remove(IReadOnlyCollection<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        Findings = [.. Findings.Where(f => !ids.Contains(f.Id))];
    }

    [RelayCommand]
    public async Task ScanAsync()
    {
        if (IsScanning)
        {
            return;
        }

        _cancel = new CancellationTokenSource();
        IsScanning = true;
        Error = null;
        Progress = 0;
        Checks.Clear();
        Status = "Starting…";
        try
        {
            var progress = new Progress<ScanProgress>(OnProgress);
            ScanResult result = await backend.ScanAsync(progress, _cancel.Token);
            long? free = backend.Volumes().FirstOrDefault(v => v.IsSystem)?.FreeBytes;
            ScanChanges? changes = backend.CompareAndRecord(result.Findings, free);
            Show(result, changes, backend.Now);
            Finished?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Status = HasScanned ? "Scan stopped. The results below are from the previous scan." : "Scan stopped. Nothing was changed.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = ex.Message;
            Status = "The scan could not finish.";
        }
        finally
        {
            IsScanning = false;
            _cancel.Dispose();
            _cancel = null;
        }
    }

    [RelayCommand]
    public void Cancel() => _cancel?.Cancel();

    /// <summary>Applies one progress event (public so tests and screenshots can drive it).</summary>
    public void OnProgress(ScanProgress value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Status = value.Text;
        Progress = value.Total == 0 ? 0 : (value.Phase == ScanPhase.Checking ? 0.7 : 0.3) * value.Done / value.Total
            + (value.Phase == ScanPhase.Measuring ? 0.7 : 0);
        CheckProgressViewModel? row = Checks.FirstOrDefault(c => c.Id == value.CheckId);
        if (value.Phase == ScanPhase.Checking && !value.Finished && row is null)
        {
            // The newest running check is the last title in the list (longest-running first).
            HashSet<string> shown = Checks.Where(c => c.IsRunning).Select(c => c.Title).ToHashSet(StringComparer.Ordinal);
            string title = value.Running.LastOrDefault(t => !shown.Contains(t)) ?? value.CheckId;
            Checks.Insert(0, new CheckProgressViewModel(value.CheckId, title));
        }
        else if (value.Phase == ScanPhase.Checking && value.Finished && row is not null)
        {
            row.IsRunning = false;
        }
        else if (value.Phase == ScanPhase.Measuring && value.Finished && row is not null)
        {
            row.Found++;
        }
    }

    public void Dispose() => _cancel?.Dispose();

    public string ProgressText => string.Create(CultureInfo.InvariantCulture, $"{Progress:P0}");
}
