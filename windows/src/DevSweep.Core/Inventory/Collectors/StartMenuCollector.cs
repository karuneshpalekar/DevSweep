using DevSweep.Core.Detectors;
using Windows.Win32;

namespace DevSweep.Core.Inventory.Collectors;

/// <summary>
/// Start menu shortcuts (<c>{Programs}</c> and <c>{CommonPrograms}</c>, four levels).
/// A shortcut whose target no longer exists is stale and does not count; an MSI
/// advertised shortcut names its product code (<c>MsiGetShortcutTargetW</c>); one
/// whose target cannot be read still counts by name (the safe side).
/// </summary>
public sealed class StartMenuCollector : IInventoryCollector
{
    private const int MaxDepth = 4;

    public InventorySource Source => InventorySource.StartMenu;

    public IReadOnlyList<AppRecord> Collect(CollectorRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var records = new List<AppRecord>();
        foreach (string root in (ReadOnlySpan<string>)["Programs", "CommonPrograms"])
        {
            if (SourceFolder.Root(run, root) is { } folder)
            {
                run.Looked(folder);
                Visit(run, folder, 0, records);
            }
        }

        return records;
    }

    private void Visit(CollectorRun run, string folder, int depth, List<AppRecord> records)
    {
        run.CancellationToken.ThrowIfCancellationRequested();
        ProbeListing listing = FileProbe.List(folder);
        if (listing.Error is ProbeError.AccessDenied or ProbeError.Other)
        {
            run.Problem($"{folder} could not be listed ({listing.Error})");
            return;
        }

        foreach (ProbeEntry entry in listing.Entries)
        {
            if (entry.CanEnter && depth < MaxDepth)
            {
                Visit(run, entry.Path, depth + 1, records);
            }
            else if (!entry.IsDirectory && entry.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && Read(entry) is { } record)
            {
                records.Add(record);
            }
        }
    }

    private AppRecord? Read(ProbeEntry shortcut)
    {
        string name = Path.GetFileNameWithoutExtension(shortcut.Name);
        ShellLinkInfo? link = ShellLink.Read(shortcut.Path);
        if (link is { IsAdvertised: true } && MsiShortcuts.ProductCode(shortcut.Path) is { } productCode)
        {
            return new AppRecord { Source = Source, Name = name, ProductCode = productCode, Origin = shortcut.Path };
        }

        if (link?.Target is not { } target)
        {
            return new AppRecord { Source = Source, Name = name, Origin = shortcut.Path };
        }

        if (!File.Exists(target) && !Directory.Exists(target))
        {
            return null; // Stale: the program it opened is gone.
        }

        return new AppRecord
        {
            Source = Source,
            Name = name,
            ExePaths = [target],
            InstallLocation = Path.GetDirectoryName(target),
            Origin = shortcut.Path,
        };
    }
}

/// <summary>The product code behind an MSI advertised shortcut.</summary>
internal static unsafe class MsiShortcuts
{
    private const int GuidChars = 39;

    public static string? ProductCode(string shortcut)
    {
        char* product = stackalloc char[GuidChars];
        char* feature = stackalloc char[GuidChars + 1];
        char* component = stackalloc char[GuidChars];
        uint result;
        fixed (char* path = shortcut)
        {
            result = PInvoke.MsiGetShortcutTarget(path, product, feature, component);
        }

        return result == 0 ? ProductCodes.Normalize(new string(product)) : null;
    }
}
