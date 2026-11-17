namespace DevSweep.App.ViewModels.Settings;

/// <summary>Whether a folder can be searched.</summary>
public enum FolderState
{
    /// <summary>It exists and can be listed.</summary>
    Ok,

    /// <summary>There is no such folder on this PC (a usual name that this PC does not use); it is skipped.</summary>
    Missing,

    /// <summary>It exists but Windows will not let DevSweep list it (Controlled folder access, or permissions).</summary>
    Denied,
}

/// <summary>A folder DevSweep looks in for projects, as Settings lists it.</summary>
/// <param name="Path">The folder.</param>
/// <param name="IsDefault">One of the usual folders every PC gets (they cannot be removed; adding yours extends them).</param>
/// <param name="State">Present, missing or blocked.</param>
public sealed record FolderRow(string Path, bool IsDefault, FolderState State)
{
    public string StateText => State switch
    {
        FolderState.Missing => "Not on this PC, so it is skipped",
        FolderState.Denied => "Windows isn't letting DevSweep look inside",
        _ => string.Empty,
    };
}

/// <summary>The project folders list: what the person added, then the usual names under the profile (like the Mac's defaults).</summary>
public static class FolderRows
{
    /// <summary>The usual project folders (Code, source, repos, src and so on under the profile, and Documents\GitHub).</summary>
    public static IReadOnlyList<string> Defaults(string? profile, string? documents = null)
    {
        var list = new List<string>();
        if (!string.IsNullOrEmpty(profile))
        {
            list.Add(System.IO.Path.Join(profile, "Code"));
            foreach (string name in (string[])["source", "Projects", "repos", "code", "src", "dev", "git", "workspace", "IdeaProjects", "AndroidStudioProjects", "RiderProjects"])
            {
                list.Add(System.IO.Path.Join(profile, name));
            }

            documents ??= System.IO.Path.Join(profile, "Documents");
        }

        if (!string.IsNullOrEmpty(documents))
        {
            list.Add(System.IO.Path.Join(documents, "GitHub"));
            list.Add(System.IO.Path.Join(documents, "Projects"));
        }

        return [.. list.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Looks at the folder: missing, blocked or fine.</summary>
    public static FolderState Probe(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return FolderState.Missing;
            }

            using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            entries.MoveNext();
            return FolderState.Ok;
        }
        catch (UnauthorizedAccessException)
        {
            return FolderState.Denied;
        }
        catch (IOException)
        {
            return FolderState.Denied;
        }
    }

    /// <summary>The folders the person added first, then the usual ones (present before missing).</summary>
    public static IReadOnlyList<FolderRow> Build(IReadOnlyList<string> added, string? profile, Func<string, FolderState>? probe = null, string? documents = null)
    {
        ArgumentNullException.ThrowIfNull(added);
        probe ??= Probe;
        IEnumerable<FolderRow> mine = added.Select(f => new FolderRow(f, false, probe(f)));
        IEnumerable<FolderRow> usual = Defaults(profile, documents)
            .Where(d => !added.Any(a => string.Equals(a.TrimEnd('\\'), d.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            .Select(d => new FolderRow(d, true, probe(d)))
            .OrderBy(r => r.State == FolderState.Missing);
        return [.. mine, .. usual];
    }

    /// <summary>"3 usual folders aren't on this PC".</summary>
    public static string MissingTitle(int count) => count == 1 ? "1 usual folder isn't on this PC" : $"{count} usual folders aren't on this PC";
}
