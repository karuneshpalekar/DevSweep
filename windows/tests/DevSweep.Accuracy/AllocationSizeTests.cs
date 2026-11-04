using DevSweep.Core.Platform;
using DevSweep.Fixtures;
using Xunit;

namespace DevSweep.Accuracy;

/// <summary>Spike (a): on-disk size is allocation, not logical length.</summary>
public sealed class AllocationSizeTests(ITestOutputHelper output) : IDisposable
{
    private const long Mb = 1024 * 1024;
    private readonly TempFixture _fixture = new("alloc");

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Ordinary_file_allocation_is_length_rounded_up_to_clusters()
    {
        string path = _fixture.CreateFile("plain.bin", 100_000);
        long cluster = Volumes.ForPath(path).ClusterSize;

        FileFacts facts = FileInspector.Inspect(path);

        output.WriteLine($"plain: length={facts.Length} allocation={facts.AllocationSize} cluster={cluster}");
        Assert.Equal(100_000, facts.Length);
        Assert.True(cluster > 0);
        Assert.Equal((100_000 + cluster - 1) / cluster * cluster, facts.AllocationSize);
    }

    [Fact]
    public void Ntfs_compressed_file_reports_compressed_allocation()
    {
        string path = _fixture.CreateCompressibleFile("compressed.txt", 4 * Mb);
        Ntfs.Compress(path);

        DirectoryEntry entry = FileInspector.ReadEntry(path);
        FileFacts facts = FileInspector.Inspect(path);
        long reference = Ntfs.CompressedFileSize(path);

        output.WriteLine($"compressed: attributes={entry.Attributes} length={facts.Length} allocation={facts.AllocationSize} GetCompressedFileSizeW={reference}");
        Assert.True(entry.Attributes.HasFlag(Win32FileAttributes.Compressed));
        Assert.Equal(4 * Mb, facts.Length);
        Assert.InRange(facts.AllocationSize, 1, facts.Length / 2);
        Assert.Equal(reference, facts.AllocationSize);
    }

    [Fact]
    public void Sparse_file_does_not_count_its_holes()
    {
        string path = _fixture.CreateFile("sparse.bin", 16 * Mb);
        Ntfs.MakeSparse(path);
        Ntfs.ZeroRange(path, 0, 12 * Mb);

        DirectoryEntry entry = FileInspector.ReadEntry(path);
        FileFacts facts = FileInspector.Inspect(path);
        long reference = Ntfs.CompressedFileSize(path);

        output.WriteLine($"sparse: attributes={entry.Attributes} length={facts.Length} allocation={facts.AllocationSize} GetCompressedFileSizeW={reference}");
        Assert.True(entry.Attributes.HasFlag(Win32FileAttributes.SparseFile));
        Assert.Equal(16 * Mb, facts.Length);
        Assert.InRange(facts.AllocationSize, 4 * Mb, (4 * Mb) + (64 * 1024));
        Assert.Equal(reference, facts.AllocationSize);
    }

    [Fact]
    public void Huge_sparse_file_takes_almost_no_space()
    {
        string path = _fixture.PathOf("huge-sparse.bin");
        File.WriteAllBytes(path, []);
        Ntfs.MakeSparse(path);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(1024L * 1024 * Mb); // 1 TiB logical
        }

        FileFacts facts = FileInspector.Inspect(path);

        output.WriteLine($"huge sparse: length={facts.Length} allocation={facts.AllocationSize}");
        Assert.Equal(1024L * 1024 * Mb, facts.Length);
        Assert.InRange(facts.AllocationSize, 0, Mb);
    }

    [Fact]
    public void Wof_compressed_file_reports_compressed_allocation()
    {
        string path = _fixture.CreateCompressibleFile("wof.txt", 4 * Mb);
        if (!Ntfs.TryWofCompress(path))
        {
            Assert.Skip("compact /exe is not available on this machine.");
        }

        DirectoryEntry entry = FileInspector.ReadEntry(path);
        FileFacts facts = FileInspector.Inspect(path);
        long reference = Ntfs.CompressedFileSize(path);

        output.WriteLine($"wof: attributes={entry.Attributes} tag=0x{entry.ReparseTag:X8} kind={entry.ReparseKind} length={facts.Length} allocation={facts.AllocationSize} GetCompressedFileSizeW={reference}");
        Assert.Equal(4 * Mb, facts.Length);
        Assert.InRange(facts.AllocationSize, 1, facts.Length / 2);
    }

    [Fact]
    public void Walker_total_matches_per_file_allocation()
    {
        string a = _fixture.CreateFile(@"tree\a.bin", 10_000);
        string b = _fixture.CreateFile(@"tree\sub\b.bin", 300_000);
        string c = _fixture.CreateCompressibleFile(@"tree\sub\c.txt", 2 * Mb);
        Ntfs.Compress(c);
        long expected = new[] { a, b, c }.Sum(p => FileInspector.Inspect(p).AllocationSize);
        long logical = new[] { a, b, c }.Sum(p => new FileInfo(p).Length);

        WalkResult result = Walk.Measure(_fixture.PathOf("tree"));

        output.WriteLine($"tree: bytes={result.Bytes} logical={result.LogicalBytes} files={result.Files} dirs={result.Directories} elapsed={result.Elapsed.TotalMilliseconds:0.0}ms");
        Assert.Equal(expected, result.Bytes);
        Assert.Equal(logical, result.LogicalBytes);
        Assert.Equal(3, result.Files);
        Assert.Equal(2, result.Directories);
        Assert.True(result.Complete);
        Assert.Equal(SizeConfidence.Exact, result.Confidence);
        Assert.True(result.Bytes < result.LogicalBytes);
    }
}
