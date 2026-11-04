using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace DevSweep.Core.Platform;

/// <summary>
/// Where an open handle really points (<c>GetFinalPathNameByHandleW</c>), compared
/// with where we meant it to point. Comparing volume-GUID forms makes the check
/// exact for drive letters and for volumes mounted in folders alike: any
/// junction, symlink or swapped folder on the way changes the answer.
/// </summary>
public static class FinalPath
{
    /// <summary>The handle's path as <c>\\?\Volume{GUID}\...</c>, or null when it cannot be read.</summary>
    public static string? VolumeGuidForm(SafeFileHandle handle)
        => Get(handle, GETFINALPATHNAMEBYHANDLE_FLAGS.VOLUME_NAME_GUID | GETFINALPATHNAMEBYHANDLE_FLAGS.FILE_NAME_NORMALIZED);

    /// <summary>The handle's path as a drive-letter path (no <c>\\?\</c>), or null.</summary>
    public static string? DosForm(SafeFileHandle handle)
    {
        string? dos = Get(handle, GETFINALPATHNAMEBYHANDLE_FLAGS.VOLUME_NAME_DOS | GETFINALPATHNAMEBYHANDLE_FLAGS.FILE_NAME_NORMALIZED);
        return dos is null ? null : LongPath.StripPrefix(dos);
    }

    /// <summary>
    /// True when <paramref name="handle"/> is the object at <paramref name="expectedPath"/>
    /// (a full, long-name path), reached without any link. <paramref name="actual"/> is what the handle says.
    /// </summary>
    public static bool Matches(SafeFileHandle handle, string expectedPath, out string? actual)
    {
        ArgumentNullException.ThrowIfNull(expectedPath);
        actual = VolumeGuidForm(handle);
        string normal = LongPath.Normalize(expectedPath);
        string root = Volumes.VolumeRoot(normal);
        string? guid = Volumes.VolumeGuidPathOf(root);
        if (actual is null || guid is null || !normal.StartsWith(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            // No volume GUID (a network share): compare drive-letter forms instead.
            actual = DosForm(handle);
            return actual is not null && string.Equals(actual, normal, StringComparison.OrdinalIgnoreCase);
        }

        string relative = normal.Length > root.Length ? normal[root.Length..].TrimStart('\\') : string.Empty;
        string expected = guid + relative;
        return string.Equals(actual.TrimEnd('\\'), expected.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    private static string? Get(SafeFileHandle handle, GETFINALPATHNAMEBYHANDLE_FLAGS flags)
    {
        Span<char> buffer = new char[512];
        uint length = PInvoke.GetFinalPathNameByHandle(handle, buffer, flags);
        if (length == 0)
        {
            return null;
        }

        if (length >= buffer.Length)
        {
            buffer = new char[length + 1];
            length = PInvoke.GetFinalPathNameByHandle(handle, buffer, flags);
            if (length == 0 || length >= buffer.Length)
            {
                return null;
            }
        }

        return buffer[..(int)length].ToString();
    }
}
