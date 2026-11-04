using System.Text.Json;
using System.Text.RegularExpressions;
using DevSweep.Core.Detectors;
using DevSweep.Core.Health.Common;
using DevSweep.Core.Locations;

namespace DevSweep.Core.Health.Runtimes;

public enum RequirementStatus
{
    /// <summary>An installed version satisfies it.</summary>
    Ok,

    /// <summary>No installed version satisfies it.</summary>
    NotInstalled,

    /// <summary>It asks for a release line that is out of support.</summary>
    EndOfLife,
}

/// <summary>A version a project asks for (from <c>.nvmrc</c>, <c>global.json</c>...), and whether it is met.</summary>
public sealed record ProjectRequirement
{
    public required string Project { get; init; }

    public required string Path { get; init; }

    /// <summary>Tool id ("node", "python", "dotnet"...).</summary>
    public required string Tool { get; init; }

    public required string ToolName { get; init; }

    /// <summary>What the file says (">=18", "3.11.4", "8.0.100").</summary>
    public required string Spec { get; init; }

    /// <summary>The file it came from (".nvmrc").</summary>
    public required string File { get; init; }

    public RequirementStatus Status { get; init; } = RequirementStatus.Ok;

    public string Message { get; init; } = string.Empty;

    /// <summary>Commands that would install what is missing.</summary>
    public IReadOnlyList<string>? Fix { get; init; }
}

/// <summary>A requirement as read from a project, before it is checked.</summary>
public sealed record RawRequirement(string ProjectPath, string Tool, string Spec, string File, string? RollForward = null);

