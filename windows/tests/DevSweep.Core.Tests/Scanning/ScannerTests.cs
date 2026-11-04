using DevSweep.Core.Cleaning;
using DevSweep.Core.Detectors;
using DevSweep.Core.Inventory;
using DevSweep.Core.Platform;
using DevSweep.Core.Rules;
using DevSweep.Core.Scanning;
using Xunit;
using static DevSweep.Core.Tests.Scanning.ScanFakes;

namespace DevSweep.Core.Tests.Scanning;

public sealed class ScannerTests
{
    private readonly FakeMeasurer _measurer = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_path_belongs_to_the_first_rule_and_paths_inside_it_are_dropped()
    {
        _measurer.Set(P("x"), 10 * Mb);
        _measurer.Set(P("y"), 5 * Mb);
        _measurer.Set(P("x", "sub"), 2 * Mb);
        Scanner scanner = Scanner(_measurer, null, null,
            Rule("first", Finds(Item("a", P("x")))),
            Rule("second", Finds(Item("b", P("x"), P("y"), P("x", "sub")), Item("c", P("x", "sub")))));

        ScanResult result = await scanner.ScanAsync(cancellationToken: Token);

        Assert.Equal(2, result.Findings.Count);
        Finding second = Assert.Single(result.Findings, f => f.RuleId == "second");
        Assert.Equal([P("y")], second.Paths);
        Assert.Equal(15 * Mb, result.Report.TotalBytes);
        Assert.Equal(1, result.Report.Checks.Single(c => c.Id == "second").Found);
    }

