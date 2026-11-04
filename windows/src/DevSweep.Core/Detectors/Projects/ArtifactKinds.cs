namespace DevSweep.Core.Detectors.Projects;

/// <summary>
/// A kind of rebuildable project folder: which folders, which marker file makes
/// the parent a project of that kind, and how each folder is confirmed to be
/// what it looks like (so a <c>bin</c> of scripts or a <c>target</c> of
/// something else is never offered).
/// </summary>
/// <param name="Ecosystem">"Node.js", ".NET"...</param>
/// <param name="Folders">Folder names (lower case not required; matched case-insensitively).</param>
/// <param name="Markers">File names or wildcards that must be in the project folder.</param>
/// <param name="Confirm">Checks one artifact folder's contents.</param>
/// <param name="Reinstall">How to get it back, given the project folder's file names.</param>
public sealed record ArtifactKind(
    string Ecosystem,
    IReadOnlyList<string> Folders,
    IReadOnlyList<string> Markers,
    Func<string, bool> Confirm,
    Func<IReadOnlyCollection<string>, string> Reinstall);

/// <summary>The artifact kinds <c>staleProjectArtifacts</c> knows (the Mac table, plus .NET, Rust and Maven).</summary>
public static class ArtifactKinds
{
    public static readonly IReadOnlyList<ArtifactKind> All =
    [
        new("Node.js", ["node_modules"], ["package.json"], _ => true, NodeReinstall),
        new("Python", [".venv", "venv"], ["pyproject.toml", "requirements.txt", "setup.py", "setup.cfg", "Pipfile"],
            folder => File.Exists(Path.Join(folder, "pyvenv.cfg")), _ => "recreate the virtual environment"),
        new(".NET", ["bin", "obj"], ["*.csproj", "*.fsproj", "*.vbproj"], DotnetOutput, _ => "dotnet build"),
        new("Rust", ["target"], ["Cargo.toml"],
            folder => File.Exists(Path.Join(folder, "CACHEDIR.TAG")) || HasChild(folder, "debug", "release"), _ => "cargo build"),
        new("Maven", ["target"], ["pom.xml"],
            folder => HasChild(folder, "classes", "maven-status", "maven-archiver", "surefire-reports"), _ => "mvn package"),
        new("Gradle", [".gradle"], ["settings.gradle", "settings.gradle.kts", "build.gradle", "build.gradle.kts"], _ => true, _ => "the next Gradle build"),
    ];

    /// <summary>Every artifact folder name (never walked into when looking for projects or measuring activity).</summary>
    public static readonly IReadOnlySet<string> FolderNames =
        All.SelectMany(k => k.Folders).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool DotnetOutput(string folder)
        => string.Equals(Path.GetFileName(folder), "obj", StringComparison.OrdinalIgnoreCase)
            ? File.Exists(Path.Join(folder, "project.assets.json")) || HasChild(folder, "Debug", "Release")
            : HasChild(folder, "Debug", "Release");

    private static bool HasChild(string folder, params string[] names)
        => names.Any(name => Directory.Exists(Path.Join(folder, name)));

    private static string NodeReinstall(IReadOnlyCollection<string> files)
        => files.Contains("pnpm-lock.yaml") ? "pnpm install"
            : files.Contains("yarn.lock") ? "yarn install"
            : files.Contains("bun.lockb") || files.Contains("bun.lock") ? "bun install"
            : "npm install";
}
