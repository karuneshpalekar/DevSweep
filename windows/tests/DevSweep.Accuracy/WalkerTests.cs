using DevSweep.Core.Platform;
using DevSweep.Fixtures;
using Xunit;

namespace DevSweep.Accuracy;

/// <summary>Spike (c): the walker never follows reparse points, handles long paths, and stops on time.</summary>
public sealed class WalkerTests(ITestOutputHelper output) : IDisposable
{
    private const long Mb = 1024 * 1024;
    private readonly TempFixture _fixture = new("walk");

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Junction_is_recorded_and_not_followed()
    {
        _fixture.CreateFile(@"outside\big.bin", 4 * Mb);
        string small = _fixture.CreateFile(@"walked\small.bin", 8192);
        string link = _fixture.PathOf(@"walked\to-outside");
        Ntfs.CreateJunction(link, _fixture.PathOf("outside"));

        WalkResult result = Walk.Measure(_fixture.PathOf("walked"));

        output.WriteLine($"junction: bytes={result.Bytes} files={result.Files} reparse={result.ReparsePointCount}");
        Assert.Equal(FileInspector.Inspect(small).AllocationSize, result.Bytes);
        Assert.Equal(1, result.Files);
        ReparsePointRecord record = Assert.Single(result.ReparsePoints);
        Assert.Equal(ReparseKind.Junction, record.Kind);
        Assert.Equal(ReparseTags.MountPoint, record.Tag);
        Assert.True(record.IsDirectory);
        Assert.Equal(link, record.Path, ignoreCase: true);
        Assert.True(File.Exists(_fixture.PathOf(@"outside\big.bin")));
    }

    [Fact]
    public void Junction_loop_terminates()
    {
        _fixture.CreateFile(@"loop\file.bin", 4096);
        Ntfs.CreateJunction(_fixture.PathOf(@"loop\again"), _fixture.PathOf("loop"));

        WalkResult result = Walk.Measure(_fixture.PathOf("loop"), new WalkOptions { TimeLimit = TimeSpan.FromSeconds(30) });

        Assert.True(result.Complete);
        Assert.Equal(1, result.Files);
        Assert.Equal(1, result.ReparsePointCount);
    }

    [Fact]
    public void Walking_a_junction_itself_measures_the_link_not_the_target()
    {
        _fixture.CreateFile(@"target\big.bin", 2 * Mb);
        string link = _fixture.PathOf("link");
        Ntfs.CreateJunction(link, _fixture.PathOf("target"));

        WalkResult result = Walk.Measure(link);

        Assert.Equal(0, result.Bytes);
        Assert.Equal(0, result.Files);
        Assert.Equal(ReparseKind.Junction, Assert.Single(result.ReparsePoints).Kind);
    }

    [Fact]
    public void Directory_symlink_is_recorded_and_not_followed()
    {
        _fixture.CreateFile(@"outside\big.bin", 2 * Mb);
        _fixture.CreateDirectory("walked");
        if (!Ntfs.TryCreateDirectorySymlink(_fixture.PathOf(@"walked\dirlink"), _fixture.PathOf("outside")))
        {
            Assert.Skip("Creating symbolic links needs Developer Mode or SeCreateSymbolicLinkPrivilege.");
        }

        WalkResult result = Walk.Measure(_fixture.PathOf("walked"));

        Assert.Equal(0, result.Bytes);
        ReparsePointRecord record = Assert.Single(result.ReparsePoints);
        Assert.Equal(ReparseKind.SymbolicLink, record.Kind);
        Assert.True(record.IsDirectory);
    }

    [Fact]
    public void File_symlink_is_recorded_and_its_target_not_counted()
    {
        string target = _fixture.CreateFile(@"outside\big.bin", 2 * Mb);
        _fixture.CreateDirectory("walked");
        if (!Ntfs.TryCreateFileSymlink(_fixture.PathOf(@"walked\filelink.bin"), target))
        {
            Assert.Skip("Creating symbolic links needs Developer Mode or SeCreateSymbolicLinkPrivilege.");
        }

        WalkResult result = Walk.Measure(_fixture.PathOf("walked"));

        Assert.Equal(0, result.Bytes);
        Assert.Equal(0, result.Files);
        ReparsePointRecord record = Assert.Single(result.ReparsePoints);
        Assert.Equal(ReparseKind.SymbolicLink, record.Kind);
        Assert.False(record.IsDirectory);
    }

