namespace DevSweep.Core.Locations.Resolvers;

/// <summary>A Known Folder by root name, as the shell resolves it (redirection included).</summary>
public sealed class KnownFolderResolver(string name, string description) : LocationResolver
{
    /// <summary>Every Known Folder root rules may use, with what it is.</summary>
    public static readonly IReadOnlyList<(string Name, string Description)> Roots =
    [
        ("Profile", "your user folder"),
        ("LocalAppData", "AppData\\Local"),
        ("RoamingAppData", "AppData\\Roaming"),
        ("LocalLow", "AppData\\LocalLow"),
        ("ProgramData", "ProgramData"),
        ("Downloads", "Downloads"),
        ("Desktop", "Desktop"),
        ("Documents", "Documents"),
        ("Programs", "your Start menu programs"),
        ("CommonPrograms", "the all-users Start menu programs"),
        ("ProgramFiles", "Program Files"),
        ("ProgramFilesX86", "Program Files (x86)"),
        ("UserProgramFiles", "AppData\\Local\\Programs"),
    ];

    public override string Name { get; } = name;

    public override string Description { get; } = description;

    protected override ResolvedLocation? Resolve(ResolutionTrail trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        if (trail.Environment.KnownFolder(Name) is { Length: > 0 } path)
        {
            return trail.Found(path, LocationSource.KnownFolder, "SHGetKnownFolderPath");
        }

        trail.Looked($"the shell has no {Name} folder");
        return null;
    }
}

/// <summary><c>{Temp}</c>: the temporary-files folder from <c>GetTempPath</c> (TMP, TEMP, then the profile default).</summary>
public sealed class TempResolver : LocationResolver
{
    public override string Name => "Temp";

    public override string Description => "your temporary-files folder";

    protected override ResolvedLocation? Resolve(ResolutionTrail trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        string variable = trail.Environment.GetVariable("TMP") is not null ? "TMP" : "TEMP";
        return trail.Found(trail.Environment.TempPath, LocationSource.Environment, variable);
    }
}
