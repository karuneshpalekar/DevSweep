using System.Buffers.Binary;
using System.Text;
using DevSweep.Core.Platform.Recycling;
using Xunit;

namespace DevSweep.Core.Tests.Platform;

public sealed class RecycleBinIndexTests
{
    private static readonly DateTimeOffset Deleted = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    [Fact]
    public void Parses_a_version_2_record()
    {
        string original = @"C:\Users\dev\project\node_modules";
        byte[] data = Version2(original, 123_456);

        Assert.True(RecycleBinIndex.TryParse(data, out string path, out long size, out DateTimeOffset at));
        Assert.Equal(original, path);
        Assert.Equal(123_456, size);
        Assert.Equal(Deleted, at);
    }

    [Fact]
    public void Parses_a_version_1_record()
    {
        string original = @"D:\old\file.bin";
        byte[] data = new byte[24 + (260 * 2)];
        BinaryPrimitives.WriteInt64LittleEndian(data, 1);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8), 42);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), Deleted.ToFileTime());
        Encoding.Unicode.GetBytes(original).CopyTo(data, 24);

        Assert.True(RecycleBinIndex.TryParse(data, out string path, out long size, out DateTimeOffset at));
        Assert.Equal(original, path);
        Assert.Equal(42, size);
        Assert.Equal(Deleted, at);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(27)]
    public void Truncated_records_are_rejected(int length)
        => Assert.False(RecycleBinIndex.TryParse(Version2(@"C:\x", 1).AsSpan(0, length), out _, out _, out _));

    [Fact]
    public void A_character_count_past_the_end_is_rejected()
    {
        byte[] data = Version2(@"C:\x", 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), 10_000);
        Assert.False(RecycleBinIndex.TryParse(data, out _, out _, out _));
    }

    [Fact]
    public void An_unknown_version_is_rejected()
    {
        byte[] data = Version2(@"C:\x", 1);
        BinaryPrimitives.WriteInt64LittleEndian(data, 3);
        Assert.False(RecycleBinIndex.TryParse(data, out _, out _, out _));
    }

    private static byte[] Version2(string path, long size)
    {
        byte[] name = Encoding.Unicode.GetBytes(path + "\0");
        byte[] data = new byte[28 + name.Length];
        BinaryPrimitives.WriteInt64LittleEndian(data, 2);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8), size);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), Deleted.ToFileTime());
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), path.Length + 1);
        name.CopyTo(data, 28);
        return data;
    }
}