/// <summary>Reads version requirements from projects and checks them against what is installed (the Mac scanner, plus <c>global.json</c>).</summary>
public static partial class ProjectRequirementScanner
{
    private const int MaxFileBytes = 256 * 1024;

    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "AppData", ".venv", "venv", "build", "dist", "bin", "obj", "target", ".gradle", "packages", ".vs", ".idea",
    };

    private static readonly HashSet<string> Markers = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "package.json", "pyproject.toml", "go.mod", "Gemfile", "build.gradle", "build.gradle.kts", "composer.json", "Cargo.toml",
        ".tool-versions", ".nvmrc", ".node-version", ".python-version", ".ruby-version", "global.json", "rust-toolchain", "rust-toolchain.toml",
    };

    /// <summary>The project folders DevSweep looks in by default (the same list as the stale-project rule, <c>rules/70-projects.yml</c>).</summary>
    public static IReadOnlyList<string> DefaultRoots(ISystemEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var roots = new List<string>();
        if (environment.KnownFolder("Profile") is { } profile)
        {
            roots.AddRange(((string[])["source", "Projects", "projects", "repos", "code", "src", "dev", "IdeaProjects", "AndroidStudioProjects", "RiderProjects"])
                .Select(n => System.IO.Path.Join(profile, n)));
        }

        if (environment.KnownFolder("Documents") is { } documents)
        {
            roots.Add(System.IO.Path.Join(documents, "GitHub"));
            roots.Add(System.IO.Path.Join(documents, "Projects"));
        }

        if (environment.KnownFolder("Downloads") is { } downloads)
        {
            roots.Add(downloads);
        }

        return roots;
    }

    /// <summary>Folders that look like projects, up to <paramref name="depth"/> levels below each root (links and cloud-only folders never entered).</summary>
    public static IReadOnlyList<string> Projects(IEnumerable<string> roots, int depth = 3)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Walk(string folder, int level)
        {
            IReadOnlyList<ProbeEntry> entries = HealthFiles.List(folder);
            if (level > 0 && entries.Any(e => Markers.Contains(e.Name)))
            {
                found.Add(folder);
                return;
            }

            if (level >= depth)
            {
                return;
            }

            foreach (ProbeEntry child in entries.Where(e => e.CanEnter && !e.Name.StartsWith('.') && !Skip.Contains(e.Name)))
            {
                Walk(child.Path, level + 1);
            }
        }

        foreach (string root in roots)
        {
            if (seen.Add(System.IO.Path.TrimEndingDirectorySeparator(root)) && HealthFiles.IsFolder(root))
            {
                Walk(root, 0);
            }
        }

        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Everything a project folder says about versions (one per tool and file).</summary>
    public static IReadOnlyList<RawRequirement> Requirements(string project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var found = new List<RawRequirement>();
        string? Read(string name) => HealthFiles.ReadText(System.IO.Path.Join(project, name), MaxFileBytes);
        void Add(string tool, string spec, string file, string? rollForward = null)
        {
            string s = spec.Trim().Trim('"', '\'');
            if (s.Length > 0 && s.Any(char.IsAsciiDigit))
            {
                found.Add(new RawRequirement(project, tool, s, file, rollForward));
            }
        }

        if (Read(".nvmrc") is { } nvmrc)
        {
            Add("node", HealthFiles.FirstLine(nvmrc), ".nvmrc");
        }
        else if (Read(".node-version") is { } nodeVersion)
        {
            Add("node", HealthFiles.FirstLine(nodeVersion), ".node-version");
        }

        if (Json(Read("package.json"), "engines", "node") is { } engines)
        {
            Add("node", engines, "package.json");
        }

        if (Read(".python-version") is { } pv && !HealthFiles.FirstLine(pv).Contains('/', StringComparison.Ordinal))
        {
            Add("python", HealthFiles.FirstLine(pv), ".python-version");
        }

        if (Read("runtime.txt") is { } runtime && HealthFiles.FirstLine(runtime).StartsWith("python-", StringComparison.Ordinal))
        {
            Add("python", HealthFiles.FirstLine(runtime)[7..], "runtime.txt");
        }

        if (Read("pyproject.toml") is { } pyproject)
        {
            if (Capture(pyproject, RequiresPython()) is { } v)
            {
                Add("python", v, "pyproject.toml");
            }
            else if (Capture(pyproject, PoetryPython()) is { } p)
            {
                Add("python", p, "pyproject.toml");
            }
        }

        if (Read(".ruby-version") is { } rv)
        {
            Add("ruby", HealthFiles.FirstLine(rv).Replace("ruby-", string.Empty, StringComparison.Ordinal), ".ruby-version");
        }

        if (Read(".java-version") is { } jv)
        {
            Add("java", HealthFiles.FirstLine(jv), ".java-version");
        }

        foreach (string gradle in (string[])["build.gradle.kts", "build.gradle", "app/build.gradle.kts", "app/build.gradle"])
        {
            if (Read(gradle) is { } g && Capture(g, GradleToolchain()) is { } v)
            {
                Add("java", v, gradle);
                break;
            }
        }

        if (Read("go.mod") is { } gomod && Capture(gomod, GoDirective()) is { } go)
        {
            Add("go", ">=" + go.TrimStart('>', '='), "go.mod");
        }

        if (Json(Read("composer.json"), "require", "php") is { } php)
        {
            Add("php", php.Split('|')[0], "composer.json");
        }

        if (Read("rust-toolchain.toml") is { } rtt && Capture(rtt, RustChannel()) is { } channel)
        {
            Add("rust", channel, "rust-toolchain.toml");
        }
        else if (Read("rust-toolchain") is { } rt && HealthFiles.FirstLine(rt) is { Length: > 0 } first && char.IsAsciiDigit(first[0]))
        {
            Add("rust", first, "rust-toolchain");
        }

        if (Read("global.json") is { } global && Json(global, "sdk", "version") is { } sdk)
        {
            Add("dotnet", sdk, "global.json", Json(global, "sdk", "rollForward") ?? "latestPatch");
        }

        if (Read(".tool-versions") is { } toolVersions)
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["nodejs"] = "node", ["python"] = "python", ["ruby"] = "ruby", ["golang"] = "go", ["java"] = "java", ["php"] = "php", ["rust"] = "rust", ["dotnet"] = "dotnet",
            };
            foreach (string line in toolVersions.Split('\n'))
            {
                string[] parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && names.TryGetValue(parts[0], out string? tool))
                {
                    Add(tool, parts[1], ".tool-versions");
                }
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        return found.Where(r => seen.Add(r.Tool + "|" + r.File)).ToList();
    }

    /// <summary>The release line a spec names and whether it is a minimum ("&gt;=18", "^3.10", "~1.2") or an exact line.</summary>
    public static (string Cycle, bool Minimum)? Parse(string spec, string tool)
    {
        ArgumentNullException.ThrowIfNull(spec);
        string s = spec.Trim();
        bool minimum = s.StartsWith('>') || s.StartsWith('^') || s.StartsWith('~');
        IReadOnlyList<long> parts = VersionText.Parts(s.Replace("v", string.Empty, StringComparison.OrdinalIgnoreCase));
        if (parts.Count == 0)
        {
            return null;
        }

        string cycle = tool switch
        {
            "node" or "java" => VersionText.Prefix(s, 1),
            _ => parts.Count > 1 ? VersionText.Prefix(s, 2) : VersionText.Prefix(s, 1),
        };
        return (cycle, minimum);
    }

    /// <summary>Whether an installed .NET SDK satisfies a <c>global.json</c> version under its <c>rollForward</c> policy.</summary>
    public static bool DotnetSatisfies(string installed, string wanted, string? rollForward)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(wanted);
        IReadOnlyList<long> have = VersionText.Parts(installed);
        IReadOnlyList<long> want = VersionText.Parts(wanted);
        if (have.Count < 3 || want.Count < 3 || VersionText.Less(installed, wanted))
        {
            return false;
        }

        bool sameMajor = have[0] == want[0];
        bool sameMinor = sameMajor && have[1] == want[1];
        bool sameBand = sameMinor && have[2] / 100 == want[2] / 100;
        return (rollForward ?? "latestPatch").ToUpperInvariant() switch
        {
            "DISABLE" => have[2] == want[2] && sameMinor,
            "PATCH" or "LATESTPATCH" => sameBand,
            "FEATURE" or "LATESTFEATURE" => sameMinor,
            "MINOR" or "LATESTMINOR" => sameMajor,
            "MAJOR" or "LATESTMAJOR" => true,
            _ => sameBand,
        };
    }

    /// <summary>Checks requirements against installs and support data.</summary>
    /// <param name="raw">What projects ask for.</param>
    /// <param name="reports">What is installed, per tool.</param>
    /// <param name="cycles">Support data per tool id.</param>
    /// <param name="toolNames">Display names per tool id.</param>
    /// <param name="today">The day to judge support on.</param>
    /// <param name="install">Commands that install a release line with a given source (null when there is no such command).</param>
    public static IReadOnlyList<ProjectRequirement> Evaluate(
        IReadOnlyList<RawRequirement> raw,
        IReadOnlyList<RuntimeReport> reports,
        IReadOnlyDictionary<string, IReadOnlyList<ReleaseCycle>> cycles,
        IReadOnlyDictionary<string, string> toolNames,
        DateOnly today,
        Func<string, string, InstallSource, IReadOnlyList<string>?> install)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(cycles);
        ArgumentNullException.ThrowIfNull(toolNames);
        ArgumentNullException.ThrowIfNull(install);
        var results = new List<ProjectRequirement>();
        foreach (RawRequirement r in raw)
        {
            if (Parse(r.Spec, r.Tool) is not { } parsed)
            {
                continue;
            }

            (string cycle, bool minimum) = parsed;
            string name = toolNames.GetValueOrDefault(r.Tool) ?? r.Tool;
            string project = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(r.ProjectPath));
            var requirement = new ProjectRequirement { Project = project, Path = r.ProjectPath, Tool = r.Tool, ToolName = name, Spec = r.Spec, File = r.File };
            List<Installation> installs = reports.FirstOrDefault(x => x.Id == r.Tool)?.Installs.Where(i => !i.IsSystem).ToList() ?? [];
            List<Installation> matching = r.Tool == "dotnet" && r.File == "global.json"
                ? installs.Where(i => DotnetSatisfies(i.Version, r.Spec, r.RollForward)).ToList()
                : installs.Where(i => minimum ? !VersionText.Less(i.Cycle, cycle) : string.Equals(i.Cycle, cycle, StringComparison.OrdinalIgnoreCase)
                    || (!minimum && Matches(i.Version, r.Spec))).ToList();
            ReleaseCycle? wanted = cycles.GetValueOrDefault(r.Tool)?.FirstOrDefault(c => c.Cycle == cycle);
            if (!minimum && wanted is not null && wanted.Status(today) == SupportStatus.EndOfLife)
            {
                requirement = requirement with
                {
                    Status = RequirementStatus.EndOfLife,
                    Message = $"{project} asks for {name} {cycle} ({r.File}), which is past end of life. Consider moving the project to a supported version.",
                };
            }
            else if (matching.Count == 0)
            {
                List<string> have = installs.Select(i => i.Version).Distinct(StringComparer.Ordinal).Order(VersionComparer.Instance).ToList();
                InstallSource via = installs.FirstOrDefault(i => i.IsDefault)?.Source ?? installs.FirstOrDefault()?.Source ?? InstallSource.Winget;
                requirement = requirement with
                {
                    Status = RequirementStatus.NotInstalled,
                    Message = $"{project} asks for {name} {r.Spec} ({r.File}), but " + (have.Count == 0 ? $"{name} isn't installed." : $"you have {Words.Join(have)}."),
                    Fix = install(r.Tool, cycle, via),
                };
            }
            else
            {
                Installation best = matching.FirstOrDefault(i => i.IsDefault) ?? matching.OrderByDescending(i => i.Version, VersionComparer.Instance).First();
                requirement = requirement with
                {
                    Message = $"{project} asks for {name} {r.Spec} ({r.File}). {best.Version} from {best.SourceTitle} works"
                        + (best.IsDefault ? " and is the one a new terminal runs." : ", though it isn't the one a new terminal runs."),
                };
            }

            results.Add(requirement);
        }

        return results.OrderBy(p => p.Status == RequirementStatus.Ok ? 1 : 0).ThenBy(p => p.Project, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>An exact spec like "3.11.4" also matches by prefix.</summary>
    private static bool Matches(string version, string spec)
    {
        string s = spec.TrimStart('v', 'V', '=');
        return version.Equals(s, StringComparison.OrdinalIgnoreCase) || version.StartsWith(s + ".", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Capture(string text, Regex pattern)
    {
        Match m = pattern.Match(text);
        if (!m.Success)
        {
            return null;
        }

        Match v = SpecToken().Match(m.Value);
        return v.Success ? v.Value.Replace(" ", string.Empty, StringComparison.Ordinal) : null;
    }

    private static string? Json(string? text, string parent, string child)
    {
        if (text is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(parent, out JsonElement p) && p.ValueKind == JsonValueKind.Object
                && p.TryGetProperty(child, out JsonElement c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"requires-python\s*=\s*""[^""]+""")]
    private static partial Regex RequiresPython();

    [GeneratedRegex(@"(?m)^python\s*=\s*""[^""]+""")]
    private static partial Regex PoetryPython();

    [GeneratedRegex(@"(jvmToolchain\(\s*\d+|JavaLanguageVersion\.of\(\s*\d+)")]
    private static partial Regex GradleToolchain();

    [GeneratedRegex(@"(?m)^go\s+\d+(\.\d+)*")]
    private static partial Regex GoDirective();

    [GeneratedRegex(@"channel\s*=\s*""\d+[^""]*""")]
    private static partial Regex RustChannel();

    [GeneratedRegex(@"[<>=^~]*\s*v?\d+(\.\d+)*(\.x)?")]
    private static partial Regex SpecToken();
}
