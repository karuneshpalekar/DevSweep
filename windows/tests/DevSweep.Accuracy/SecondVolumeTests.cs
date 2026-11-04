using DevSweep.Core.Platform;
using DevSweep.Core.Platform.Quarantine;
using DevSweep.Core.Safety;
using DevSweep.Fixtures;
using Xunit;

namespace DevSweep.Accuracy;

/// <summary>A 64 MB exFAT volume mounted in a folder under %TEMP%, shared by the tests in <see cref="SecondVolumeTests"/>.</summary>
public sealed class ExFatVolume : IDisposable
{
    private readonly TempFixture _fixture = new("exfat");

    public ExFatVolume()
    {
        Disk = VirtualDisk.TryCreate(_fixture.PathOf("disk"), "exfat", out string reason);
        SkipReason = reason;
    }

    public VirtualDisk? Disk { get; }

    public string SkipReason { get; }

    public string Mount => Disk?.MountPath ?? throw new InvalidOperationException(SkipReason);

    public void Dispose()
    {
        Disk?.Dispose();
        _fixture.Dispose();
    }
}

/// <summary>
/// A second volume, exFAT, mounted in a folder: the walker's fallback listing
/// (exFAT has no FileIdExtdDirectoryInfo), a volume mount point that is not
/// treated as a junction yet never crossed, and same-volume-only quarantine.
/// </summary>
public sealed class SecondVolumeTests(ExFatVolume volume, ITestOutputHelper output) : IClassFixture<ExFatVolume>
{
    private string Mount
    {
        get
        {
            if (volume.Disk is null)
            {
                Assert.Skip(volume.SkipReason);
            }

            return volume.Mount;
        }
    }

    [Fact]
    public void ExFat_folders_fall_back_to_FindFirstFile_and_sizes_still_match()
    {
        string root = Directory.CreateDirectory(Path.Combine(Mount, "fallback", "sub")).Parent!.FullName;
        string[] files =
        [
            Write(Path.Combine(root, "a.bin"), 100_000),
            Write(Path.Combine(root, "sub", "b.bin"), 5000),
            Write(Path.Combine(root, "sub", "c.bin"), 1),
        ];
        VolumeInfo info = Volumes.ForPath(root);
        long expected = files.Sum(f => FileInspector.Inspect(f).AllocationSize);

        WalkResult v2 = Walk.Measure(root);
        WalkResult fast = Walk.Measure(root, new WalkOptions { AlternateStreams = false });

        output.WriteLine($"volume: {info}");
        output.WriteLine($"exFAT walk: listing={v2.Listing} bytes={v2.Bytes} expected={expected} files={v2.Files} dirs={v2.Directories} problems={string.Join(", ", v2.Problems)}; no-streams bytes={fast.Bytes}");
        Assert.Equal("exFAT", info.FileSystem, ignoreCase: true);
        Assert.Equal(ListingUsed.FindFirstFile, v2.Listing);
        Assert.Equal(3, v2.Files);
        Assert.Equal(2, v2.Directories);
        Assert.Equal(expected, v2.Bytes);
        Assert.Equal(expected, fast.Bytes);
        Assert.Equal(SizeConfidence.Exact, v2.Confidence);
        Assert.All(files, f => Assert.Equal(0, FileInspector.Inspect(f).AlternateStreams));
    }

    [Fact]
    public void A_volume_mounted_in_a_folder_is_recorded_and_never_entered()
    {
        Write(Path.Combine(Mount, "inside-the-mount.bin"), 3 * 1024 * 1024);
        string parent = Path.GetDirectoryName(Mount)!;

        WalkResult result = Walk.Measure(parent);

        output.WriteLine($"parent of mount: bytes={result.Bytes} files={result.Files} reparse={string.Join(", ", result.ReparsePoints.Select(r => $"{r.Kind} {r.Path}"))}");
        ReparsePointRecord mount = Assert.Single(result.ReparsePoints, r => string.Equals(r.Path, Mount, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ReparseTags.MountPoint, mount.Tag);
        // The only file counted is the virtual disk's own .vhdx beside the mount folder.
        string vhdx = Assert.Single(Directory.GetFiles(parent, "*.vhdx"));
        Assert.Equal(1, result.Files);
        Assert.Equal(FileInspector.Inspect(vhdx).AllocationSize, result.Bytes);
        Assert.True(ReparseGuard.IsVolumeMountPoint(Mount));
    }

    [Fact]
    public void Items_on_a_mounted_volume_are_not_blocked_but_junctions_still_are()
    {
        string item = Write(Path.Combine(Mount, "guard", "item.bin"), 4096);
        string junction = Path.Combine(Path.GetDirectoryName(Mount)!, "junction-to-mount");
        Ntfs.CreateJunction(junction, Path.Combine(Mount, "guard"));
        SafetyGuard guard = SafetyGuard.ForCurrentUser();

        string? ancestor = ReparseGuard.FindReparseAncestor(item);
        SafetyVerdict direct = guard.Check(item);
        SafetyVerdict viaJunction = guard.Check(Path.Combine(junction, "item.bin"));

        output.WriteLine($"mounted item: ancestor={ancestor ?? "none"}; guard={direct.Refusal} ({direct.Reason}); via junction={viaJunction.Refusal}");
        Assert.Null(ancestor);
        Assert.True(direct.Allowed, direct.Reason);
        Assert.Equal(SafetyRefusal.ThroughLink, viaJunction.Refusal);
        Directory.Delete(junction);
    }

    [Fact]
    public void Quarantine_is_same_volume_only()
    {
        string item = Write(Path.Combine(Mount, "q", "keep.bin"), 64 * 1024);
        SafetyGuard guard = SafetyGuard.ForCurrentUser();
        using var systemSide = new TempFixture("q-system");

        // A quarantine set up only on the system volume: refused, nothing copied.
        Assert.True(guard.TryOpen(item, out VerifiedItem? first).Allowed);
        QuarantineResult refused;
        using (first)
        {
            refused = new QuarantineStore(new FixedQuarantineLocations(systemSide.Root)).Move(first!, Walk.Measure(item));
        }

        // A quarantine on the mounted volume itself: an instant rename, and back.
        Assert.True(guard.TryOpen(item, out VerifiedItem? second).Allowed);
        var store = new QuarantineStore(new FixedQuarantineLocations(Path.Combine(Mount, "quarantine")));
        QuarantineResult moved;
        using (second)
        {
            moved = store.Move(second!, Walk.Measure(item));
        }

        output.WriteLine($"other volume: {refused.Message}; same volume: {moved.Message}");
        Assert.False(refused.Moved);
        Assert.Contains("volume", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(systemSide.Root));
        Assert.True(moved.Moved, moved.Message);
        Assert.False(File.Exists(item));
        Assert.True(store.Restore(moved.Entry!.Id).Restored);
        Assert.Equal(64 * 1024, new FileInfo(item).Length);
    }

    private static string Write(string path, int length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }
}
