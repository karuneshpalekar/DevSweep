using DevSweep.Core.Platform;
using DevSweep.Fixtures;
using Xunit;

namespace DevSweep.Accuracy;

/// <summary>
/// Walker v2: folder listings from <c>FileIdExtdDirectoryInfo</c>, alternate
/// streams, small (MFT-resident) files, and the same answers from every listing
/// method and measuring mode.
/// </summary>
public sealed class WalkerV2Tests(ITestOutputHelper output) : IDisposable
{
    private const long Mb = 1024 * 1024;
    private static readonly WalkOptions FindFirstFile = new() { Listing = ListingMethod.FindFirstFile };
    private static readonly WalkOptions NoStreams = new() { AlternateStreams = false };
    private static readonly WalkOptions M0 = new() { Listing = ListingMethod.FindFirstFile, AlternateStreams = false };
    private readonly TempFixture _fixture = new("walk2");

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Ntfs_folders_are_listed_from_their_handle()
    {
        _fixture.CreateFile(@"tree\a.bin", 10_000);
        _fixture.CreateFile(@"tree\sub\b.bin", 20_000);

        WalkResult result = Walk.Measure(_fixture.PathOf("tree"));

        Assert.Equal(ListingUsed.FileIdExtdDirectoryInfo, result.Listing);
        Assert.Equal(2, result.Files);
        Assert.Equal(2, result.Directories);
    }

    [Fact]
    public void Every_listing_method_and_mode_gives_the_same_answer_on_a_mixed_tree()
    {
        string root = BuildMixedTree();

        WalkResult v2 = Walk.Measure(root);
        WalkResult v2Fast = Walk.Measure(root, NoStreams);
        WalkResult fallback = Walk.Measure(root, FindFirstFile);
        WalkResult m0 = Walk.Measure(root, M0);

        foreach ((string name, WalkResult r) in new[] { ("v2", v2), ("v2 no streams", v2Fast), ("FindFirstFile", fallback), ("M0", m0) })
        {
            output.WriteLine($"{name,-14} listing={r.Listing} bytes={r.Bytes} logical={r.LogicalBytes} files={r.Files} dirs={r.Directories} dup={r.HardLinkDuplicates} shared={r.SharedLinkBytes} ads={r.AlternateStreamCount}/{r.AlternateStreamBytes} reparse={r.ReparsePointCount} stale={r.StaleDirectoryEntries} {r.Elapsed.TotalMilliseconds:0.0}ms");
        }

        Assert.Equal(ListingUsed.FileIdExtdDirectoryInfo, v2.Listing);
        Assert.Equal(ListingUsed.FindFirstFile, fallback.Listing);
        Assert.Equal(v2.Bytes, fallback.Bytes);
        Assert.Equal(v2.LogicalBytes, fallback.LogicalBytes);
        Assert.Equal(v2Fast.Bytes, m0.Bytes);
        Assert.Equal(v2.Bytes - v2.AlternateStreamBytes, v2Fast.Bytes);
        foreach (WalkResult r in new[] { v2Fast, fallback, m0 })
        {
            Assert.Equal(v2.Files, r.Files);
            Assert.Equal(v2.Directories, r.Directories);
            Assert.Equal(v2.HardLinkDuplicates, r.HardLinkDuplicates);
            Assert.Equal(v2.SharedLinkBytes, r.SharedLinkBytes);
            Assert.Equal(v2.ReparsePointCount, r.ReparsePointCount);
            Assert.Equal(SizeConfidence.SharedLinks, r.Confidence);
        }

        Assert.Equal(1, v2.AlternateStreamCount);
        Assert.Equal(0, v2Fast.AlternateStreamCount);
    }

    [Fact]
    public void Alternate_data_streams_are_counted()
    {
        string file = _fixture.CreateFile(@"ads\main.bin", 4096);
        WriteStream(file, "extra", 1 * (int)Mb);
        WriteStream(file, "Zone.Identifier", 40);

        FileFacts facts = FileInspector.Inspect(file);
        WalkResult result = Walk.Measure(_fixture.PathOf("ads"));
        WalkResult withoutStreams = Walk.Measure(_fixture.PathOf("ads"), NoStreams);

        output.WriteLine($"ads: main allocation={facts.AllocationSize} streams={facts.AlternateStreams} stream length={facts.AlternateStreamLength} stream allocation={facts.AlternateStreamAllocation}; walk bytes={result.Bytes} without streams={withoutStreams.Bytes}");
        Assert.Equal(2, facts.AlternateStreams);
        Assert.Equal(Mb + 40, facts.AlternateStreamLength);
        Assert.InRange(facts.AlternateStreamAllocation, Mb, Mb + 8192);
        Assert.Equal(facts.AllocationSize + facts.AlternateStreamAllocation, result.Bytes);
        Assert.Equal(4096 + Mb + 40, result.LogicalBytes);
        Assert.Equal(2, result.AlternateStreamCount);
        Assert.Equal(facts.AllocationSize, withoutStreams.Bytes);
    }

