using DevSweep.Core.Platform;
using Xunit;

namespace DevSweep.Core.Tests.Platform;

public sealed class ReparseTagsTests
{
    [Theory]
    [InlineData(0xA0000003u, ReparseKind.Junction)]
    [InlineData(0xA000000Cu, ReparseKind.SymbolicLink)]
    [InlineData(0x8000001Bu, ReparseKind.AppExecLink)]
    [InlineData(0x80000017u, ReparseKind.Wof)]
    [InlineData(0x80000013u, ReparseKind.Dedup)]
    [InlineData(0xA000001Du, ReparseKind.WslSymlink)]
    [InlineData(0x9000001Cu, ReparseKind.ProjectedFs)]
    [InlineData(0x80000023u, ReparseKind.UnixSocket)]
    [InlineData(0x9000001Au, ReparseKind.Cloud)]
    [InlineData(0x9000501Au, ReparseKind.Cloud)]
    [InlineData(0x80000021u, ReparseKind.Cloud)]
    [InlineData(0x12345678u, ReparseKind.Other)]
    [InlineData(0u, ReparseKind.None)]
    public void Classifies_known_tags(uint tag, ReparseKind expected)
        => Assert.Equal(expected, ReparseTags.Classify(tag));

    [Theory]
    [InlineData(0xA0000003u, true)]
    [InlineData(0xA000000Cu, true)]
    [InlineData(0x8000001Bu, true)]
    [InlineData(0x12345678u, true)]
    [InlineData(0x80000017u, false)]
    [InlineData(0x80000013u, false)]
    [InlineData(0x9000001Au, false)]
    public void Links_are_every_reparse_point_except_data_and_cloud(uint tag, bool isLink)
    {
        var entry = new DirectoryEntry("x", Win32FileAttributes.ReparsePoint, tag, 0);
        Assert.Equal(isLink, entry.IsLink);
    }

    [Fact]
    public void Plain_files_are_not_links()
        => Assert.False(new DirectoryEntry("x", Win32FileAttributes.Archive, 0, 1).IsLink);
}
