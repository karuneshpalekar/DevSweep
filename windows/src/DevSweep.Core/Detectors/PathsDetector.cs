using System.Globalization;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Locations;
using DevSweep.Core.Rules;

namespace DevSweep.Core.Detectors;

/// <summary>
/// <c>kind: paths</c>. Fixed paths or wildcards under named roots.
/// <list type="bullet">
/// <item><c>paths</c>: rule paths (required);</item>
/// <item><c>groupEach</c>: one finding per match instead of one for all;</item>
/// <item><c>olderThanDays</c>: only items where nothing inside changed or was added in that many days;</item>
/// <item><c>exclude</c>: names (wildcards allowed) never to list.</item>
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
            options.TextList("exclude"));
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
    public sealed record Options(IReadOnlyList<PathTemplate> Paths, bool GroupEach, int? OlderThanDays, IReadOnlyList<string> Exclude);
}
