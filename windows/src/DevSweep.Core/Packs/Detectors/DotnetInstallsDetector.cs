using System.Text.Json;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Detectors;
using DevSweep.Core.Locations;
using DevSweep.Core.Packs.Tools;
using DevSweep.Core.Rules;

namespace DevSweep.Core.Packs.Detectors;

/// <summary>
/// <c>kind: dotnetInstalls</c>. Old .NET SDKs, runtimes and orphaned workload packs.
/// <list type="bullet">
/// <item><c>roots</c>: dotnet install folders (<c>{ProgramFiles}\dotnet</c>, or per-user ones);</item>
/// <item><c>what</c>: <c>sdk</c> (the newest SDK of each major version stays, and every SDK in the
/// feature band a <c>global.json</c> pins), <c>runtime</c> (the newest patch of each framework's
/// major.minor stays) or <c>workloadPacks</c> (pack versions no workload install record names,
/// when that pack has records for other versions: what <c>dotnet workload clean</c> removes);</item>
/// <item><c>projectRoots</c>: where to look for <c>global.json</c> (Settings' project folders replace them);</item>
/// <item><c>records</c>: the workload install records folder (<c>{ProgramData}\dotnet\workloads</c>).</item>
/// </list>
/// The folder listing is what <c>dotnet</c> itself reads; <c>dotnet --list-sdks</c> is run (10 s
/// limit) only to say whether it agrees.
/// </summary>
public sealed class DotnetInstallsDetector(IMachineQueries machine) : Detector<DotnetInstallsDetector.Options>
{
    public override string Kind => "dotnetInstalls";

