using System.Globalization;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Inventory;
using DevSweep.Core.Rules;

namespace DevSweep.Core.Scanning;

/// <summary>How one check ended.</summary>
public enum CheckOutcome
{
    Found,
    NothingFound,

    /// <summary>Took longer than the check time limit; skipped, so one slow folder cannot hold up the scan.</summary>
    TimedOut,

    /// <summary>The detector failed (the error is in the report).</summary>
    Failed,
}

/// <summary>What one check did (the Mac ScanReport.Check, plus Windows detail).</summary>
public sealed record CheckReport
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Category { get; init; }

    public required string Kind { get; init; }

    /// <summary>Time spent detecting.</summary>
    public double Seconds { get; init; }

    /// <summary>Findings it produced (after size limits and ignores).</summary>
    public int Found { get; init; }

    public CheckOutcome Outcome { get; init; }

    /// <summary>Time spent measuring what it found.</summary>
    public double MeasureSeconds { get; init; }

    /// <summary>Its findings whose size is partial (time limit, unreadable files).</summary>
    public int PartialSizes { get; init; }

    /// <summary>Folders it could not list.</summary>
    public IReadOnlyList<string> AccessDenied { get; init; } = [];

    /// <summary>Locations it resolved, and how ("npm cache: ... (from NPM_CONFIG_CACHE)").</summary>
    public IReadOnlyList<string> Locations { get; init; } = [];

    /// <summary>Things it left alone and why.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public string? Error { get; init; }
}

/// <summary>
/// What a scan did, check by check, so "nothing found" can be told from "did not
/// run" (same purpose as the Mac ScanReport): timings, outcomes, partial sizes,
/// access denied, bytes shared between findings, inventory sources, rule errors.
/// </summary>
public sealed record ScanReport
{
    public DateTimeOffset Date { get; init; }

    public double TotalSeconds { get; init; }

    public double MeasuringSeconds { get; init; }

    public double CheckTimeoutSeconds { get; init; }

    public double MeasureTimeoutSeconds { get; init; }

    public SizeMode Mode { get; init; }

    public IReadOnlyList<CheckReport> Checks { get; init; } = [];

    /// <summary>Findings whose measuring hit the time limit or could not read everything ("at least" sizes).</summary>
    public int Unmeasured { get; init; }

    /// <summary>The scan total: every finding's size, with bytes shared between findings and items inside other items counted once.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Bytes of hard-linked files that appear in more than one finding, counted once in <see cref="TotalBytes"/>.</summary>
    public long SharedBetweenFindingsBytes { get; init; }

    /// <summary>Bytes of findings that lie inside another finding's folder, counted once in <see cref="TotalBytes"/>.</summary>
    public long NestedBytes { get; init; }

    /// <summary>Every folder some check could not list (first 100).</summary>
    public IReadOnlyList<string> AccessDenied { get; init; } = [];

    /// <summary>Rule pack problems (those packs were skipped).</summary>
    public IReadOnlyList<RuleError> RuleErrors { get; init; } = [];

    /// <summary>Inventory sources, when a check needed the inventory.</summary>
    public IReadOnlyList<SourceReport> Inventory { get; init; } = [];

    /// <summary>Feature flags that turned something off for this scan.</summary>
    public IReadOnlyList<string> Disabled { get; init; } = [];

    public IReadOnlyList<CheckReport> TimedOut => Checks.Where(c => c.Outcome == CheckOutcome.TimedOut).ToList();

    public IReadOnlyList<CheckReport> Failed => Checks.Where(c => c.Outcome == CheckOutcome.Failed).ToList();

    /// <summary>"Finished in 16 s · 50 checks · 2 skipped (too slow) · 1 only partly measured".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { $"Finished in {Duration(TotalSeconds)}", Count(Checks.Count, "check") };
            if (TimedOut.Count > 0)
            {
                parts.Add($"{TimedOut.Count} skipped (too slow)");
            }

            if (Failed.Count > 0)
            {
                parts.Add($"{Failed.Count} failed");
            }

            if (AccessDenied.Count > 0)
            {
                parts.Add($"{Count(AccessDenied.Count, "folder")} not readable");
            }

            if (Unmeasured > 0)
            {
                parts.Add($"{Unmeasured} only partly measured");
            }

            if (RuleErrors.Count > 0)
            {
                parts.Add(Count(RuleErrors.Count, "rule pack problem"));
            }

            return string.Join(" · ", parts);
        }
    }

    public static string Duration(double seconds)
    {
        if (seconds < 1)
        {
            return "under a second";
        }

        if (seconds < 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{Math.Round(seconds):0} s");
        }

        int minutes = (int)seconds / 60;
        int rest = (int)seconds % 60;
        return rest == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{minutes} min")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes} min {rest} s");
    }

    private static string Count(int n, string noun) => string.Create(CultureInfo.InvariantCulture, $"{n} {noun}{(n == 1 ? "" : "s")}");
}

/// <summary>Findings and what the scan did.</summary>
public sealed record ScanResult(IReadOnlyList<Finding> Findings, ScanReport Report);

/// <summary>Which half of the scan a progress event is about.</summary>
public enum ScanPhase
{
    Checking,
    Measuring,
}

/// <summary>A progress event: a check (or a measurement) started or finished.</summary>
/// <param name="Phase">Checking or measuring.</param>
/// <param name="Done">How many are finished.</param>
/// <param name="Total">How many there are.</param>
/// <param name="CheckId">The rule the event is about.</param>
/// <param name="Finished">False when it started, true when it finished.</param>
/// <param name="Running">Titles still running, longest-running first.</param>
public sealed record ScanProgress(ScanPhase Phase, int Done, int Total, string CheckId, bool Finished, IReadOnlyList<string> Running)
{
    /// <summary>"Checking 21 of 50 · Gradle caches, npm cache" or "Measuring sizes · 40 of 85".</summary>
    public string Text
    {
        get
        {
            string names = string.Join(", ", Running.Take(2)) + (Running.Count > 2 ? $" and {Running.Count - 2} more" : string.Empty);
            string head = Phase == ScanPhase.Checking
                ? string.Create(CultureInfo.InvariantCulture, $"Checking {Math.Min(Done + 1, Total)} of {Total}")
                : string.Create(CultureInfo.InvariantCulture, $"Measuring sizes · {Done} of {Total}");
            return names.Length == 0 ? head : $"{head} · {names}";
        }
    }
}

/// <summary>Options for one scan.</summary>
public sealed record ScanOptions
{
    /// <summary>A check that runs longer is skipped (its partial work is discarded).</summary>
    public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>Measuring one finding stops after this long; it keeps the size counted so far, marked "at least".</summary>
    public TimeSpan MeasureTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public SizeMode Mode { get; init; } = SizeMode.Overview;

    /// <summary>Only these categories; null means all.</summary>
    public IReadOnlySet<Category>? Categories { get; init; }

    /// <summary>Finding ids the user chose to ignore.</summary>
    public IReadOnlySet<string> Ignored { get; init; } = new HashSet<string>();

    /// <summary>Checks (and measurements) running at once.</summary>
    public int MaxParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount, 2, 8);

    public IProgress<ScanProgress>? Progress { get; init; }

    /// <summary>Rule load problems to carry into the report.</summary>
    public IReadOnlyList<RuleError> RuleErrors { get; init; } = [];

    /// <summary>What feature flags turned off, for the report.</summary>
    public IReadOnlyList<string> Disabled { get; init; } = [];
}
