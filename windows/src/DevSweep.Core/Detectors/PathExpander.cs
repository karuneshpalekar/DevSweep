using DevSweep.Core.Locations;

namespace DevSweep.Core.Detectors;

/// <summary>What a rule path matched on disk.</summary>
/// <param name="Template">The rule path.</param>
/// <param name="Root">How its root was resolved (goes into "What was checked").</param>
/// <param name="Matches">Existing items, never links or cloud placeholders.</param>
/// <param name="Skipped">Links met on the way or as matches, which are never followed or listed.</param>
public sealed record PathExpansion(PathTemplate Template, ResolvedLocation Root, IReadOnlyList<ProbeEntry> Matches, IReadOnlyList<string> Skipped);

/// <summary>
/// Expands rule paths segment by segment. Every folder on the way must be an
/// ordinary folder: a junction, symlink or cloud folder ends that branch (and is
/// reported), so a rule can never reach outside the location it names.
/// </summary>
public static class PathExpander
{
    public static PathExpansion Expand(PathTemplate template, DetectorRun run)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(run);
        ResolvedLocation root = run.Context.Locations.Resolve(template.Root);
        var skipped = new List<string>();
        if (root.Path is null || FileProbe.Stat(root.Path) is not { } rootEntry)
        {
            return new PathExpansion(template, root, [], skipped);
        }

        List<ProbeEntry> current = [rootEntry];
        foreach (string segment in template.Segments)
        {
            run.ThrowIfCancelled();
            var next = new List<ProbeEntry>();
            foreach (ProbeEntry folder in current)
            {
                if (!folder.CanEnter)
                {
                    if (folder.IsReparsePoint || folder.IsCloudPlaceholder)
                    {
                        skipped.Add(folder.Path);
                    }

                    continue;
                }

                if (!Glob.HasWildcards(segment))
                {
                    if (FileProbe.Stat(Path.Join(folder.Path, segment)) is { } entry)
                    {
                        next.Add(entry);
                    }

                    continue;
                }

                ProbeListing listing = FileProbe.List(folder.Path);
                if (listing.Error == ProbeError.AccessDenied)
                {
                    run.Denied(folder.Path);
                }

                next.AddRange(listing.Entries.Where(e => Glob.IsMatch(segment, e.Name)));
            }

            current = next;
        }

        var matches = new List<ProbeEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ProbeEntry entry in current)
        {
            if (entry.IsReparsePoint || entry.IsCloudPlaceholder)
            {
                skipped.Add(entry.Path);
            }
            else if (seen.Add(entry.Path))
            {
                matches.Add(entry);
            }
        }

        return new PathExpansion(template, root, matches, skipped);
    }
}

/// <summary>When an item last changed: the newest write or creation time of it and everything inside (links not entered).</summary>
public static class ItemAge
{
    /// <summary>The newest change, or null when it could not be told (unreadable, or more than <paramref name="maxEntries"/> entries).</summary>
    public static DateTime? Newest(ProbeEntry item, DetectorRun run, int maxEntries = 20_000)
    {
        ArgumentNullException.ThrowIfNull(run);
        DateTime newest = item.ChangedUtc;
        if (!item.CanEnter)
        {
            return newest;
        }

        var pending = new Stack<string>();
        pending.Push(item.Path);
        int seen = 0;
        while (pending.Count > 0)
        {
            run.ThrowIfCancelled();
            ProbeListing listing = FileProbe.List(pending.Pop());
            if (listing.Error is ProbeError.AccessDenied or ProbeError.Other)
            {
                return null;
            }

            foreach (ProbeEntry entry in listing.Entries)
            {
                if (++seen > maxEntries)
                {
                    return null;
                }

                if (entry.ChangedUtc > newest)
                {
                    newest = entry.ChangedUtc;
                }

                if (entry.CanEnter)
                {
                    pending.Push(entry.Path);
                }
            }
        }

        return newest;
    }
}
