using System.Globalization;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Platform;
using DevSweep.Core.Rules;
using DevSweep.Core.Scanning;

namespace DevSweep.App.ViewModels.Text;

/// <summary>The Mac risk labels, with Windows wording for admin items.</summary>
public static class RiskLabels
{
    public static string Title(Risk risk) => risk switch
    {
        Risk.Rebuilds => "Rebuilds itself",
        Risk.OldVersion => "Old version",
        Risk.Leftover => "Leftover from a removed app",
        Risk.HoldsData => "Holds your data",
        Risk.NeedsAdmin => "Needs admin",
        _ => risk.ToString(),
    };

    public static string Summary(Risk risk) => risk switch
    {
        Risk.Rebuilds => "Comes back on its own when a tool needs it.",
        Risk.OldVersion => "A newer version is installed or nothing uses it.",
        Risk.Leftover => "The app that made it is gone.",
        Risk.HoldsData => "May contain your own data. Back up first.",
        Risk.NeedsAdmin => "Lives outside your user folder and needs an administrator. DevSweep shows the command; you run it, and Windows asks (UAC).",
        _ => string.Empty,
    };

    /// <summary>Tone for the badge colour.</summary>
    public static Tone Tone(Risk risk) => risk switch
    {
        Risk.Rebuilds => Text.Tone.Good,
        Risk.OldVersion => Text.Tone.Accent,
        Risk.Leftover => Text.Tone.Caution,
        Risk.HoldsData => Text.Tone.Critical,
        _ => Text.Tone.Neutral,
    };
}

/// <summary>Badge colours, resolved to theme brushes by the views (light, dark and high contrast).</summary>
public enum Tone
{
    Neutral,
    Accent,
    Good,
    Caution,
    Critical,
}

/// <summary>Kinds of things to clean, shown as filter chips (the Mac CleanupKind, plus Windows ones).</summary>
public enum CleanupKind
{
    Caches,
    OldVersions,
    LargeFiles,
    DockerAndWsl,
    Backups,
    Leftovers,
    ProjectFiles,
    WindowsFiles,
}

public static class CleanupKinds
{
    public static IReadOnlyList<CleanupKind> All { get; } = Enum.GetValues<CleanupKind>();

    public static string Title(CleanupKind kind) => kind switch
    {
        CleanupKind.Caches => "Caches",
        CleanupKind.OldVersions => "Old and unused",
        CleanupKind.LargeFiles => "Large files",
        CleanupKind.DockerAndWsl => "Docker and WSL",
        CleanupKind.Backups => "Backups",
        CleanupKind.Leftovers => "Leftovers",
        CleanupKind.ProjectFiles => "Project dependencies",
        CleanupKind.WindowsFiles => "Windows",
        _ => kind.ToString(),
    };

    public static CleanupKind Of(Finding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return finding.Category switch
        {
            Category.LargeFiles => CleanupKind.LargeFiles,
            Category.Docker or Category.Wsl => CleanupKind.DockerAndWsl,
            Category.Backups => CleanupKind.Backups,
            Category.PackageCaches or Category.Browsers or Category.AiModels => CleanupKind.Caches,
            Category.IdeVersions or Category.Android or Category.Toolchains => CleanupKind.OldVersions,
            Category.Dotnet or Category.VisualStudio => finding.Risk == Risk.Rebuilds ? CleanupKind.Caches : CleanupKind.OldVersions,
            Category.Leftovers or Category.BackgroundServices => CleanupKind.Leftovers,
            Category.Projects => CleanupKind.ProjectFiles,
            _ => CleanupKind.WindowsFiles,
        };
    }
}

/// <summary>One badge next to a size: why the number is not simply exact.</summary>
public sealed record SizeBadge(string Text, string Tooltip, Tone Tone);

/// <summary>
/// Sizes with their confidence. A size is never shown bare when it is not exact:
/// it carries a badge (and the same text as a tooltip) for overview, partial,
/// shared links, cloud placeholders, access denied or tool-reported sizes.
/// </summary>
public static class SizeLabels
{
    public static string Size(long bytes) => bytes <= 0 ? "—" : SizeText.Format(bytes);

    /// <summary>"1.2 GB", or "at least 1.2 GB" when the walk stopped or could not read everything.</summary>
    public static string Size(Finding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (finding.Size <= 0)
        {
            return finding.Measurement == SizeMeasurement.Reported ? "size unknown" : "—";
        }

        return finding.Confidence is SizeConfidence.Partial or SizeConfidence.AccessDenied
            ? "at least " + SizeText.Format(finding.Size)
            : SizeText.Format(finding.Size);
    }

