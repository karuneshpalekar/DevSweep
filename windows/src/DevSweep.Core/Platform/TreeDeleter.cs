using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace DevSweep.Core.Platform;

/// <summary>What a permanent delete did.</summary>
/// <param name="Files">Files (and file links) removed.</param>
/// <param name="Directories">Folders (and folder links) removed.</param>
/// <param name="Failures">Paths that could not be removed, and the Win32 error.</param>
public sealed record DeleteReport(long Files, long Directories, IReadOnlyList<WalkProblem> Failures)
{
    public bool Succeeded => Failures.Count == 0;
}

/// <summary>
/// Permanently deletes a folder's contents, deepest first, never following a
/// link: a junction or symbolic link inside is removed as a link and its target
/// is left alone. Folders are listed from their own handle, checked to still be
/// folders, so one swapped for a junction mid-way is unlinked, not entered.
/// Read-only files are deleted (the attribute is not a permission); ownership and
/// ACLs are never changed.
/// </summary>
public static unsafe class TreeDeleter
{
    private const int MaxRecordedFailures = 100;

    /// <summary>The attributes SetFileAttributes accepts.</summary>
    private const Win32FileAttributes KeepableAttributes = Win32FileAttributes.Hidden | Win32FileAttributes.System
        | Win32FileAttributes.Archive | Win32FileAttributes.NotContentIndexed | Win32FileAttributes.Offline
        | Win32FileAttributes.Temporary;

    /// <summary>Deletes everything inside <paramref name="directory"/>, leaving the folder itself.</summary>
    public static DeleteReport DeleteContents(string directory)
    {
        string root = LongPath.ToExtended(directory);
        var failures = new List<WalkProblem>();
        long files = 0;
        long directories = 0;
        var lister = new DirectoryLister(ListingMethod.Auto);
        var pending = new Stack<(string Path, bool Listed)>();
        pending.Push((root, false));

        while (pending.TryPop(out (string Path, bool Listed) item))
        {
            if (item.Listed)
            {
                if (!ReferenceEquals(item.Path, root))
                {
                    Count(RemoveDirectory(item.Path), item.Path, ref directories, failures);
                }

                continue;
            }

            pending.Push((item.Path, true));
            ListingOpen opened = lister.Open(item.Path);
            if (opened.Link is not null)
            {
                continue; // Became a link after it was listed: removed as a link when popped.
            }

            using IDirectoryListing? listing = opened.Listing;
            if (listing is null)
            {
                Fail(item.Path, opened.Error, failures);
                continue;
            }

            while (listing.MoveNext(out DirectoryEntry entry))
            {
                string child = LongPath.Join(item.Path, entry.Name);
                if (entry.IsTraversableDirectory)
                {
                    pending.Push((child, false));
                }
                else if (entry.IsDirectory)
                {
                    Count(RemoveDirectory(child), child, ref directories, failures);
                }
                else
                {
                    Count(DeleteFile(child, entry.Attributes), child, ref files, failures);
                }
            }

            if (listing.Error != 0)
            {
                Fail(item.Path, listing.Error, failures);
            }
        }

        return new DeleteReport(files, directories, failures);
    }

    /// <summary>
    /// Deletes an item for good through its open handle: a folder's contents
    /// first (when <paramref name="isFolder"/>; a folder link is removed as a link), then the item itself.
    /// </summary>
    public static DeleteReport DeleteItem(SafeFileHandle handle, string path, bool isFolder)
    {
        ArgumentNullException.ThrowIfNull(handle);
        DeleteReport contents = isFolder ? DeleteContents(path) : new DeleteReport(0, 0, []);
        if (!contents.Succeeded)
        {
            return contents;
        }

        if (!DeleteByHandle(handle, out int error))
        {
            return contents with { Failures = [new WalkProblem(LongPath.StripPrefix(path), error)] };
        }

        return isFolder
            ? contents with { Directories = contents.Directories + 1 }
            : contents with { Files = contents.Files + 1 };
    }

    /// <summary>
    /// Deletes the object behind <paramref name="handle"/> (opened with DELETE access):
    /// POSIX semantics, read-only ignored. A folder must already be empty.
    /// </summary>
    public static bool DeleteByHandle(SafeFileHandle handle, out int error)
    {
        var info = new FILE_DISPOSITION_INFO_EX
        {
            Flags = FILE_DISPOSITION_INFO_EX_FLAGS.FILE_DISPOSITION_FLAG_DELETE
                | FILE_DISPOSITION_INFO_EX_FLAGS.FILE_DISPOSITION_FLAG_POSIX_SEMANTICS
                | FILE_DISPOSITION_INFO_EX_FLAGS.FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE,
        };
        if (NativeFile.Set(handle, FILE_INFO_BY_HANDLE_CLASS.FileDispositionInfoEx, &info, (uint)sizeof(FILE_DISPOSITION_INFO_EX), out error))
        {
            return true;
        }

        if (!Win32Errors.IsUnsupported(error))
        {
            return false;
        }

        // FAT and exFAT: the classic disposition (the file goes when the last handle closes).
        byte delete = 1;
        return NativeFile.Set(handle, FILE_INFO_BY_HANDLE_CLASS.FileDispositionInfo, &delete, sizeof(byte), out error);
    }

    private static int DeleteFile(string extendedPath, Win32FileAttributes attributes)
    {
        ClearReadOnly(extendedPath, attributes);
        fixed (char* p = extendedPath)
        {
            return PInvoke.DeleteFile(p) ? 0 : Marshal.GetLastPInvokeError();
        }
    }

    private static int RemoveDirectory(string extendedPath)
    {
        // RemoveDirectory on a junction or directory symlink removes the link itself.
        fixed (char* p = extendedPath)
        {
            if (PInvoke.RemoveDirectory(p))
            {
                return 0;
            }
        }

        int error = Marshal.GetLastPInvokeError();
        if (error == Win32Errors.AccessDenied && FindFileEnumerator.TryReadEntry(extendedPath, out DirectoryEntry entry, out _)
            && (entry.Attributes & Win32FileAttributes.ReadOnly) != 0)
        {
            ClearReadOnly(extendedPath, entry.Attributes);
            fixed (char* p = extendedPath)
            {
                return PInvoke.RemoveDirectory(p) ? 0 : Marshal.GetLastPInvokeError();
            }
        }

        return error;
    }

    private static void ClearReadOnly(string extendedPath, Win32FileAttributes attributes)
    {
        if ((attributes & Win32FileAttributes.ReadOnly) == 0)
        {
            return;
        }

        var remaining = (FILE_FLAGS_AND_ATTRIBUTES)(uint)(attributes & ~Win32FileAttributes.ReadOnly & KeepableAttributes);
        fixed (char* p = extendedPath)
        {
            PInvoke.SetFileAttributes(p, remaining == 0 ? FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL : remaining);
        }
    }

    private static void Count(int error, string path, ref long counter, List<WalkProblem> failures)
    {
        if (error == 0)
        {
            counter++;
        }
        else if (!Win32Errors.IsNotFound(error))
        {
            Fail(path, error, failures);
        }
    }

    private static void Fail(string path, int error, List<WalkProblem> failures)
    {
        if (failures.Count < MaxRecordedFailures)
        {
            failures.Add(new WalkProblem(LongPath.StripPrefix(path), error));
        }
    }
}
