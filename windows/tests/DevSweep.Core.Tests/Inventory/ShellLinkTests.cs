using System.Buffers.Binary;
using System.Text;
using DevSweep.Core.Inventory;
using Xunit;

namespace DevSweep.Core.Tests.Inventory;

/// <summary>.lnk parsing from bytes built to [MS-SHLLINK] (the accuracy suite also parses links Windows wrote).</summary>
public sealed class ShellLinkTests
{
    private const uint HasLinkInfo = 0x2;
    private const uint HasName = 0x4;
    private const uint HasRelativePath = 0x8;
    private const uint IsUnicode = 0x80;
    private const uint HasExpString = 0x200;
    private const uint HasDarwinId = 0x1000;

    [Fact]
    public void Unicode_local_base_path_is_the_target()
    {
        byte[] link = Link(HasLinkInfo | IsUnicode, LinkInfo(@"C:\Program Files\App\", "app.exe", unicode: true));

        ShellLinkInfo? info = ShellLink.Parse(link);

        Assert.Equal(@"C:\Program Files\App\app.exe", info?.Target);
        Assert.False(info!.IsAdvertised);
    }

    [Fact]
    public void Ansi_local_base_path_is_read_when_there_is_no_unicode_one()
    {
        byte[] link = Link(HasLinkInfo, LinkInfo(@"C:\Tools\tool.exe", string.Empty, unicode: false));

        Assert.Equal(@"C:\Tools\tool.exe", ShellLink.Parse(link)?.Target);
    }

    [Fact]
    public void String_data_is_skipped_and_the_environment_block_gives_the_target()
    {
        byte[] strings = [.. CountedUnicode("My app"), .. CountedUnicode(@"..\..\x.exe")];
        byte[] link = Link(HasName | HasRelativePath | IsUnicode | HasExpString, strings, EnvironmentBlock(@"%DEVSWEEP_NOT_SET%\app\app.exe"));

        ShellLinkInfo? info = ShellLink.Parse(link);

        Assert.Equal(@"%DEVSWEEP_NOT_SET%\app\app.exe", info?.Target);
    }

    [Fact]
    public void Relative_path_is_the_fallback()
    {
        byte[] link = Link(HasRelativePath | IsUnicode, CountedUnicode(@"..\Programs\App\app.exe"));

        Assert.Equal(@"..\Programs\App\app.exe", ShellLink.Parse(link)?.Target);
    }

    [Fact]
    public void Advertised_shortcuts_are_flagged()
    {
        byte[] link = Link(HasDarwinId | IsUnicode, []);

        ShellLinkInfo? info = ShellLink.Parse(link);

        Assert.True(info?.IsAdvertised);
        Assert.Null(info!.Target);
    }

    [Fact]
    public void Garbage_and_truncated_links_give_null()
    {
        Assert.Null(ShellLink.Parse(Encoding.ASCII.GetBytes("not a shortcut at all, not at all, not at all, not at all, not at all.")));
        byte[] link = Link(HasLinkInfo | IsUnicode, LinkInfo(@"C:\A\", "a.exe", unicode: true));
        Assert.Null(ShellLink.Parse(link.AsSpan(0, 0x50)));
    }

    private static byte[] Link(uint flags, byte[] body, byte[]? extra = null)
    {
        byte[] header = new byte[0x4C];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x4C);
        byte[] clsid = [0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46];
        clsid.CopyTo(header, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x14), flags);
        return [.. header, .. body, .. extra ?? [], 0, 0, 0, 0];
    }

    private static byte[] LinkInfo(string basePath, string suffix, bool unicode)
    {
        uint headerSize = unicode ? 0x24u : 0x1Cu;
        byte[] volume = new byte[0x10];
        BinaryPrimitives.WriteUInt32LittleEndian(volume, 0x10);
        byte[] ansiBase = [.. Encoding.Latin1.GetBytes(basePath), 0];
        byte[] ansiSuffix = [.. Encoding.Latin1.GetBytes(suffix), 0];
        byte[] wideBase = [.. Encoding.Unicode.GetBytes(basePath), 0, 0];
        byte[] wideSuffix = [.. Encoding.Unicode.GetBytes(suffix), 0, 0];
        uint volumeOffset = headerSize;
        uint baseOffset = volumeOffset + (uint)volume.Length;
        uint suffixOffset = baseOffset + (uint)ansiBase.Length;
        uint wideBaseOffset = suffixOffset + (uint)ansiSuffix.Length;
        uint wideSuffixOffset = wideBaseOffset + (uint)wideBase.Length;
        byte[] body = unicode ? [.. volume, .. ansiBase, .. ansiSuffix, .. wideBase, .. wideSuffix] : [.. volume, .. ansiBase, .. ansiSuffix];
        byte[] info = new byte[headerSize + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(info, (uint)info.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(4), headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(12), volumeOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(16), baseOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(24), suffixOffset);
        if (unicode)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(28), wideBaseOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(32), wideSuffixOffset);
        }

        body.CopyTo(info, (int)headerSize);
        return info;
    }

    private static byte[] CountedUnicode(string text)
    {
        byte[] count = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(count, (ushort)text.Length);
        return [.. count, .. Encoding.Unicode.GetBytes(text)];
    }

    private static byte[] EnvironmentBlock(string target)
    {
        byte[] block = new byte[0x314];
        BinaryPrimitives.WriteUInt32LittleEndian(block, 0x314);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), 0xA000_0001);
        Encoding.Latin1.GetBytes(target).CopyTo(block, 8);
        Encoding.Unicode.GetBytes(target).CopyTo(block, 8 + 260);
        return block;
    }
}
