using System.Text.Json;
using System.Text.RegularExpressions;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Detectors;
using DevSweep.Core.Locations;
using DevSweep.Core.Rules;

namespace DevSweep.Core.Packs.Detectors;

/// <summary>
/// <c>kind: visualStudioInstances</c>. Per-user folders of Visual Studio instances that are no
/// longer installed (<c>%LOCALAPPDATA%\Microsoft\VisualStudio\17.0_1a2b3c4d</c>, and its
/// <c>Exp</c> hive). The installed instances are read (only read) from the Visual Studio
/// setup configuration, <c>_Instances\&lt;id&gt;\state.json</c>; when that folder cannot be read
/// or does not exist, nothing is offered.
/// <list type="bullet">
/// <item><c>parents</c>: where the per-user folders are;</item>
/// <item><c>instances</c>: the setup configuration's <c>_Instances</c> folder.</item>
/// </list>
/// </summary>
public sealed partial class VisualStudioInstancesDetector : Detector<VisualStudioInstancesDetector.Options>
{
    public override string Kind => "visualStudioInstances";

    /// <summary>An installed instance from its state.json.</summary>
    public sealed record Instance(string Id, string? Version, string? Name, string? Path);

    /// <summary>Reads <c>installationVersion</c>, <c>installationName</c> and <c>installationPath</c>; null when not JSON.</summary>
    public static Instance? ParseState(string id, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new Instance(id, Text(root, "installationVersion"), Text(root, "installationName"), Text(root, "installationPath"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex("^(?<major>[0-9]+)\\.0_(?<id>[0-9a-fA-F]{8})(?<suffix>Exp|_Config|_ConfigExp)?$", RegexOptions.CultureInvariant)]
    public static partial Regex FolderName();

    protected override Options? BindOptions(OptionReader options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new Options(options.Paths("parents", required: true), options.Paths("instances", required: true));
    }

    protected override IReadOnlyList<Candidate> Detect(DetectorRun run, Options options)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);
        var installed = new Dictionary<string, Instance>(StringComparer.OrdinalIgnoreCase);
        string? instancesFolder = null;
        foreach (PathTemplate template in options.Instances)
        {
            run.Expand(template); // recorded for the report
            string? path = run.Context.Locations.Expand(template, out _);
            ProbeListing listing = path is null ? new ProbeListing([], ProbeError.NotFound) : FileProbe.List(path);
            if (listing.Error != ProbeError.None)
            {
                run.Note(listing.Error == ProbeError.NotFound
                    ? $"{path}: no Visual Studio setup configuration, so no Visual Studio folder is called a leftover"
                    : $"{path} could not be read, so no Visual Studio folder is called a leftover");
                return [];
            }

            instancesFolder = path;
            foreach (ProbeEntry entry in listing.Entries.Where(e => e.CanEnter))
            {
                string? text = run.Context.Locations.Environment.ReadAllText(Path.Join(entry.Path, "state.json"));
                installed[entry.Name] = (text is null ? null : ParseState(entry.Name, text)) ?? new Instance(entry.Name, null, null, null);
            }

        }

        if (instancesFolder is null)
        {
            return [];
        }

        string installedText = installed.Count == 0
            ? "no instance is installed"
            : Words.Join(installed.Values.Select(i => $"{i.Name ?? i.Id} ({i.Id}{(i.Version is null ? "" : ", " + i.Version)})").Order(StringComparer.Ordinal).ToList());
        var byId = new Dictionary<string, List<ProbeEntry>>(StringComparer.OrdinalIgnoreCase);
        var majors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (PathTemplate template in options.Parents)
        {
            foreach (ProbeEntry parent in run.Expand(template).Matches.Where(m => m.CanEnter))
            {
                foreach (ProbeEntry child in FileProbe.List(parent.Path).Entries.Where(c => c.CanEnter))
                {
                    Match match = FolderName().Match(child.Name);
                    if (!match.Success || installed.ContainsKey(match.Groups["id"].Value))
                    {
                        continue;
                    }

                    string id = match.Groups["id"].Value;
                    (byId.TryGetValue(id, out List<ProbeEntry>? list) ? list : byId[id] = []).Add(child);
                    majors[id] = match.Groups["major"].Value;
                }
            }
        }

        return byId.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(pair => new Candidate
        {
            Key = pair.Key,
            Paths = pair.Value.Select(e => e.Path).ToList(),
            Title = $"Visual Studio {majors[pair.Key]}.0 instance {pair.Key} (no longer installed)",
            Facts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["instance"] = pair.Key,
                ["major"] = majors[pair.Key],
                ["installed"] = installedText,
            },
            Checks =
            [
                Check.Passed($"Visual Studio's setup configuration ({instancesFolder}) has no instance {pair.Key}"),
                Check.Passed($"Installed: {installedText}"),
            ],
        }).ToList();
    }

    /// <summary>Bound options of a <c>visualStudioInstances</c> rule.</summary>
    public sealed record Options(IReadOnlyList<PathTemplate> Parents, IReadOnlyList<PathTemplate> Instances);
}
