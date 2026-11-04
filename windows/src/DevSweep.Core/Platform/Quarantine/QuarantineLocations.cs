using System.Security.AccessControl;
using System.Security.Principal;

namespace DevSweep.Core.Platform.Quarantine;

/// <summary>Where the quarantine lives on each volume.</summary>
public interface IQuarantineLocations
{
    /// <summary>
    /// The quarantine folder on the same volume as <paramref name="itemPath"/>,
    /// created if needed; null (with <paramref name="reason"/>) when that volume has none.
    /// </summary>
    string? RootFor(string itemPath, out string reason);

    /// <summary>Every quarantine folder that exists now.</summary>
    IReadOnlyList<string> ExistingRoots();
}

/// <summary>
/// The quarantine for the signed-in user:
/// <list type="bullet">
/// <item>on the volume holding the profile: <c>%LOCALAPPDATA%\DevSweep\Quarantine</c>
/// (inside the profile, so it inherits the profile's user-only permissions);</item>
/// <item>on any other fixed volume: <c>&lt;volume root&gt;\DevSweep Quarantine\&lt;user SID&gt;</c>,
/// hidden and system, laid out like the Recycle Bin's own <c>$Recycle.Bin\&lt;SID&gt;</c>: at the
/// root because an item can only be renamed within its own volume and the root
/// is the one folder every volume has; one folder per user, whose permissions
/// (on NTFS and ReFS) admit only that user, SYSTEM and Administrators;</item>
/// <item>removable, network and optical drives: none (the item would have to be copied, which DevSweep never does).</item>
/// </list>
/// </summary>
public sealed class DefaultQuarantineLocations : IQuarantineLocations
{
    public const string OtherVolumeFolder = "DevSweep Quarantine";

    /// <summary><c>%LOCALAPPDATA%\DevSweep\Quarantine</c>.</summary>
    public static string ProfileRoot => Path.Combine(KnownFolders.LocalAppData, "DevSweep", "Quarantine");

    public string? RootFor(string itemPath, out string reason)
    {
        VolumeInfo volume = Volumes.ForPath(itemPath);
        if (volume.Kind != DriveKind.Fixed)
        {
            reason = $"{volume.Kind} drives have no DevSweep quarantine: the item would have to be copied off the drive.";
            return null;
        }

        if (Volumes.ForPath(KnownFolders.LocalAppData).SerialNumber == volume.SerialNumber)
        {
            reason = string.Empty;
            return Ensure(ProfileRoot, restrict: false, hide: false);
        }

        string shared = Path.Combine(volume.RootPath, OtherVolumeFolder);
        Ensure(shared, restrict: false, hide: true);
        bool acls = volume.FileSystem is "NTFS" or "ReFS";
        reason = string.Empty;
        return Ensure(Path.Combine(shared, CurrentUserSid), restrict: acls, hide: false);
    }

    public IReadOnlyList<string> ExistingRoots()
    {
        var roots = new List<string>();
        if (Directory.Exists(ProfileRoot))
        {
            roots.Add(ProfileRoot);
        }

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType == DriveType.Fixed)
            {
                string root = Path.Combine(drive.RootDirectory.FullName, OtherVolumeFolder, CurrentUserSid);
                if (Directory.Exists(root))
                {
                    roots.Add(root);
                }
            }
        }

        return roots;
    }

    private static string CurrentUserSid
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? throw new InvalidOperationException("The current user has no SID.");
        }
    }

    private static string Ensure(string folder, bool restrict, bool hide)
    {
        if (!Directory.Exists(folder))
        {
            if (restrict)
            {
                new DirectoryInfo(folder).Create(UserOnly());
            }
            else
            {
                Directory.CreateDirectory(folder);
            }

            if (hide)
            {
                File.SetAttributes(folder, FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System);
            }
        }

        return folder;
    }

    private static DirectorySecurity UserOnly()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        using var identity = WindowsIdentity.GetCurrent();
        foreach (IdentityReference who in new IdentityReference[]
        {
            identity.User!,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        })
        {
            security.AddAccessRule(new FileSystemAccessRule(who, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        return security;
    }
}

/// <summary>Quarantine folders chosen by the caller (tests, or a portable setup): one per volume at most.</summary>
public sealed class FixedQuarantineLocations(params string[] roots) : IQuarantineLocations
{
    public string? RootFor(string itemPath, out string reason)
    {
        uint serial = Volumes.ForPath(itemPath).SerialNumber;
        foreach (string root in roots)
        {
            Directory.CreateDirectory(root);
            if (Volumes.ForPath(root).SerialNumber == serial)
            {
                reason = string.Empty;
                return root;
            }
        }

        reason = "No quarantine folder is set up on this item's volume.";
        return null;
    }

    public IReadOnlyList<string> ExistingRoots() => [.. roots.Where(Directory.Exists)];
}
