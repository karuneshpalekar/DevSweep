using System.Globalization;
using DevSweep.Core.Detectors;
using DevSweep.Core.Health.Common;

namespace DevSweep.Core.Health.Runtimes;

/// <summary>What one tool's report is built from.</summary>
public sealed record ReportInput
{
    public required RuntimeProbe Probe { get; init; }

    /// <summary>De-duplicated, default marked.</summary>
    public required IReadOnlyList<Installation> Installs { get; init; }

    /// <summary>Support data per endoflife.date product.</summary>
    public IReadOnlyDictionary<string, EolData> Eol { get; init; } = new Dictionary<string, EolData>();

    public CommandResolution? Resolution { get; init; }

    /// <summary>JAVA_HOME as a new program sees it (Java only).</summary>
    public string? JavaHome { get; init; }

    public string? Profile { get; init; }

    public DateOnly Today { get; init; }
}

/// <summary>Builds a tool's report: support status per install, issues and suggested fixes (the Mac report, for Windows owners).</summary>
public static class RuntimeReporter
{
    public static RuntimeReport Build(ReportInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        RuntimeProbe tool = input.Probe;
        DateOnly today = input.Today;
        IReadOnlyList<ReleaseCycle> mainCycles = tool.Product is { } p && input.Eol.TryGetValue(p, out EolData? d) ? d.Cycles : [];

        List<Installation> installs = input.Installs.Select(i => WithSupport(i, i.Product is { } ip && input.Eol.TryGetValue(ip, out EolData? data) ? data.Cycles : mainCycles, tool.Id, today)).ToList();
        installs = installs
            .OrderByDescending(i => i.IsRunning == true)
            .ThenByDescending(i => i.IsDefault)
            .ThenBy(i => i.IsSystem)
            .ThenByDescending(i => i.Version, VersionComparer.Instance)
            .ToList();

        ReleaseCycle? recommended = mainCycles.FirstOrDefault(c => !c.Ended(today) && (c.ReleaseDate is null || c.ReleaseDate <= today) && (!tool.PreferLts || c.IsLts(today)));
        var issues = new List<RuntimeIssue>();
        var steps = new List<RuntimeStep>();
        string name = tool.Name;
        List<Installation> user = installs.Where(i => !i.IsSystem).ToList();
        Installation? def = installs.FirstOrDefault(i => i.IsDefault);

        foreach (Installation i in installs)
        {
            string label = i.Label(name);
            string? when = i.SupportEnds?.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
            switch (i.Support)
            {
                case SupportStatus.EndOfLife when i.IsSystem:
                    issues.Add(new(IssueLevel.Info, $"{label} is past end of life, but it belongs to {(i.Source == InstallSource.ToolCache ? "a CI runner's tool cache" : "an IDE")}. Leave it; your own copy is what matters."));
                    break;
                case SupportStatus.EndOfLife:
                    issues.Add(new(IssueLevel.Critical, $"{label} reached end of life{(when is null ? string.Empty : " on " + when)}. It no longer gets security fixes." + (i.IsDefault ? " It's the one a new terminal runs." : string.Empty)));
                    break;
                case SupportStatus.EndingSoon:
                    int days = i.SupportEnds is { } end ? end.DayNumber - today.DayNumber : 0;
                    issues.Add(new(IssueLevel.Warning, string.Create(CultureInfo.InvariantCulture, $"{label}: support ends {when ?? "soon"} (in {days} days).")));
                    break;
                default:
                    break;
            }
        }

        List<string> patches = installs.Where(i => i.PatchAvailable && i.Support != SupportStatus.EndOfLife && !i.IsSystem)
            .Select(i => $"{i.Version} → {i.LatestInCycle} ({i.SourceTitle})").ToList();
        if (patches.Count > 0)
        {
            issues.Add(new(IssueLevel.Info, $"Newer patch releases (security and bug fixes, same release line): {Words.Join(patches)}."));
        }

        List<string> sources = user.Select(i => i.SourceTitle).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (sources.Count > 1)
        {
            issues.Add(new(IssueLevel.Warning, string.Create(CultureInfo.InvariantCulture, $"Installed by {sources.Count} different tools ({Words.Join(sources)}). Mixing them makes it easy to run a different version than you expect.")));
        }

        foreach (IGrouping<string, Installation> same in user.GroupBy(i => i.Version, StringComparer.OrdinalIgnoreCase).Where(g => g.Select(i => i.Source).Distinct().Count() > 1))
        {
            issues.Add(new(IssueLevel.Info, $"{name} {same.Key} is installed more than once ({Words.Join(same.Select(i => i.SourceTitle).Distinct(StringComparer.Ordinal).ToList())})."));
        }

        if (def is not null && user.Where(i => !i.IsDefault && VersionText.Less(def.Cycle, i.Cycle) && i.Support != SupportStatus.EndOfLife)
                .OrderByDescending(i => i.Version, VersionComparer.Instance).FirstOrDefault() is { } newer)
        {
            issues.Add(new(IssueLevel.Warning, $"A new terminal runs {name} {def.Version} from {def.SourceTitle}, even though {newer.Version} from {newer.SourceTitle} is installed, because it comes first in PATH."));
        }

        issues.AddRange(PathIssues(input, installs, name));
        if (tool.Id == "java" && input.JavaHome is { } javaHome && def is not null && !ProbeKit.IsInside(javaHome, def.Path)
            && installs.FirstOrDefault(i => ProbeKit.IsInside(javaHome, i.Path) || ProbeKit.IsInside(i.Path, javaHome)) is { } fromHome)
        {
            issues.Add(new(IssueLevel.Warning, $"Build tools (Gradle, Maven) use JAVA_HOME, which is Java {fromHome.Version} ({fromHome.SourceTitle}), but `java` in a new terminal is {def.Version}."));
        }

        var seenText = new HashSet<string>(StringComparer.Ordinal);
        issues = issues.Where(i => seenText.Add(i.Text)).ToList();

        int n = 0;
        string NextId(StepKind kind) => string.Create(CultureInfo.InvariantCulture, $"{tool.Id}:{kind.ToString().ToLowerInvariant()}:{++n}");
        Installation? current = def is { IsSystem: false } ? def : user.FirstOrDefault();
        if (current is not null && current.Support is SupportStatus.EndOfLife or SupportStatus.EndingSoon && recommended is not null
            && FixCommands.Install(tool.Id, recommended.Cycle, current.Source, current) is { } install)
        {
            steps.Add(new RuntimeStep
            {
                Id = NextId(StepKind.SwitchDefault),
                Kind = StepKind.SwitchDefault,
                Title = $"Switch to {name} {recommended.Cycle}{(recommended.IsLts(today) ? " LTS" : string.Empty)}",
                Detail = $"Installs {name} {recommended.Cycle} with {InstallSources.Title(current.Source)} and makes it the one a new terminal runs. Your projects may need testing on the new version.",
                Commands = install.Commands,
                NeedsAdmin = install.NeedsAdmin,
            });
        }

        foreach (Installation i in user.Where(i => !i.IsDefault && i.IsRunning != true && (i.Support is SupportStatus.EndOfLife or SupportStatus.EndingSoon)))
        {
            if (FixCommands.Remove(i, tool.Id, input.Profile) is { } remove)
            {
                string why = i.Support == SupportStatus.EndOfLife ? "It's out of support" : "Its support ends soon";
                steps.Add(new RuntimeStep
                {
                    Id = NextId(StepKind.Remove),
                    Kind = StepKind.Remove,
                    Title = $"Remove {i.Label(name)}",
                    Detail = $"{why} and it isn't the one a new terminal runs." + (i.DataPath is { } data ? $" Its data folder ({data}) is kept; delete it yourself if you're sure." : string.Empty),
                    Commands = remove.Commands,
                    NeedsAdmin = remove.NeedsAdmin,
                });
            }
        }

        var upgrades = user.Where(i => i.PatchAvailable && i.Support != SupportStatus.EndOfLife).Select(i => FixCommands.Upgrade(i, tool.Id)).OfType<FixCommand>().ToList();
        if (upgrades.Count > 0)
        {
            steps.Add(new RuntimeStep
            {
                Id = NextId(StepKind.Upgrade),
                Kind = StepKind.Upgrade,
                Title = "Install the latest patch releases",
                Detail = "Security and bug fixes within the same release line. Safe for your projects.",
                Commands = upgrades.SelectMany(u => u.Commands).Distinct(StringComparer.Ordinal).ToList(),
                NeedsAdmin = upgrades.Any(u => u.NeedsAdmin),
            });
        }

        string? rec = recommended is null ? null : $"{name} {recommended.Cycle}{(recommended.IsLts(today) ? " LTS" : string.Empty)}"
            + (recommended.EolDate is { } ends ? ", supported until " + ends.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : string.Empty);
        EolData? main = tool.Product is { } product ? input.Eol.GetValueOrDefault(product) : null;
        return new RuntimeReport
        {
            Id = tool.Id,
            Name = name,
            Installs = installs,
            Issues = issues,
            Steps = steps,
            Recommended = rec,
            DataSource = tool.Product is null ? null : $"endoflife.date/{tool.Product}",
            CheckedAt = main?.Fetched,
            Note = tool.Id == "java" ? "Support dates follow each JDK's vendor where endoflife.date has it (Temurin otherwise)." : null,
            Resolution = input.Resolution,
            NotInstalls = input.Resolution?.Hits.Where(h => h.Alias is { Kind: AliasKind.StoreInstallerStub }).Select(h => $"{h.Path}: {h.Alias!.Describe()}").ToList() ?? [],
        };
    }

    /// <summary>Applies a release line's support data to one install.</summary>
    public static Installation WithSupport(Installation install, IReadOnlyList<ReleaseCycle> cycles, string toolId, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(cycles);
        if (cycles.FirstOrDefault(c => string.Equals(c.Cycle, install.Cycle, StringComparison.OrdinalIgnoreCase)) is { } c)
        {
            string? latest = c.Latest;
            string version = install.Version;
            if (toolId == "java" && latest is not null)
            {
                latest = VersionText.Prefix(latest, 3);
                version = VersionText.Prefix(version, 3);
            }

            return install with
            {
                Version = version,
                Support = c.Status(today),
                ReleaseDate = c.ReleaseDate,
                SupportEnds = c.EolDate,
                LatestInCycle = VersionText.Parts(install.Version).Count >= 3 ? latest : null,
                IsLts = c.IsLts(today),
            };
        }

        // Older than every listed line means long out of support.
        return cycles.Count > 0 && VersionText.Less(install.Cycle, cycles[^1].Cycle) ? install with { Support = SupportStatus.EndOfLife } : install;
    }

    /// <summary>What PATH order means: a Store stub that wins, a version manager that is shadowed, shells that may differ.</summary>
    private static List<RuntimeIssue> PathIssues(ReportInput input, IReadOnlyList<Installation> installs, string name)
    {
        var issues = new List<RuntimeIssue>();
        if (input.Resolution is not { } r || r.Hits.Count == 0)
        {
            return issues;
        }

        PathHit first = r.Hits[0];
        if (first.Alias is { Kind: AliasKind.StoreInstallerStub })
        {
            Installation? real = installs.FirstOrDefault(i => !i.IsSystem);
            issues.Add(new(IssueLevel.Warning, real is null
                ? $"Typing {r.Command} opens the Microsoft Store instead of running {name}: it's the Store's alias in {Path.GetDirectoryName(first.Path)}, not an install."
                : $"Typing {r.Command} in a new terminal opens the Microsoft Store instead of running {real.Label(name)}, because the Store's alias comes first in PATH. Turn the alias off in Settings > Apps > Advanced app settings > App execution aliases, or put {name}'s folder earlier in PATH."));
        }

        Installation? winner = installs.FirstOrDefault(i => i.IsDefault);
        foreach (PathHit later in r.Hits.Skip(1))
        {
            if (later.Alias is not null)
            {
                continue;
            }

            Installation? behind = installs.FirstOrDefault(i => ProbeKit.IsInside(ShimResolver.FollowLinks(later.Path), ShimResolver.FollowLinks(i.Path)));
            bool manager = later.Path.Contains("nvm", StringComparison.OrdinalIgnoreCase) || later.Path.Contains("Volta", StringComparison.OrdinalIgnoreCase)
                || later.Path.Contains("scoop", StringComparison.OrdinalIgnoreCase) || later.Path.Contains("pyenv", StringComparison.OrdinalIgnoreCase)
                || behind?.Source is InstallSource.Nvm or InstallSource.Fnm or InstallSource.Volta or InstallSource.Pyenv or InstallSource.Scoop;
            if (manager && (behind is null || behind != winner))
            {
                issues.Add(new(IssueLevel.Warning, $"{later.Path} ({(behind is null ? "a version manager" : behind.SourceTitle)}) comes after {first.Path} in PATH, so switching versions with it has no effect in a new terminal."));
            }
        }

        foreach (string shell in r.MayDiffer)
        {
            issues.Add(new(IssueLevel.Info, $"May differ in {shell}."));
        }

        return issues;
    }
}
