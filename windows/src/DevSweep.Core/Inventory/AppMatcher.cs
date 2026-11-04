using System.Text;
using DevSweep.Core.Inventory.Collectors;

namespace DevSweep.Core.Inventory;

/// <summary>
/// Matches <see cref="AppHint"/>s against <see cref="AppRecord"/>s, case-insensitively.
/// Names match by whole leading words, so "Slack" matches "Slack (Machine - MSI)"
/// but not "Slackware"; version numbers and architecture words are ignored.
/// </summary>
public static class AppMatcher
{
    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        "x64", "x86", "amd64", "arm64", "64", "32", "bit", "the", "inc", "ltd", "llc", "corp", "corporation", "gmbh",
    };

    /// <summary>The first hint that matches <paramref name="record"/>, described, or null.</summary>
    public static string? Match(AppHint hint, AppRecord record)
    {
        ArgumentNullException.ThrowIfNull(hint);
        ArgumentNullException.ThrowIfNull(record);
        foreach (string name in hint.Names)
        {
            if (WordsMatch(name, record.Name))
            {
                return $"name \"{record.Name}\"";
            }
        }

        foreach (string publisher in hint.Publishers)
        {
            if (record.Publisher is { } p && WordsMatch(publisher, p))
            {
                return $"publisher \"{p}\"";
            }
        }

        foreach (string code in hint.ProductCodes)
        {
            if (record.ProductCode is { } c && string.Equals(ProductCodes.Normalize(code), c, StringComparison.OrdinalIgnoreCase))
            {
                return $"product code {{{c}}}";
            }
        }

        foreach (string family in hint.PackageFamilies)
        {
            if (record.PackageFamilyName is { } f && FamilyMatches(family, f))
            {
                return $"package {f}";
            }
        }

        foreach (string id in hint.PackageIds)
        {
            if (record.PackageId is { } r && string.Equals(id, r, StringComparison.OrdinalIgnoreCase))
            {
                return $"package id {r}";
            }
        }

        foreach (string exe in hint.Exes)
        {
            if (record.ExePaths.Any(path => string.Equals(Path.GetFileName(path), exe, StringComparison.OrdinalIgnoreCase)))
            {
                return $"program {exe}";
            }
        }

        foreach (string folder in hint.Folders)
        {
            if (record.InstallLocation is { } location && string.Equals(Path.GetFileName(location.TrimEnd('\\', '/')), folder, StringComparison.OrdinalIgnoreCase))
            {
                return $"install folder {location}";
            }

            if (record.Source == InventorySource.ProgramFiles && string.Equals(record.Name, folder, StringComparison.OrdinalIgnoreCase))
            {
                return $"folder {record.InstallLocation}";
            }
        }

        return null;
    }

    /// <summary>True when the words of <paramref name="hint"/> are the leading words of <paramref name="text"/> (noise and numbers aside), or both read the same without spaces.</summary>
    public static bool WordsMatch(string hint, string text)
    {
        IReadOnlyList<string> wanted = Words(hint);
        IReadOnlyList<string> have = Words(text);
        if (wanted.Count == 0 || have.Count == 0)
        {
            return false;
        }

        if (have.Count >= wanted.Count && wanted.Select((w, i) => w == have[i]).All(x => x))
        {
            return true;
        }

        return string.Concat(wanted) == string.Concat(have);
    }

    /// <summary>Lowercase alphanumeric words without version numbers and noise words.</summary>
    public static IReadOnlyList<string> Words(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (char c in (text ?? string.Empty) + " ")
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(char.ToLowerInvariant(c));
                continue;
            }

            if (current.Length > 0)
            {
                string word = current.ToString();
                if (!Noise.Contains(word) && !word.All(char.IsDigit))
                {
                    words.Add(word);
                }

                current.Clear();
            }
        }

        return words;
    }

    private static bool FamilyMatches(string hint, string family)
    {
        if (string.Equals(hint, family, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        int underscore = family.IndexOf('_', StringComparison.Ordinal);
        return !hint.Contains('_', StringComparison.Ordinal) && underscore > 0
            && string.Equals(hint, family[..underscore], StringComparison.OrdinalIgnoreCase);
    }
}
