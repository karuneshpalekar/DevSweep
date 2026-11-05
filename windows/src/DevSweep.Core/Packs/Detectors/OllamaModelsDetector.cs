using System.Text.Json;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Detectors;
using DevSweep.Core.Locations;
using DevSweep.Core.Rules;
using DevSweep.Core.Scanning;

namespace DevSweep.Core.Packs.Detectors;

/// <summary>
/// <c>kind: ollamaModels</c>. Each model Ollama has pulled, from its manifests. Models share
/// layers, so a model's size is the layers only it uses: what <c>ollama rm</c> frees. Listed, never
/// removed by DevSweep (the rule shows Ollama's own command).
/// <list type="bullet"><item><c>paths</c>: Ollama's models folders (<c>{ollamaModels}</c>).</item></list>
/// </summary>
public sealed class OllamaModelsDetector : Detector<OllamaModelsDetector.Options>
{
    public override string Kind => "ollamaModels";

    /// <summary>The blob digests a manifest names (layers and config), as file names (<c>sha256-abc...</c>).</summary>
    public static IReadOnlyList<string>? Blobs(string manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        try
        {
            using var doc = JsonDocument.Parse(manifest);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var digests = new List<string>();
            void Add(JsonElement e)
            {
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("digest", out JsonElement d) && d.ValueKind == JsonValueKind.String)
                {
                    digests.Add(d.GetString()!.Replace(':', '-'));
                }
            }

            if (doc.RootElement.TryGetProperty("config", out JsonElement config))
            {
                Add(config);
            }

            if (doc.RootElement.TryGetProperty("layers", out JsonElement layers) && layers.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement layer in layers.EnumerateArray())
                {
                    Add(layer);
                }
            }

            return digests;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The name <c>ollama list</c> shows for a manifest path below <c>manifests</c> (<c>registry.ollama.ai\library\llama3\latest</c> is <c>llama3:latest</c>).</summary>
    public static string ModelName(IReadOnlyList<string> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count < 2)
        {
            return string.Join('/', parts);
        }

        string tag = parts[^1];
        List<string> name = parts.Take(parts.Count - 1).ToList();
        if (name.Count >= 1 && name[0].Equals("registry.ollama.ai", StringComparison.OrdinalIgnoreCase))
        {
            name.RemoveAt(0);
            if (name.Count >= 1 && name[0].Equals("library", StringComparison.OrdinalIgnoreCase))
            {
                name.RemoveAt(0);
            }
        }

        return $"{string.Join('/', name)}:{tag}";
    }

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
            foreach (ProbeEntry models in run.Expand(template).Matches.Where(m => m.CanEnter))
            {
                var manifests = new List<(string Name, IReadOnlyList<string> Blobs)>();
                var pending = new Stack<(string Folder, List<string> Parts)>();
                pending.Push((Path.Join(models.Path, "manifests"), []));
                while (pending.Count > 0)
                {
                    (string folder, List<string> parts) = pending.Pop();
                    foreach (ProbeEntry entry in FileProbe.List(folder).Entries)
                    {
                        List<string> next = [.. parts, entry.Name];
                        if (entry.CanEnter && next.Count < 6)
                        {
                            pending.Push((entry.Path, next));
                        }
                        else if (!entry.IsDirectory && !entry.IsCloudPlaceholder)
                        {
                            if (run.Context.Locations.Environment.ReadAllText(entry.Path) is not { } text || Blobs(text) is not { } blobs)
                            {
                                run.Note($"{entry.Path} could not be read; no Ollama model is listed");
                                return [];
                            }

                            manifests.Add((ModelName(next), blobs));
                        }
                    }
                }

                var users = manifests.SelectMany(m => m.Blobs.Distinct().Select(b => (Blob: b, m.Name)))
                    .GroupBy(x => x.Blob, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Select(x => x.Name).Distinct().Count(), StringComparer.OrdinalIgnoreCase);
                string blobsFolder = Path.Join(models.Path, "blobs");
                foreach ((string name, IReadOnlyList<string> blobs) in manifests.OrderBy(m => m.Name, StringComparer.Ordinal))
                {
                    long own = 0;
                    long shared = 0;
                    foreach (string blob in blobs.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        long bytes = FileProbe.Stat(Path.Join(blobsFolder, blob))?.Length ?? 0;
                        if (users[blob] == 1)
                        {
                            own += bytes;
                        }
                        else
                        {
                            shared += bytes;
                        }
                    }

                    candidates.Add(new Candidate
                    {
                        Key = $"{models.Path}|{name}",
                        Size = own,
                        Title = $"Ollama model {name}",
                        Subtitle = shared > 0 ? $"plus {SizeText.Format(shared)} shared with other models" : models.Path,
                        Facts = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["model"] = name,
                            ["shared"] = SizeText.Format(shared),
                            ["models"] = models.Path,
                        },
                        Checks =
                        [
                            Check.Passed($"From Ollama's manifest for {name} in {models.Path}"),
                            Check.Passed(shared > 0
                                ? $"Only the layers no other model uses are counted; {SizeText.Format(shared)} is shared and stays"
                                : "No other model shares its layers"),
                        ],
                    });
                }
            }
        }

        return candidates;
    }

    /// <summary>Bound options of an <c>ollamaModels</c> rule.</summary>
    public sealed record Options(IReadOnlyList<PathTemplate> Paths);
}
