using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevSweep.Core.Health.Packages;

/// <summary>What a parser made of a manager's output: the packages, or that it could not tell.</summary>
public sealed record ParsedPackages(IReadOnlyList<OutdatedPackage> Outdated, bool Understood);

/// <summary>
/// Parsers for what the managers print. winget's table has localised headings, so its columns are found by position
/// (the line of dashes and the heading above it), and a result that does not look like that table is reported as
/// not understood, never guessed. Chocolatey's <c>-r</c> output is pipe separated and the same in every language.
/// </summary>
public static partial class PackageParsers
{
    /// <summary>winget's exit code when nothing matched (<c>APPINSTALLER_CLI_ERROR_NO_APPLICATIONS_FOUND</c>, 0x8A150014).</summary>
    public const int WingetNothingFound = unchecked((int)0x8A150014);

    public static ParsedPackages Winget(string output, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(output);
        string[] lines = [.. output.Split('\n').Select(l => l.TrimEnd('\r')).Select(l => l.Contains('\r', StringComparison.Ordinal) ? l[(l.LastIndexOf('\r') + 1)..] : l)];
        int rule = Array.FindIndex(lines, l => Dashes().IsMatch(l));
        if (rule < 1)
        {
            return new ParsedPackages([], exitCode == WingetNothingFound);
        }

        List<int> starts = [.. HeadingColumns().Matches(lines[rule - 1]).Select(m => m.Index)];
        if (starts.Count < 4)
        {
            return new ParsedPackages([], false);
        }

        var found = new List<OutdatedPackage>();
        for (int i = rule + 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Trim().Length == 0 || line.Length <= starts[3])
            {
                break;
            }

            string Cell(int column) => column < starts.Count
                ? line[Math.Min(starts[column], line.Length)..Math.Min(column + 1 < starts.Count ? starts[column + 1] : line.Length, line.Length)].Trim()
                : string.Empty;
            string id = Cell(1);
            string installed = Cell(2).TrimStart('<', '>', ' ');
            string available = Cell(3);
            if (id.Length == 0 || id.Contains(' ', StringComparison.Ordinal) || !installed.Any(char.IsAsciiDigit) || !available.Any(char.IsAsciiDigit) || available.Contains(' ', StringComparison.Ordinal))
            {
                break;
            }

            found.Add(new OutdatedPackage(Cell(0).TrimEnd('…'), id, installed, available));
        }

        return new ParsedPackages(found, found.Count > 0);
    }

    /// <summary><c>choco outdated -r</c>: <c>name|current|available|pinned</c> per line.</summary>
    public static ParsedPackages Chocolatey(string output, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(output);
        var found = new List<OutdatedPackage>();
        foreach (string raw in output.Split('\n'))
        {
            string[] parts = raw.Trim().Split('|');
            if (parts.Length == 4 && parts[1].Any(char.IsAsciiDigit) && parts[2].Any(char.IsAsciiDigit) && !parts[0].Contains(' ', StringComparison.Ordinal))
            {
                found.Add(new OutdatedPackage(parts[0], parts[0], parts[1], parts[2], string.Equals(parts[3], "true", StringComparison.OrdinalIgnoreCase)));
            }
        }

        return new ParsedPackages(found, found.Count > 0 || exitCode == 0);
    }

    /// <summary>The <c>version</c> of a Scoop manifest, or null when it has none a number can be compared with ("nightly", "latest").</summary>
    public static string? ScoopManifestVersion(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("version", out JsonElement version) && version.ValueKind == JsonValueKind.String
                && version.GetString() is { Length: > 0 } text && char.IsAsciiDigit(text[0]))
            {
                return text;
            }
        }
        catch (JsonException)
        {
            // An unreadable manifest is "unknown", not an error to show.
        }

        return null;
    }

    /// <summary>Scoop's <c>install.json</c>: the bucket the app came from and whether it is held back.</summary>
    public static (string? Bucket, bool Held) ScoopInstall(string? json)
    {
        if (json is null)
        {
            return (null, false);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, false);
            }

            string? bucket = root.TryGetProperty("bucket", out JsonElement b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            bool held = root.TryGetProperty("hold", out JsonElement h) && h.ValueKind == JsonValueKind.True;
            return (string.IsNullOrWhiteSpace(bucket) ? null : bucket, held);
        }
        catch (JsonException)
        {
            return (null, false);
        }
    }

    [GeneratedRegex(@"^-{5,}\s*$")]
    private static partial Regex Dashes();

    [GeneratedRegex(@"\S+(?:\s\S+)*(?=\s{2,}|\s*$)")]
    private static partial Regex HeadingColumns();
}