    /// <summary><c>sdk.version</c> of a global.json (comments allowed), or null.</summary>
    public static string? PinnedSdk(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("sdk", out JsonElement sdk) && sdk.ValueKind == JsonValueKind.Object
                && sdk.TryGetProperty("version", out JsonElement version) && version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The feature band of an SDK version: <c>8.0.404</c> is <c>8.0.4</c>.</summary>
    public static string FeatureBand(string sdkVersion)
    {
        IReadOnlyList<long> n = VersionComparer.Numbers(sdkVersion.Split('-')[0]);
        return n.Count < 3 ? sdkVersion : $"{n[0]}.{n[1]}.{n[2] / 100}";
    }

    /// <summary>Versions from <c>dotnet --list-sdks</c> / <c>--list-runtimes</c> lines ("8.0.404 [C:\...]" or "Microsoft.NETCore.App 8.0.11 [...]").</summary>
    public static IReadOnlySet<string> ParseList(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1].StartsWith('['))
            {
                versions.Add(parts[0]);
            }
            else if (parts.Length >= 3)
            {
                versions.Add($"{parts[0]} {parts[1]}");
            }
        }

        return versions;
    }

    protected override Options? BindOptions(OptionReader options)
    {
        ArgumentNullException.ThrowIfNull(options);
        IReadOnlyList<PathTemplate> roots = options.Paths("roots", required: true);
        string? what = options.Text("what", required: true);
        if (what is not null and not ("sdk" or "runtime" or "workloadPacks"))
        {
            options.Errors.Add(options.Node.Get("what"), "what should be sdk, runtime or workloadPacks");
        }

        IReadOnlyList<PathTemplate> projectRoots = options.Paths("projectRoots");
        IReadOnlyList<PathTemplate> records = options.Paths("records");
        return new Options(roots, what ?? "sdk", projectRoots, records);
    }

    protected override IReadOnlyList<Candidate> Detect(DetectorRun run, Options options)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);
        var candidates = new List<Candidate>();
        foreach (PathTemplate template in options.Roots)
        {
            foreach (ProbeEntry root in run.Expand(template).Matches.Where(m => m.CanEnter))
            {
                candidates.AddRange(options.What switch
                {
                    "sdk" => Sdks(run, options, root.Path),
                    "runtime" => Runtimes(run, root.Path),
                    _ => WorkloadPacks(run, options, root.Path),
                });
            }
        }

        return candidates;
    }

    private static List<(string Version, string Path)> Versions(string folder)
        => FileProbe.List(folder).Entries
            .Where(e => e.CanEnter && e.Name.Length > 0 && char.IsAsciiDigit(e.Name[0]) && VersionComparer.Numbers(e.Name).Count >= 3)
            .Select(e => (e.Name, e.Path))
            .ToList();

    private List<Candidate> Sdks(DetectorRun run, Options options, string root)
    {
        List<(string Version, string Path)> sdks = Versions(Path.Join(root, "sdk"));
        if (sdks.Count < 2)
        {
            return [];
        }

        IReadOnlyList<string> projectRoots = ProjectFiles.Roots(run, options.ProjectRoots);
        var pins = new List<(string Version, string File)>();
        foreach (string file in ProjectFiles.Find(run, projectRoots, ["global.json"]))
        {
            if (run.Context.Locations.Environment.ReadAllText(file) is { } text && PinnedSdk(text) is { } pin)
            {
                pins.Add((pin, file));
            }
        }

        var keep = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var newestOfMajor = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (IGrouping<string, (string Version, string Path)> major in sdks.GroupBy(s => SemVerComparer.Prefix(s.Version, 1)))
        {
            string newest = major.Select(s => s.Version).Max(SemVerComparer.Instance)!;
            newestOfMajor[major.Key] = newest;
            keep[newest] = $"the newest .NET {major.Key} SDK";
        }

        foreach ((string pin, string file) in pins)
        {
            foreach ((string version, _) in sdks.Where(s => FeatureBand(s.Version) == FeatureBand(pin)))
            {
                keep.TryAdd(version, $"{file} pins {pin}");
            }
        }

        Check? listed = ListCheck(run, root, "--list-sdks", sdks.Select(s => s.Version));
        var candidates = new List<Candidate>();
        foreach ((string version, string path) in sdks.Where(s => !keep.ContainsKey(s.Version)).OrderBy(s => s.Version, SemVerComparer.Instance))
        {
            string major = SemVerComparer.Prefix(version, 1);
            string newest = newestOfMajor[major];
            var checks = new List<Check>
            {
                Check.Passed($"Found in {Path.Join(root, "sdk")}, the folder dotnet reads its SDKs from"),
                Check.Passed($".NET SDK {newest} is newer and builds everything {version} builds (a newer SDK also builds older target frameworks)"),
                Check.Passed(pins.Count == 0
                    ? $"No global.json pins an SDK in {Describe(projectRoots)}"
                    : $"global.json pins checked ({pins.Count} in {Describe(projectRoots)}); none is in the {FeatureBand(version)}xx band"),
            };
            if (listed is not null)
            {
                checks.Add(listed);
            }

            candidates.Add(new Candidate
            {
                Key = $"{root}|sdk|{version}",
                Paths = [path],
                Title = $".NET SDK {version}",
                Subtitle = path,
                Facts = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["version"] = version,
                    ["newest"] = newest,
                    ["major"] = major,
                    ["root"] = root,
                },
                Checks = checks,
            });
        }

        return candidates;
    }

    private List<Candidate> Runtimes(DetectorRun run, string root)
    {
        var candidates = new List<Candidate>();
        foreach (ProbeEntry framework in FileProbe.List(Path.Join(root, "shared")).Entries.Where(e => e.CanEnter))
        {
            List<(string Version, string Path)> versions = Versions(framework.Path);
            Check? listed = ListCheck(run, root, "--list-runtimes", versions.Select(v => $"{framework.Name} {v.Version}"));
            foreach (IGrouping<string, (string Version, string Path)> line in versions.GroupBy(v => SemVerComparer.Prefix(v.Version, 2)))
            {
                string newest = line.Select(v => v.Version).Max(SemVerComparer.Instance)!;
                foreach ((string version, string path) in line.Where(v => v.Version != newest).OrderBy(v => v.Version, SemVerComparer.Instance))
                {
                    var checks = new List<Check>
                    {
                        Check.Passed($"Found in {framework.Path}, where dotnet looks for {framework.Name}"),
                        Check.Passed($"{framework.Name} {newest} is installed: apps built for {line.Key} run on the newest {line.Key} patch, not this one"),
                    };
                    if (listed is not null)
                    {
                        checks.Add(listed);
                    }

                    candidates.Add(new Candidate
                    {
                        Key = $"{root}|{framework.Name}|{version}",
                        Paths = [path],
                        Title = $"{framework.Name} {version}",
                        Subtitle = path,
                        Facts = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["framework"] = framework.Name,
                            ["version"] = version,
                            ["newest"] = newest,
                            ["root"] = root,
                        },
                        Checks = checks,
                    });
                }
            }
        }

        return candidates;
    }

    private static List<Candidate> WorkloadPacks(DetectorRun run, Options options, string root)
    {
        // Install records: <records>\<arch>\<band>\InstalledPacks\v1\<packId>\<packVersion>\<band>.
        var recorded = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        bool readAll = true;
        foreach (PathTemplate template in options.Records)
        {
            foreach (ProbeEntry records in run.Expand(template).Matches.Where(m => m.CanEnter))
            {
                foreach (ProbeEntry arch in FileProbe.List(records.Path).Entries.Where(e => e.CanEnter))
                {
                    foreach (ProbeEntry band in FileProbe.List(arch.Path).Entries.Where(e => e.CanEnter))
                    {
                        ProbeListing packs = FileProbe.List(Path.Join(band.Path, "InstalledPacks", "v1"));
                        readAll &= packs.Error is ProbeError.None or ProbeError.NotFound;
                        foreach (ProbeEntry pack in packs.Entries.Where(e => e.CanEnter))
                        {
                            ProbeListing versions = FileProbe.List(pack.Path);
                            readAll &= versions.Error == ProbeError.None;
                            HashSet<string> set = recorded.TryGetValue(pack.Name, out HashSet<string>? s) ? s : recorded[pack.Name] = new(StringComparer.OrdinalIgnoreCase);
                            set.UnionWith(versions.Entries.Where(e => e.CanEnter).Select(e => e.Name));
                        }
                    }
                }
            }
        }

        if (!readAll)
        {
            run.Note("Some workload install records could not be read; no workload pack is offered");
            return [];
        }

        var candidates = new List<Candidate>();
        foreach (ProbeEntry pack in FileProbe.List(Path.Join(root, "packs")).Entries.Where(e => e.CanEnter))
        {
            if (!recorded.TryGetValue(pack.Name, out HashSet<string>? kept) || kept.Count == 0)
            {
                continue; // Not a workload pack, or one DevSweep cannot tie to records: left alone.
            }

            foreach (ProbeEntry version in FileProbe.List(pack.Path).Entries.Where(e => e.CanEnter && !kept.Contains(e.Name)))
            {
                candidates.Add(new Candidate
                {
                    Key = $"{root}|pack|{pack.Name}|{version.Name}",
                    Paths = [version.Path],
                    Title = $"Workload pack {pack.Name} {version.Name}",
                    Subtitle = version.Path,
                    Facts = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["pack"] = pack.Name,
                        ["version"] = version.Name,
                        ["kept"] = Words.Join(kept.Order(SemVerComparer.Instance).ToList()),
                    },
                    Checks =
                    [
                        Check.Passed($"No installed workload records {pack.Name} {version.Name}; the records name {Words.Join(kept.Order(SemVerComparer.Instance).ToList())}"),
                        Check.Passed("dotnet workload clean removes pack versions no workload records"),
                    ],
                });
            }
        }

        return candidates;
    }

    private Check? ListCheck(DetectorRun run, string root, string argument, IEnumerable<string> onDisk)
    {
        string dotnet = Path.Join(root, "dotnet.exe");
        if (!run.Context.Locations.Environment.FileExists(dotnet))
        {
            return null;
        }

        CommandResult result = machine.Run([dotnet, argument], TimeSpan.FromSeconds(10), run.CancellationToken);
        if (!result.Succeeded)
        {
            return null;
        }

        IReadOnlySet<string> listed = ParseList(result.StandardOutput);
        var disk = onDisk.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return disk.IsSubsetOf(listed)
            ? Check.Passed($"dotnet {argument} lists these versions too")
            : Check.Warning($"dotnet {argument} does not list every version in the folder ({disk.Count - disk.Count(listed.Contains)} missing): the folder may hold a broken install");
    }

    private static string Describe(IReadOnlyList<string> roots)
        => roots.Count == 0 ? "no project folders (none found)" : roots.Count <= 3 ? Words.Join(roots) : $"{roots.Count} project folders";

    /// <summary>Bound options of a <c>dotnetInstalls</c> rule.</summary>
    public sealed record Options(IReadOnlyList<PathTemplate> Roots, string What, IReadOnlyList<PathTemplate> ProjectRoots, IReadOnlyList<PathTemplate> Records);
}
