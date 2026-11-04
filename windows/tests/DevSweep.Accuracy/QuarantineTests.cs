using DevSweep.Core.Platform;
using DevSweep.Core.Platform.Quarantine;
using DevSweep.Core.Safety;
using DevSweep.Fixtures;
using Xunit;

namespace DevSweep.Accuracy;

/// <summary>Quarantine: same-volume rename in, exact restore out, listing, emptying.</summary>
[Collection(AloneOnTheVolume.Name)]
public sealed class QuarantineTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempFixture _fixture = new("quarantine");
    private readonly SafetyGuard _guard = SafetyGuard.ForCurrentUser();

    public void Dispose() => _fixture.Dispose();

    private QuarantineStore Store => new(new FixedQuarantineLocations(_fixture.PathOf("q")));

    [Fact]
    public void A_folder_goes_in_by_rename_and_comes_back_exactly()
    {
        string folder = _fixture.PathOf(@"project\node_modules");
        _fixture.CreateFile(@"project\node_modules\pkg\index.js", 50_000);
        _fixture.CreateFile(@"project\node_modules\pkg\big.bin", 2 * 1024 * 1024);
        Ntfs.CreateHardLink(_fixture.PathOf(@"project\node_modules\pkg\index-link.js"), _fixture.PathOf(@"project\node_modules\pkg\index.js"));
        WalkResult before = Walk.Measure(folder);
        long free = Volumes.FreeBytes(folder);

        QuarantineResult moved = Move(folder);
        long freeAfterMove = Volumes.FreeBytes(folder);

        output.WriteLine($"moved: {moved.Message}; volume free {free:N0} -> {freeAfterMove:N0}");
        Assert.True(moved.Moved, moved.Message);
        Assert.False(Directory.Exists(folder));
        QuarantineEntry entry = Assert.Single(Store.List());
        Assert.Equal(before.Bytes, entry.Bytes);
        Assert.True(entry.IsDirectory);
        Assert.InRange(entry.AgeAt(DateTimeOffset.UtcNow), TimeSpan.Zero, TimeSpan.FromMinutes(5));
        Assert.Equal(before.Bytes, Walk.Measure(entry.StoredPath).Bytes);
        Assert.InRange(Math.Abs(freeAfterMove - free), 0, 10 * 1024 * 1024); // a rename frees and costs nothing

        QuarantineRestoreResult restored = Store.Restore(entry.Id);

        Assert.True(restored.Restored, restored.Message);
        WalkResult after = Walk.Measure(folder);
        Assert.Equal(before.Bytes, after.Bytes);
        Assert.Equal(before.Files, after.Files);
        Assert.Equal(before.HardLinkDuplicates, after.HardLinkDuplicates);
        Assert.Empty(Store.List());
        Assert.Empty(Directory.EnumerateFileSystemEntries(_fixture.PathOf("q")));
    }

    [Fact]
    public void Restore_recreates_missing_parents()
    {
        string file = _fixture.CreateFile(@"gone\deeper\file.bin", 4096);
        QuarantineResult moved = Move(file);
        Directory.Delete(_fixture.PathOf("gone"), recursive: true);

        QuarantineRestoreResult restored = Store.Restore(moved.Entry!.Id);

        Assert.True(restored.Restored, restored.Message);
        Assert.Equal(4096, new FileInfo(file).Length);
    }

    [Fact]
    public void Restore_refuses_when_something_now_exists_there()
    {
        string file = _fixture.CreateFile(@"taken\file.bin", 4096);
        QuarantineResult moved = Move(file);
        File.WriteAllBytes(file, [9, 9, 9]);

        QuarantineRestoreResult restored = Store.Restore(moved.Entry!.Id);

        output.WriteLine(restored.Message);
        Assert.False(restored.Restored);
        Assert.Equal([9, 9, 9], File.ReadAllBytes(file));
        Assert.Single(Store.List());
        Assert.True(File.Exists(moved.Entry.StoredPath));
    }

    [Fact]
    public void Restore_refuses_when_the_way_back_goes_through_a_junction()
    {
        string file = _fixture.CreateFile(@"was\file.bin", 4096);
        QuarantineResult moved = Move(file);
        Directory.Delete(_fixture.PathOf("was"));
        _fixture.CreateDirectory("elsewhere");
        Ntfs.CreateJunction(_fixture.PathOf("was"), _fixture.PathOf("elsewhere"));

        QuarantineRestoreResult restored = Store.Restore(moved.Entry!.Id);

        Assert.False(restored.Restored);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_fixture.PathOf("elsewhere")));
    }

    [Fact]
    public void A_path_too_long_for_the_recycle_bin_can_be_quarantined()
    {
        string relative = Path.Combine("long", Path.Combine([.. Enumerable.Repeat("a-folder-name-of-twenty", 14)]), "leaf.bin");
        string deep = _fixture.CreateFile(relative, 10_000);
        Assert.True(deep.Length > 300);

        QuarantineResult moved = Move(Path.GetDirectoryName(deep)!);

        Assert.True(moved.Moved, moved.Message);
        Assert.True(Store.Restore(moved.Entry!.Id).Restored);
        Assert.True(File.Exists(deep));
    }

    [Fact]
    public void Empty_deletes_for_good_and_frees_the_space()
    {
        string old = _fixture.CreateFile(@"e\old.bin", 8 * 1024 * 1024);
        string recent = _fixture.CreateFile(@"e\recent.bin", 4096);
        QuarantineResult a = Move(old);
        QuarantineResult b = Move(recent);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long free = Volumes.FreeBytes(old);

        var none = Store.Empty(TimeSpan.FromDays(1), now);
        var all = Store.Empty(TimeSpan.Zero, now.AddDays(2));
        long freed = Volumes.FreeBytes(old) - free;

        output.WriteLine($"emptied {all.Count} items, volume freed {freed:N0} bytes for {a.Entry!.Bytes + b.Entry!.Bytes:N0} quarantined");
        Assert.Empty(none);
        Assert.Equal(2, all.Count);
        Assert.All(all, r => Assert.True(r.Report.Succeeded));
        Assert.Empty(Store.List());
        Assert.False(File.Exists(a.Entry.StoredPath));
        Assert.InRange(freed, (8 * 1024 * 1024) - (10 * 1024 * 1024), (8 * 1024 * 1024) + (10 * 1024 * 1024));
    }

    [Fact]
    public void The_default_quarantine_is_in_local_app_data_on_the_same_volume()
    {
        string file = _fixture.CreateFile(@"default\file.bin", 4096);
        var store = new QuarantineStore();

        Assert.True(_guard.TryOpen(file, out VerifiedItem? item).Allowed);
        QuarantineResult moved;
        using (item)
        {
            moved = store.Move(item!, Walk.Measure(file));
        }

        try
        {
            output.WriteLine($"default quarantine: {moved.Entry?.StoredPath}");
            Assert.True(moved.Moved, moved.Message);
            Assert.StartsWith(DefaultQuarantineLocations.ProfileRoot, moved.Entry!.StoredPath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Volumes.ForPath(file).SerialNumber, Volumes.ForPath(moved.Entry.StoredPath).SerialNumber);
            Assert.Contains(store.List(), e => e.Id == moved.Entry.Id);
        }
        finally
        {
            if (moved.Entry is { } entry)
            {
                Assert.True(store.Restore(entry.Id).Restored);
            }
        }

        Assert.True(File.Exists(file));
    }

    private QuarantineResult Move(string path)
    {
        SafetyVerdict verdict = _guard.TryOpen(path, out VerifiedItem? item);
        Assert.True(verdict.Allowed, verdict.Reason);
        using (item)
        {
            return Store.Move(item!, Walk.Measure(path), "test");
        }
    }
}
