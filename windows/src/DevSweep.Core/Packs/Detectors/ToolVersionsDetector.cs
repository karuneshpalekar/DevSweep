using System.Text.Json;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Detectors;
using DevSweep.Core.Locations;
using DevSweep.Core.Rules;

namespace DevSweep.Core.Packs.Detectors;

/// <summary>
/// <c>kind: toolVersions</c>. Old Node or Python versions kept by a version manager. Kept: the
/// version in use (nvm's active link, fnm's aliases, Volta's default, pyenv's global and
/// <c>PYENV_VERSION</c>), versions pinned by <c>.python-version</c> in project folders, and the
/// newest of each line (Node major, Python major.minor). Only older patches of a line are offered.
/// Links are read (where they point), never followed or removed; a version folder that is a link
/// is never offered.
/// <list type="bullet">
/// <item><c>manager</c>: <c>nvm</c>, <c>fnm</c>, <c>volta</c> or <c>pyenv</c>;</item>
/// <item><c>projectRoots</c>: where to look for <c>.python-version</c> (Settings' project folders replace them).</item>
/// </list>
/// </summary>
public sealed class ToolVersionsDetector : Detector<ToolVersionsDetector.Options>
{
    private static readonly string[] Managers = ["nvm", "fnm", "volta", "pyenv"];

    public override string Kind => "toolVersions";

    /// <summary>A setting in nvm for Windows' settings.txt (<c>root: C:\...</c>).</summary>
    public static string? NvmSetting(string settings, string key)
    {
        ArgumentNullException.ThrowIfNull(settings);
        foreach (string raw in settings.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
            {
                string value = line[(key.Length + 1)..].Trim();
                return value.Length > 0 ? value : null;
            }
        }

        return null;
    }

    /// <summary>Volta's default Node (<c>{"node":{"runtime":"20.11.0"}}</c> in platform.json).</summary>
    public static string? VoltaDefault(string platformJson)
    {
        ArgumentNullException.ThrowIfNull(platformJson);
        try
        {
            using var doc = JsonDocument.Parse(platformJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("node", out JsonElement node) && node.ValueKind == JsonValueKind.Object
                && node.TryGetProperty("runtime", out JsonElement runtime) && runtime.ValueKind == JsonValueKind.String
                ? runtime.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Where a link points (read from the link, never followed); null when it is not a link or cannot be read.</summary>
    public static string? LinkTarget(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists && !File.Exists(path))
            {
                return null;
            }

            string? target = info.LinkTarget;
            if (target is null)
            {
                return null;
            }

            target = VhdxDisksDetector.StripExtendedPrefix(target);
            return Path.TrimEndingDirectorySeparator(Path.IsPathFullyQualified(target) ? target : Path.GetFullPath(Path.Join(Path.GetDirectoryName(path), target)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    protected override Options? BindOptions(OptionReader options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string? manager = options.Text("manager", required: true);
        if (manager is not null && !Managers.Contains(manager, StringComparer.Ordinal))
        {
            options.Errors.Add(options.Node.Get("manager"), $"manager should be one of {string.Join(", ", Managers)}");
        }

        return new Options(manager ?? "nvm", options.Paths("projectRoots"));
    }

    protected override IReadOnlyList<Candidate> Detect(DetectorRun run, Options options)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);
        Inventory? found = options.Manager switch
        {
            "nvm" => Nvm(run),
            "fnm" => Fnm(run),
            "volta" => Volta(run),
            _ => Pyenv(run, options),
        };
        if (found is null || found.Versions.Count < 2)
        {
            return [];
        }

        bool python = options.Manager == "pyenv";
        string tool = python ? "Python" : "Node.js";
        var keep = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string version, string why) in found.InUse)
        {
            keep.TryAdd(version.TrimStart('v'), why);
        }

        foreach (IGrouping<string, Version> line in found.Versions.GroupBy(v => SemVerComparer.Prefix(v.Name, python ? 2 : 1)))
        {
            string newest = line.Select(v => v.Name).Max(SemVerComparer.Instance)!;
            keep.TryAdd(newest, $"the newest {tool} {line.Key}");
        }

        var candidates = new List<Candidate>();
        foreach (Version version in found.Versions.Where(v => !keep.ContainsKey(v.Name)).OrderBy(v => v.Name, SemVerComparer.Instance))
        {
            string line = SemVerComparer.Prefix(version.Name, python ? 2 : 1);
            string newest = found.Versions.Where(v => SemVerComparer.Prefix(v.Name, python ? 2 : 1) == line).Select(v => v.Name).Max(SemVerComparer.Instance)!;
            var checks = new List<Check>(found.Checks)
            {
                Check.Passed($"{tool} {newest} is newer in the same line ({line})"),
                Check.Passed($"Kept: {Words.Join(keep.Select(k => $"{k.Key} ({k.Value})").Order(StringComparer.Ordinal).ToList())}"),
            };
            candidates.Add(new Candidate
            {
                Key = version.Path,
                Paths = [version.Path],
                Title = $"{tool} {version.Name} ({found.Title})",
                Subtitle = version.Path,
                Facts = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["tool"] = tool,
                    ["version"] = version.Name,
                    ["newest"] = newest,
                    ["manager"] = found.Title,
                },
                Checks = checks,
            });
        }

        return candidates;
    }

