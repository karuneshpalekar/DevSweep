using System.Xml;
using DevSweep.Core.Detectors;

namespace DevSweep.Core.Inventory.Collectors;

/// <summary>Lists a source folder: missing means "nothing installed this way"; unreadable is a problem.</summary>
internal static class SourceFolder
{
    public static IReadOnlyList<ProbeEntry> Directories(CollectorRun run, string? folder)
    {
        if (folder is null)
        {
            return [];
        }

        run.Looked(folder);
        ProbeListing listing = FileProbe.List(folder);
        switch (listing.Error)
        {
            case ProbeError.None:
                return listing.Entries.Where(e => e.IsDirectory).ToList();
            case ProbeError.NotFound:
                return [];
            default:
                run.Problem($"{folder} could not be listed ({listing.Error})");
                return [];
        }
    }

    public static string? Root(CollectorRun run, string rootName, params string[] parts)
        => run.Context.Locations.Resolve(rootName).Path is { } root ? Path.Join([root, .. parts]) : null;
}

/// <summary>Scoop apps: <c>{scoop}\apps\*</c> and <c>{scoopGlobal}\apps\*</c> (the app's <c>current</c> link is never followed).</summary>
public sealed class ScoopCollector : IInventoryCollector
{
    public InventorySource Source => InventorySource.Scoop;

    public IReadOnlyList<AppRecord> Collect(CollectorRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var records = new List<AppRecord>();
        foreach (string root in (ReadOnlySpan<string>)["scoop", "scoopGlobal"])
        {
            string? apps = SourceFolder.Root(run, root, "apps");
            foreach (ProbeEntry app in SourceFolder.Directories(run, apps))
            {
                records.Add(new AppRecord
                {
                    Source = Source,
                    Name = app.Name,
                    PackageId = app.Name,
                    InstallLocation = Path.Join(app.Path, "current"),
                    Origin = app.Path,
                });
            }
        }

        return records;
    }
}

/// <summary>Chocolatey packages: <c>{chocolatey}\lib\&lt;id&gt;</c>, named from the package's <c>.nuspec</c> title.</summary>
public sealed class ChocolateyCollector : IInventoryCollector
{
    public InventorySource Source => InventorySource.Chocolatey;

    public IReadOnlyList<AppRecord> Collect(CollectorRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var records = new List<AppRecord>();
        foreach (ProbeEntry package in SourceFolder.Directories(run, SourceFolder.Root(run, "chocolatey", "lib")))
        {
            (string? title, string? version, string? authors) = ReadNuspec(Path.Join(package.Path, package.Name + ".nuspec"));
            records.Add(new AppRecord
            {
                Source = Source,
                Name = title ?? package.Name,
                Publisher = authors,
                Version = version,
                PackageId = package.Name,
                InstallLocation = package.Path,
                Origin = package.Path,
            });
        }

        return records;
    }

    private static (string? Title, string? Version, string? Authors) ReadNuspec(string path)
    {
        string? title = null, version = null, authors = null;
        try
        {
            if (!File.Exists(path))
            {
                return (null, null, null);
            }

            using XmlReader reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            reader.MoveToContent();
            while (!reader.EOF)
            {
                // ReadElementContentAsString already moves to the next node: do not Read() again after it.
                bool wanted = reader.NodeType == XmlNodeType.Element && !reader.IsEmptyElement && reader.LocalName is "title" or "version" or "authors";
                if (!wanted)
                {
                    reader.Read();
                    continue;
                }

                string name = reader.LocalName;
                string value = reader.ReadElementContentAsString().Trim();
                title = name == "title" ? value : title;
                version = name == "version" ? value : version;
                authors = name == "authors" ? value : authors;
            }
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            // The folder name is the package id; that is enough to match.
        }

        return (title is { Length: > 0 } ? title : null, version, authors);
    }
}

/// <summary>winget portable packages: <c>{wingetPackages}\&lt;PackageIdentifier&gt;_&lt;Source&gt;</c> (per user and machine).</summary>
public sealed class WingetCollector : IInventoryCollector
{
    public InventorySource Source => InventorySource.Winget;

    public IReadOnlyList<AppRecord> Collect(CollectorRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var records = new List<AppRecord>();
        foreach (string root in (ReadOnlySpan<string>)["wingetPackages", "wingetMachinePackages"])
        {
            foreach (ProbeEntry package in SourceFolder.Directories(run, SourceFolder.Root(run, root)))
            {
                string id = PackageId(package.Name);
                records.Add(new AppRecord
                {
                    Source = Source,
                    Name = id,
                    PackageId = id,
                    InstallLocation = package.Path,
                    Origin = package.Path,
                });
            }
        }

        return records;
    }

    /// <summary><c>Golf.Golf_Microsoft.Winget.Source_8wekyb3d8bbwe</c> to <c>Golf.Golf</c> (the source id has an underscore of its own).</summary>
    public static string PackageId(string folderName)
    {
        ArgumentNullException.ThrowIfNull(folderName);
        int source = folderName.IndexOf("_Microsoft.Winget.Source", StringComparison.OrdinalIgnoreCase);
        int underscore = source > 0 ? source : folderName.LastIndexOf('_');
        return underscore > 0 ? folderName[..underscore] : folderName;
    }
}

/// <summary>
/// Folders in <c>{ProgramFiles}</c>, <c>{ProgramFilesX86}</c> and <c>{UserProgramFiles}</c>,
/// two levels deep (vendor and product). Empty folders do not count.
/// </summary>
public sealed class ProgramFilesCollector : IInventoryCollector
{
    public InventorySource Source => InventorySource.ProgramFiles;

    public IReadOnlyList<AppRecord> Collect(CollectorRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var records = new List<AppRecord>();
        foreach (string root in (ReadOnlySpan<string>)["ProgramFiles", "ProgramFilesX86", "UserProgramFiles"])
        {
            foreach (ProbeEntry vendor in SourceFolder.Directories(run, SourceFolder.Root(run, root)))
            {
                if (vendor.IsReparsePoint)
                {
                    continue;
                }

                ProbeListing inside = FileProbe.List(vendor.Path);
                if (inside.Error == ProbeError.None && inside.Entries.Count == 0)
                {
                    continue;
                }

                records.Add(Folder(vendor));
                foreach (ProbeEntry product in inside.Entries.Where(e => e.CanEnter))
                {
                    records.Add(Folder(product));
                }
            }
        }

        return records;
    }

    private AppRecord Folder(ProbeEntry entry)
        => new() { Source = Source, Name = entry.Name, InstallLocation = entry.Path, Origin = entry.Path };
}

/// <summary>Programs running now (process names), so an app in use is never called gone.</summary>
public sealed class RunningProcessesCollector : IInventoryCollector
{
    public InventorySource Source => InventorySource.RunningProcesses;

    public IReadOnlyList<AppRecord> Collect(CollectorRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        run.Looked("running processes");
        return run.Context.Processes.Names()
            .Select(name => new AppRecord { Source = Source, Name = name, ExePaths = [name + ".exe"], Origin = "process" })
            .ToList();
    }
}
