namespace DevSweep.Core.Projects.Git;

/// <summary>What one entry of <c>git status --porcelain=v2</c> is.</summary>
public enum StatusKind
{
    Changed,
    Renamed,
    Unmerged,
    Untracked,
    Ignored,
}

/// <summary>One entry of <c>git status --porcelain=v2 -z</c>.</summary>
/// <param name="Kind">Changed (<c>1</c>), renamed or copied (<c>2</c>), unmerged (<c>u</c>), untracked (<c>?</c>), ignored (<c>!</c>).</param>
/// <param name="Path">Path relative to the top of the working tree, with forward slashes.</param>
/// <param name="Xy">The two status letters (staged, unstaged); empty for untracked and ignored.</param>
/// <param name="Submodule">The submodule field: <c>N...</c> for a file, <c>S&lt;c&gt;&lt;m&gt;&lt;u&gt;</c> for a submodule.</param>
/// <param name="OriginalPath">For a rename, the old path.</param>
public sealed record StatusEntry(StatusKind Kind, string Path, string Xy = "", string Submodule = "N...", string? OriginalPath = null)
{
    public bool IsSubmodule => Submodule.Length == 4 && Submodule[0] == 'S';

    /// <summary>Submodule whose recorded commit changed, or with changes or untracked files inside.</summary>
    public bool SubmoduleHasWork => IsSubmodule && Submodule.AsSpan(1).IndexOfAnyExcept('.') >= 0;

    /// <summary>Changed only in the working tree (not staged): the line-ending comparison applies.</summary>
    public bool UnstagedOnly => Xy.Length == 2 && Xy[0] == '.' && Xy[1] != '.';
}

/// <summary>Parses <c>git status --porcelain=v2 -z</c> (the format git keeps stable for scripts).</summary>
public static class StatusParser
{
    public static IReadOnlyList<StatusEntry> Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        string[] records = output.Split('\0');
        var entries = new List<StatusEntry>();
        for (int i = 0; i < records.Length; i++)
        {
            string record = records[i];
            if (record.Length < 2 || record[0] == '#')
            {
                continue;
            }

            switch (record[0])
            {
                case '?':
                    entries.Add(new StatusEntry(StatusKind.Untracked, record[2..]));
                    break;
                case '!':
                    entries.Add(new StatusEntry(StatusKind.Ignored, record[2..]));
                    break;
                case '1':
                    // 1 XY sub mH mI mW hH hI path
                    if (Fields(record, 9) is { } one)
                    {
                        entries.Add(new StatusEntry(StatusKind.Changed, one[8], one[1], one[2]));
                    }

                    break;
                case '2':
                    // 2 XY sub mH mI mW hH hI Xscore path, then the original path as the next record
                    if (Fields(record, 10) is { } two)
                    {
                        string? original = i + 1 < records.Length ? records[++i] : null;
                        entries.Add(new StatusEntry(StatusKind.Renamed, two[9], two[1], two[2], original));
                    }

                    break;
                case 'u':
                    // u XY sub m1 m2 m3 mW h1 h2 h3 path
                    if (Fields(record, 11) is { } u)
                    {
                        entries.Add(new StatusEntry(StatusKind.Unmerged, u[10], u[1], u[2]));
                    }

                    break;
            }
        }

        return entries;
    }

    /// <summary>Splits into <paramref name="count"/> space-separated fields; the last keeps any spaces (it is the path).</summary>
    private static string[]? Fields(string record, int count)
    {
        string[] fields = record.Split(' ', count);
        return fields.Length == count ? fields : null;
    }
}