    private static Inventory? Nvm(DetectorRun run)
    {
        var env = run.Context.Locations.Environment;
        string? home = run.Context.Locations.Resolve("nvmHome").Path;
        string? settings = home is null ? null : env.ReadAllText(Path.Join(home, "settings.txt"));
        string? root = settings is null ? home : NvmSetting(settings, "root") ?? home;
        string? link = (settings is null ? null : NvmSetting(settings, "path")) ?? run.Context.Locations.Resolve("nvmSymlink").Path;
        if (root is null || !Path.IsPathFullyQualified(root))
        {
            return null;
        }

        var checks = new List<Check> { Check.Passed($"nvm for Windows keeps its versions in {root}{(settings is null ? "" : " (root in settings.txt)")}") };
        var inUse = new List<(string, string)>();
        if (link is not null && FileProbe.Stat(link) is { } entry)
        {
            if (!entry.IsReparsePoint || LinkTarget(link) is not { } target)
            {
                run.Note($"{link} is not a link DevSweep can read, so the active Node version is unknown; nothing is offered");
                return null;
            }

            inUse.Add((Path.GetFileName(target), $"active: {link} points to it"));
            checks.Add(Check.Passed($"The active version is {Path.GetFileName(target)} ({link} points to {target}); it stays, and the link is never touched"));
        }
        else
        {
            checks.Add(Check.Passed($"No active version ({link ?? "the nvm link"} does not exist)"));
        }

        return new Inventory("nvm for Windows", Versions(root, "v"), inUse, checks);
    }

    private static Inventory? Fnm(DetectorRun run)
    {
        string? dir = run.Context.Locations.Resolve("fnmDir").Path;
        if (dir is null)
        {
            return null;
        }

        string versions = Path.Join(dir, "node-versions");
        var inUse = new List<(string, string)>();
        foreach (ProbeEntry alias in FileProbe.List(Path.Join(dir, "aliases")).Entries)
        {
            if (!alias.IsReparsePoint || LinkTarget(alias.Path) is not { } target)
            {
                run.Note($"fnm alias {alias.Path} could not be read; nothing is offered");
                return null;
            }

            // Aliases point to <version>\installation.
            string version = Path.GetFileName(Path.GetFileName(target).Equals("installation", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(target)! : target);
            inUse.Add((version, $"fnm alias {alias.Name}"));
        }

        return new Inventory("fnm", Versions(versions, "v"), inUse, [Check.Passed($"fnm keeps its versions in {versions}; every alias's version stays")]);
    }

    private static Inventory? Volta(DetectorRun run)
    {
        string? home = run.Context.Locations.Resolve("voltaHome").Path;
        if (home is null)
        {
            return null;
        }

        string versions = Path.Join(home, "tools", "image", "node");
        string platform = Path.Join(home, "tools", "user", "platform.json");
        string? text = run.Context.Locations.Environment.ReadAllText(platform);
        var inUse = new List<(string, string)>();
        if (text is not null && VoltaDefault(text) is { } current)
        {
            inUse.Add((current, $"Volta's default ({platform})"));
        }

        return new Inventory("Volta", Versions(versions, string.Empty), inUse,
            [Check.Passed($"Volta keeps its versions in {versions}"), Check.Passed("Volta downloads a version again by itself when a project pins it")]);
    }

    private static Inventory? Pyenv(DetectorRun run, Options options)
    {
        var env = run.Context.Locations.Environment;
        string? root = run.Context.Locations.Resolve("pyenvRoot").Path;
        if (root is null)
        {
            return null;
        }

        var inUse = new List<(string, string)>();
        string globalFile = Path.Join(root, "version");
        foreach (string v in (env.ReadAllText(globalFile) ?? string.Empty).Split(['\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            inUse.Add((v, $"pyenv global ({globalFile})"));
        }

        if (env.GetVariable("PYENV_VERSION") is { } shell)
        {
            inUse.Add((shell.Trim(), "PYENV_VERSION"));
        }

        IReadOnlyList<string> roots = ProjectFiles.Roots(run, options.ProjectRoots);
        foreach (string file in ProjectFiles.Find(run, roots, [".python-version"]))
        {
            foreach (string v in (env.ReadAllText(file) ?? string.Empty).Split(['\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                inUse.Add((v, $"pinned in {file}"));
            }
        }

        string versions = Path.Join(root, "versions");
        return new Inventory("pyenv-win", Versions(versions, string.Empty), inUse,
            [Check.Passed($"pyenv-win keeps its versions in {versions}"), Check.Passed($".python-version files looked for in {(roots.Count == 0 ? "no project folders" : Words.Join(roots))}")]);
    }

    /// <summary>Version folders (<c>v20.11.0</c> or <c>3.12.1</c>); links are never listed as versions.</summary>
    private static List<Version> Versions(string folder, string prefix)
        => FileProbe.List(folder).Entries
            .Where(e => e.CanEnter && e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && e.Name.Length > prefix.Length && char.IsAsciiDigit(e.Name[prefix.Length]))
            .Select(e => new Version(e.Name[prefix.Length..], e.Path))
            .ToList();

    private sealed record Version(string Name, string Path);

    private sealed record Inventory(string Title, IReadOnlyList<Version> Versions, IReadOnlyList<(string Version, string Why)> InUse, IReadOnlyList<Check> Checks);

    /// <summary>Bound options of a <c>toolVersions</c> rule.</summary>
    public sealed record Options(string Manager, IReadOnlyList<PathTemplate> ProjectRoots);
}
