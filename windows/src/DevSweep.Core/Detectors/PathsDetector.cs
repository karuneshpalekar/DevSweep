using System.Globalization;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Inventory;
using DevSweep.Core.Locations;
using DevSweep.Core.Rules;

namespace DevSweep.Core.Detectors;

/// <summary>
/// <c>kind: paths</c>. Fixed paths or wildcards under named roots.
/// <list type="bullet">
/// <item><c>paths</c>: rule paths (required);</item>
/// <item><c>groupEach</c>: one finding per match instead of one for all;</item>
/// <item><c>olderThanDays</c>: only items where nothing inside changed or was added in that many days;</item>
/// <item><c>exclude</c>: names (wildcards allowed) never to list;</item>
/// <item><c>requireApps</c>: only look while one of these apps is installed (display names, as in the
/// installed-apps list; <c>requireAppExes</c> and <c>requireAppPackageIds</c> add program file names and
/// winget/Scoop ids). Once the app is gone, the leftovers check covers its folders (Mac <c>requireApps</c>).
/// When the installed-apps list can't say, the rule still looks.</item>
/// </list>
/// </summary>
public sealed class PathsDetector : Detector<PathsDetector.Options>
{
    public override string Kind => "paths";

    protected override Options? BindOptions(OptionReader options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new Options(
            options.Paths("paths", required: true),
            options.Flag("groupEach"),
            options.Number("olderThanDays", 1, 3650),
            options.TextList("exclude"),
            RequiredApp(options.TextList("requireApps"), options.TextList("requireAppExes"), options.TextList("requireAppPackageIds")));
    }

    /// <summary>The app a rule needs installed, or null when the rule doesn't name one.</summary>
    public static AppHint? RequiredApp(IReadOnlyList<string> names, IReadOnlyList<string> exes, IReadOnlyList<string> packageIds)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(exes);
        ArgumentNullException.ThrowIfNull(packageIds);
        if (names.Count == 0 && exes.Count == 0 && packageIds.Count == 0)
        {
            return null;
        }

        return new AppHint
        {
            Name = names.Count > 0 ? names[0] : exes.Count > 0 ? Path.GetFileNameWithoutExtension(exes[0]) : packageIds[0],
            Names = names,
            Exes = exes,
            PackageIds = packageIds,
        };
    }

    protected override IReadOnlyList<Candidate> Detect(DetectorRun run, Options options)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);
        var matches = new List<ProbeEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var checks = new List<Check>();
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        DateTime? cutoff = options.OlderThanDays is int days ? run.Context.Now.UtcDateTime.AddDays(-days) : null;
        int recent = 0;
        int unknownAge = 0;
        if (options.RequireApp is { } app)
        {
            if (!run.Context.HasInventory)
            {
                run.Note($"{app.Name}: no installed-apps list in this scan, so its folders were looked at anyway");
            }
            else
            {
                InstallMatch match = run.Context.Inventory.IsInstalled(app);
                if (match.IsGone)
                {
                    run.Note($"{app.Name} isn't installed, so nothing is offered here; the leftovers check covers its folders");
                    return [];
                }

                checks.Add(match.IsInstalled
                    ? Check.Passed($"{app.Name} is installed ({InventorySources.Title(match.Evidence[0].Source)})")
                    : Check.Warning($"Whether {app.Name} is installed couldn't be confirmed ({match.UnknownBecause})"));
            }
        }

        foreach (PathTemplate template in options.Paths)
        {
            PathExpansion expansion = run.Expand(template);
            if (expansion.Root.Path is { } rootPath)
            {
                facts.TryAdd(expansion.Root.Name, rootPath);
                facts.TryAdd("location", rootPath);
            }

            checks.Add(Check.Passed(expansion.Root.Describe()));
            checks.AddRange(expansion.Skipped.Select(path => Check.Warning($"{path} is a link or cloud folder; DevSweep does not follow it.")));
            foreach (ProbeEntry entry in expansion.Matches)
            {
                if (options.Exclude.Any(pattern => Glob.IsMatch(pattern, entry.Name)) || !seen.Add(entry.Path))
                {
                    continue;
                }

                if (cutoff is { } limit)
                {
                    DateTime? newest = ItemAge.Newest(entry, run);
                    if (newest is null)
                    {
                        unknownAge++;
                        continue;
                    }

                    if (newest >= limit)
                    {
                        recent++;
                        continue;
                    }
                }

                matches.Add(entry);
            }
        }

        if (options.OlderThanDays is int olderThan)
        {
            facts["days"] = olderThan.ToString(CultureInfo.InvariantCulture);
            checks.Add(Check.Passed(string.Create(CultureInfo.InvariantCulture,
                $"Only items where nothing changed or was added in the last {olderThan} days; {recent} newer item{(recent == 1 ? "" : "s")} left alone")));
            if (unknownAge > 0)
            {
                checks.Add(Check.Passed(string.Create(CultureInfo.InvariantCulture,
                    $"{unknownAge} item{(unknownAge == 1 ? "" : "s")} left alone because their age could not be read completely")));
            }
        }

        if (matches.Count == 0)
        {
            return [];
        }

        if (options.GroupEach)
        {
            return matches.Select(entry => new Candidate
            {
                Key = entry.Name,
                Paths = [entry.Path],
                Facts = new Dictionary<string, string>(facts) { ["name"] = entry.Name },
                Checks = checks,
            }).ToList();
        }

        return [new Candidate { Key = "all", Paths = matches.Select(m => m.Path).ToList(), Facts = facts, Checks = checks }];
    }

    /// <summary>Bound options of a <c>paths</c> rule.</summary>
    public sealed record Options(IReadOnlyList<PathTemplate> Paths, bool GroupEach, int? OlderThanDays, IReadOnlyList<string> Exclude, AppHint? RequireApp = null);
}
