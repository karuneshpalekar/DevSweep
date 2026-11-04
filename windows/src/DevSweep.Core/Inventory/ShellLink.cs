using System.Buffers.Binary;
using System.Text;

namespace DevSweep.Core.Inventory;

/// <summary>What a <c>.lnk</c> file points at.</summary>
/// <param name="Target">The target path, when the link says (local path, environment-variable target or relative path).</param>
/// <param name="IsAdvertised">An MSI advertised shortcut (Darwin id): it names a product, not a path.</param>
public sealed record ShellLinkInfo(string? Target, bool IsAdvertised);

/// <summary>
/// Reads <c>.lnk</c> files from their bytes ([MS-SHLLINK]): the header, the
/// LinkInfo local base path, the string data and the environment-variable and
/// Darwin extra-data blocks. Pure managed code: no COM <c>IShellLink</c>, so it is
/// Native AOT safe and never loads shell extensions.
/// </summary>
public static class ShellLink
{
    private const int HeaderSize = 0x4C;
    private const uint HasLinkTargetIdList = 0x1;
    private const uint HasLinkInfo = 0x2;
    private const uint HasName = 0x4;
    private const uint HasRelativePath = 0x8;
    private const uint HasWorkingDir = 0x10;
    private const uint HasArguments = 0x20;
    private const uint HasIconLocation = 0x40;
    private const uint IsUnicode = 0x80;
    private const uint ForceNoLinkInfo = 0x100;
    private const uint HasDarwinId = 0x1000;
    private const uint EnvironmentBlock = 0xA000_0001;
    private const int MaxBytes = 1024 * 1024;

    private static readonly byte[] LinkClsid = [0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46];

    /// <summary>Reads a <c>.lnk</c> file; null when it cannot be read or is not a shell link.</summary>
    public static ShellLinkInfo? Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes)
            {
                return null;
            }

            ShellLinkInfo? link = Parse(File.ReadAllBytes(path));
            if (link?.Target is { } target && !Path.IsPathFullyQualified(target) && info.DirectoryName is { } folder)
            {
                link = link with { Target = Path.GetFullPath(Path.Join(folder, target)) };
            }

            return link;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Parses the bytes of a <c>.lnk</c>; null when malformed. A relative target is returned as written.</summary>
    public static ShellLinkInfo? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != HeaderSize || !data[4..20].SequenceEqual(LinkClsid))
        {
            return null;
        }

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data[0x14..]);
        int position = HeaderSize;
        if ((flags & HasLinkTargetIdList) != 0)
        {
            if (!TryU16(data, position, out int idListSize))
            {
                return null;
            }

            position += 2 + idListSize;
        }

        string? localPath = null;
        if ((flags & HasLinkInfo) != 0 && (flags & ForceNoLinkInfo) == 0)
        {
            if (!TryU32(data, position, out uint linkInfoSize) || linkInfoSize < 0x1C || position + linkInfoSize > data.Length)
            {
                return null;
            }

            localPath = LocalPath(data.Slice(position, (int)linkInfoSize));
            position += (int)linkInfoSize;
        }

        bool unicode = (flags & IsUnicode) != 0;
        string? relativePath = null;
        foreach (uint flag in (ReadOnlySpan<uint>)[HasName, HasRelativePath, HasWorkingDir, HasArguments, HasIconLocation])
        {
            if ((flags & flag) == 0)
            {
                continue;
            }

            if (!TryU16(data, position, out int characters))
            {
                return null;
            }

            int bytes = characters * (unicode ? 2 : 1);
            if (position + 2 + bytes > data.Length)
            {
                return null;
            }

            ReadOnlySpan<byte> text = data.Slice(position + 2, bytes);
            if (flag == HasRelativePath)
            {
                relativePath = unicode ? Encoding.Unicode.GetString(text) : Encoding.Latin1.GetString(text);
            }

            position += 2 + bytes;
        }

        string? environmentTarget = EnvironmentTarget(data, position);
        string? target = localPath ?? environmentTarget ?? (relativePath is { Length: > 0 } ? relativePath : null);
        return new ShellLinkInfo(target, (flags & HasDarwinId) != 0);
    }

    private static string? LocalPath(ReadOnlySpan<byte> info)
    {
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(info[4..]);
        uint linkInfoFlags = BinaryPrimitives.ReadUInt32LittleEndian(info[8..]);
        if ((linkInfoFlags & 1) == 0)
        {
            return null; // Network-only target: no local path.
        }

        uint baseOffset = BinaryPrimitives.ReadUInt32LittleEndian(info[16..]);
        uint suffixOffset = BinaryPrimitives.ReadUInt32LittleEndian(info[24..]);
        if (headerSize >= 0x24 && info.Length >= 0x24)
        {
            uint baseUnicode = BinaryPrimitives.ReadUInt32LittleEndian(info[28..]);
            uint suffixUnicode = BinaryPrimitives.ReadUInt32LittleEndian(info[32..]);
            if (Utf16Z(info, baseUnicode) is { Length: > 0 } wide)
            {
                return wide + (Utf16Z(info, suffixUnicode) ?? string.Empty);
            }
        }

        return AnsiZ(info, baseOffset) is { Length: > 0 } ansi ? ansi + (AnsiZ(info, suffixOffset) ?? string.Empty) : null;
    }

    private static string? EnvironmentTarget(ReadOnlySpan<byte> data, int position)
    {
        while (TryU32(data, position, out uint size) && size >= 8 && position + size <= data.Length)
        {
            uint signature = BinaryPrimitives.ReadUInt32LittleEndian(data[(position + 4)..]);
            if (signature == EnvironmentBlock && size >= 0x314)
            {
                ReadOnlySpan<byte> block = data.Slice(position, (int)size);
                string? target = Utf16Z(block, 8 + 260) ?? AnsiZ(block, 8);
                return target is { Length: > 0 } ? Environment.ExpandEnvironmentVariables(target) : null;
            }

            position += (int)size;
        }

        return null;
    }

    private static string? Utf16Z(ReadOnlySpan<byte> data, uint offset)
    {
        if (offset == 0 || offset >= data.Length)
        {
            return null;
        }

        ReadOnlySpan<byte> rest = data[(int)offset..];
        int length = 0;
        while (length + 1 < rest.Length && (rest[length] != 0 || rest[length + 1] != 0))
        {
            length += 2;
        }

        return Encoding.Unicode.GetString(rest[..length]);
    }

    private static string? AnsiZ(ReadOnlySpan<byte> data, uint offset)
    {
        if (offset == 0 || offset >= data.Length)
        {
            return null;
        }

        ReadOnlySpan<byte> rest = data[(int)offset..];
        int end = rest.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? rest : rest[..end]);
    }

    private static bool TryU16(ReadOnlySpan<byte> data, int position, out int value)
    {
        value = position >= 0 && position + 2 <= data.Length ? BinaryPrimitives.ReadUInt16LittleEndian(data[position..]) : -1;
        return value >= 0;
    }

    private static bool TryU32(ReadOnlySpan<byte> data, int position, out uint value)
    {
        bool ok = position >= 0 && position + 4 <= data.Length;
        value = ok ? BinaryPrimitives.ReadUInt32LittleEndian(data[position..]) : 0;
        return ok;
    }
}