    [Fact]
    public async Task A_later_folder_containing_an_earlier_item_is_kept_and_counted_once()
    {
        _measurer.Set(P("app", "cache"), 10 * Mb);
        _measurer.Set(P("app"), 30 * Mb);
        Scanner scanner = Scanner(_measurer, null, null,
            Rule("cache", Finds(Item("c", P("app", "cache")))),
            Rule("leftover", Finds(Item("l", P("app"))), risk: Risk.Leftover, title: "App leftovers"));

        ScanResult result = await scanner.ScanAsync(cancellationToken: Token);

        Assert.Equal(2, result.Findings.Count);
        Assert.Equal(30 * Mb, result.Report.TotalBytes);
        Assert.Equal(10 * Mb, result.Report.NestedBytes);
        Finding leftover = result.Findings.Single(f => f.RuleId == "leftover");
        Assert.Contains(leftover.Checks, c => c.Status == CheckStatus.Warning && c.Text.Contains("also listed as \"Thing\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hard_links_shared_between_findings_are_counted_once_in_the_total()
    {
        _measurer.Set(P("store"), 20 * Mb, null, Link(1, 4 * Mb, links: 2, seen: 1));
        _measurer.Set(P("project", "node_modules"), 6 * Mb, null, Link(1, 4 * Mb, links: 2, seen: 1));
        Scanner scanner = Scanner(_measurer, null, null,
            Rule("store", Finds(Item("s", P("store")))),
            Rule("modules", Finds(Item("m", P("project", "node_modules")))));

        ScanResult result = await scanner.ScanAsync(cancellationToken: Token);

        Assert.Equal(22 * Mb, result.Report.TotalBytes);
        Assert.Equal(4 * Mb, result.Report.SharedBetweenFindingsBytes);
        Assert.All(result.Findings, f => Assert.Equal(4 * Mb, f.SharedWithOtherFindingsBytes));
        Assert.All(result.Findings, f => Assert.Equal(SizeConfidence.SharedLinks, f.Confidence));
        Assert.Equal(16 * Mb, result.Findings.Single(f => f.RuleId == "store").FreeableBytes);
    }

    [Fact]
    public async Task Hard_links_between_paths_of_one_finding_are_counted_once_and_freed()
    {
        _measurer.Set(P("a"), 10 * Mb, null, Link(9, 3 * Mb, links: 2, seen: 1));
        _measurer.Set(P("b"), 5 * Mb, null, Link(9, 3 * Mb, links: 2, seen: 1));
        Scanner scanner = Scanner(_measurer, null, null, Rule("both", Finds(Item("ab", P("a"), P("b")))));

        ScanResult result = await scanner.ScanAsync(cancellationToken: Token);

        Finding finding = Assert.Single(result.Findings);
        Assert.Equal(12 * Mb, finding.Size);
        Assert.Equal(12 * Mb, finding.FreeableBytes);
        Assert.Equal(SizeConfidence.Exact, finding.Confidence);
        Assert.Equal([10 * Mb, 5 * Mb], finding.PathSizes);
    }

    [Fact]
    public async Task Small_ignored_and_vanished_items_are_left_out()
    {
        _measurer.Set(P("small"), 2 * Mb);
        _measurer.Set(P("big"), 9 * Mb);
        Scanner scanner = Scanner(_measurer, null, null,
            Rule("small", Finds(Item("s", P("small"))), minSizeMB: 5),
            Rule("ignored", Finds(Item("i", P("big")))),
            Rule("vanished", Finds(Item("v", P("gone")))));

        ScanResult result = await scanner.ScanAsync(new ScanOptions { Ignored = new HashSet<string> { "ignored:i" } }, Token);

        Assert.Empty(result.Findings);
        Assert.All(result.Report.Checks, c => Assert.Equal(CheckOutcome.NothingFound, c.Outcome));
    }

    [Fact]
    public async Task A_slow_check_times_out_and_a_failing_one_is_reported_without_stopping_the_scan()
    {
        _measurer.Set(P("ok"), 3 * Mb);
        Scanner scanner = Scanner(_measurer, null, null,
            Rule("slow", run =>
            {
                Task.Delay(TimeSpan.FromSeconds(20), run.CancellationToken).Wait(run.CancellationToken);
                return [];
            }),
            Rule("broken", _ => throw new InvalidOperationException("detector bug")),
            Rule("ok", Finds(Item("o", P("ok")))));

        ScanResult result = await scanner.ScanAsync(new ScanOptions { CheckTimeout = TimeSpan.FromMilliseconds(300) }, Token);

        Assert.Equal(CheckOutcome.TimedOut, result.Report.Checks.Single(c => c.Id == "slow").Outcome);
        CheckReport broken = result.Report.Checks.Single(c => c.Id == "broken");
        Assert.Equal(CheckOutcome.Failed, broken.Outcome);
        Assert.Contains("detector bug", broken.Error, StringComparison.Ordinal);
        Assert.Equal(CheckOutcome.Found, result.Report.Checks.Single(c => c.Id == "ok").Outcome);
        Assert.Single(result.Findings);
        Assert.Contains("1 skipped (too slow)", result.Report.Summary, StringComparison.Ordinal);
        Assert.True(result.Report.TotalSeconds < 10);
    }

    [Fact]
    public async Task Progress_reports_each_check_starting_and_finishing_and_each_measurement()
    {
        _measurer.Set(P("a"), 3 * Mb);
        _measurer.Set(P("b"), 3 * Mb);
        var events = new List<ScanProgress>();
        var progress = new SyncProgress(events);
        Scanner scanner = Scanner(_measurer, null, null, Rule("a", Finds(Item("a", P("a")))), Rule("b", Finds(Item("b", P("b")))));

        await scanner.ScanAsync(new ScanOptions { Progress = progress }, Token);

        Assert.Equal(2, events.Count(e => e.Phase == ScanPhase.Checking && !e.Finished));
        Assert.Equal(2, events.Count(e => e.Phase == ScanPhase.Checking && e.Finished));
        Assert.Contains(events, e => e.Phase == ScanPhase.Checking && e.Finished && e.Done == 2 && e.Total == 2);
        Assert.Equal(2, events.Count(e => e.Phase == ScanPhase.Measuring));
    }

    [Fact]
    public async Task Findings_carry_rendered_explanations_actions_blockers_and_the_overview_flag()
    {
        _measurer.Set(P("cache"), 3 * Mb, exactBytes: 4 * Mb);
        Scanner scanner = Scanner(_measurer, new FixedProcesses("Thing", "explorer"), null,
            Rule("cache", Finds(Item("c", P("cache"))), blockers: ["thing", "other"]));

        ScanResult result = await scanner.ScanAsync(cancellationToken: Token);

        Finding finding = Assert.Single(result.Findings);
        Assert.Equal("cache:c", finding.Id);
        Assert.Equal(SizeMeasurement.Overview, finding.Measurement);
        Assert.Equal($"Thing at {P("cache")}", finding.Explanation!.What);
        Assert.Equal("2.9 MB in 1 places", finding.Explanation.Why);
        Assert.Equal(["tool", "clean", P("cache")], finding.Actions[1].Command);
        Assert.Equal(["thing"], finding.BlockingApps);
        Assert.Contains(finding.Checks, c => c.Status == CheckStatus.Warning && c.Text.Contains("thing is running", StringComparison.Ordinal));
        Assert.Equal("Nothing to undo for Thing.", finding.UndoNote);
        Assert.All(_measurer.Calls, c => Assert.Equal(SizeMode.Overview, c.Mode));
    }

    [Fact]
    public async Task Exact_re_measure_counts_streams_and_marks_the_finding_exact()
    {
        _measurer.Set(P("cache"), 3 * Mb, exactBytes: 5 * Mb);
        Scanner scanner = Scanner(_measurer, null, null, Rule("cache", Finds(Item("c", P("cache")))));
        Finding overview = Assert.Single((await scanner.ScanAsync(cancellationToken: Token)).Findings);
        Rule rule = Rule("cache", Finds());

        Finding exact = await new FindingRemeasurer(_measurer, id => id == "cache" ? rule : null).RemeasureAsync(overview, cancellationToken: Token);

        Assert.Equal(3 * Mb, overview.Size);
        Assert.Equal(5 * Mb, exact.Size);
        Assert.Equal(SizeMeasurement.Exact, exact.Measurement);
        Assert.Equal("4.8 MB in 1 places", exact.Explanation!.Why);
        Assert.Contains(_measurer.Calls, c => c.Mode == SizeMode.Exact);
    }

    [Fact]
    public async Task Exact_mode_measures_with_streams_from_the_start()
    {
        _measurer.Set(P("cache"), 3 * Mb, exactBytes: 5 * Mb);
        Scanner scanner = Scanner(_measurer, null, null, Rule("cache", Finds(Item("c", P("cache")))));

        Finding finding = Assert.Single((await scanner.ScanAsync(new ScanOptions { Mode = SizeMode.Exact }, Token)).Findings);

        Assert.Equal(5 * Mb, finding.Size);
        Assert.Equal(SizeMeasurement.Exact, finding.Measurement);
    }

    [Fact]
    public async Task A_path_the_safety_rules_refuse_loses_its_remove_actions()
    {
        _measurer.Set(P("protected"), 3 * Mb);
        Scanner scanner = Scanner(_measurer, null, path => path.EndsWith("protected", StringComparison.Ordinal) ? "it is your Documents folder" : null,
            Rule("p", Finds(Item("p", P("protected")))));

        Finding finding = Assert.Single((await scanner.ScanAsync(cancellationToken: Token)).Findings);

        Assert.Equal([CleanActionKind.Command], finding.Actions.Select(a => a.Kind));
        Assert.Contains(finding.Checks, c => c.Text.Contains("it is your Documents folder", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Categories_filter_the_rules_and_reported_sizes_are_not_walked()
    {
        Scanner scanner = Scanner(_measurer, null, null,
            Rule("docker", Finds(new Candidate { Key = "images", Size = 7 * Mb }), category: Category.Docker),
            Rule("other", Finds(Item("x", P("x")))));

        ScanResult result = await scanner.ScanAsync(new ScanOptions { Categories = new HashSet<Category> { Category.Docker } }, Token);

        Finding finding = Assert.Single(result.Findings);
        Assert.Equal(SizeMeasurement.Reported, finding.Measurement);
        Assert.Equal(7 * Mb, finding.Size);
        Assert.Single(result.Report.Checks);
        Assert.Empty(_measurer.Calls);
    }

    private sealed class SyncProgress(List<ScanProgress> events) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value)
        {
            lock (events)
            {
                events.Add(value);
            }
        }
    }
}
