using System.Buffers.Binary;
using System.Security.Principal;
using System.Text;

namespace DevSweep.Core.Platform.Recycling;

/// <summary>An item in the current user's Recycle Bin.</summary>
/// <param name="InfoPath">The <c>$I</c> file: original path, size, deletion time.</param>
/// <param name="DataPath">The <c>$R</c> file or folder: the item itself.</param>
/// <param name="OriginalPath">Where it was deleted from.</param>
/// <param name="Size">Size recorded by the shell.</param>
/// <param name="DeletedAt">When it was deleted.</param>
public sealed record RecycledItem(string InfoPath, string DataPath, string OriginalPath, long Size, DateTimeOffset DeletedAt);

/// <summary>
/// Reads the Recycle Bin the way the shell stores it since Vista: per volume and
/// per user, <c>&lt;volume&gt;\$Recycle.Bin\&lt;SID&gt;</c> holds a <c>$Ixxxxxx.ext</c>
/// record (version, size, deletion time, original path) for each <c>$Rxxxxxx.ext</c> item.
/// </summary>
public static class RecycleBinIndex
{
    private const string BinFolder = "$Recycle.Bin";

    /// <summary>The current user's Recycle Bin folder on the volume holding <paramref name="path"/>.</summary>
    public static string BinFolderFor(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new InvalidOperationException("The current user has no SID.");
        return Path.Combine(Volumes.VolumeRoot(path), BinFolder, sid);
    }

    /// <summary>Every item in the current user's bin on the volume holding <paramref name="path"/>.</summary>
    public static IReadOnlyList<RecycledItem> List(string path)
    {
        string folder = BinFolderFor(path);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var items = new List<RecycledItem>();
        foreach (string info in Directory.EnumerateFiles(folder, "$I*"))
        {
            if (TryRead(info) is { } item)
            {
                items.Add(item);
            }
        }

        return items;
    }

    /// <summary>
    /// The most recently deleted bin item that came from <paramref name="originalPath"/>
    /// and was deleted at or after <paramref name="notBefore"/>, or null.
    /// </summary>
    public static RecycledItem? FindNewest(string originalPath, DateTimeOffset notBefore)
    {
        string wanted = LongPath.Normalize(originalPath);
        return List(wanted)
            .Where(i => string.Equals(LongPath.Normalize(i.OriginalPath), wanted, StringComparison.OrdinalIgnoreCase) && i.DeletedAt >= notBefore)
            .OrderByDescending(i => i.DeletedAt)
            .FirstOrDefault();
    }

    /// <summary>Reads one <c>$I</c> file; null when it is not a record we understand.</summary>
    public static RecycledItem? TryRead(string infoPath)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(infoPath);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (!TryParse(data, out string original, out long size, out DateTimeOffset deletedAt))
        {
            return null;
        }

        string name = Path.GetFileName(infoPath);
        string dataPath = Path.Combine(Path.GetDirectoryName(infoPath)!, string.Concat("$R", name.AsSpan(2)));
        return new RecycledItem(infoPath, dataPath, original, size, deletedAt);
    }

    /// <summary>
    /// Parses a <c>$I</c> record. Version 1 (Vista to 8.1): 8-byte version, 8-byte size,
    /// FILETIME, then the path in 260 UTF-16 characters. Version 2 (Windows 10 and later):
    /// the same three fields, a 4-byte character count, then the path.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out string originalPath, out long size, out DateTimeOffset deletedAt)
    {
        originalPath = string.Empty;
        size = 0;
        deletedAt = default;
        if (data.Length < 24)
        {
            return false;
        }

        long version = BinaryPrimitives.ReadInt64LittleEndian(data);
        size = BinaryPrimitives.ReadInt64LittleEndian(data[8..]);
        long fileTime = BinaryPrimitives.ReadInt64LittleEndian(data[16..]);
        ReadOnlySpan<byte> path;
        if (version == 2 && data.Length >= 28)
        {
            int chars = BinaryPrimitives.ReadInt32LittleEndian(data[24..]);
            if (chars <= 0 || 28 + ((long)chars * 2) > data.Length)
            {
                return false;
            }

            path = data.Slice(28, chars * 2);
        }
        else if (version == 1 && data.Length >= 24 + 2)
        {
            path = data[24..Math.Min(data.Length, 24 + (260 * 2))];
        }
        else
        {
            return false;
        }

        originalPath = Encoding.Unicode.GetString(path).TrimEnd('\0');
        int end = originalPath.IndexOf('\0', StringComparison.Ordinal);
        if (end >= 0)
        {
            originalPath = originalPath[..end];
        }

        if (fileTime < 0)
        {
            return false;
        }

        deletedAt = DateTimeOffset.FromFileTime(fileTime).ToUniversalTime();
        return originalPath.Length > 0;
    }
}
