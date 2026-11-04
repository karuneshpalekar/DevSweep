namespace DevSweep.Core.Locations;

/// <summary>
/// A machine described by hand, for tests and fixtures: environment variables,
/// Known Folders and the temp folder are given; config files come from
/// <see cref="Files"/> when listed there, otherwise from the real disk (so a
/// fixture folder can stand in for a profile).
/// </summary>
public sealed class FixedEnvironment : ISystemEnvironment
{
    public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Known Folders by root name ("Profile", "LocalAppData"...).</summary>
    public Dictionary<string, string> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Config files held in memory, by full path.</summary>
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When false, only <see cref="Files"/> exist (pure unit tests).</summary>
    public bool ReadRealFiles { get; init; } = true;

    public string TempPath { get; set; } = Path.TrimEndingDirectorySeparator(Path.GetTempPath());

    /// <summary>
    /// A profile under <paramref name="root"/> with the usual Known Folders
    /// (<c>Users\Dev</c>, <c>AppData\Local</c>...), <c>ProgramData</c>, Program Files and Temp.
    /// </summary>
    public static FixedEnvironment UnderRoot(string root, bool readRealFiles = true)
    {
        ArgumentNullException.ThrowIfNull(root);
        string profile = Path.Join(root, "Users", "Dev");
        var environment = new FixedEnvironment { ReadRealFiles = readRealFiles, TempPath = Path.Join(profile, "AppData", "Local", "Temp") };
        environment.Folders["Profile"] = profile;
        environment.Folders["LocalAppData"] = Path.Join(profile, "AppData", "Local");
        environment.Folders["RoamingAppData"] = Path.Join(profile, "AppData", "Roaming");
        environment.Folders["LocalLow"] = Path.Join(profile, "AppData", "LocalLow");
        environment.Folders["Downloads"] = Path.Join(profile, "Downloads");
        environment.Folders["Desktop"] = Path.Join(profile, "Desktop");
        environment.Folders["Documents"] = Path.Join(profile, "Documents");
        environment.Folders["Programs"] = Path.Join(profile, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs");
        environment.Folders["UserProgramFiles"] = Path.Join(profile, "AppData", "Local", "Programs");
        environment.Folders["ProgramData"] = Path.Join(root, "ProgramData");
        environment.Folders["CommonPrograms"] = Path.Join(root, "ProgramData", "Microsoft", "Windows", "Start Menu", "Programs");
        environment.Folders["ProgramFiles"] = Path.Join(root, "Program Files");
        environment.Folders["ProgramFilesX86"] = Path.Join(root, "Program Files (x86)");
        return environment;
    }

    public string? GetVariable(string name) => Variables.TryGetValue(name, out string? value) && value.Length > 0 ? value : null;

    public string? KnownFolder(string rootName) => Folders.TryGetValue(rootName, out string? path) ? path : null;

    public string? ReadAllText(string path)
    {
        if (Files.TryGetValue(path, out string? text))
        {
            return text;
        }

        return ReadRealFiles ? WindowsEnvironment.Instance.ReadAllText(path) : null;
    }

    public bool FileExists(string path) => Files.ContainsKey(path) || (ReadRealFiles && File.Exists(path));

    public bool DirectoryExists(string path) => ReadRealFiles && Directory.Exists(path);
}
