using System.Text.Json;
using System.Text.RegularExpressions;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Detectors;
using DevSweep.Core.Locations;
using DevSweep.Core.Rules;

namespace DevSweep.Core.Packs.Detectors;

/// <summary>
/// <c>kind: editorExtensions</c> (the Mac detector, ported). Extension folders VS Code, Cursor
/// or Windsurf no longer loads: not in the editor's own list (<c>extensions.json</c>), or marked
/// obsolete (<c>.obsolete</c>) after an update or uninstall. Nothing is offered when the list is
/// missing, empty or unreadable.
/// <list type="bullet"><item><c>paths</c>: extension folders (<c>{Profile}\.vscode\extensions</c>...).</item></list>
/// </summary>
public sealed partial class EditorExtensionsDetector : Detector<EditorExtensionsDetector.Options>
{
    public override string Kind => "editorExtensions";

    /// <summary>Folder names <c>extensions.json</c> refers to (<c>relativeLocation</c>, or the last part of <c>location.path</c>); null when it is not a list.</summary>
    public static IReadOnlySet<string>? Referenced(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement e in doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
            {
                if (e.TryGetProperty("relativeLocation", out JsonElement rel) && rel.ValueKind == JsonValueKind.String)
                {
                    names.Add(rel.GetString()!);
                }

                if (e.TryGetProperty("location", out JsonElement loc) && loc.ValueKind == JsonValueKind.Object
                    && loc.TryGetProperty("path", out JsonElement p) && p.ValueKind == JsonValueKind.String)
                {
                    string path = p.GetString()!.TrimEnd('/', '\\');
                    names.Add(path[(path.LastIndexOfAny(['/', '\\']) + 1)..]);
                }
            }

            return names;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The keys of <c>.obsolete</c> ({"folder": true}).</summary>
    public static IReadOnlySet<string> Obsolete(string? json)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (json is null)
        {
            return names;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in doc.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.True))
                {
                    names.Add(p.Name);
                }
            }
        }
        catch (JsonException)
        {
            // Unreadable: nothing is called obsolete because of it.
        }

        return names;
    }

    /// <summary>The editor a folder belongs to, from its parent (<c>.cursor</c> is Cursor).</summary>
    public static string Editor(string extensionsFolder) => Path.GetFileName(Path.GetDirectoryName(extensionsFolder) ?? string.Empty).ToLowerInvariant() switch
    {
        ".cursor" => "Cursor",
        ".windsurf" => "Windsurf",
        ".vscode-insiders" => "VS Code Insiders",
        ".vscodium" or ".vscode-oss" => "VSCodium",
        _ => "VS Code",
    };

    [GeneratedRegex("^[^.\\s]+\\.[^\\s]+-[0-9]+\\.[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionFolder();

    protected override Options? BindOptions(OptionReader options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new Options(options.Paths("paths", required: true));
    }

    protected override IReadOnlyList<Candidate> Detect(DetectorRun run, Options options)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);
        var candidates = new List<Candidate>();
        foreach (PathTemplate template in options.Paths)
        {
            foreach (ProbeEntry root in run.Expand(template).Matches.Where(m => m.CanEnter))
            {
                string editor = Editor(root.Path);
                string listFile = Path.Join(root.Path, "extensions.json");
                if (run.Context.Locations.Environment.ReadAllText(listFile) is not { } json || Referenced(json) is not { Count: > 0 } referenced)
                {
                    run.Note($"{listFile} is missing, empty or unreadable: no {editor} extension is called stale");
                    continue;
                }

                IReadOnlySet<string> obsolete = Obsolete(run.Context.Locations.Environment.ReadAllText(Path.Join(root.Path, ".obsolete")));
                List<ProbeEntry> stale = FileProbe.List(root.Path).Entries
                    .Where(e => e.CanEnter && !e.Name.StartsWith('.') && ExtensionFolder().IsMatch(e.Name)
                        && (!referenced.Contains(e.Name) || obsolete.Contains(e.Name)))
                    .ToList();
                if (stale.Count == 0)
                {
                    continue;
                }

                List<string> names = stale.Select(e => e.Name).ToList();
                candidates.Add(new Candidate
                {
                    Key = root.Path,
                    Paths = stale.Select(e => e.Path).ToList(),
                    Title = $"Old {editor} extension versions",
                    Subtitle = string.Join(", ", names.Take(2)) + (names.Count > 2 ? $" and {names.Count - 2} more" : string.Empty),
                    Facts = new Dictionary<string, string>(StringComparer.Ordinal) { ["editor"] = editor, ["extensions"] = root.Path },
                    Checks =
                    [
                        Check.Passed($"Not in {editor}'s list of installed extensions ({listFile}, {referenced.Count} listed), or marked obsolete by {editor}"),
                    ],
                });
            }
        }

        return candidates;
    }

    /// <summary>Bound options of an <c>editorExtensions</c> rule.</summary>
    public sealed record Options(IReadOnlyList<PathTemplate> Paths);
}
