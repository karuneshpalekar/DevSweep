using System.Globalization;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace DevSweep.Core.Platform.Recycling;

/// <summary>What restoring a Recycle Bin item did.</summary>
public sealed record RecycleRestoreResult(bool Restored, string Path, string Message);

/// <summary>
/// Puts a Recycle Bin item back where it was, as Explorer's "Restore" does:
/// renames the <c>$R</c> item to its original path (same volume, never a copy:
/// MoveFileExW without MOVEFILE_COPY_ALLOWED), recreating missing parent
/// folders, then removes its <c>$I</c> record. Refuses when something now exists
/// at the original path or the way there goes through a link.
/// </summary>
public static class RecycleBinRestore
{
    public static RecycleRestoreResult Restore(RecycledItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        string original = LongPath.Normalize(item.OriginalPath);
        if (!Exists(item.DataPath))
        {
            return new RecycleRestoreResult(false, original, "The item is no longer in the Recycle Bin (it may have been emptied).");
        }

        if (Exists(original))
        {
            return new RecycleRestoreResult(false, original, $"Something now exists at {original}, so the item was left in the Recycle Bin.");
        }

        string? parent = Path.GetDirectoryName(original);
        if (parent is not null)
        {
            Directory.CreateDirectory(LongPath.ToExtended(parent));
        }

        if (ReparseGuard.FindReparseAncestor(original) is { } link)
        {
            return new RecycleRestoreResult(false, original, $"The way to {original} now goes through a junction or link ({link}), so the item was left in the Recycle Bin.");
        }

        if (!PInvoke.MoveFileEx(LongPath.ToExtended(item.DataPath), LongPath.ToExtended(original), MOVE_FILE_FLAGS.MOVEFILE_WRITE_THROUGH))
        {
            int error = Marshal.GetLastPInvokeError();
            return new RecycleRestoreResult(false, original, string.Create(CultureInfo.InvariantCulture,
                $"The item could not be moved back from the Recycle Bin (Win32 error {error})."));
        }

        File.Delete(item.InfoPath);
        return new RecycleRestoreResult(true, original, $"Restored from the Recycle Bin to {original}.");
    }

    private static bool Exists(string path)
        => FindFileEnumerator.TryReadEntry(LongPath.ToExtended(path), out _, out int error) || !Win32Errors.IsNotFound(error);
}
