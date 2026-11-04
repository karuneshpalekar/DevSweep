using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace DevSweep.Core.Platform;

/// <summary>A known folder as the shell resolves it for the current user, redirection included.</summary>
/// <param name="Name">Short name, for example "Documents".</param>
/// <param name="Path">Where it is now (OneDrive, another drive...).</param>
public sealed record KnownFolder(string Name, string Path);

/// <summary>Resolves Known Folders with <c>SHGetKnownFolderPath</c> (never from hard-coded paths or environment variables).</summary>
public static unsafe class KnownFolders
{
    private static readonly (string Name, Guid Id)[] Ids =
    [
        ("Profile", PInvoke.FOLDERID_Profile),
        ("Desktop", PInvoke.FOLDERID_Desktop),
        ("Documents", PInvoke.FOLDERID_Documents),
        ("Downloads", PInvoke.FOLDERID_Downloads),
        ("Pictures", PInvoke.FOLDERID_Pictures),
        ("Music", PInvoke.FOLDERID_Music),
        ("Videos", PInvoke.FOLDERID_Videos),
        ("Favorites", PInvoke.FOLDERID_Favorites),
        ("Contacts", PInvoke.FOLDERID_Contacts),
        ("Links", PInvoke.FOLDERID_Links),
        ("Saved Games", PInvoke.FOLDERID_SavedGames),
        ("Searches", PInvoke.FOLDERID_SavedSearches),
        ("3D Objects", PInvoke.FOLDERID_Objects3D),
        ("AppData\\Roaming", PInvoke.FOLDERID_RoamingAppData),
        ("AppData\\Local", PInvoke.FOLDERID_LocalAppData),
        ("AppData\\LocalLow", PInvoke.FOLDERID_LocalAppDataLow),
        ("OneDrive", PInvoke.FOLDERID_SkyDrive),
        ("Camera Roll", PInvoke.FOLDERID_CameraRoll),
        ("Screenshots", PInvoke.FOLDERID_Screenshots),
        ("Start Menu", PInvoke.FOLDERID_StartMenu),
        ("Programs", PInvoke.FOLDERID_Programs),
        ("Startup", PInvoke.FOLDERID_Startup),
        ("Templates", PInvoke.FOLDERID_Templates),
        ("SendTo", PInvoke.FOLDERID_SendTo),
        ("Recent", PInvoke.FOLDERID_Recent),
        ("Programs (per user)", PInvoke.FOLDERID_UserProgramFiles),
    ];

    /// <summary>Every known folder DevSweep protects that exists for this user (missing ones are left out).</summary>
    public static IReadOnlyList<KnownFolder> ResolveAll()
    {
        var folders = new List<KnownFolder>(Ids.Length);
        foreach ((string name, Guid id) in Ids)
        {
            if (TryGet(id, out string? path))
            {
                folders.Add(new KnownFolder(name, path));
            }
        }

        return folders;
    }

    /// <summary>The user profile folder.</summary>
    /// <exception cref="InvalidOperationException">The shell cannot resolve it.</exception>
    public static string Profile => Get(PInvoke.FOLDERID_Profile, "Profile");

    /// <summary><c>%LOCALAPPDATA%</c>, as the shell resolves it.</summary>
    public static string LocalAppData => Get(PInvoke.FOLDERID_LocalAppData, "LocalAppData");

    /// <summary>The Desktop folder, wherever it is redirected.</summary>
    public static string Desktop => Get(PInvoke.FOLDERID_Desktop, "Desktop");

    /// <summary>
    /// Folders rules and resolvers name as roots (<c>{LocalAppData}</c>...), by
    /// root name. Each is resolved by the shell, redirection included.
    /// </summary>
    private static readonly Dictionary<string, Guid> Roots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Profile"] = PInvoke.FOLDERID_Profile,
        ["LocalAppData"] = PInvoke.FOLDERID_LocalAppData,
        ["RoamingAppData"] = PInvoke.FOLDERID_RoamingAppData,
        ["LocalLow"] = PInvoke.FOLDERID_LocalAppDataLow,
        ["ProgramData"] = PInvoke.FOLDERID_ProgramData,
        ["Downloads"] = PInvoke.FOLDERID_Downloads,
        ["Desktop"] = PInvoke.FOLDERID_Desktop,
        ["Documents"] = PInvoke.FOLDERID_Documents,
        ["Programs"] = PInvoke.FOLDERID_Programs,
        ["CommonPrograms"] = PInvoke.FOLDERID_CommonPrograms,
        ["ProgramFiles"] = PInvoke.FOLDERID_ProgramFiles,
        ["ProgramFilesX86"] = PInvoke.FOLDERID_ProgramFilesX86,
        ["UserProgramFiles"] = PInvoke.FOLDERID_UserProgramFiles,
    };

    /// <summary>The names <see cref="TryGetRoot"/> knows.</summary>
    public static IReadOnlyCollection<string> RootNames => Roots.Keys;

    /// <summary>Resolves a root by name ("LocalAppData", "Downloads"...); null when unknown or not resolvable.</summary>
    public static string? TryGetRoot(string name)
        => Roots.TryGetValue(name, out Guid id) && TryGet(id, out string? path) ? path : null;

    private static string Get(Guid id, string name)
        => TryGet(id, out string? path) ? path : throw new InvalidOperationException($"The shell cannot resolve the {name} folder.");

    private static bool TryGet(Guid id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? path)
    {
        PWSTR result;
        HRESULT hr = PInvoke.SHGetKnownFolderPath(&id, KNOWN_FOLDER_FLAG.KF_FLAG_DONT_VERIFY, HANDLE.Null, &result);
        try
        {
            path = hr.Succeeded && result.Value is not null ? result.ToString() : null;
            return path is { Length: > 0 };
        }
        finally
        {
            PInvoke.CoTaskMemFree(result.Value);
        }
    }
}
