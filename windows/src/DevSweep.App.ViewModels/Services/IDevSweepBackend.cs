using DevSweep.App.ViewModels.Places;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Platform.Quarantine;
using DevSweep.Core.Health.Alerts;
using DevSweep.Core.Review;
using DevSweep.Core.Scanning;

namespace DevSweep.App.ViewModels.Services;

/// <summary>A module the app knows about, for navigation and Settings.</summary>
/// <param name="Id">The flag name (<c>module:&lt;Id&gt;</c>).</param>
/// <param name="Title">Its title.</param>
/// <param name="State">Registered and on, turned off by a flag, or not in this build.</param>
public sealed record ModuleInfo(string Id, string Title, ModuleState State)
{
    public bool IsOn => State == ModuleState.On;
}

public enum ModuleState
{
    On,
    TurnedOff,
    NotInThisBuild,
}

/// <summary>Free space on one fixed volume (GetDiskFreeSpaceExW, through DriveInfo).</summary>
/// <param name="Root">"C:\".</param>
/// <param name="Label">Volume label, or "Local Disk".</param>
/// <param name="FreeBytes">Free to this user (quotas included).</param>
/// <param name="TotalBytes">Volume size.</param>
/// <param name="IsSystem">The volume holding the profile (where most findings are).</param>
public sealed record VolumeSpace(string Root, string Label, long FreeBytes, long TotalBytes, bool IsSystem)
{
    public string Name => $"{Label} ({Root.TrimEnd('\\')})";
}

/// <summary>The update check M5 brings; a stub until then.</summary>
public interface IUpdateChecker
{
    bool IsAvailable { get; }

    /// <summary>A sentence for Settings ("You're up to date", "Version 1.1 is available").</summary>
    Task<string> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>Until M5: says so, contacts nothing.</summary>
public sealed class NoUpdateChecker : IUpdateChecker
{
    public bool IsAvailable => false;

    public Task<string> CheckAsync(CancellationToken cancellationToken)
        => Task.FromResult("Checking for updates arrives in a later version. Nothing was sent anywhere.");
}

/// <summary>
/// Everything the view models ask of the engine. <see cref="EngineBackend"/> is the real one
/// (DevSweep.Core); <see cref="Sample.SampleBackend"/> serves deterministic sample data for
/// screenshots and tests. View models never touch files themselves.
/// </summary>
public interface IDevSweepBackend
{
    IReadOnlyList<ModuleInfo> Modules { get; }

    /// <summary>The Health source when the <c>health</c> module is on and has an adapter.</summary>
    IHealthSource? Health { get; }

    IProjectsSource? Projects { get; }

    IUpdateChecker Updates { get; }

    /// <summary>The profile folder, for shortening paths to <c>~\</c>.</summary>
    string? ProfilePath { get; }

    /// <summary>Where settings, History and the quarantine live.</summary>
    string DataFolder { get; }

    DateTimeOffset Now { get; }

    /// <summary>Turns a module on or off for the next start (<c>features.json</c>).</summary>
    void SetModuleEnabled(string id, bool enabled);

    Task<ScanResult> ScanAsync(IProgress<ScanProgress> progress, CancellationToken cancellationToken);

    /// <summary>Exact re-measure (alternate data streams on) before Review shows a size.</summary>
    Task<Finding> RemeasureAsync(Finding finding, CancellationToken cancellationToken);

    /// <summary>What the cleaner will do with each item (Recycle Bin, quarantine, delete, command, manual, blocked).</summary>
    Task<ReviewPlan> PrepareAsync(IReadOnlyList<PlannedAction> plan, CancellationToken cancellationToken);

    Task<IReadOnlyList<ActionOutcome>> CleanAsync(IReadOnlyList<PlannedAction> plan, IProgress<string> progress, CancellationToken cancellationToken);

    IReadOnlyList<HistoryEntry> History();

    /// <summary>True while something of the entry can still be put back.</summary>
    bool CanRestore(HistoryEntry entry);

    Task<HistoryRestoreResult> RestoreAsync(Guid id);

    IReadOnlyList<QuarantineEntry> Quarantine();

    /// <summary>Deletes everything in the quarantine for good; returns bytes removed.</summary>
    Task<long> EmptyQuarantineAsync();

    IReadOnlyList<VolumeSpace> Volumes();

    /// <summary>
    /// Opens commands in a visible PowerShell window that lists them and waits for Enter
    /// (M3b's TerminalScript); admin ones offer to reopen as administrator, so Windows asks (UAC).
    /// </summary>
    void OpenInTerminal(string title, IReadOnlyList<string> commands, bool needsAdmin);

    /// <summary>What grew or is new since an earlier scan (null on a first scan); records this scan.</summary>
    ScanChanges? CompareAndRecord(IReadOnlyList<Finding> findings, long? diskFree);
}
