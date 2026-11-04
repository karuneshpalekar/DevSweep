using DevSweep.Core.Platform;
using Xunit;

namespace DevSweep.Core.Tests.Platform;

public sealed class SizeConfidenceTests
{
    [Theory]
    [InlineData(true, 0, 0, 0, 0, SizeConfidence.Exact)]
    [InlineData(true, 0, 0, 0, 4096, SizeConfidence.SharedLinks)]
    [InlineData(true, 0, 0, 3, 4096, SizeConfidence.CloudPlaceholders)]
    [InlineData(true, 0, 1, 3, 4096, SizeConfidence.AccessDenied)]
    [InlineData(false, 0, 1, 3, 4096, SizeConfidence.Partial)]
    [InlineData(true, 2, 0, 0, 0, SizeConfidence.Partial)]
    public void Most_serious_reason_wins(bool complete, long unreadable, long denied, long placeholders, long shared, SizeConfidence expected)
        => Assert.Equal(expected, SizeConfidenceRules.From(complete, unreadable, denied, placeholders, shared));

    [Fact]
    public void Freeable_bytes_exclude_shared_links()
    {
        var result = new WalkResult { Path = @"C:\x", Bytes = 10_000, SharedLinkBytes = 4_096, Complete = true };
        Assert.Equal(5_904, result.FreeableBytes);
        Assert.Equal(SizeConfidence.SharedLinks, result.Confidence);
    }
}
