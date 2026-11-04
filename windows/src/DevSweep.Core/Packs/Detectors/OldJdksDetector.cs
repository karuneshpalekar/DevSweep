using DevSweep.Core.Cleaning;
using DevSweep.Core.Detectors;
using DevSweep.Core.Locations;
using DevSweep.Core.Rules;

namespace DevSweep.Core.Packs.Detectors;

/// <summary>
/// <c>kind: oldJdks</c> (the Mac detector, ported). Java versions older than <c>minMajor</c>
/// when a newer one (at least <c>minMajor</c>) is also installed. The major version comes from
/// each JDK's own <c>release</c> file, never from its folder name.
/// <list type="bullet">
/// <item><c>paths</c>: JDK folders that may be offered (wildcards);</item>
/// <item><c>references</c>: other JDK folders, counted as "newer Java installed" but never offered;</item>
/// <item><c>minMajor</c>: default 11.</item>
/// </list>
/// A JDK that <c>JAVA_HOME</c> or <c>PATH</c> points into is still offered, with a warning.
/// </summary>
public sealed class OldJdksDetector : Detector<OldJdksDetector.Options>
{
    public override string Kind => "oldJdks";

    /// <summary>The major version from a JDK's <c>release</c> file (<c>JAVA_VERSION="1.8.0_392"</c> is 8, <c>"17.0.9"</c> is 17).</summary>
    public static int? Major(string releaseFile)
    {
        ArgumentNullException.ThrowIfNull(releaseFile);
        foreach (string raw in releaseFile.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("JAVA_VERSION=", StringComparison.Ordinal))
            {
                continue;
            }

            IReadOnlyList<long> parts = VersionComparer.Numbers(line["JAVA_VERSION=".Length..].Trim('"'));
            return parts.Count == 0 ? null : parts[0] == 1 && parts.Count > 1 ? (int)parts[1] : (int)parts[0];
        }

        return null;
    }

    protected override Options? BindOptions(OptionReader options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new Options(options.Paths("paths", required: true), options.Paths("references"), options.Number("minMajor", 2, 100) ?? 11);
    }

    protected override IReadOnlyList<Candidate> Detect(DetectorRun run, Options options)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);
        var env = run.Context.Locations.Environment;
        List<(string Path, int Major)> Read(IReadOnlyList<PathTemplate> templates)
            => templates.SelectMany(t => run.Expand(t).Matches.Where(m => m.CanEnter))
                .Select(m => (m.Path, Major: env.ReadAllText(Path.Join(m.Path, "release")) is { } text ? Major(text) : null))
                .Where(j => j.Major is not null)
                .Select(j => (j.Path, j.Major!.Value))
                .ToList();

        List<(string Path, int Major)> removable = Read(options.Paths);
        List<(string Path, int Major)> all = [.. removable, .. Read(options.References)];
        List<int> newer = all.Select(j => j.Major).Where(m => m >= options.MinMajor).Distinct().Order().ToList();
        if (newer.Count == 0)
        {
            return [];
        }

        string newerText = Words.Join(newer.Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList());
        string javaHome = env.GetVariable("JAVA_HOME") ?? string.Empty;
        string[] path = (env.GetVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var candidates = new List<Candidate>();
        foreach ((string jdk, int major) in removable.Where(j => j.Major < options.MinMajor))
        {
            string name = Path.GetFileName(jdk);
            var checks = new List<Check>
            {
                Check.Passed($"{Path.Join(jdk, "release")} says Java {major}"),
                Check.Passed($"Java {newerText} {(newer.Count == 1 ? "is" : "are")} also installed"),
            };
            checks.Add(Inside(javaHome, jdk)
                ? Check.Warning("JAVA_HOME points to it. Change JAVA_HOME after removing it.")
                : Check.Passed("JAVA_HOME doesn't point to it"));
            if (path.Any(p => Inside(p.Trim('"'), jdk)))
            {
                checks.Add(Check.Warning("It is on your PATH. Remove it from PATH after removing it."));
            }

            candidates.Add(new Candidate
            {
                Key = jdk,
                Paths = [jdk],
                Title = $"Java {major} ({name})",
                Subtitle = jdk,
                Facts = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["major"] = major.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["newer"] = newerText,
                    ["name"] = name,
                },
                Checks = checks,
            });
        }

        return candidates;
    }

    private static bool Inside(string path, string folder)
        => path.Length > 0 && (string.Equals(Path.TrimEndingDirectorySeparator(path), folder, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase));

    /// <summary>Bound options of an <c>oldJdks</c> rule.</summary>
    public sealed record Options(IReadOnlyList<PathTemplate> Paths, IReadOnlyList<PathTemplate> References, int MinMajor);
}
