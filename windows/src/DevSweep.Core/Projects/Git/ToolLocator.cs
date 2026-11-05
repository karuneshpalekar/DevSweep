namespace DevSweep.Core.Projects.Git;

/// <summary>
/// Finds a command-line tool (git, gh, Windows Terminal) as a full path, so it is never
/// started by a bare name (which would let a <c>git.exe</c> in the current folder run).
/// Order: an override variable (<c>DEVSWEEP_GIT</c>, <c>DEVSWEEP_GH</c>), each absolute
/// <c>PATH</c> entry, then the folders the tool's installers use.
/// </summary>
public static class ToolLocator
{
    /// <summary>The full path of <paramref name="name"/> (no extension), or null when it is not installed.</summary>
    public static string? Find(string name, string? overrideVariable = null, IEnumerable<string>? fallbacks = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (overrideVariable is not null && Environment.GetEnvironmentVariable(overrideVariable) is { Length: > 0 } forced)
        {
            return Path.IsPathFullyQualified(forced) && File.Exists(forced) ? forced : null;
        }

        string file = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string trimmed = folder.Trim('"');
            if (!Path.IsPathFullyQualified(trimmed))
            {
                continue; // "." or a relative entry: never search the current folder.
            }

            string candidate = Path.Join(trimmed, file);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return fallbacks?.FirstOrDefault(File.Exists);
    }

    /// <summary>git.exe: PATH, then Git for Windows' machine and per-user install folders.</summary>
    public static string? Git() => Find("git", "DEVSWEEP_GIT", InstallFolders(@"Git\cmd\git.exe"));

    /// <summary>gh.exe: PATH, then the GitHub CLI's machine and per-user install folders.</summary>
    public static string? GitHubCli() => Find("gh", "DEVSWEEP_GH", InstallFolders(@"GitHub CLI\gh.exe"));

    private static IEnumerable<string> InstallFolders(string relative)
    {
        foreach (Environment.SpecialFolder folder in (Environment.SpecialFolder[])[Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.LocalApplicationData])
        {
            string root = Environment.GetFolderPath(folder);
            if (root.Length > 0)
            {
                yield return folder == Environment.SpecialFolder.LocalApplicationData
                    ? Path.Join(root, "Programs", relative)
                    : Path.Join(root, relative);
            }
        }
    }
}
