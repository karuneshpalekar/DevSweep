using DevSweep.Core.Platform;
using Xunit;

namespace DevSweep.Core.Tests.Platform;

public sealed class HardLinkLedgerTests
{
    private static FileFacts Facts(UInt128 id, uint links, long allocation = 4096)
        => new(allocation, allocation, links, new FileIdentity(1, id));

    [Fact]
    public void Single_link_files_are_always_new()
    {
        var ledger = new HardLinkLedger();
        Assert.True(ledger.Register(Facts(1, 1)));
        Assert.True(ledger.Register(Facts(1, 1)));
        Assert.Equal((0L, 0L), ledger.SharedOutside());
    }

    [Fact]
    public void All_links_inside_the_walk_are_counted_once_and_not_shared()
    {
        var ledger = new HardLinkLedger();
        Assert.True(ledger.Register(Facts(7, 3)));
        Assert.False(ledger.Register(Facts(7, 3)));
        Assert.False(ledger.Register(Facts(7, 3)));
        Assert.Equal((0L, 0L), ledger.SharedOutside());
    }

    [Fact]
    public void Links_seen_fewer_times_than_link_count_are_shared_outside()
    {
        var ledger = new HardLinkLedger();
        ledger.Register(Facts(7, 2, 8192));
        ledger.Register(Facts(9, 3, 4096));
        ledger.Register(Facts(9, 3, 4096));
        ledger.Register(Facts(9, 3, 4096));
        Assert.Equal((1L, 8192L), ledger.SharedOutside());
    }

    [Fact]
    public void Same_file_id_on_another_volume_is_a_different_file()
    {
        var ledger = new HardLinkLedger();
        Assert.True(ledger.Register(new FileFacts(1, 1, 2, new FileIdentity(1, 5))));
        Assert.True(ledger.Register(new FileFacts(1, 1, 2, new FileIdentity(2, 5))));
    }
}
