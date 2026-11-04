using System.Globalization;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Locations;
using DevSweep.Core.Rules;

namespace DevSweep.Core.Detectors.Projects;

/// <summary>
/// <c>kind: staleProjectArtifacts</c>. Rebuildable folders (<c>node_modules</c>,
/// <c>.venv</c>, <c>bin</c>+<c>obj</c>, <c>target</c>, <c>.gradle</c>) in projects nobody
/// has worked on for a while.
/// <list type="bullet">
/// <item><c>roots</c>: rule paths to look for projects in (Settings' project folders replace them);</item>
/// <item><c>names</c>: which artifact folders (default: all known);</item>
/// <item><c>staleDays</c>: days without activity (default 30);</item>
/// <item><c>maxDepth</c>: how deep below a root projects are looked for (default 5).</item>
/// </list>
/// A project is a folder with the kind's marker file; its activity comes from git
/// and its own files, never from the artifact folders (<see cref="ProjectActivity"/>).
/// Links and cloud folders are never entered.
/// </summary>
public sealed class StaleProjectArtifactsDetector : Detector<StaleProjectArtifactsDetector.Options>
{
    private static readonly HashSet<string> NeverEnter = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "AppData", "$Recycle.Bin", "Windows", "Program Files", "Program Files (x86)",
    };

    public override string Kind => "staleProjectArtifacts";

    protected override Options? BindOptions(OptionReader options)
    {
        ArgumentNullException.ThrowIfNull(options);
        IReadOnlyList<PathTemplate> roots = options.Paths("roots", required: true);
        IReadOnlyList<string> names = options.TextList("names");
        foreach (string name in names.Where(n => !ArtifactKinds.FolderNames.Contains(n)))
        {
            options.Errors.Add(options.Node.Get("names"), $"names: \"{name}\" is not a known artifact folder (known: {string.Join(", ", ArtifactKinds.FolderNames)})");
        }

        int staleDays = options.Number("staleDays", 1, 3650) ?? 30;
        int maxDepth = options.Number("maxDepth", 1, 12) ?? 5;
        return new Options(roots, names.Count == 0 ? ArtifactKinds.FolderNames : names.ToHashSet(StringComparer.OrdinalIgnoreCase), staleDays, maxDepth);
    }

    protected override IReadOnlyList<Candidate> Detect(DetectorRun run, Options options)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);
        var walk = new Walk(run, options);
        foreach (string root in Roots(run, options))
        {
            walk.Visit(root, 1);
        }

        return walk.Candidates;
    }

    private static List<string> Roots(DetectorRun run, Options options)
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (run.Context.ProjectRoots is { } chosen)
        {
            roots.AddRange(chosen.Where(r => FileProbe.Stat(r) is { CanEnter: true }));
        }
        else
        {
            foreach (PathTemplate template in options.Roots)
            {
                roots.AddRange(run.Expand(template).Matches.Where(m => m.CanEnter).Select(m => m.Path));
            }
        }

        return roots.Where(r => seen.Add(Path.TrimEndingDirectorySeparator(r))).ToList();
    }

    private sealed class Walk(DetectorRun run, Options options)
    {
        private readonly DateTime _cutoff = run.Context.Now.UtcDateTime.AddDays(-options.StaleDays);

        public List<Candidate> Candidates { get; } = [];

        public void Visit(string folder, int depth)
        {
            run.ThrowIfCancelled();
            ProbeListing listing = FileProbe.List(folder);
            if (listing.Error == ProbeError.AccessDenied)
            {
                run.Denied(folder);
                return;
            }

            var files = listing.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            ProjectActivityResult? activity = null;
            bool activityRead = false;
            foreach (ArtifactKind kind in ArtifactKinds.All)
            {
                string? marker = kind.Markers.FirstOrDefault(m => files.Any(f => Glob.IsMatch(m, f)));
                if (marker is null)
                {
                    continue;
                }

                var artifacts = listing.Entries
                    .Where(e => e.CanEnter && options.Names.Contains(e.Name) && kind.Folders.Contains(e.Name, StringComparer.OrdinalIgnoreCase) && kind.Confirm(e.Path))
                    .ToList();
                if (artifacts.Count == 0)
                {
                    continue;
                }

                if (!activityRead)
                {
                    activity = ProjectActivity.Find(folder, run, _cutoff);
                    activityRead = true;
                }

                if (activity is null)
                {
                    run.Note($"{folder}: left alone because its last activity could not be read completely");
                }
                else if (activity.LastUtc < _cutoff)
                {
                    string manifest = files.First(f => Glob.IsMatch(marker, f));
                    Candidates.Add(ToCandidate(folder, kind, artifacts, manifest, kind.Reinstall(files), activity));
                }
            }

            if (depth >= options.MaxDepth)
            {
                return;
            }

            foreach (ProbeEntry child in listing.Entries)
            {
                if (child.CanEnter && !child.Name.StartsWith('.') && !ArtifactKinds.FolderNames.Contains(child.Name) && !NeverEnter.Contains(child.Name))
                {
                    Visit(child.Path, depth + 1);
                }
            }
        }

        private Candidate ToCandidate(string project, ArtifactKind kind, List<ProbeEntry> artifacts, string manifest, string reinstall, ProjectActivityResult activity)
        {
            string name = Path.GetFileName(project);
            string folders = Words.Join(artifacts.Select(a => a.Name).ToList());
            int days = activity.LastUtc == DateTime.MinValue ? options.StaleDays : (int)(run.Context.Now.UtcDateTime - activity.LastUtc).TotalDays;
            string daysText = days.ToString(CultureInfo.InvariantCulture);
            var checks = new List<Check>
            {
                Check.Passed($"{manifest} is here, so {reinstall} brings {folders} back"),
                Check.Passed(activity.LastUtc == DateTime.MinValue
                    ? "No project files have changed (the dependency and build folders are not counted)"
                    : $"Nothing in {name} changed for {daysText} days: newest is {activity.What}, {activity.LastUtc:yyyy-MM-dd} (dependency and build folders not counted)"),
            };
            if (activity.GitUtc is { } git)
            {
                checks.Add(Check.Passed($"Last commit or checkout {git:yyyy-MM-dd}"));
            }

            checks.Add(Check.Passed($"Only {folders} {(artifacts.Count == 1 ? "is" : "are")} removed; your source, {manifest} and git history stay"));
            return new Candidate
            {
                Key = $"{project}|{kind.Ecosystem}",
                Paths = artifacts.Select(a => a.Path).ToList(),
                Title = $"{folders} in {name}",
                Subtitle = project,
                Facts = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["project"] = name,
                    ["projectPath"] = project,
                    ["days"] = daysText,
                    ["reinstall"] = reinstall,
                    ["manifest"] = manifest,
                    ["folders"] = folders,
                    ["ecosystem"] = kind.Ecosystem,
                },
                Checks = checks,
            };
        }
    }

    /// <summary>Bound options of a <c>staleProjectArtifacts</c> rule.</summary>
    public sealed record Options(IReadOnlyList<PathTemplate> Roots, IReadOnlySet<string> Names, int StaleDays, int MaxDepth);
}
