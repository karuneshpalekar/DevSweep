using System.Text.RegularExpressions;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Platform;
using DevSweep.Core.Platform.Quarantine;
using Xunit;

namespace DevSweep.Core.Tests.Platform;

public sealed partial class SmallPartsTests
{
    [Theory]
    [InlineData(@"\\?\C:\a\b", @"\??\C:\a\b")]
    [InlineData(@"\\?\UNC\server\share\a", @"\??\UNC\server\share\a")]
    public void Nt_paths_for_by_name_queries(string extended, string expected)
        => Assert.Equal(expected, FileStat.ToNtPath(extended));

    [Fact]
    public void Quarantine_ids_sort_by_time_and_are_unique()
    {
        var at = new DateTimeOffset(2026, 11, 4, 9, 8, 7, TimeSpan.Zero);
        string a = QuarantineStore.NewId(at);
        string b = QuarantineStore.NewId(at);
        Assert.Matches(IdPattern(), a);
        Assert.StartsWith("20261104-090807-", a, StringComparison.Ordinal);
        Assert.NotEqual(a, b);
        Assert.True(string.CompareOrdinal(QuarantineStore.NewId(at.AddSeconds(1)), a) > 0);
    }

    [Fact]
    public void Lock_report_names_every_process()
    {
        var report = new LockReport([new LockingProcess(42, "Code", "Code.exe", null), new LockingProcess(7, "node.exe", "node.exe", null)], 3, false);
        Assert.True(report.IsLocked);
        Assert.Equal("Close Code (Code.exe, PID 42), node.exe (PID 7) first; it has files in this item open.", report.BlockerMessage);
        Assert.Null(new LockReport([], 3, false).BlockerMessage);
    }

    [Theory]
    [InlineData(CleanActionKind.Delete, "Delete")]
    [InlineData(CleanActionKind.Trash, "Move to Recycle Bin")]
    [InlineData(CleanActionKind.Manual, "Show commands")]
    public void Action_labels(CleanActionKind kind, string expected)
        => Assert.Equal(expected, new CleanAction(kind).DisplayLabel);

    [Fact]
    public void Command_label_names_the_program()
        => Assert.Equal("Run npm", new CleanAction(CleanActionKind.Command, Command: ["npm", "cache", "clean", "--force"]).DisplayLabel);

    [Theory]
    [InlineData(0x0040_0000u, true)]
    [InlineData(0x0000_0010u, true)]
    public void Cloud_folders_that_are_placeholders_are_not_entered(uint extra, bool isDirectory)
    {
        var attributes = (Win32FileAttributes)extra | Win32FileAttributes.Directory | Win32FileAttributes.ReparsePoint;
        var entry = new DirectoryEntry("OneDrive", attributes, ReparseTags.Cloud, 0);
        Assert.Equal(isDirectory, entry.IsDirectory);
        Assert.Equal((extra & 0x0040_0000u) == 0, entry.IsTraversableDirectory);
    }

    [Fact]
    public void Junction_folders_are_never_traversable_but_plain_ones_are()
    {
        Assert.False(new DirectoryEntry("j", Win32FileAttributes.Directory | Win32FileAttributes.ReparsePoint, ReparseTags.MountPoint, 0).IsTraversableDirectory);
        Assert.True(new DirectoryEntry("d", Win32FileAttributes.Directory, 0, 0).IsTraversableDirectory);
        Assert.False(new DirectoryEntry("f", Win32FileAttributes.Archive, 0, 0).IsTraversableDirectory);
    }

    [GeneratedRegex("^[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$")]
    private static partial Regex IdPattern();
}
