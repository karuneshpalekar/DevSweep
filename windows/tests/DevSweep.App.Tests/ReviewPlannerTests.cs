using DevSweep.Core.Cleaning;
using DevSweep.Core.Platform;
using DevSweep.Core.Platform.Recycling;
using DevSweep.Core.Review;
using DevSweep.Core.Safety;
using Xunit;

namespace DevSweep.App.Tests;

/// <summary>Review groups items by what the cleaner will really do, using the cleaner's own checks.</summary>
public sealed class ReviewPlannerTests
{
    [Fact]
    public void Recyclable_items_go_to_the_Recycle_Bin_and_can_be_undone()
    {
        ReviewItem item = Plan(Trash(@"C:\Users\a\AppData\Local\Tool\Cache"));
        Assert.Equal(ReviewOutcome.RecycleBin, item.Outcome);
        Assert.True(item.CanBeUndone);
        Assert.Contains("Recycle Bin", item.Commands[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Items_the_bin_would_not_keep_go_to_the_quarantine_with_the_reason()
    {
        var probe = new FakeProbe { Recycle = RecycleVerdict.WouldBePermanent };
        ReviewItem item = Plan(Trash(@"C:\Users\a\Downloads\big.iso"), probe);
        Assert.Equal(ReviewOutcome.Quarantine, item.Outcome);
        Assert.True(item.CanBeUndone);
        Assert.Contains("too big", item.Notes[0], StringComparison.Ordinal);
        Assert.Contains(@"-> ""C:\Q""", item.Commands[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_bin_or_a_quarantine_the_item_is_blocked_not_deleted()
    {
        var probe = new FakeProbe { Recycle = RecycleVerdict.Unknown, QuarantineRoot = null };
        ReviewItem item = Plan(Trash(@"E:\stick\cache"), probe);
        Assert.Equal(ReviewOutcome.Blocked, item.Outcome);
        Assert.False(item.CanBeUndone);
    }

    [Fact]
    public void Delete_is_permanent_and_only_for_items_that_rebuild()
    {
        ReviewItem rebuilds = Plan(new PlannedAction(Finding(Risk.Rebuilds, @"C:\Users\a\.npm"), new CleanAction(CleanActionKind.Delete)));
        Assert.Equal(ReviewOutcome.DeleteForGood, rebuilds.Outcome);
        Assert.False(rebuilds.CanBeUndone);
        Assert.True(new ReviewPlan([rebuilds]).HasPermanentChanges);

        ReviewItem data = Plan(new PlannedAction(Finding(Risk.HoldsData, @"C:\Users\a\data"), new CleanAction(CleanActionKind.Delete)));
        Assert.Equal(ReviewOutcome.Blocked, data.Outcome);
    }

    [Fact]
    public void Files_in_use_and_running_blockers_block_with_a_close_first_message()
    {
        var locked = new FakeProbe { Locked = new LockReport([new LockingProcess(4120, "Visual Studio Code", "Code.exe", null)], 10, false) };
        ReviewItem item = Plan(Trash(@"C:\Users\a\AppData\Roaming\Code\Cache"), locked);
        Assert.Equal(ReviewOutcome.Blocked, item.Outcome);
        Assert.Contains("Visual Studio Code", item.Blockers[0], StringComparison.Ordinal);

        var running = new FakeProbe { RunningNames = ["chrome"] };
        Finding chrome = Finding(Risk.Rebuilds, @"C:\Users\a\Chrome\Cache") with { Blockers = ["chrome"] };
        ReviewItem blocked = Plan(new PlannedAction(chrome, new CleanAction(CleanActionKind.Delete)), running);
        Assert.Equal(ReviewOutcome.Blocked, blocked.Outcome);
        Assert.Equal("Close chrome first.", blocked.Blockers[0]);
    }

    [Fact]
    public void The_guard_refusing_a_path_blocks_the_whole_item()
    {
        var probe = new FakeProbe { Refuse = @"C:\Users\a\Documents" };
        ReviewItem item = Plan(new PlannedAction(Finding(Risk.Leftover, @"C:\Users\a\AppData\Roaming\X", @"C:\Users\a\Documents"), new CleanAction(CleanActionKind.Trash)), probe);
        Assert.Equal(ReviewOutcome.Blocked, item.Outcome);
        Assert.Contains("Protected", item.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Manual_and_command_actions_show_the_exact_command_line()
    {
        ReviewItem manual = Plan(new PlannedAction(Finding(Risk.NeedsAdmin, @"C:\Windows\WinSxS"), new CleanAction(CleanActionKind.Manual, Command: ["dism", "/Online", "/Cleanup-Image", "/StartComponentCleanup"])));
        Assert.Equal(ReviewOutcome.Manual, manual.Outcome);
        Assert.Equal("dism /Online /Cleanup-Image /StartComponentCleanup", manual.Commands[0]);

        ReviewItem command = Plan(new PlannedAction(Finding(Risk.Rebuilds, @"C:\x"), new CleanAction(CleanActionKind.Command, Command: ["docker", "builder", "prune", "--filter", "until=24h ago"])));
        Assert.Equal(ReviewOutcome.RunCommand, command.Outcome);
        Assert.Equal("docker builder prune --filter \"until=24h ago\"", command.Commands[0]);
        Assert.Empty(new ReviewPlan([manual]).Runnable);
        Assert.Single(new ReviewPlan([manual, command]).Runnable);
    }

    [Fact]
    public void Missing_paths_are_noted_and_an_empty_item_is_blocked()
    {
        var probe = new FakeProbe { Missing = @"C:\gone" };
        ReviewItem item = Plan(Trash(@"C:\gone"), probe);
        Assert.Equal(ReviewOutcome.Blocked, item.Outcome);
        Assert.Equal("Nothing is there any more.", item.Summary);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
    [InlineData("", "\"\"")]
    public void Command_lines_are_quoted_the_way_Windows_parses_them(string argument, string expected)
        => Assert.Equal(expected, CommandLineText.Quote(argument));

    private static PlannedAction Trash(string path) => new(Finding(Risk.Rebuilds, path), new CleanAction(CleanActionKind.Trash));

    private static Finding Finding(Risk risk, params string[] paths) => new() { Id = "x", Title = "X", Risk = risk, Paths = paths, Size = 100 };

    private static ReviewItem Plan(PlannedAction action, FakeProbe? probe = null) => new ReviewPlanner(probe ?? new FakeProbe()).PrepareOne(action);

    private sealed class FakeProbe : IReviewProbe
    {
        public RecycleVerdict Recycle { get; init; } = RecycleVerdict.Recyclable;

        public string? QuarantineRoot { get; init; } = @"C:\Q";

        public LockReport Locked { get; init; } = new([], 1, false);

        public IReadOnlyList<string> RunningNames { get; init; } = [];

        public string? Refuse { get; init; }

        public string? Missing { get; init; }

        public SafetyVerdict Check(string path)
            => path == Refuse ? new SafetyVerdict(path, SafetyRefusal.Protected, "Protected folder.")
                : path == Missing ? new SafetyVerdict(path, SafetyRefusal.Missing, "Missing.")
                : SafetyVerdict.Allow(path);

        public RecycleAssessment AssessRecycle(string path) => new(path, Recycle, Recycle == RecycleVerdict.Recyclable ? "Fits." : "It's too big for the Recycle Bin.", 100, 1000, DriveKind.Fixed);

        string? IReviewProbe.QuarantineRoot(string path, out string reason)
        {
            reason = "Removable drives have no quarantine.";
            return QuarantineRoot;
        }

        public LockReport Locks(string path) => Locked;

        public IReadOnlyList<string> Running(IReadOnlyList<string> names) => [.. names.Where(RunningNames.Contains)];
    }
}
