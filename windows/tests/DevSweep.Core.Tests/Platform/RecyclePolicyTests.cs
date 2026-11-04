using DevSweep.Core.Platform;
using DevSweep.Core.Platform.Recycling;
using Xunit;

namespace DevSweep.Core.Tests.Platform;

public sealed class RecyclePolicyTests
{
    private const long Mb = 1024 * 1024;
    private static readonly RecycleQuota Known = new(100 * Mb, false, "test");

    private static RecycleInputs Inputs(
        DriveKind drive = DriveKind.Fixed,
        int pathLength = 40,
        string? ancestor = null,
        bool complete = true,
        long bytes = Mb,
        RecycleQuota? quota = null)
        => new(drive, pathLength, ancestor, complete, bytes, quota ?? Known);

    [Fact]
    public void Fits_on_a_fixed_drive_is_recyclable()
        => Assert.Equal(RecycleVerdict.Recyclable, RecyclePolicy.Evaluate(Inputs()).Verdict);

    [Fact]
    public void Exactly_the_quota_still_fits()
        => Assert.Equal(RecycleVerdict.Recyclable, RecyclePolicy.Evaluate(Inputs(bytes: 100 * Mb)).Verdict);

    [Fact]
    public void Over_quota_would_be_permanent()
    {
        (RecycleVerdict verdict, string reason) = RecyclePolicy.Evaluate(Inputs(bytes: (100 * Mb) + 1));
        Assert.Equal(RecycleVerdict.WouldBePermanent, verdict);
        Assert.Contains("bigger", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DriveKind.Removable)]
    [InlineData(DriveKind.Remote)]
    [InlineData(DriveKind.CdRom)]
    [InlineData(DriveKind.RamDisk)]
    [InlineData(DriveKind.Unknown)]
    public void Drives_without_a_bin_would_be_permanent(DriveKind drive)
        => Assert.Equal(RecycleVerdict.WouldBePermanent, RecyclePolicy.Evaluate(Inputs(drive: drive)).Verdict);

    [Fact]
    public void Bin_turned_off_would_be_permanent()
        => Assert.Equal(RecycleVerdict.WouldBePermanent,
            RecyclePolicy.Evaluate(Inputs(quota: new RecycleQuota(null, true, "test"))).Verdict);

    [Fact]
    public void Unknown_quota_never_claims_reversibility()
        => Assert.Equal(RecycleVerdict.Unknown,
            RecyclePolicy.Evaluate(Inputs(quota: new RecycleQuota(null, false, "test"))).Verdict);

    [Fact]
    public void Incomplete_measurement_is_unknown()
        => Assert.Equal(RecycleVerdict.Unknown, RecyclePolicy.Evaluate(Inputs(complete: false)).Verdict);

    [Fact]
    public void Going_through_a_junction_is_not_allowed_even_when_it_fits()
        => Assert.Equal(RecycleVerdict.NotAllowed, RecyclePolicy.Evaluate(Inputs(ancestor: @"C:\link")).Verdict);

    [Fact]
    public void Paths_too_long_for_the_shell_are_not_allowed()
        => Assert.Equal(RecycleVerdict.NotAllowed,
            RecyclePolicy.Evaluate(Inputs(pathLength: RecyclePolicy.MaxShellPathLength + 1)).Verdict);

    [Theory]
    [InlineData(@"\\?\Volume{3f1c2b4a-0000-0000-0000-100000000000}\", "{3f1c2b4a-0000-0000-0000-100000000000}")]
    [InlineData(null, null)]
    [InlineData("garbage", null)]
    public void Parses_volume_guid(string? path, string? expected)
        => Assert.Equal(expected, VolumeInfo.ParseVolumeGuid(path));
}
