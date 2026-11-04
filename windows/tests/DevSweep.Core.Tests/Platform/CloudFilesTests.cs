using DevSweep.Core.Platform;
using Xunit;

namespace DevSweep.Core.Tests.Platform;

public sealed class CloudFilesTests
{
    [Theory]
    [InlineData(Win32FileAttributes.Archive)]
    [InlineData(Win32FileAttributes.Normal)]
    [InlineData(Win32FileAttributes.Compressed | Win32FileAttributes.SparseFile)]
    public void Ordinary_files_are_not_cloud(Win32FileAttributes attributes)
        => Assert.Equal(CloudFileState.NotCloud, CloudFiles.Classify(attributes));

    [Theory]
    [InlineData(Win32FileAttributes.RecallOnDataAccess)]
    [InlineData(Win32FileAttributes.RecallOnOpen)]
    [InlineData(Win32FileAttributes.Offline)]
    [InlineData(Win32FileAttributes.RecallOnDataAccess | Win32FileAttributes.Unpinned)]
    [InlineData(Win32FileAttributes.RecallOnDataAccess | Win32FileAttributes.Pinned)]
    [InlineData(Win32FileAttributes.RecallOnDataAccess | Win32FileAttributes.ReparsePoint | Win32FileAttributes.SparseFile)]
    public void Files_whose_data_is_not_local_are_placeholders(Win32FileAttributes attributes)
        => Assert.Equal(CloudFileState.Placeholder, CloudFiles.Classify(attributes, ReparseTags.Cloud));

    [Theory]
    [InlineData(Win32FileAttributes.Pinned)]
    [InlineData(Win32FileAttributes.Unpinned)]
    [InlineData(Win32FileAttributes.Archive | Win32FileAttributes.Pinned)]
    public void Pinned_or_unpinned_without_recall_bits_is_local(Win32FileAttributes attributes)
        => Assert.Equal(CloudFileState.LocallyAvailable, CloudFiles.Classify(attributes));

    [Theory]
    [InlineData(0x9000001Au)]
    [InlineData(0x9000101Au)]
    [InlineData(0x9000F01Au)]
    [InlineData(0x80000021u)]
    public void Hydrated_cloud_reparse_file_is_local(uint tag)
        => Assert.Equal(CloudFileState.LocallyAvailable, CloudFiles.Classify(Win32FileAttributes.ReparsePoint, tag));

    [Fact]
    public void Reparse_tag_is_ignored_without_the_reparse_attribute()
        => Assert.Equal(CloudFileState.NotCloud, CloudFiles.Classify(Win32FileAttributes.Archive, ReparseTags.Cloud));

    [Fact]
    public void Attribute_values_match_winnt_h()
    {
        Assert.Equal(0x00400000u, (uint)Win32FileAttributes.RecallOnDataAccess);
        Assert.Equal(0x00040000u, (uint)Win32FileAttributes.RecallOnOpen);
        Assert.Equal(0x00080000u, (uint)Win32FileAttributes.Pinned);
        Assert.Equal(0x00100000u, (uint)Win32FileAttributes.Unpinned);
        Assert.Equal(0x00001000u, (uint)Win32FileAttributes.Offline);
        Assert.Equal((uint)FileAttributes.ReparsePoint, (uint)Win32FileAttributes.ReparsePoint);
        Assert.Equal((uint)FileAttributes.SparseFile, (uint)Win32FileAttributes.SparseFile);
        Assert.Equal((uint)FileAttributes.Compressed, (uint)Win32FileAttributes.Compressed);
    }

    [Fact]
    public void Directory_entry_exposes_cloud_state()
    {
        var placeholder = new DirectoryEntry("a.docx", Win32FileAttributes.RecallOnDataAccess | Win32FileAttributes.ReparsePoint, ReparseTags.Cloud, 4096);
        Assert.Equal(CloudFileState.Placeholder, placeholder.CloudState);
        Assert.False(placeholder.IsLink);
    }
}
