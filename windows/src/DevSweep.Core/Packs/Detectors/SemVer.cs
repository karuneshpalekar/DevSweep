using DevSweep.Core.Detectors;

namespace DevSweep.Core.Packs.Detectors;

/// <summary>
/// Orders versions with prerelease tags the way .NET, Node and Rust do:
/// <c>9.0.100-rc.1</c> is older than <c>9.0.100</c>; otherwise number by number.
/// </summary>
public sealed class SemVerComparer : IComparer<string>
{
    public static SemVerComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        (string baseX, string? preX) = Split(x);
        (string baseY, string? preY) = Split(y);
        IReadOnlyList<long> a = VersionComparer.Numbers(baseX);
        IReadOnlyList<long> b = VersionComparer.Numbers(baseY);
        for (int i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            long left = i < a.Count ? a[i] : 0;
            long right = i < b.Count ? b[i] : 0;
            if (left != right)
            {
                return left.CompareTo(right);
            }
        }

        if (preX is null || preY is null)
        {
            return (preX is null ? 1 : 0) - (preY is null ? 1 : 0);
        }

        return ComparePrerelease(preX, preY);
    }

    /// <summary>SemVer precedence: dot-separated parts, numbers numerically, words by text, numbers before words.</summary>
    private static int ComparePrerelease(string x, string y)
    {
        string[] a = x.Split('.');
        string[] b = y.Split('.');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool numA = long.TryParse(a[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long na);
            bool numB = long.TryParse(b[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long nb);
            int order = numA && numB ? na.CompareTo(nb)
                : numA ? -1
                : numB ? 1
                : StringComparer.OrdinalIgnoreCase.Compare(a[i], b[i]);
            if (order != 0)
            {
                return order;
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    /// <summary>True when the version has a prerelease tag (<c>-preview.3</c>).</summary>
    public static bool IsPrerelease(string version) => Split(version).Pre is not null;

    /// <summary>The first <paramref name="count"/> numbers ("8.0.404" with 1 gives "8").</summary>
    public static string Prefix(string version, int count)
        => string.Join('.', VersionComparer.Numbers(Split(version).Base).Take(count));

    private static (string Base, string? Pre) Split(string? version)
    {
        string v = (version ?? string.Empty).TrimStart('v', 'V');
        int dash = v.IndexOf('-', StringComparison.Ordinal);
        return dash < 0 ? (v, null) : (v[..dash], v[(dash + 1)..]);
    }
}
