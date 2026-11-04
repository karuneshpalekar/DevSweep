using DevSweep.Core.Cleaning;
using DevSweep.Core.Platform;
using DevSweep.Core.Platform.Quarantine;
using DevSweep.Core.Platform.Recycling;
using DevSweep.Core.Safety;
using DevSweep.Fixtures;
using Xunit;

namespace DevSweep.Accuracy;

/// <summary>The cleaner end to end: blockers, delete, Recycle Bin and quarantine, commands, History, restore, freed space.</summary>
public sealed class CleanerTests : IDisposable
{
    private const long Mb = 1024 * 1024;
    private readonly ITestOutputHelper _output;
    private readonly TempFixture _fixture = new("clean");
    private readonly HistoryStore _history;
    private readonly QuarantineStore _quarantine;

    public CleanerTests(ITestOutputHelper output)
    {
        _output = output;
        _history = new HistoryStore(_fixture.PathOf("history.json"));
        _quarantine = new QuarantineStore(new FixedQuarantineLocations(_fixture.PathOf("quarantine")));
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Delete_frees_what_was_measured()
    {
        string cache = _fixture.PathOf(@"tool\cache");
        _fixture.CreateFile(@"tool\cache\a.bin", 48 * Mb);
        _fixture.CreateFile(@"tool\cache\sub\b.bin", 16 * Mb);
        File.SetAttributes(_fixture.CreateFile(@"tool\cache\sub\readonly.bin", 4096), FileAttributes.ReadOnly);
        _fixture.CreateFile(@"outside\keep.bin", 4096);
        Ntfs.CreateJunction(_fixture.PathOf(@"tool\cache\link"), _fixture.PathOf("outside"));
        WalkResult measured = Walk.Measure(cache);

        ActionOutcome outcome = await Run(Finding(cache, Risk.Rebuilds, measured.Bytes), CleanActionKind.Delete);

        FreedSpace freed = outcome.Reconciliation!;
        _output.WriteLine($"delete: {outcome.Message} shown={freed.ShownBytes:N0} expected={freed.ExpectedFreedBytes:N0} freed={freed.FreedBytes:N0} difference={freed.Difference:N0} tolerance={freed.Tolerance:N0}");
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.False(Directory.Exists(cache));
        Assert.True(File.Exists(_fixture.PathOf(@"outside\keep.bin")));
        Assert.Equal(ItemDestination.Deleted, Assert.Single(outcome.Items).Destination);
        Assert.Equal(measured.FreeableBytes, freed.ExpectedFreedBytes);
        Assert.True(freed.WithinTolerance, $"freed {freed.FreedBytes:N0} vs expected {freed.ExpectedFreedBytes:N0}");
        Assert.Equal(CleanActionKind.Delete, Assert.Single(_history.Entries).ActionKind);
    }

    [Fact]
    public async Task Delete_is_refused_for_anything_that_does_not_rebuild()
    {
        string data = _fixture.CreateFile(@"data\notes.db", 4096);

        ActionOutcome outcome = await Run(Finding(data, Risk.HoldsData, 4096), CleanActionKind.Delete);

        Assert.False(outcome.Succeeded);
        Assert.True(File.Exists(data));
        Assert.Empty(_history.Entries);
    }

    [Fact]
    public async Task Trash_goes_to_the_recycle_bin_and_history_restores_it()
    {
        string file = _fixture.CreateFile(@"bin\old-installer.msi", 256 * 1024);
        byte[] content = File.ReadAllBytes(file);
        var bin = new RecycleBin();
        RecycleAssessment assessment = bin.Assess(file);
        if (assessment.Verdict != RecycleVerdict.Recyclable)
        {
            Assert.Skip($"The Recycle Bin on this volume is not verifiably usable: {assessment.Reason}");
        }

        ActionOutcome outcome = await Run(Finding(file, Risk.OldVersion, 256 * 1024), CleanActionKind.Trash, bin);

        MovedItem moved = Assert.Single(outcome.Items);
        _output.WriteLine($"trash: {outcome.Message}; {moved.Destination} at {moved.Location} (record {moved.RecycleInfo}); reconciliation freed={outcome.Reconciliation!.FreedBytes:N0} expected={outcome.Reconciliation.ExpectedFreedBytes}");
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(ItemDestination.RecycleBin, moved.Destination);
        Assert.NotNull(moved.Location);
        Assert.True(File.Exists(moved.Location));
        Assert.False(File.Exists(file));
        Assert.Equal(0, outcome.Reconciliation.ExpectedFreedBytes);
        HistoryEntry entry = Assert.Single(new HistoryStore(_history.FilePath).Entries);
        Assert.True(entry.CanRestore);

        HistoryRestoreResult restored = new HistoryStore(_history.FilePath).Restore(entry.Id, _quarantine);

        _output.WriteLine($"restore: {string.Join(" | ", restored.Messages)}");
        Assert.True(restored.Succeeded, string.Join(" | ", restored.Messages));
        Assert.Equal(content, File.ReadAllBytes(file));
        Assert.False(File.Exists(moved.Location));
        Assert.False(File.Exists(moved.RecycleInfo));
        Assert.NotNull(new HistoryStore(_history.FilePath).Entries[0].RestoredAt);
    }

    [Fact]
    public async Task Trash_too_big_for_the_bin_goes_to_quarantine_and_says_so()
    {
        string folder = _fixture.PathOf(@"big\sdk");
        _fixture.CreateFile(@"big\sdk\image.bin", 3 * Mb);
        var tinyBin = new RecycleBin(new FixedQuota(Mb));

        ActionOutcome outcome = await Run(Finding(folder, Risk.OldVersion, 3 * Mb), CleanActionKind.Trash, tinyBin);

        MovedItem moved = Assert.Single(outcome.Items);
        _output.WriteLine($"quarantine: {outcome.Message}; {moved.Location}; note: {moved.Note}");
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(ItemDestination.Quarantine, moved.Destination);
        Assert.Contains("quarantine", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bigger", moved.Note, StringComparison.Ordinal);
        Assert.False(Directory.Exists(folder));

        HistoryRestoreResult restored = _history.Restore(outcome.HistoryId!.Value, _quarantine);

        Assert.True(restored.Succeeded, string.Join(" | ", restored.Messages));
        Assert.Equal(3 * Mb, new FileInfo(Path.Combine(folder, "image.bin")).Length);
    }

    [Fact]
    public async Task Trash_with_an_unknown_bin_limit_goes_to_quarantine_never_to_a_maybe()
    {
        string file = _fixture.CreateFile(@"unknown\x.bin", 4096);

        ActionOutcome outcome = await Run(Finding(file, Risk.OldVersion, 4096), CleanActionKind.Trash, new RecycleBin(new UnknownQuota()));

        MovedItem moved = Assert.Single(outcome.Items);
        Assert.Equal(ItemDestination.Quarantine, moved.Destination);
        Assert.Contains("could not be found", moved.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trash_with_a_path_too_long_for_the_shell_goes_to_quarantine()
    {
        string relative = Path.Combine("deep", Path.Combine([.. Enumerable.Repeat("a-folder-name-of-twenty", 13)]), "node_modules");
        string folder = _fixture.CreateDirectory(relative);
        File.WriteAllBytes(Path.Combine(folder, "x.js"), new byte[5000]);

        ActionOutcome outcome = await Run(Finding(folder, Risk.Rebuilds, 8192), CleanActionKind.Trash);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(ItemDestination.Quarantine, Assert.Single(outcome.Items).Destination);
    }

    [Fact]
    public async Task A_file_held_open_by_another_process_blocks_the_clean_and_names_it()
    {
        string folder = _fixture.PathOf(@"locked\cache");
        string held = _fixture.CreateFile(@"locked\cache\in-use.db", 64 * 1024);
        _fixture.CreateFile(@"locked\cache\other.bin", 4096);
        using FileHolder holder = FileHolder.Start(held);

        LockReport locks = FileLocks.Check(folder);
        ActionOutcome outcome = await Run(Finding(folder, Risk.Rebuilds, 0), CleanActionKind.Delete);

        _output.WriteLine($"locks: {string.Join(", ", locks.Processes)} (checked {locks.FilesChecked} files); cleaner: {outcome.Message}");
        LockingProcess process = Assert.Single(locks.Processes);
        Assert.Equal(holder.ProcessId, process.ProcessId);
        Assert.False(outcome.Succeeded);
        Assert.StartsWith("Close ", outcome.Message, StringComparison.Ordinal);
        Assert.Contains(holder.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), outcome.Message, StringComparison.Ordinal);
        Assert.True(outcome.NothingChanged);
        Assert.True(File.Exists(held));
        Assert.True(File.Exists(_fixture.PathOf(@"locked\cache\other.bin")));

        holder.Dispose();
        Assert.False(FileLocks.Check(folder).IsLocked);
    }

    [Fact]
    public async Task A_named_blocker_that_is_running_stops_the_clean()
    {
        string file = _fixture.CreateFile(@"blocked\x.bin", 4096);
        string self = System.Diagnostics.Process.GetCurrentProcess().ProcessName;

        ActionOutcome outcome = await Run(Finding(file, Risk.Rebuilds, 4096) with { Blockers = [self] }, CleanActionKind.Delete);

        Assert.Equal($"Close {self} first.", outcome.Message);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Protected_and_outside_paths_are_refused_before_anything_happens()
    {
        string ok = _fixture.CreateFile(@"mix\ok.bin", 4096);

        ActionOutcome outcome = await Run(Finding(ok, Risk.Rebuilds, 4096) with { Paths = [ok, KnownFolders.LocalAppData] }, CleanActionKind.Delete);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.NothingChanged);
        Assert.True(File.Exists(ok));
    }

    [Fact]
    public async Task Commands_run_without_a_shell_and_are_stopped_at_their_time_limit()
    {
        string needle = _fixture.PathOf("needle.txt");
        File.WriteAllText(needle, "a line with \"quotes\" & ampersands | pipes");
        var cleaner = NewCleaner(new CleanerOptions { CommandTimeout = TimeSpan.FromSeconds(2) });
        var finding = Finding(_fixture.Root, Risk.Rebuilds, 0);

        ActionOutcome found = await Perform(cleaner, finding, new CleanAction(CleanActionKind.Command, Command: ["findstr.exe", "/L", "/c:\"quotes\" & ampersands | pipes", needle]));
        ActionOutcome timedOut = await Perform(cleaner, finding, new CleanAction(CleanActionKind.Command, Command: ["ping.exe", "-n", "30", "127.0.0.1"]));
        ActionOutcome missing = await Perform(cleaner, finding, new CleanAction(CleanActionKind.Command, Command: ["no-such-program-devsweep.exe"]));
        ActionOutcome manual = await Perform(cleaner, finding, new CleanAction(CleanActionKind.Manual, Command: ["dism.exe", "/online"]));

        _output.WriteLine($"findstr: exit={found.Command?.ExitCode} out={found.Command?.StandardOutput.Trim()}; ping: {timedOut.Message} after {timedOut.Command?.Elapsed.TotalSeconds:0.0}s; missing: {missing.Message}; manual: {manual.Message}");
        Assert.True(found.Succeeded, found.Message);
        Assert.Contains("ampersands", found.Command!.StandardOutput, StringComparison.Ordinal);
        Assert.False(timedOut.Succeeded);
        Assert.True(timedOut.Command!.TimedOut);
        Assert.InRange(timedOut.Command.Elapsed.TotalSeconds, 1.5, 20);
        Assert.False(missing.Succeeded);
        Assert.False(manual.Succeeded);
        Assert.True(manual.NothingChanged);
        HistoryEntry entry = Assert.Single(_history.Entries);
        Assert.Equal(CleanActionKind.Command, entry.ActionKind);
        Assert.Contains("ampersands", entry.CommandOutput, StringComparison.Ordinal);
    }

    private static Finding Finding(string path, Risk risk, long size)
        => new() { Id = Path.GetFileName(path), Title = Path.GetFileName(path), Risk = risk, Paths = [path], Size = size };

    private Cleaner NewCleaner(CleanerOptions? options = null, RecycleBin? bin = null)
        => new(SafetyGuard.ForCurrentUser(), _history, _quarantine, bin ?? new RecycleBin(), options);

    private Task<ActionOutcome> Run(Finding finding, CleanActionKind kind, RecycleBin? bin = null)
        => Perform(NewCleaner(bin: bin), finding, new CleanAction(kind));

    private static Task<ActionOutcome> Perform(Cleaner cleaner, Finding finding, CleanAction action)
        => cleaner.PerformAsync(new PlannedAction(finding, action), TestContext.Current.CancellationToken);

    private sealed class UnknownQuota : IRecycleBinQuotaSource
    {
        public RecycleQuota GetQuota(VolumeInfo volume) => new(null, NukeOnDelete: false, "test: no setting");
    }

    private sealed class FixedQuota(long maxBytes) : IRecycleBinQuotaSource
    {
        public RecycleQuota GetQuota(VolumeInfo volume) => new(maxBytes, NukeOnDelete: false, "test");
    }
}
