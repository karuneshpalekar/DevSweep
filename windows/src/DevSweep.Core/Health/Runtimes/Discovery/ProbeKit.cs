using DevSweep.Core.Detectors;
using DevSweep.Core.Health.Common;
using DevSweep.Core.Inventory;

namespace DevSweep.Core.Health.Runtimes;

/// <summary>Who owns a folder, judged from where it is (Scoop, Chocolatey, winget, a CI tool cache).</summary>
public sealed record Owner(InstallSource Source, string? Detail);

/// <summary>Helpers every probe uses: version folders, owners, inventory matches, de-duplication.</summary>
public static class ProbeKit
{
    /// <summary>Sub-folders of <paramref name="root"/> (ordinary folders only) whose names match <paramref name="pattern"/>.</summary>
    public static IReadOnlyList<ProbeEntry> Folders(string? root, string pattern = "*")
        => HealthFiles.Folders(root).Where(e => Locations.Glob.IsMatch(pattern, e.Name)).ToList();

    /// <summary>A path inside <paramref name="folder"/> (or the folder itself).</summary>
    public static bool IsInside(string path, string? folder)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrEmpty(folder))
        {
            return false;
        }

        string f = Path.TrimEndingDirectorySeparator(folder);
        string p = Path.TrimEndingDirectorySeparator(path);
        return p.Equals(f, StringComparison.OrdinalIgnoreCase) || p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Owner from the folder's location: Scoop app, Chocolatey package or tool, winget portable package, CI tool cache.</summary>
    public static Owner? OwnerOf(ProbeContext context, string path)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(path);
        foreach (string root in (string[])["scoop", "scoopGlobal"])
        {
            if (context.Location(root) is { } scoop && IsInside(path, Path.Join(scoop, "apps")))
            {
                return new Owner(InstallSource.Scoop, FirstSegment(path, Path.Join(scoop, "apps")));
            }
        }

        if (context.Location("chocolatey") is { } choco && IsInside(path, choco))
        {
            return new Owner(InstallSource.Chocolatey, FirstSegment(path, Path.Join(choco, "lib")));
        }

        foreach (string root in (string[])["wingetPackages", "wingetMachinePackages"])
        {
            if (context.Location(root) is { } winget && IsInside(path, winget))
            {
                string? folder = FirstSegment(path, winget);
                return new Owner(InstallSource.Winget, folder?.Split('_')[0]);
            }
        }

        if (context.ToolCache is { } cache && IsInside(path, cache))
        {
            return new Owner(InstallSource.ToolCache, FirstSegment(path, cache));
        }

        return null;
    }

    /// <summary>
    /// Refines an install in a vendor's default location with what the inventory knows:
    /// a Chocolatey package of one of <paramref name="chocolateyIds"/> (same version) makes it
    /// Chocolatey's; an Uninstall entry for this folder gives the MSI product code and winget id.
    /// </summary>
    public static Installation Refine(ProbeContext context, Installation install, params string[] chocolateyIds)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(install);
        AppInventory? inventory = context.Inventory;
        if (inventory is null)
        {
            return install;
        }

        AppRecord? uninstall = inventory.Records.FirstOrDefault(r => r.Source.ToString().StartsWith("Uninstall", StringComparison.Ordinal)
            && r.InstallLocation is { Length: > 0 } location && IsInside(install.Path, location) && IsInside(location, install.Path));
        if (uninstall is not null)
        {
            install = install with { ProductCode = uninstall.ProductCode ?? install.ProductCode, WingetId = uninstall.PackageId ?? install.WingetId };
            if (uninstall.PackageId is not null && install.Source is InstallSource.Unknown or InstallSource.NodejsOrg or InstallSource.PythonOrg or InstallSource.GoOrg
                or InstallSource.JdkInstaller or InstallSource.RubyInstaller or InstallSource.PostgresInstaller or InstallSource.DotnetInstaller)
            {
                install = install with { Source = InstallSource.Winget, SourceDetail = uninstall.PackageId };
            }
        }

        AppRecord? choco = inventory.Records.FirstOrDefault(r => r.Source == InventorySource.Chocolatey && r.PackageId is { } id
            && chocolateyIds.Any(c => Locations.Glob.IsMatch(c, id))
            && (r.Version is null || VersionText.Prefix(r.Version, 3) == VersionText.Prefix(install.Version, 3)));
        return choco is null ? install : install with { Source = InstallSource.Chocolatey, SourceDetail = choco.PackageId };
    }

    /// <summary>Drops installs that are the same folder (reached twice, or through a link); the first one found wins.</summary>
    public static List<Installation> Dedupe(IEnumerable<Installation> installs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return installs.Where(i => seen.Add(Path.TrimEndingDirectorySeparator(ShimResolver.FollowLinks(i.Path)))).ToList();
    }

    /// <summary>Marks the install that holds <paramref name="runs"/> (the deepest one) as the default.</summary>
    public static List<Installation> MarkDefault(List<Installation> installs, string? runs)
    {
        ArgumentNullException.ThrowIfNull(installs);
        if (runs is null)
        {
            return installs;
        }

        string real = ShimResolver.FollowLinks(runs);
        int best = -1;
        for (int i = 0; i < installs.Count; i++)
        {
            string folder = ShimResolver.FollowLinks(installs[i].Path);
            bool holds = IsInside(real, folder) || (installs[i].Executable is { } exe && string.Equals(ShimResolver.FollowLinks(exe), real, StringComparison.OrdinalIgnoreCase));
            if (holds && (best < 0 || folder.Length > installs[best].Path.Length))
            {
                best = i;
            }
        }

        if (best >= 0)
        {
            installs[best] = installs[best] with { IsDefault = true };
        }

        return installs;
    }

    private static string? FirstSegment(string path, string root)
    {
        if (!IsInside(path, root))
        {
            return null;
        }

        string rest = Path.GetRelativePath(root, path);
        return rest == "." ? null : rest.Split(Path.DirectorySeparatorChar)[0];
    }
}
