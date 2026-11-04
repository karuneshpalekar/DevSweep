using DevSweep.Core.Platform;
using DevSweep.Fixtures;
using Xunit;

namespace DevSweep.Accuracy;

/// <summary>Spike (b): file identity and hard-link dedupe.</summary>
public sealed class HardLinkTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempFixture _fixture = new("links");

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Every_hard_link_has_the_same_identity()
    {
        string original = _fixture.CreateFile(@"store\file.bin", 1024 * 1024);
        string other = _fixture.CreateFile(@"store\other.bin", 4096);
        for (int i = 1; i <= 3; i++)
        {
            Ntfs.CreateHardLink(_fixture.PathOf($@"store\link{i}.bin"), original);
        }

        FileFacts facts = FileInspector.Inspect(original);
        output.WriteLine($"identity={facts.Identity} links={facts.LinkCount}");

        Assert.Equal(4u, facts.LinkCount);
        for (int i = 1; i <= 3; i++)
        {
            Assert.Equal(facts.Identity, FileInspector.Inspect(_fixture.PathOf($@"store\link{i}.bin")).Identity);
        }

        Assert.NotEqual(facts.Identity, FileInspector.Inspect(other).Identity);
        Assert.NotEqual(0UL, facts.Identity.VolumeSerial);
    }

    [Fact]
    public void A_file_linked_three_times_measures_as_one_file()
    {
        string original = _fixture.CreateFile(@"farm\file.bin", 1024 * 1024);
        for (int i = 1; i <= 3; i++)
        {
            Ntfs.CreateHardLink(_fixture.PathOf($@"farm\link{i}.bin"), original);
        }

        long one = FileInspector.Inspect(original).AllocationSize;
        WalkResult result = Walk.Measure(_fixture.PathOf("farm"));

        output.WriteLine($"farm: bytes={result.Bytes} one-file={one} files={result.Files} duplicates={result.HardLinkDuplicates} shared={result.SharedLinkBytes}");
        Assert.Equal(one, result.Bytes);
        Assert.Equal(1, result.Files);
        Assert.Equal(3, result.HardLinkDuplicates);
        Assert.Equal(0, result.SharedLinkBytes);
        Assert.Equal(one, result.FreeableBytes);
        Assert.Equal(SizeConfidence.Exact, result.Confidence);
    }

    [Fact]
    public void A_link_outside_the_walk_is_reported_as_shared()
    {
        string inside = _fixture.CreateFile(@"project\node_modules\pkg.bin", 512 * 1024);
        _fixture.CreateDirectory("store");
        Ntfs.CreateHardLink(_fixture.PathOf(@"store\pkg.bin"), inside);
        long one = FileInspector.Inspect(inside).AllocationSize;

        WalkResult result = Walk.Measure(_fixture.PathOf("project"));

        output.WriteLine($"shared: bytes={result.Bytes} shared={result.SharedLinkBytes} freeable={result.FreeableBytes}");
        Assert.Equal(one, result.Bytes);
        Assert.Equal(one, result.SharedLinkBytes);
        Assert.Equal(1, result.SharedLinkFiles);
        Assert.Equal(0, result.FreeableBytes);
        Assert.Equal(SizeConfidence.SharedLinks, result.Confidence);
    }
}
