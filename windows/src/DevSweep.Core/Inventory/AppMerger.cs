namespace DevSweep.Core.Inventory;

/// <summary>
/// Groups records that describe the same app: same product code, package
/// family, package id, install location, or the same name once versions and
/// architecture words are ignored.
/// </summary>
internal static class AppMerger
{
    public static IReadOnlyList<InstalledApp> Merge(IReadOnlyList<AppRecord> records)
    {
        int[] parent = Enumerable.Range(0, records.Count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        var owners = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < records.Count; i++)
        {
            foreach (string key in Keys(records[i]))
            {
                if (owners.TryGetValue(key, out int other))
                {
                    parent[Find(i)] = Find(other);
                }
                else
                {
                    owners[key] = i;
                }
            }
        }

        return Enumerable.Range(0, records.Count)
            .GroupBy(Find)
            .Select(group => ToApp(group.Select(i => records[i]).ToList()))
            .OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> Keys(AppRecord record)
    {
        if (record.ProductCode is { } code)
        {
            yield return "pc:" + code;
        }

        if (record.PackageFamilyName is { } family)
        {
            yield return "pfn:" + family;
        }

        if (record.PackageId is { } id)
        {
            yield return "id:" + id;
        }

        if (record.InstallLocation is { Length: > 3 } location && record.Source != InventorySource.ProgramFiles)
        {
            yield return "loc:" + Path.TrimEndingDirectorySeparator(location);
        }

        IReadOnlyList<string> words = AppMatcher.Words(record.Name);
        if (words.Count > 0 && record.Source is not (InventorySource.RunningProcesses or InventorySource.ProgramFiles))
        {
            yield return "name:" + string.Join(' ', words);
        }
    }

    private static InstalledApp ToApp(List<AppRecord> group)
    {
        static IReadOnlyList<string> Distinct(IEnumerable<string?> values)
            => values.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        AppRecord best = group.OrderBy(r => r.Source).First();
        return new InstalledApp
        {
            Name = best.Name,
            Names = Distinct(group.Select(r => r.Name)),
            Publishers = Distinct(group.Select(r => r.Publisher)),
            ProductCodes = Distinct(group.Select(r => r.ProductCode)),
            PackageFamilyNames = Distinct(group.Select(r => r.PackageFamilyName)),
            PackageIds = Distinct(group.Select(r => r.PackageId)),
            InstallLocations = Distinct(group.Select(r => r.InstallLocation)),
            ExePaths = Distinct(group.SelectMany(r => r.ExePaths)),
            Sources = group.Select(r => r.Source).Distinct().Order().ToList(),
        };
    }
}
