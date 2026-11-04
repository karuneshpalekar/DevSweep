using System.Text.RegularExpressions;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Locations;
using DevSweep.Core.Rules;

namespace DevSweep.Core.Detectors;

/// <summary>
/// <c>kind: versionedSiblings</c>. Versioned folders side by side where only the
/// newest are needed (JetBrains <c>IntelliJIdea2024.1</c>, <c>IntelliJIdea2024.2</c>...).
/// <list type="bullet">
/// <item><c>parents</c>: rule paths of the folders that hold them (required);</item>
/// <item><c>pattern</c>: a regular expression with a <c>(?&lt;version&gt;...)</c> group and optionally <c>(?&lt;key&gt;...)</c> (one family per key);</item>
/// <item><c>keep</c>: how many of the newest versions per key stay (default 1);</item>
/// <item><c>groupEach</c>: one finding per old version instead of one per key.</item>
/// </list>
/// Versions compare number by number (2024.10 is newer than 2024.9). Only ordinary folders count; links are skipped.
/// </summary>
public sealed class VersionedSiblingsDetector : Detector<VersionedSiblingsDetector.Options>
{
    public override string Kind => "versionedSiblings";

    protected override Options? BindOptions(OptionReader options)
    {
        ArgumentNullException.ThrowIfNull(options);
        IReadOnlyList<PathTemplate> parents = options.Paths("parents", required: true);
        Regex? pattern = options.Pattern("pattern", required: true, "version");
        int keep = options.Number("keep", 1, 100) ?? 1;
        bool groupEach = options.Flag("groupEach");
        return pattern is null ? null : new Options(parents, pattern, keep, groupEach);
    }

    protected override IReadOnlyList<Candidate> Detect(DetectorRun run, Options options)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);
        var entries = new List<(string Path, string Key, string Version)>();
        var locationChecks = new List<Check>();
        bool hasKey = options.Pattern.GroupNumberFromName("key") >= 0;
        foreach (PathTemplate parentTemplate in options.Parents)
        {
            PathExpansion expansion = run.Expand(parentTemplate);
            locationChecks.Add(Check.Passed(expansion.Root.Describe()));
            foreach (ProbeEntry parent in expansion.Matches.Where(m => m.CanEnter))
            {
                ProbeListing listing = FileProbe.List(parent.Path);
                if (listing.Error == ProbeError.AccessDenied)
                {
                    run.Denied(parent.Path);
                }

                foreach (ProbeEntry child in listing.Entries.Where(c => c.CanEnter))
                {
                    Match match = options.Pattern.Match(child.Name);
                    if (match.Success && match.Groups["version"] is { Success: true } version)
                    {
                        string key = hasKey && match.Groups["key"] is { Success: true } k ? k.Value : string.Empty;
                        entries.Add((child.Path, key, version.Value));
                    }
                }
            }
        }

        var candidates = new List<Candidate>();
        foreach (IGrouping<string, (string Path, string Key, string Version)> family in entries
            .GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            List<string> versions = family.Select(e => e.Version).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(v => v, VersionComparer.Instance).ToList();
            List<string> kept = versions.Take(options.Keep).ToList();
            List<string> old = versions.Skip(options.Keep).ToList();
            if (old.Count == 0)
            {
                continue;
            }

            string key = family.Key;
            var facts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["key"] = key,
                ["kept"] = Words.Join(kept),
                ["newest"] = kept[0],
            };
            var checks = new List<Check>(locationChecks)
            {
                Check.Passed($"{(key.Length == 0 ? "" : key + " ")}{Words.Join(kept)} stay{(kept.Count == 1 ? "s" : "")} as {(kept.Count == 1 ? "the newest version" : "the newest versions")}"),
            };
            if (options.GroupEach)
            {
                foreach (string version in old)
                {
                    candidates.Add(new Candidate
                    {
                        Key = key + version,
                        Paths = family.Where(e => string.Equals(e.Version, version, StringComparison.OrdinalIgnoreCase)).Select(e => e.Path).ToList(),
                        Facts = new Dictionary<string, string>(facts) { ["version"] = version },
                        Checks = checks,
                    });
                }
            }
            else
            {
                string versionsText = Words.Join(old.OrderBy(v => v, VersionComparer.Instance).ToList());
                candidates.Add(new Candidate
                {
                    Key = key.Length == 0 ? "old" : key,
                    Paths = family.Where(e => old.Contains(e.Version, StringComparer.OrdinalIgnoreCase)).Select(e => e.Path).ToList(),
                    Subtitle = $"Versions {versionsText}",
                    Facts = new Dictionary<string, string>(facts) { ["versions"] = versionsText },
                    Checks = checks,
                });
            }
        }

        return candidates;
    }

    /// <summary>Bound options of a <c>versionedSiblings</c> rule.</summary>
    public sealed record Options(IReadOnlyList<PathTemplate> Parents, Regex Pattern, int Keep, bool GroupEach);
}

/// <summary>Compares version strings number by number ("2024.10" &gt; "2024.9" &gt; "2023.3"); ties by text.</summary>
public sealed class VersionComparer : IComparer<string>
{
    public static VersionComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        IReadOnlyList<long> a = Numbers(x);
        IReadOnlyList<long> b = Numbers(y);
        for (int i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            long left = i < a.Count ? a[i] : 0;
            long right = i < b.Count ? b[i] : 0;
            if (left != right)
            {
                return left.CompareTo(right);
            }
        }

        return StringComparer.OrdinalIgnoreCase.Compare(x, y);
    }

    /// <summary>The runs of digits in <paramref name="version"/>.</summary>
    public static IReadOnlyList<long> Numbers(string? version)
    {
        var numbers = new List<long>();
        long current = -1;
        foreach (char c in (version ?? string.Empty) + ".")
        {
            if (char.IsAsciiDigit(c))
            {
                current = Math.Min((Math.Max(current, 0) * 10) + (c - '0'), long.MaxValue / 20);
            }
            else if (current >= 0)
            {
                numbers.Add(current);
                current = -1;
            }
        }

        return numbers;
    }
}

/// <summary>"a", "a and b", "a, b and c" (as the Mac app).</summary>
public static class Words
{
    public static string Join(IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.Count switch
        {
            0 => string.Empty,
            1 => items[0],
            _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
        };
    }
}
