using DevSweep.Core.Platform;

namespace DevSweep.Core.Locations;

/// <summary>
/// What location resolvers may read about the machine: environment variables,
/// Known Folders and small config files. Tests pass a fake; the engine uses
/// <see cref="WindowsEnvironment"/>.
/// </summary>
public interface ISystemEnvironment
{
    /// <summary>An environment variable (case-insensitive, as on Windows); null when unset or empty.</summary>
    string? GetVariable(string name);

    /// <summary>A Known Folder by root name ("Profile", "LocalAppData"...), or null.</summary>
    string? KnownFolder(string rootName);

    /// <summary>The temporary-files folder (<c>GetTempPath</c>), without a trailing separator.</summary>
    string TempPath { get; }

    /// <summary>A small text file's contents; null when it is missing, unreadable or larger than 1 MiB.</summary>
    string? ReadAllText(string path);

    bool FileExists(string path);

    bool DirectoryExists(string path);
}

/// <summary>The real machine: process environment, <c>SHGetKnownFolderPath</c>, the file system.</summary>
public sealed class WindowsEnvironment : ISystemEnvironment
{
    private const long MaxConfigBytes = 1024 * 1024;

    public static WindowsEnvironment Instance { get; } = new();

    public string TempPath => Path.TrimEndingDirectorySeparator(Path.GetTempPath());

    public string? GetVariable(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    public string? KnownFolder(string rootName) => KnownFolders.TryGetRoot(rootName);

    public string? ReadAllText(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaxConfigBytes ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);
}
