using System.Text;
using DevSweep.Core.Detectors;
using DevSweep.Core.Platform;
using Microsoft.Win32.SafeHandles;
using Windows.Win32.Storage.FileSystem;

namespace DevSweep.Core.Health.Common;

/// <summary>
/// File access for Health: metadata from directory listings, and at most the
/// first few kilobytes of small files. Links are never followed, cloud
/// placeholders are never opened (and every open uses <c>FILE_FLAG_OPEN_NO_RECALL</c>,
/// so a file that turns into a placeholder after it was listed is still not downloaded).
/// </summary>
public static class HealthFiles
{
    private const uint GenericRead = 0x0012_0089;

    /// <summary>The entries of a folder (empty when missing or unreadable).</summary>
    public static IReadOnlyList<ProbeEntry> List(string? directory)
        => directory is null ? [] : FileProbe.List(directory).Entries;

    /// <summary>Sub-folders that may be entered (no links, no cloud-only folders).</summary>
    public static IReadOnlyList<ProbeEntry> Folders(string? directory)
        => List(directory).Where(e => e.CanEnter).ToList();

    /// <summary>An ordinary folder (not a link, not cloud-only).</summary>
    public static bool IsFolder(string? path) => path is not null && FileProbe.Stat(path) is { CanEnter: true };

    /// <summary>A local file (any kind of link counts as not a plain file).</summary>
    public static bool IsFile(string? path)
        => path is not null && FileProbe.Stat(path) is { IsDirectory: false, IsReparsePoint: false, IsCloudPlaceholder: false };

    /// <summary>Something is at <paramref name="path"/> (links included, never followed).</summary>
    public static bool Exists(string? path) => path is not null && FileProbe.Stat(path) is not null;

    /// <summary>
    /// The first <paramref name="maxBytes"/> bytes of a plain local file, or null
    /// (missing, a folder, a link, a cloud placeholder, or unreadable).
    /// </summary>
    public static byte[]? ReadHead(string path, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!IsFile(path))
        {
            return null;
        }

        try
        {
            using FileStream? stream = OpenRead(path);
            if (stream is null)
            {
                return null;
            }

            byte[] buffer = new byte[maxBytes];
            int total = 0;
            int read;
            while (total < maxBytes && (read = stream.Read(buffer, total, maxBytes - total)) > 0)
            {
                total += read;
            }

            return buffer[..total];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>A small text file (BOM-aware, UTF-8 otherwise), cut at <paramref name="maxBytes"/>; null when it cannot be read safely.</summary>
    public static string? ReadText(string path, int maxBytes = 256 * 1024)
    {
        byte[]? bytes = ReadHead(path, maxBytes);
        return bytes is null ? null : Decode(bytes);
    }

    /// <summary>Text from bytes: UTF-16 or UTF-8 by BOM, UTF-8 otherwise.</summary>
    public static string Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip);
    }

    /// <summary>The first non-empty line, trimmed.</summary>
    public static string FirstLine(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return string.Empty;
    }

    private static FileStream? OpenRead(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }

        SafeFileHandle? handle = NativeFile.Open(
            LongPath.ToExtended(Path.GetFullPath(path)),
            GenericRead,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_NO_RECALL | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_SEQUENTIAL_SCAN,
            out _);
        return handle is null ? null : new FileStream(handle, FileAccess.Read);
    }
}
