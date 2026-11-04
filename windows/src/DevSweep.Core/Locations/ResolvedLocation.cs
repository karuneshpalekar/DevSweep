namespace DevSweep.Core.Locations;

/// <summary>Where a resolver found its answer.</summary>
public enum LocationSource
{
    /// <summary>The shell's Known Folder (redirection included).</summary>
    KnownFolder,

    /// <summary>An environment variable.</summary>
    Environment,

    /// <summary>The tool's own configuration file.</summary>
    ConfigFile,

    /// <summary>Nothing was set, so the tool's default on Windows.</summary>
    Default,

    /// <summary>The tool's own command (run with a time limit).</summary>
    Command,

    /// <summary>Could not be resolved.</summary>
    NotFound,
}

/// <summary>A named location and how it was worked out (this goes into a finding's "What was checked").</summary>
/// <param name="Name">The root name, for example <c>npmCache</c>.</param>
/// <param name="Description">What it is, for example "npm cache".</param>
/// <param name="Path">The folder, or null when it could not be resolved.</param>
/// <param name="Source">Where the answer came from.</param>
/// <param name="Detail">The variable, file or command it came from, and what else was looked at.</param>
public sealed record ResolvedLocation(string Name, string Description, string? Path, LocationSource Source, string Detail)
{
    /// <summary>"npm cache: C:\Users\me\AppData\Local\npm-cache (default; NPM_CONFIG_CACHE not set, no cache= in C:\Users\me\.npmrc)".</summary>
    public string Describe() => Path is null
        ? $"{Description}: not found ({Detail})"
        : $"{Description}: {Path} ({SourceText})";

    private string SourceText => Source switch
    {
        LocationSource.KnownFolder => "Windows Known Folder",
        LocationSource.Environment => $"from the {Detail} environment variable",
        LocationSource.ConfigFile => $"from {Detail}",
        LocationSource.Command => $"from {Detail}",
        _ => Detail,
    };
}
