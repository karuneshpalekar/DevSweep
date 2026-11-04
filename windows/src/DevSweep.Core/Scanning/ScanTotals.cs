using System.Globalization;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Platform;

namespace DevSweep.Core.Scanning;

/// <summary>The scan total, with nothing counted twice.</summary>
/// <param name="TotalBytes">Sum of finding sizes less the two overlaps.</param>
/// <param name="SharedBetweenFindings">Hard-linked bytes in more than one finding (each extra appearance).</param>
/// <param name="Nested">Bytes of finding paths that lie inside another finding's path.</param>
internal sealed record ScanTotal(long TotalBytes, long SharedBetweenFindings, long Nested);

/// <summary>
/// Cross-finding accounting. A file hard-linked into two findings (a pnpm store
/// and a project's node_modules, say) is counted once in the total, and each
/// finding says how much it shares. A finding inside another finding's folder
/// is counted once too.
/// </summary>
internal static class ScanTotals
{
    public static (List<Finding> Findings, ScanTotal Total) Apply(IReadOnlyList<Finding> findings, IReadOnlyList<IReadOnlyList<LinkedFile>> linked)
    {
        var owners = new Dictionary<FileIdentity, List<int>>();
        var bytesOf = new Dictionary<FileIdentity, long>();
        for (int i = 0; i < findings.Count; i++)
        {
            foreach (LinkedFile file in linked[i])
            {
                if (!owners.TryGetValue(file.Identity, out List<int>? list))
                {
                    list = [];
                    owners[file.Identity] = list;
                }

                list.Add(i);
                bytesOf[file.Identity] = Math.Max(bytesOf.GetValueOrDefault(file.Identity), file.Bytes);
            }
        }

        long duplicate = 0;
        long[] sharedWithOthers = new long[findings.Count];
        foreach ((FileIdentity identity, List<int> list) in owners)
        {
            if (list.Count < 2)
            {
                continue;
            }

            long bytes = bytesOf[identity];
            duplicate += (list.Count - 1) * bytes;
            foreach (int i in list)
            {
                sharedWithOthers[i] += bytes;
            }
        }

        var result = new List<Finding>(findings.Count);
        long nested = 0;
        for (int i = 0; i < findings.Count; i++)
        {
            Finding finding = findings[i];
            var checks = new List<Check>(finding.Checks);
            if (sharedWithOthers[i] > 0)
            {
                checks.Add(Check.Warning(string.Create(CultureInfo.InvariantCulture,
                    $"{SizeText.Format(sharedWithOthers[i])} of this is hard-linked with other items in this scan; the scan total counts it once.")));
            }

            foreach ((string inside, long bytes, string other) in Inside(finding, findings, i))
            {
                nested += bytes;
                checks.Add(Check.Warning($"Includes {inside} ({SizeText.Format(bytes)}), also listed as \"{other}\"; the scan total counts it once."));
            }

            result.Add(finding with { SharedWithOtherFindingsBytes = sharedWithOthers[i], Checks = checks });
        }

        long total = findings.Sum(f => f.Size) - duplicate - nested;
        return (result, new ScanTotal(Math.Max(0, total), duplicate, nested));
    }

    /// <summary>Paths of other findings that lie inside one of <paramref name="finding"/>'s paths.</summary>
    private static IEnumerable<(string Path, long Bytes, string Title)> Inside(Finding finding, IReadOnlyList<Finding> all, int self)
    {
        for (int j = 0; j < all.Count; j++)
        {
            if (j == self)
            {
                continue;
            }

            Finding other = all[j];
            for (int k = 0; k < other.Paths.Count; k++)
            {
                if (finding.Paths.Any(folder => PathClaims.IsInside(other.Paths[k], folder)))
                {
                    yield return (other.Paths[k], k < other.PathSizes.Count ? other.PathSizes[k] : 0, other.Title);
                }
            }
        }
    }
}
