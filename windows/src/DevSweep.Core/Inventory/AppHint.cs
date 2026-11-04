namespace DevSweep.Core.Inventory;

/// <summary>
/// How to recognise one app in the inventory. Any one matching hint in any
/// source means "installed". Rules write these in YAML (<c>knownApps</c>).
/// </summary>
public sealed record AppHint
{
    /// <summary>The app's name, for messages.</summary>
    public required string Name { get; init; }

    /// <summary>Display names; a record matches when its name starts with these words ("Postman" matches "Postman x64 11.2.0").</summary>
    public IReadOnlyList<string> Names { get; init; } = [];

    /// <summary>Publishers, matched the same way (for vendor-wide folders such as <c>%APPDATA%\JetBrains</c>).</summary>
    public IReadOnlyList<string> Publishers { get; init; } = [];

    /// <summary>MSI product codes.</summary>
    public IReadOnlyList<string> ProductCodes { get; init; } = [];

    /// <summary>MSIX package family names, or just the package name before the <c>_</c>.</summary>
    public IReadOnlyList<string> PackageFamilies { get; init; } = [];

    /// <summary>Scoop app names, Chocolatey ids and winget package identifiers.</summary>
    public IReadOnlyList<string> PackageIds { get; init; } = [];

    /// <summary>Program file names (<c>Postman.exe</c>).</summary>
    public IReadOnlyList<string> Exes { get; init; } = [];

    /// <summary>Install folder names (the last part of an install location or a Program Files folder).</summary>
    public IReadOnlyList<string> Folders { get; init; } = [];

    public bool IsEmpty => Names.Count + Publishers.Count + ProductCodes.Count + PackageFamilies.Count
        + PackageIds.Count + Exes.Count + Folders.Count == 0;
}

/// <summary>Whether an app is installed, as far as the inventory can tell.</summary>
public enum InstallState
{
    /// <summary>At least one source lists it.</summary>
    Installed,

    /// <summary>Every source was read completely and none lists it.</summary>
    NotInstalled,

    /// <summary>No source lists it, but a source could not be read completely or was turned off: never treat as gone.</summary>
    Unknown,
}

/// <summary>One source listing the app, and which hint matched.</summary>
public sealed record InstallEvidence(InventorySource Source, string AppName, string MatchedBy, string? Origin);

/// <summary>The answer to <see cref="AppInventory.IsInstalled"/>: the state, every source checked, and what matched.</summary>
/// <param name="State">Installed, not installed, or unknown.</param>
/// <param name="Checked">Every source that was consulted (with whether it was read completely).</param>
/// <param name="Evidence">The records that matched.</param>
/// <param name="UnknownBecause">Why the answer is <see cref="InstallState.Unknown"/>.</param>
public sealed record InstallMatch(InstallState State, IReadOnlyList<SourceReport> Checked, IReadOnlyList<InstallEvidence> Evidence, string? UnknownBecause)
{
    public bool IsInstalled => State == InstallState.Installed;

    public bool IsGone => State == InstallState.NotInstalled;
}
