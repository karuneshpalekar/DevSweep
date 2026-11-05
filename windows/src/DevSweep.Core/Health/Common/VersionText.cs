using System.Globalization;
using System.Text.RegularExpressions;
using DevSweep.Core.Detectors;

namespace DevSweep.Core.Health.Common;

/// <summary>Version strings as the Mac app treats them: numbers compared part by part, release lines cut from the front.</summary>
public static partial class VersionText
{
    /// <summary>The numeric parts ("v20.11.0" gives 20, 11, 0).</summary>
    public static IReadOnlyList<long> Parts(string? version) => VersionComparer.Numbers(version);

    /// <summary>True when <paramref name="a"/> is older than <paramref name="b"/> (numbers only; "3.12" equals "3.12.0").</summary>
    public static bool Less(string? a, string? b)
    {
        IReadOnlyList<long> x = Parts(a);
        IReadOnlyList<long> y = Parts(b);
        for (int i = 0; i < Math.Max(x.Count, y.Count); i++)
        {
            long l = i < x.Count ? x[i] : 0;
            long r = i < y.Count ? y[i] : 0;
            if (l != r)
            {
                return l < r;
            }
        }

        return false;
    }

    /// <summary>The first <paramref name="count"/> numbers joined by dots ("3.12.1", 2 gives "3.12").</summary>
    public static string Prefix(string? version, int count)
    {
        IReadOnlyList<long> parts = Parts(version);
        return parts.Count == 0 ? version ?? string.Empty : string.Join('.', parts.Take(count).Select(p => p.ToString(CultureInfo.InvariantCulture)));
    }

    public static string Major(string? version) => Prefix(version, 1);

    public static string MajorMinor(string? version) => Prefix(version, 2);

    /// <summary>The first version-looking token in <paramref name="text"/> ("go1.22.0" gives "1.22.0"), or null.</summary>
    public static string? FirstVersion(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        Match match = Dotted().Match(text);
        return match.Success ? match.Value : null;
    }

    /// <summary>A folder name that is a version ("v20.11.0", "20.11.0", "3.12.1"), or null.</summary>
    public static string? FromFolderName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Match match = FolderVersion().Match(name);
        return match.Success ? match.Groups["v"].Value : null;
    }

    [GeneratedRegex(@"\d+(\.\d+)+")]
    private static partial Regex Dotted();

    [GeneratedRegex(@"^v?(?<v>\d+(\.\d+){1,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex FolderVersion();
}
