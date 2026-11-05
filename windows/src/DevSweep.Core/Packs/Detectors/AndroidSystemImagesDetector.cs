using DevSweep.Core.Cleaning;
using DevSweep.Core.Detectors;
using DevSweep.Core.Locations;
using DevSweep.Core.Rules;

namespace DevSweep.Core.Packs.Detectors;

/// <summary>
/// <c>kind: androidSystemImages</c> (the Mac detector, ported). System images in the SDK that no
/// emulator boots from. Each emulator's <c>config.ini</c> names its image
/// (<c>image.sysdir.1=system-images\android-34\google_apis\x86_64\</c>). An API level no emulator
/// uses is offered whole; in a level one emulator uses, only the unused variants are.
/// <list type="bullet">
/// <item><c>sdks</c>: SDK folders (<c>{androidSdk}</c>);</item>
/// <item><c>avds</c>: emulator folders (<c>{androidAvd}</c>).</item>
/// </list>
/// When any emulator's config cannot be read, nothing is offered.
/// </summary>
public sealed class AndroidSystemImagesDetector : Detector<AndroidSystemImagesDetector.Options>
{
    public override string Kind => "androidSystemImages";

    /// <summary>The image an emulator uses, as <c>android-34/google_apis/x86_64</c>, from its config.ini; null when absent.</summary>
    public static string? ImageOf(string configIni)
    {
        ArgumentNullException.ThrowIfNull(configIni);
        foreach (string raw in configIni.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("image.sysdir.1", StringComparison.Ordinal) || line.IndexOf('=', StringComparison.Ordinal) is not (int eq and > 0))
            {
                continue;
            }

            string value = line[(eq + 1)..].Trim().Replace('\\', '/');
            if (value.StartsWith("system-images/", StringComparison.OrdinalIgnoreCase))
            {
                value = value["system-images/".Length..];
            }

            return value.Trim('/');
        }

        return null;
    }

    protected override Options? BindOptions(OptionReader options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new Options(options.Paths("sdks", required: true), options.Paths("avds", required: true));
    }

    protected override IReadOnlyList<Candidate> Detect(DetectorRun run, Options options)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);
        var used = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var avdChecks = new List<Check>();
        foreach (PathTemplate template in options.Avds)
        {
            PathExpansion expansion = run.Expand(template);
            avdChecks.Add(Check.Passed(expansion.Root.Describe()));
            foreach (ProbeEntry avdHome in expansion.Matches.Where(m => m.CanEnter))
            {
                foreach ((string name, string folder) in AvdFolders(run, avdHome.Path))
                {
                    string? config = run.Context.Locations.Environment.ReadAllText(Path.Join(folder, "config.ini"));
                    string? image = config is null ? null : ImageOf(config);
                    if (image is null)
                    {
                        run.Note($"{folder}: its config.ini names no system image, so no image is offered");
                        return [];
                    }

                    List<string> names = used.TryGetValue(image, out List<string>? list) ? list : used[image] = [];
                    if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        names.Add(name);
                    }
                }
            }
        }

        List<string> avdNames = used.Values.SelectMany(n => n).Order(StringComparer.OrdinalIgnoreCase).ToList();
        List<string> usedLevels = used.Keys.Select(k => k.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).Select(Api).Order(StringComparer.Ordinal).ToList();
        var candidates = new List<Candidate>();
        foreach (PathTemplate template in options.Sdks)
        {
            foreach (ProbeEntry sdk in run.Expand(template).Matches.Where(m => m.CanEnter))
            {
                foreach (ProbeEntry level in FileProbe.List(Path.Join(sdk.Path, "system-images")).Entries.Where(e => e.CanEnter))
                {
                    var unused = new List<string>();
                    bool anyUsed = false;
                    foreach (ProbeEntry tag in FileProbe.List(level.Path).Entries.Where(e => e.CanEnter))
                    {
                        foreach (ProbeEntry abi in FileProbe.List(tag.Path).Entries.Where(e => e.CanEnter))
                        {
                            if (used.ContainsKey($"{level.Name}/{tag.Name}/{abi.Name}"))
                            {
                                anyUsed = true;
                            }
                            else
                            {
                                unused.Add(abi.Path);
                            }
                        }
                    }

                    List<string> paths = anyUsed ? unused : [level.Path];
                    if (paths.Count == 0)
                    {
                        continue;
                    }

                    string api = Api(level.Name);
                    var checks = new List<Check>(avdChecks) { Check.Passed(anyUsed ? $"An emulator uses {api}; only the variants none uses are offered" : $"No emulator uses {api}") };
                    checks.Add(Check.Passed(avdNames.Count == 0
                        ? "You have no emulators set up"
                        : $"Your emulators ({Words.Join(avdNames)}) use {Words.Join(usedLevels)}"));
                    candidates.Add(new Candidate
                    {
                        Key = $"{sdk.Path}|{level.Name}",
                        Paths = paths,
                        Facts = new Dictionary<string, string>(StringComparer.Ordinal) { ["level"] = api, ["sdk"] = sdk.Path },
                        Checks = checks,
                    });
                }
            }
        }

        return candidates;
    }

    /// <summary>Every emulator: <c>&lt;name&gt;.avd</c> folders, and the <c>path=</c> of each <c>&lt;name&gt;.ini</c> (an AVD can live elsewhere).</summary>
    private static List<(string Name, string Folder)> AvdFolders(DetectorRun run, string avdHome)
    {
        var avds = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ProbeEntry entry in FileProbe.List(avdHome).Entries)
        {
            if (entry.CanEnter && entry.Name.EndsWith(".avd", StringComparison.OrdinalIgnoreCase) && seen.Add(entry.Path))
            {
                avds.Add((entry.Name[..^4], entry.Path));
            }
            else if (!entry.IsDirectory && entry.Name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
                && run.Context.Locations.Environment.ReadAllText(entry.Path) is { } ini
                && ini.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("path=", StringComparison.Ordinal)) is { } line
                && line[5..].Trim() is { Length: > 0 } target && Path.IsPathFullyQualified(target)
                && FileProbe.Stat(target) is { CanEnter: true } && seen.Add(Path.TrimEndingDirectorySeparator(target)))
            {
                avds.Add((entry.Name[..^4], Path.TrimEndingDirectorySeparator(target)));
            }
        }

        return avds;
    }

    private static string Api(string level) => level.StartsWith("android-", StringComparison.OrdinalIgnoreCase) ? "API " + level[8..] : level;

    /// <summary>Bound options of an <c>androidSystemImages</c> rule.</summary>
    public sealed record Options(IReadOnlyList<PathTemplate> Sdks, IReadOnlyList<PathTemplate> Avds);
}