    public static IReadOnlyList<SizeBadge> Badges(Finding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        var badges = new List<SizeBadge>();
        switch (finding.Measurement)
        {
            case SizeMeasurement.Overview:
                badges.Add(new SizeBadge("Overview",
                    "Measured quickly, without alternate data streams. Review measures it exactly before anything changes.", Tone.Neutral));
                break;
            case SizeMeasurement.Reported:
                badges.Add(new SizeBadge("Reported",
                    "This size comes from the tool that owns it, not from DevSweep measuring the files.", Tone.Neutral));
                break;
        }

        switch (finding.Confidence)
        {
            case SizeConfidence.Partial:
                badges.Add(new SizeBadge("Partial",
                    "Measuring stopped before it counted everything (time limit or unreadable files), so the real size is larger.", Tone.Caution));
                break;
            case SizeConfidence.AccessDenied:
                badges.Add(new SizeBadge("Access denied",
                    "Some folders inside could not be read, so the real size is larger. DevSweep never changes permissions to look.", Tone.Caution));
                break;
            case SizeConfidence.CloudPlaceholders:
                badges.Add(new SizeBadge("Cloud files",
                    "Some files are online-only placeholders (OneDrive and similar). They take no space here and count as 0; removing them would remove them from the cloud too.", Tone.Caution));
                break;
            case SizeConfidence.SharedLinks:
                badges.Add(new SizeBadge("Shared links",
                    string.Create(CultureInfo.InvariantCulture,
                        $"Some files are hard links shared with places outside this item. Removing it frees {SizeText.Format(finding.FreeableBytes)}, not the full size."),
                    Tone.Accent));
                break;
        }

        return badges;
    }

    /// <summary>The tooltip for a size: every badge's explanation, or "Exact" when there is none.</summary>
    public static string Tooltip(Finding finding)
    {
        IReadOnlyList<SizeBadge> badges = Badges(finding);
        return badges.Count == 0
            ? "Exact: every file measured on disk, alternate data streams included, hard links counted once."
            : string.Join(Environment.NewLine, badges.Select(b => $"{b.Text}: {b.Tooltip}"));
    }
}

/// <summary>Freed-space reconciliation in words (shown vs actually freed, PLAN 6).</summary>
public static class ReconciliationText
{
    public static string Describe(ActionOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Reconciliation is not { } r)
        {
            return string.Empty;
        }

        string volume = r.Volume.TrimEnd('\\');
        bool onlyMoved = outcome.Items.Count > 0 && outcome.Items.All(i => i.Destination != ItemDestination.Deleted);
        if (onlyMoved)
        {
            string where = outcome.Items.All(i => i.Destination == ItemDestination.RecycleBin) ? "the Recycle Bin" : "DevSweep's quarantine";
            return $"Shown {SizeLabels.Size(r.ShownBytes)}. Nothing is freed on {volume} yet: it's in {where} until you empty it.";
        }

        string freed = r.FreedBytes >= 0 ? SizeText.Format(r.FreedBytes) : "-" + SizeText.Format(-r.FreedBytes);
        string verdict = r.WithinTolerance
            ? "as expected"
            : string.Create(CultureInfo.InvariantCulture,
                $"{SizeText.Format(Math.Abs(r.Difference))} {(r.Difference < 0 ? "less" : "more")} than expected (other programs may have written to the drive meanwhile)");
        return $"Shown {SizeLabels.Size(r.ShownBytes)} · expected {SizeLabels.Size(r.ExpectedFreedBytes)} · {volume} actually freed {freed}, {verdict}.";
    }
}

/// <summary>Small text helpers.</summary>
public static class Words
{
    public static string Count(int n, string noun) => string.Create(CultureInfo.InvariantCulture, $"{n} {noun}{(n == 1 ? "" : "s")}");

    public static string Items(int n) => Count(n, "item");

    /// <summary>"just now", "5 minutes ago", "yesterday", "3 days ago", or the date.</summary>
    public static string Relative(DateTimeOffset date, DateTimeOffset now)
    {
        TimeSpan ago = now - date;
        if (ago < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (ago < TimeSpan.FromHours(1))
        {
            return Count((int)ago.TotalMinutes, "minute") + " ago";
        }

        if (ago < TimeSpan.FromDays(1))
        {
            return Count((int)ago.TotalHours, "hour") + " ago";
        }

        if (ago < TimeSpan.FromDays(2))
        {
            return "yesterday";
        }

        return ago < TimeSpan.FromDays(30)
            ? Count((int)ago.TotalDays, "day") + " ago"
            : date.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    }

    public static string Day(DateTimeOffset date) => date.ToLocalTime().ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture);

    public static string Time(DateTimeOffset date) => date.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Shortens a path under the profile to <c>~\...</c>.</summary>
    public static string Path(string path, string? profile)
    {
        ArgumentNullException.ThrowIfNull(path);
        return profile is { Length: > 0 } && path.StartsWith(profile, StringComparison.OrdinalIgnoreCase)
            ? "~" + path[profile.Length..]
            : path;
    }
}