    [Fact]
    public void Small_files_report_the_allocation_ntfs_reports()
    {
        // Decision: report what NTFS reports. A file small enough to live inside its
        // MFT record has a few bytes of "allocation" (rounded to 8) and no clusters;
        // Explorer's "Size on disk" shows 0 for it. Either way it is under one cluster.
        string tiny = _fixture.PathOf(@"small\tiny.txt");
        Directory.CreateDirectory(_fixture.PathOf("small"));
        File.WriteAllBytes(tiny, new byte[100]);
        long cluster = Volumes.ForPath(tiny).ClusterSize;

        FileFacts facts = FileInspector.Inspect(tiny);
        WalkResult v2 = Walk.Measure(_fixture.PathOf("small"));
        WalkResult fast = Walk.Measure(_fixture.PathOf("small"), NoStreams);

        output.WriteLine($"100-byte file: allocation={facts.AllocationSize} (cluster {cluster}); v2={v2.Bytes} by-name={fast.Bytes}; Explorer shows 0 for MFT-resident files");
        Assert.InRange(facts.AllocationSize, 0, cluster - 1);
        Assert.Equal(facts.AllocationSize, v2.Bytes);
        Assert.Equal(facts.AllocationSize, fast.Bytes);
    }

    [Fact]
    public void Fresh_sizes_are_used_when_the_directory_entry_is_stale()
    {
        // NTFS updates the size copy in a folder's index only for the name a file
        // was written through; the other name's entry keeps the old size.
        string original = _fixture.CreateFile(@"stale\a\file.bin", 4096);
        _fixture.CreateDirectory(@"stale\b");
        string link = _fixture.PathOf(@"stale\b\link.bin");
        Ntfs.CreateHardLink(link, original);
        using (var stream = new FileStream(link, FileMode.Append, FileAccess.Write))
        {
            stream.Write(new byte[2 * Mb]);
        }

        FileFacts truth = FileInspector.Inspect(original);
        WalkResult v2 = Walk.Measure(_fixture.PathOf(@"stale\a"));
        WalkResult fast = Walk.Measure(_fixture.PathOf(@"stale\a"), NoStreams);

        output.WriteLine($"stale: truth allocation={truth.AllocationSize} length={truth.Length}; v2 bytes={v2.Bytes} stale entries={v2.StaleDirectoryEntries}; by-name bytes={fast.Bytes} stale={fast.StaleDirectoryEntries}");
        Assert.Equal(truth.AllocationSize, v2.Bytes);
        Assert.Equal(truth.AllocationSize, fast.Bytes);
        Assert.Equal(truth.Length, v2.LogicalBytes);
        Assert.Equal(truth.AllocationSize, v2.SharedLinkBytes);
    }

    [Fact]
    public void Wof_files_are_not_counted_twice_through_their_stream()
    {
        string path = _fixture.CreateCompressibleFile(@"wof\w.txt", 4 * Mb);
        if (!Ntfs.TryWofCompress(path))
        {
            Assert.Skip("compact /exe is not available on this machine.");
        }

        FileFacts facts = FileInspector.Inspect(path);
        WalkResult v2 = Walk.Measure(_fixture.PathOf("wof"));
        WalkResult fast = Walk.Measure(_fixture.PathOf("wof"), NoStreams);

        output.WriteLine($"wof: allocation={facts.AllocationSize} streams={facts.AlternateStreams}/{facts.AlternateStreamAllocation}; v2={v2.Bytes}; by-name={fast.Bytes}; reference={Ntfs.CompressedFileSize(path)}");
        Assert.InRange(v2.Bytes, 1, 2 * Mb);
        Assert.Equal(0, v2.AlternateStreamBytes);
        Assert.Equal(v2.Bytes, fast.Bytes);
    }

    private static void WriteStream(string file, string stream, int length)
    {
        using var handle = File.OpenHandle($"{file}:{stream}", FileMode.Create, FileAccess.Write);
        RandomAccess.Write(handle, new byte[length], 0);
    }

    /// <summary>Hard links inside and to outside, compressed, sparse, a junction, an alternate stream, a deep path.</summary>
    private string BuildMixedTree()
    {
        string a = _fixture.CreateFile(@"mixed\plain\a.bin", 300_000);
        Ntfs.CreateHardLink(_fixture.PathOf(@"mixed\plain\a-link.bin"), a);
        string shared = _fixture.CreateFile(@"mixed\shared.bin", 200_000);
        _fixture.CreateDirectory("outside");
        Ntfs.CreateHardLink(_fixture.PathOf(@"outside\shared-link.bin"), shared);
        Ntfs.Compress(_fixture.CreateCompressibleFile(@"mixed\c.txt", 2 * Mb));
        string sparse = _fixture.CreateFile(@"mixed\sparse.bin", 4 * Mb);
        Ntfs.MakeSparse(sparse);
        Ntfs.ZeroRange(sparse, 0, 3 * Mb);
        _fixture.CreateFile(@"outside\big.bin", Mb);
        Ntfs.CreateJunction(_fixture.PathOf(@"mixed\to-outside"), _fixture.PathOf("outside"));
        WriteStream(_fixture.CreateFile(@"mixed\with-stream.bin", 1000), "data", 70_000);
        File.WriteAllBytes(_fixture.PathOf(@"mixed\tiny.txt"), new byte[10]);
        _fixture.CreateFile(Path.Combine("mixed", Path.Combine([.. Enumerable.Repeat("a-folder-name-of-twenty", 13)]), "deep.bin"), 5000);
        return _fixture.PathOf("mixed");
    }
}