    [Fact]
    public void Paths_longer_than_260_characters_are_walked()
    {
        string relative = Path.Combine("deep", Path.Combine([.. Enumerable.Repeat("a-folder-name-of-twenty", 14)]), "leaf.bin");
        string deepFile = _fixture.CreateFile(relative, 50_000);
        Assert.True(deepFile.Length > 300, $"fixture path is only {deepFile.Length} characters");

        WalkResult result = Walk.Measure(_fixture.PathOf("deep"));

        output.WriteLine($"long path: length={deepFile.Length} bytes={result.Bytes} dirs={result.Directories}");
        Assert.Equal(1, result.Files);
        Assert.Equal(FileInspector.Inspect(deepFile).AllocationSize, result.Bytes);
        Assert.Equal(15, result.Directories);
        Assert.Equal(SizeConfidence.Exact, result.Confidence);
    }

    [Fact]
    public void Zero_time_limit_returns_partial_result()
    {
        _fixture.CreateFile(@"many\a.bin", 4096);

        WalkResult result = Walk.Measure(_fixture.PathOf("many"), new WalkOptions { TimeLimit = TimeSpan.Zero });

        Assert.False(result.Complete);
        Assert.Equal(SizeConfidence.Partial, result.Confidence);
    }

    [Fact]
    public void Time_limit_stops_a_large_walk_with_partial_counts()
    {
        for (int d = 0; d < 20; d++)
        {
            string directory = _fixture.CreateDirectory($@"wide\d{d}");
            for (int f = 0; f < 100; f++)
            {
                File.WriteAllBytes(Path.Combine(directory, $"f{f}.bin"), [1, 2, 3]);
            }
        }

        WalkResult full = Walk.Measure(_fixture.PathOf("wide"));
        WalkResult limited = Walk.Measure(_fixture.PathOf("wide"), new WalkOptions { TimeLimit = TimeSpan.FromTicks(full.Elapsed.Ticks / 4) });

        output.WriteLine($"full: files={full.Files} in {full.Elapsed.TotalMilliseconds:0.0}ms; limited: files={limited.Files} complete={limited.Complete}");
        Assert.True(full.Complete);
        Assert.Equal(2000, full.Files);
        Assert.InRange(limited.Files, 0, full.Files);
        if (!limited.Complete)
        {
            Assert.True(limited.Files < full.Files);
        }
    }

    [Fact]
    public void Cancellation_returns_partial_result()
    {
        _fixture.CreateFile(@"cancel\a.bin", 4096);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        WalkResult result = DirectoryWalker.Measure(_fixture.PathOf("cancel"), cancellationToken: cancel.Token);

        Assert.False(result.Complete);
        Assert.Equal(0, result.Files);
    }

    [Fact]
    public void Unlistable_folder_is_counted_as_access_denied()
    {
        string readable = _fixture.CreateFile(@"acl\ok.bin", 4096);
        _fixture.CreateFile(@"acl\locked\hidden.bin", 4096);
        IDisposable deny = Ntfs.DenyListing(_fixture.PathOf(@"acl\locked"));
        _fixture.OnCleanup(deny.Dispose);

        WalkResult result = Walk.Measure(_fixture.PathOf("acl"));

        output.WriteLine($"acl: bytes={result.Bytes} denied={result.AccessDenied} problems={string.Join(", ", result.Problems)}");
        Assert.Equal(1, result.AccessDenied);
        Assert.Equal(FileInspector.Inspect(readable).AllocationSize, result.Bytes);
        Assert.True(result.Complete);
        Assert.Equal(SizeConfidence.AccessDenied, result.Confidence);
    }

    [Fact]
    public void A_single_file_can_be_measured()
    {
        string file = _fixture.CreateFile("single.bin", 70_000);

        WalkResult result = Walk.Measure(file);

        Assert.Equal(1, result.Files);
        Assert.Equal(0, result.Directories);
        Assert.Equal(FileInspector.Inspect(file).AllocationSize, result.Bytes);
    }

    [Fact]
    public void Missing_path_throws_file_not_found()
        => Assert.Throws<FileNotFoundException>(() => Walk.Measure(_fixture.PathOf("nope")));
}
