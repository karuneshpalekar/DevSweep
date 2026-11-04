using DevSweep.Core.Platform;
using Microsoft.Win32;

namespace DevSweep.Core.Safety;

/// <summary>
/// Builds the <see cref="SafetyContext"/> for the signed-in user (PLAN 4.4):
/// every Known Folder wherever it is redirected, the AppData roots, the tool
/// dot-folders, every OneDrive root, the Scoop root, <c>%LOCALAPPDATA%\Microsoft</c>,
/// <c>%LOCALAPPDATA%\Packages</c> and <c>%TEMP%</c> (each protected as a folder; things inside may still be cleaned).
/// </summary>
public static class SafetyContextFactory
{
    /// <summary>Tool folders in the profile whose root is never removed (PLAN 4.4, plus the Mac app's list).</summary>
    public static readonly IReadOnlyList<string> ProtectedDotFolders =
    [
        ".ssh", ".gnupg", ".aws", ".kube", ".docker", ".gradle", ".android", ".nuget",
        ".azure", ".config", ".local", ".cache", ".vscode", ".vscode\\extensions", ".cursor\\extensions",
        ".m2", ".cargo", ".rustup", ".npm", ".claude",
    ];

    public static SafetyContext ForCurrentUser(IEnumerable<string>? devRoots = null, IEnumerable<string>? quarantineRoots = null)
    {
        string profile = KnownFolders.Profile;
        string localAppData = KnownFolders.LocalAppData;
        var paths = new List<ProtectedPath>();

        foreach (KnownFolder folder in KnownFolders.ResolveAll())
        {
            paths.Add(new ProtectedPath(folder.Path, $"your {folder.Name} folder"));
        }

        paths.Add(new ProtectedPath(Path.Combine(profile, "AppData"), "your AppData folder"));
        paths.Add(new ProtectedPath(Path.Combine(localAppData, "Microsoft"), "the folder Windows and Microsoft apps keep their data in"));
        paths.Add(new ProtectedPath(Path.Combine(localAppData, "Packages"), "the folder Store apps keep their data in"));
        paths.Add(new ProtectedPath(Path.Combine(localAppData, "Temp"), "your temporary-files folder"));
        paths.Add(new ProtectedPath(Path.GetTempPath(), "your temporary-files folder"));
        paths.AddRange(ProtectedDotFolders.Select(name => new ProtectedPath(Path.Combine(profile, name), $"the {name} folder")));
        paths.AddRange(OneDriveRoots(profile).Select(root => new ProtectedPath(root, "a OneDrive folder")));
        paths.Add(new ProtectedPath(ScoopRoot(profile), "the Scoop folder"));

        return SafetyContext.Create(profile, paths, devRoots, quarantineRoots, Canonicalise);
    }

    /// <summary>Resolves short names in a canonical path; the path itself stays canonical.</summary>
    internal static string Canonicalise(string canonicalPath)
    {
        string resolved = LongNames.Resolve(canonicalPath);
        return SafePath.TryCanonicalize(resolved, out string? canonical, out _) ? canonical : canonicalPath;
    }

    /// <summary>
    /// OneDrive roots: the shell's OneDrive folder, the OneDrive environment
    /// variables, each signed-in account's UserFolder in the registry, and any
    /// <c>OneDrive*</c> folder in the profile.
    /// </summary>
    private static IEnumerable<string> OneDriveRoots(string profile)
    {
        foreach (string name in (string[])["OneDrive", "OneDriveConsumer", "OneDriveCommercial"])
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                yield return value;
            }
        }

        using (RegistryKey? accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts"))
        {
            foreach (string account in accounts?.GetSubKeyNames() ?? [])
            {
                using RegistryKey? key = accounts!.OpenSubKey(account);
                if (key?.GetValue("UserFolder") is string { Length: > 0 } folder)
                {
                    yield return folder;
                }
            }
        }

        string[] inProfile;
        try
        {
            inProfile = Directory.GetDirectories(profile, "OneDrive*");
        }
        catch (IOException)
        {
            inProfile = [];
        }
        catch (UnauthorizedAccessException)
        {
            inProfile = [];
        }

        foreach (string folder in inProfile)
        {
            yield return folder;
        }
    }

    private static string ScoopRoot(string profile)
        => Environment.GetEnvironmentVariable("SCOOP") is { Length: > 0 } scoop ? scoop : Path.Combine(profile, "scoop");
}
