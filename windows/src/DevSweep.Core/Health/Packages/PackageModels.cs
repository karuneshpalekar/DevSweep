using DevSweep.Core.Health.Runtimes;

namespace DevSweep.Core.Health.Packages;

/// <summary>The package managers Health reports on (the Windows counterpart of the Mac's Homebrew report).</summary>
public enum PackageManager
{
    Winget,
    Scoop,
    Chocolatey,
}

/// <summary>A package with a newer version available.</summary>
/// <param name="Name">Display name.</param>
/// <param name="Id">The id the manager upgrades it by.</param>
/// <param name="Installed">Installed version.</param>
/// <param name="Latest">Available version.</param>
/// <param name="Pinned">The manager is told to leave it alone (a pin or a hold).</param>
public sealed record OutdatedPackage(string Name, string Id, string Installed, string Latest, bool Pinned = false);

/// <summary>A package its manager no longer offers (removed from its bucket, for example).</summary>
public sealed record DeprecatedPackage(string Name, string Installed, string Reason);

/// <summary>One of the manager's download or build caches.</summary>
/// <param name="Path">Folder.</param>
/// <param name="Title">What it holds ("Scoop downloads").</param>
/// <param name="Bytes">Size on disk (a lower bound when <paramref name="Exact"/> is false).</param>
/// <param name="Exact">Whether the whole folder was measured.</param>
public sealed record PackageCache(string Path, string Title, long Bytes, bool Exact);

/// <summary>A manager's folder that no package is using any more.</summary>
public sealed record OrphanFolder(string Path, string Why);

/// <summary>What one package manager says about this PC.</summary>
public sealed record PackageManagerReport
{
    public required PackageManager Manager { get; init; }

    public string Id => Manager.ToString().ToLowerInvariant();

    public string Name => Manager == PackageManager.Chocolatey ? "Chocolatey" : Manager == PackageManager.Scoop ? "Scoop" : "winget";

    /// <summary>Where it lives (its program or its root folder).</summary>
    public string? Location { get; init; }

    /// <summary>The result could not be read (timed out, localised or changed output); empty lists then mean "unknown", not "none".</summary>
    public string? Problem { get; init; }

    public int? InstalledCount { get; init; }

    public IReadOnlyList<OutdatedPackage> Outdated { get; init; } = [];

    public IReadOnlyList<DeprecatedPackage> Deprecated { get; init; } = [];

    public IReadOnlyList<PackageCache> Caches { get; init; } = [];

    public IReadOnlyList<OrphanFolder> Orphans { get; init; } = [];

    public IReadOnlyList<RuntimeStep> Steps { get; init; } = [];

    /// <summary>How fresh the answer is ("as fresh as your last scoop update").</summary>
    public string? Note { get; init; }

    public IReadOnlyList<RuntimeIssue> Issues
    {
        get
        {
            var issues = new List<RuntimeIssue>();
            if (Problem is { } problem)
            {
                issues.Add(new RuntimeIssue(IssueLevel.Info, problem));
            }

            if (Deprecated.Count > 0)
            {
                issues.Add(new RuntimeIssue(IssueLevel.Warning, $"{Deprecated.Count} package{(Deprecated.Count == 1 ? " is" : "s are")} no longer offered by {Name}."));
            }

            if (Outdated.Count > 0)
            {
                issues.Add(new RuntimeIssue(IssueLevel.Warning, $"{Outdated.Count} package{(Outdated.Count == 1 ? " has" : "s have")} a newer version."));
            }

            if (Orphans.Count > 0)
            {
                issues.Add(new RuntimeIssue(IssueLevel.Info, $"{Orphans.Count} leftover folder{(Orphans.Count == 1 ? string.Empty : "s")} that no package uses."));
            }

            return issues;
        }
    }
}

/// <summary>The same tool installed by two owners.</summary>
public sealed record DuplicateInstall(string Tool, string ToolName, string Cycle, IReadOnlyList<string> Owners)
{
    public string Text => $"{ToolName} {Cycle} is installed more than once: by {string.Join(" and by ", Owners)}. Only one of them runs; the other takes space and may be updated separately.";
}

/// <summary>Everything the package-manager row and panel show.</summary>
public sealed record PackagesResult
{
    /// <summary>Managers that are installed on this PC.</summary>
    public IReadOnlyList<PackageManagerReport> Managers { get; init; } = [];

    public IReadOnlyList<DuplicateInstall> Duplicates { get; init; } = [];

    public DateTimeOffset Date { get; init; }

    public int Outdated => Managers.Sum(m => m.Outdated.Count);

    public int Deprecated => Managers.Sum(m => m.Deprecated.Count);

    /// <summary>Things worth a look (outdated or removed packages, duplicates).</summary>
    public int AttentionCount => Outdated + Deprecated + Duplicates.Count;
}
