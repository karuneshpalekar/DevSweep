namespace DevSweep.Core.Inventory;

/// <summary>Where an installed app was seen (PLAN 4.3).</summary>
public enum InventorySource
{
    /// <summary>HKLM Uninstall, 64-bit view (per-machine MSI and EXE installers).</summary>
    UninstallMachine64,

    /// <summary>HKLM Uninstall, 32-bit view (<c>WOW6432Node</c>).</summary>
    UninstallMachine32,

    /// <summary>HKCU Uninstall, 64-bit view (per-user installers, winget portable packages).</summary>
    UninstallUser64,

    /// <summary>HKCU Uninstall, 32-bit view.</summary>
    UninstallUser32,

    /// <summary>MSIX and Microsoft Store packages registered for this user.</summary>
    StorePackages,

    /// <summary>Scoop apps (per user and global).</summary>
    Scoop,

    /// <summary>Chocolatey packages.</summary>
    Chocolatey,

    /// <summary>winget portable packages.</summary>
    Winget,

    /// <summary>App Paths registrations (HKLM and HKCU) whose program exists.</summary>
    AppPaths,

    /// <summary>Start menu shortcuts (this user's and all users') whose target exists.</summary>
    StartMenu,

    /// <summary>Folders in Program Files, Program Files (x86) and AppData\Local\Programs.</summary>
    ProgramFiles,

    /// <summary>Programs running now.</summary>
    RunningProcesses,
}

/// <summary>One app as one source sees it.</summary>
public sealed record AppRecord
{
    public required InventorySource Source { get; init; }

    /// <summary>Display name (or package, folder or program name where that is all the source has).</summary>
    public required string Name { get; init; }

    public string? Publisher { get; init; }

    public string? Version { get; init; }

    /// <summary>MSI product code, upper case, without braces.</summary>
    public string? ProductCode { get; init; }

    /// <summary>MSIX package family name (<c>Name_PublisherId</c>).</summary>
    public string? PackageFamilyName { get; init; }

    /// <summary>The package manager's id: Scoop app, Chocolatey package or winget package identifier.</summary>
    public string? PackageId { get; init; }

    public string? InstallLocation { get; init; }

    /// <summary>Programs that belong to it (DisplayIcon, App Paths, shortcut targets).</summary>
    public IReadOnlyList<string> ExePaths { get; init; } = [];

    /// <summary>The registry key, folder or file it came from.</summary>
    public string? Origin { get; init; }
}

/// <summary>What one source contributed, and whether it could be read completely.</summary>
/// <param name="Source">The source.</param>
/// <param name="Where">What was read (keys, folders).</param>
/// <param name="Count">Apps it listed.</param>
/// <param name="Milliseconds">How long it took.</param>
/// <param name="Problems">What could not be read; any problem makes the source incomplete.</param>
public sealed record SourceReport(InventorySource Source, string Where, int Count, double Milliseconds, IReadOnlyList<string> Problems)
{
    /// <summary>Everything the source holds was read, so "not listed here" means not installed through it.</summary>
    public bool Complete => Problems.Count == 0;

    /// <summary>"installed programs (HKLM, 64-bit)".</summary>
    public string Title => InventorySources.Title(Source);
}

/// <summary>Names of <see cref="InventorySource"/> values.</summary>
public static class InventorySources
{
    public static string Title(InventorySource source) => source switch
    {
        InventorySource.UninstallMachine64 => "installed programs for all users (64-bit registry)",
        InventorySource.UninstallMachine32 => "installed programs for all users (32-bit registry)",
        InventorySource.UninstallUser64 => "installed programs for this user (64-bit registry)",
        InventorySource.UninstallUser32 => "installed programs for this user (32-bit registry)",
        InventorySource.StorePackages => "Microsoft Store and MSIX packages",
        InventorySource.Scoop => "Scoop apps",
        InventorySource.Chocolatey => "Chocolatey packages",
        InventorySource.Winget => "winget portable packages",
        InventorySource.AppPaths => "registered program paths (App Paths)",
        InventorySource.StartMenu => "Start menu shortcuts",
        InventorySource.ProgramFiles => "Program Files folders",
        InventorySource.RunningProcesses => "running programs",
        _ => source.ToString(),
    };
}
