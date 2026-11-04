using System.IO.Enumeration;

namespace DevSweep.Core.Detectors;

/// <summary>
/// One directory entry as detectors see it: attributes and times from the
/// listing, never from opening the file (so cloud placeholders are not
/// recalled and antivirus does not scan contents).
/// </summary>
public readonly record struct ProbeEntry(string Path, string Name, FileAttributes Attributes, DateTime LastWriteUtc, DateTime CreatedUtc, long Length)
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x0004_0000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x0040_0000;

    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;

    /// <summary>A junction, symlink, mount point, cloud file or other reparse point.</summary>
    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>Cloud-only: reading it would download it.</summary>
    public bool IsCloudPlaceholder => (Attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;

    /// <summary>An ordinary folder a detector may look inside (never a link, never a cloud folder).</summary>
    public bool CanEnter => IsDirectory && !IsReparsePoint && !IsCloudPlaceholder;

    /// <summary>When it last changed or was put here, whichever is later (a copied file keeps its old write time).</summary>
    public DateTime ChangedUtc => LastWriteUtc > CreatedUtc ? LastWriteUtc : CreatedUtc;
}

/// <summary>Why a folder could not be listed.</summary>
public enum ProbeError
{
    None,
    NotFound,
    AccessDenied,
    Other,
}

/// <summary>A folder's entries, or why there are none.</summary>
public sealed record ProbeListing(IReadOnlyList<ProbeEntry> Entries, ProbeError Error);

/// <summary>
/// Metadata-only file system access for detectors (listing and one entry's
/// attributes). Links are reported, never followed.
/// </summary>
public static class FileProbe
{
    private static readonly EnumerationOptions ListOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Simple,
    };

    /// <summary>The entry at <paramref name="path"/> itself (a link is reported as the link), or null when nothing is there.</summary>
    public static ProbeEntry? Stat(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if ((int)info.Attributes == -1)
            {
                return null;
            }

            long length = (info.Attributes & FileAttributes.Directory) != 0 ? 0 : info.Length;
            return new ProbeEntry(info.FullName, info.Name, info.Attributes, info.LastWriteTimeUtc, info.CreationTimeUtc, length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The entries of <paramref name="directory"/>, sorted by name.</summary>
    public static ProbeListing List(string directory)
    {
        try
        {
            var entries = new FileSystemEnumerable<ProbeEntry>(directory, Transform, ListOptions).ToList();
            entries.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            return new ProbeListing(entries, ProbeError.None);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
        {
            return new ProbeListing([], ProbeError.NotFound);
        }
        catch (UnauthorizedAccessException)
        {
            return new ProbeListing([], ProbeError.AccessDenied);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return new ProbeListing([], ProbeError.Other);
        }
    }

    private static ProbeEntry Transform(ref FileSystemEntry entry)
        => new(
            entry.ToFullPath(),
            entry.FileName.ToString(),
            entry.Attributes,
            entry.LastWriteTimeUtc.UtcDateTime,
            entry.CreationTimeUtc.UtcDateTime,
            entry.IsDirectory ? 0 : entry.Length);
}
