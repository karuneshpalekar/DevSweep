using System.Globalization;
using System.Text.Json;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Detectors;
using DevSweep.Core.Packs.Tools;
using DevSweep.Core.Rules;

namespace DevSweep.Core.Packs.Detectors;

/// <summary>
/// <c>kind: docker</c> (the Mac detector, ported). Sizes come from Docker itself
/// (<c>docker system df</c>); nothing is found when Docker is not installed or not running.
/// <list type="bullet"><item><c>names</c>: which row (<c>Build Cache</c>, <c>Images</c>, <c>Containers</c>, <c>Local Volumes</c>).</item></list>
/// </summary>
public sealed class DockerDetector(IMachineQueries machine) : Detector<DockerDetector.Options>
{
    private static readonly string[] Rows = ["Build Cache", "Images", "Containers", "Local Volumes"];

    public override string Kind => "docker";

    /// <summary>docker.exe on PATH, then Docker Desktop's own copy.</summary>
    public static string? Cli(DetectorRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var env = run.Context.Locations.Environment;
        return ToolLocator.Find(env, "docker.exe",
            run.Context.Locations.Resolve("ProgramFiles").Path is { } pf ? Path.Join(pf, "Docker", "Docker", "resources", "bin", "docker.exe") : null);
    }

    /// <summary>Docker's sizes ("1.2GB", "512.3MB (45%)", "0B"); Docker uses powers of 1000.</summary>
    public static long Bytes(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string s = text.Trim();
        int i = 0;
        while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '.'))
        {
            i++;
        }

        if (i == 0 || !double.TryParse(s.AsSpan(0, i), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            return 0;
        }

        string unit = new(s[i..].TakeWhile(char.IsAsciiLetter).ToArray());
        double scale = unit.ToUpperInvariant() switch
        {
            "B" => 1,
            "KB" => 1e3,
            "MB" => 1e6,
            "GB" => 1e9,
            "TB" => 1e12,
            _ => double.NaN,
        };
        return double.IsNaN(scale) ? 0 : (long)Math.Round(value * scale);
    }

    /// <summary><c>docker system df --format "{{json .}}"</c>: one JSON object per line, by Type.</summary>
    public static Dictionary<string, Dictionary<string, string>> ParseSystemDf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var rows = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("Type", out JsonElement type) || type.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                rows[type.GetString()!] = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString(), StringComparer.Ordinal);
            }
            catch (JsonException)
            {
                // Not a row (a warning Docker printed): skipped.
            }
        }

        return rows;
    }

    protected override Options? BindOptions(OptionReader options)
    {
        ArgumentNullException.ThrowIfNull(options);
        IReadOnlyList<string> names = options.TextList("names", required: true);
        foreach (string name in names.Where(n => !Rows.Contains(n, StringComparer.Ordinal)))
        {
            options.Errors.Add(options.Node.Get("names"), $"names: \"{name}\" is not a docker system df row (known: {string.Join(", ", Rows)})");
        }

        return new Options(names);
    }

    protected override IReadOnlyList<Candidate> Detect(DetectorRun run, Options options)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);
        if (Cli(run) is not { } cli)
        {
            run.Note("Docker is not installed (no docker.exe on PATH or in Docker Desktop's folder)");
            return [];
        }

        CommandResult result = machine.Run([cli, "system", "df", "--format", "{{json .}}"], TimeSpan.FromSeconds(30), run.CancellationToken);
        if (!result.Succeeded)
        {
            run.Note($"docker system df gave no answer (Docker is probably not running): {(result.StartError ?? result.StandardError).Trim()}");
            return [];
        }

        Dictionary<string, Dictionary<string, string>> rows = ParseSystemDf(result.StandardOutput);
        var candidates = new List<Candidate>();
        foreach (string name in options.Names)
        {
            if (!rows.TryGetValue(name, out Dictionary<string, string>? row))
            {
                continue;
            }

            long reclaimable = Bytes(row.GetValueOrDefault("Reclaimable", string.Empty));
            if (reclaimable <= 0)
            {
                continue;
            }

            string total = row.GetValueOrDefault("TotalCount", "?");
            string active = row.GetValueOrDefault("Active", "0");
            candidates.Add(new Candidate
            {
                Key = name,
                Size = reclaimable,
                Subtitle = $"{total} total, {active} in use",
                Facts = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["total"] = total,
                    ["active"] = active,
                    ["docker"] = cli,
                    ["dockerSize"] = row.GetValueOrDefault("Size", string.Empty),
                },
                Checks =
                [
                    Check.Passed($"Measured by Docker itself (docker system df, {cli})"),
                    Check.Passed("Only what no container uses is counted (Docker's \"reclaimable\")"),
                ],
            });
        }

        return candidates;
    }

    /// <summary>Bound options of a <c>docker</c> rule.</summary>
    public sealed record Options(IReadOnlyList<string> Names);
}
