using DevSweep.Core.Cleaning;
using DevSweep.Core.Platform;
using DevSweep.Core.Platform.Quarantine;
using DevSweep.Core.Platform.Recycling;
using DevSweep.Core.Safety;
using DevSweep.Fixtures;
using Xunit;

namespace DevSweep.Accuracy;

/// <summary>
/// Real Cloud Files placeholders from a test sync provider: counted as 0 bytes,
/// flagged, never hydrated (no FETCH_DATA request reaches the provider, and the
/// filter still reports no data on disk afterwards); provider folders are
/// entered, reading attributes only; removal is refused while cloud-only files are inside.
/// </summary>
public sealed class CloudFilterTests(ITestOutputHelper output) : IDisposable
{
    private const long Mb = 1024 * 1024;
    private readonly TempFixture _fixture = new("cloud");
    private CloudSyncRoot? _root;

    public void Dispose()
    {
        _root?.Dispose();
        _fixture.Dispose();
    }

    [Fact]
    public void Dehydrated_placeholders_count_as_zero_and_are_never_hydrated()
    {
        CloudSyncRoot root = Connect();
        root.CreateFilePlaceholders(string.Empty, [("cloud-a.bin", 8 * Mb), ("cloud-b.bin", 3 * Mb)]);
        root.CreateFolderPlaceholder(string.Empty, "Synced Folder", populated: true);
        root.CreateFilePlaceholders("Synced Folder", [("deep-cloud.bin", 5 * Mb)]);
        string local = _fixture.CreateFile(@"sync\Synced Folder\local.bin", 256 * 1024);
        CloudSyncRoot.ConvertToPlaceholder(local);
        long localAllocation = FileInspector.Inspect(local).AllocationSize;
        string[] placeholders = [Path.Combine(root.Root, "cloud-a.bin"), Path.Combine(root.Root, "cloud-b.bin"), Path.Combine(root.Root, "Synced Folder", "deep-cloud.bin")];
        foreach (string path in placeholders.Append(local).Append(Path.Combine(root.Root, "Synced Folder")))
        {
            DirectoryEntry entry = FileInspector.ReadEntry(path);
            output.WriteLine($"before: {Path.GetFileName(path)} attributes={entry.Attributes} tag=0x{entry.ReparseTag:X8} kind={entry.ReparseKind} cloud={entry.CloudState}");
        }

        WalkResult v2 = Walk.Measure(root.Root);
        WalkResult fast = Walk.Measure(root.Root, new WalkOptions { AlternateStreams = false });
        WalkResult fallback = Walk.Measure(root.Root, new WalkOptions { Listing = ListingMethod.FindFirstFile });

        foreach (WalkResult r in new[] { v2, fast, fallback })
        {
            output.WriteLine($"walk {r.Listing}: bytes={r.Bytes} files={r.Files} placeholders={r.CloudPlaceholderCount} ({r.CloudPlaceholderLogicalBytes} in the cloud) cloud folders={r.CloudDirectories} confidence={r.Confidence} reparse={r.ReparsePointCount}");
            Assert.Equal(3, r.CloudPlaceholderCount);
            Assert.Equal(16 * Mb, r.CloudPlaceholderLogicalBytes);
            Assert.Equal(localAllocation, r.Bytes);
            Assert.Equal(1, r.Files);
            Assert.Equal(SizeConfidence.CloudPlaceholders, r.Confidence);
            Assert.Equal(0, r.ReparsePointCount);
        }

        foreach (string path in placeholders)
        {
            DirectoryEntry entry = FileInspector.ReadEntry(path);
            PlaceholderInfo info = CloudSyncRoot.GetInfo(path);
            output.WriteLine($"after: {Path.GetFileName(path)} attributes={entry.Attributes} on-disk data={info.OnDiskDataSize} pin={info.PinState} in-sync={info.InSync}");
            Assert.Equal(CloudFileState.Placeholder, entry.CloudState);
            Assert.Equal(0, info.OnDiskDataSize);
        }

        output.WriteLine($"provider requests: fetch-data={root.FetchDataRequests} fetch-placeholders={root.FetchPlaceholderRequests}");
        Assert.Equal(0, root.FetchDataRequests);
        Assert.Equal(0, root.FetchPlaceholderRequests);
    }

    [Fact]
    public async Task A_folder_holding_cloud_only_files_is_not_removed()
    {
        CloudSyncRoot root = Connect();
        root.CreateFolderPlaceholder(string.Empty, "cache", populated: true);
        root.CreateFilePlaceholders("cache", [("only-in-cloud.bin", 2 * Mb)]);
        string target = Path.Combine(root.Root, "cache");
        string quarantine = _fixture.PathOf("quarantine");
        var history = new HistoryStore(_fixture.PathOf("history.json"));
        var cleaner = new Cleaner(SafetyGuard.ForCurrentUser(), history, new QuarantineStore(new FixedQuarantineLocations(quarantine)), new RecycleBin());
        var finding = new Finding { Id = "cloud", Title = "cloud cache", Risk = Risk.Rebuilds, Paths = [target], Size = 0 };

        ActionOutcome outcome = await cleaner.PerformAsync(new PlannedAction(finding, new CleanAction(CleanActionKind.Delete)), TestContext.Current.CancellationToken);

        output.WriteLine($"outcome: {outcome.Message}");
        Assert.False(outcome.Succeeded);
        Assert.True(outcome.NothingChanged);
        Assert.Contains("cloud", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(target, "only-in-cloud.bin")));
        Assert.Equal(0, root.FetchDataRequests);
        Assert.Empty(history.Entries);
    }

    private CloudSyncRoot Connect()
    {
        _root = CloudSyncRoot.TryCreate(_fixture.PathOf("sync"), out string reason);
        if (_root is null)
        {
            Assert.Skip($"{reason} ({Elevation.Describe()})");
        }

        return _root;
    }
}
