using DevSweep.Core.Platform;
using DevSweep.Core.Platform.Recycling;
using DevSweep.Fixtures;
using Xunit;

namespace DevSweep.Accuracy;

/// <summary>Spike (e): recycle only what really lands in the Recycle Bin, and never through a junction.</summary>
public sealed class RecycleBinTests(ITestOutputHelper output) : IDisposable
{
    private const long Mb = 1024 * 1024;
    private readonly TempFixture _fixture = new("bin");

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Temp_volume_is_fixed_and_its_bin_settings_are_readable()
    {
        VolumeInfo volume = Volumes.ForPath(_fixture.Root);
        RecycleQuota quota = new RegistryRecycleBinQuotaSource().GetQuota(volume);
        RecycleBinUsage usage = RecycleBin.QueryUsage(_fixture.Root);

        output.WriteLine($"volume: {volume}");
        output.WriteLine($"quota: {quota}");
        output.WriteLine($"usage: {usage}");
        Assert.Equal(DriveKind.Fixed, volume.Kind);
        Assert.NotNull(volume.VolumeGuid);
        Assert.True(volume.ClusterSize > 0);
    }

    [Fact]
    public void Small_file_goes_to_the_recycle_bin()
    {
        string file = _fixture.CreateFile("small.bin", 64 * 1024);
        var bin = new RecycleBin();
        RecycleAssessment assessment = bin.Assess(file);
        RecycleBinUsage before = RecycleBin.QueryUsage(file);
        output.WriteLine($"assessment: {assessment}");

        // On a machine whose bin limit is not recorded the verdict is Unknown; the
        // spike still proves the mechanics, and says so in the log.
        Assert.Contains(assessment.Verdict, new[] { RecycleVerdict.Recyclable, RecycleVerdict.Unknown });
        RecycleOutcome outcome = bin.Recycle(file, allowUnverified: true);
        RecycleBinUsage after = RecycleBin.QueryUsage(_fixture.Root);

        output.WriteLine($"outcome: recycled={outcome.Recycled} error={outcome.Error}; bin items {before.Items} -> {after.Items}, bytes {before.Bytes} -> {after.Bytes}");
        Assert.True(outcome.Recycled, outcome.Error);
        Assert.False(File.Exists(file));
        Assert.True(after.Items > before.Items, "the item count of the Recycle Bin did not grow");
    }

    [Fact]
    public void Item_bigger_than_the_quota_would_be_permanent_and_is_left_alone()
    {
        string file = _fixture.CreateFile("big.bin", 2 * Mb);
        var bin = new RecycleBin(new FixedQuota(Mb));

        RecycleAssessment assessment = bin.Assess(file);
        RecycleOutcome outcome = bin.Recycle(file, allowUnverified: true);

        output.WriteLine($"assessment: {assessment}");
        Assert.Equal(RecycleVerdict.WouldBePermanent, assessment.Verdict);
        Assert.False(assessment.IsReversible);
        Assert.False(outcome.Recycled);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Huge_sparse_file_is_never_called_reversible_with_real_settings()
    {
        string path = _fixture.PathOf("huge.bin");
        File.WriteAllBytes(path, []);
        Ntfs.MakeSparse(path);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(1024L * 1024 * Mb);
        }

        RecycleAssessment assessment = new RecycleBin().Assess(path);

        output.WriteLine($"assessment: {assessment}");
        Assert.NotEqual(RecycleVerdict.Recyclable, assessment.Verdict);
        Assert.Equal(1024L * 1024 * Mb, assessment.ItemBytes);
    }

    [Fact]
    public void Recycling_a_junction_removes_the_link_and_keeps_the_target()
    {
        string kept = _fixture.CreateFile(@"target\keep.bin", 256 * 1024);
        string link = _fixture.PathOf("link");
        Ntfs.CreateJunction(link, _fixture.PathOf("target"));
        var bin = new RecycleBin(new FixedQuota(1024 * Mb));

        RecycleOutcome outcome = bin.Recycle(link);

        output.WriteLine($"junction: {outcome}");
        Assert.True(outcome.Recycled, outcome.Error);
        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(kept));
        Assert.Equal(256 * 1024, new FileInfo(kept).Length);
    }

    [Fact]
    public void Item_reached_through_a_junction_is_not_recycled()
    {
        string kept = _fixture.CreateFile(@"target\keep.bin", 4096);
        string link = _fixture.PathOf("link");
        Ntfs.CreateJunction(link, _fixture.PathOf("target"));
        var bin = new RecycleBin(new FixedQuota(1024 * Mb));

        RecycleOutcome outcome = bin.Recycle(Path.Combine(link, "keep.bin"), allowUnverified: true);

        output.WriteLine($"through junction: {outcome}");
        Assert.Equal(RecycleVerdict.NotAllowed, outcome.Assessment.Verdict);
        Assert.False(outcome.Recycled);
        Assert.True(File.Exists(kept));
    }

    private sealed class FixedQuota(long maxBytes) : IRecycleBinQuotaSource
    {
        public RecycleQuota GetQuota(VolumeInfo volume) => new(maxBytes, NukeOnDelete: false, "test");
    }
}
