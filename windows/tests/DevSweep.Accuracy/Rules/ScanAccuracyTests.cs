using DevSweep.Core.Cleaning;
using DevSweep.Core.Platform;
using DevSweep.Core.Rules;
using DevSweep.Core.Scanning;
using DevSweep.Fixtures;
using Xunit;

namespace DevSweep.Accuracy.Rules;

/// <summary>Scan totals and measurement modes on real NTFS, through the whole engine.</summary>
public sealed class ScanAccuracyTests(ITestOutputHelper output) : IDisposable
{
    private const long Mb = 1024 * 1024;
    private readonly MachineFixture _machine = new("scan");

    public void Dispose() => _machine.Dispose();

    [Fact]
    public async Task A_file_hard_linked_into_two_findings_is_counted_once_in_the_total()
    {
        string store = Path.Join(_machine.Local, "store");
        string project = Path.Join(_machine.Local, "project-modules");
        string shared = MachineFixture.Allocate(Path.Join(store, "shared.bin"), 8 * Mb);
        MachineFixture.Allocate(Path.Join(store, "own.bin"), 2 * Mb);
        Directory.CreateDirectory(project);
        Ntfs.CreateHardLink(Path.Join(project, "shared.bin"), shared);
        MachineFixture.Allocate(Path.Join(project, "own.bin"), 3 * Mb);

        ScanResult result = await _machine.ScanAsync([Category.PackageCaches], userPack: TwoRules(@"{LocalAppData}\store", @"{LocalAppData}\project-modules"));

        Finding a = result.Findings.Single(f => f.RuleId == "first");
        Finding b = result.Findings.Single(f => f.RuleId == "second");
        output.WriteLine($"store {a.Size:N0} ({a.Confidence}, freeable {a.FreeableBytes:N0}); project {b.Size:N0}; total {result.Report.TotalBytes:N0}; shared {result.Report.SharedBetweenFindingsBytes:N0}");
        Assert.Equal(10 * Mb, a.Size);
        Assert.Equal(11 * Mb, b.Size);
        Assert.Equal(SizeConfidence.SharedLinks, a.Confidence);
        Assert.Equal(2 * Mb, a.FreeableBytes);
        Assert.Equal(8 * Mb, a.SharedWithOtherFindingsBytes);
        Assert.Equal(8 * Mb, result.Report.SharedBetweenFindingsBytes);
        Assert.Equal(13 * Mb, result.Report.TotalBytes);
    }

    [Fact]
    public async Task Links_between_paths_of_one_finding_count_once_and_are_freed()
    {
        string first = Path.Join(_machine.Local, "pair", "a");
        string second = Path.Join(_machine.Local, "pair", "b");
        string shared = MachineFixture.Allocate(Path.Join(first, "shared.bin"), 4 * Mb);
        Directory.CreateDirectory(second);
        Ntfs.CreateHardLink(Path.Join(second, "shared.bin"), shared);

        ScanResult result = await _machine.ScanAsync([Category.PackageCaches], userPack: TwoRules(@"{LocalAppData}\pair\*", null));

        Finding finding = Assert.Single(result.Findings);
        Assert.Equal(2, finding.Paths.Count);
        Assert.Equal(4 * Mb, finding.Size);
        Assert.Equal(4 * Mb, finding.FreeableBytes);
        Assert.Equal(SizeConfidence.Exact, finding.Confidence);
        Assert.Equal([4 * Mb, 4 * Mb], finding.PathSizes);
    }

    [Fact]
    public async Task Overview_leaves_streams_out_and_the_exact_re_measure_counts_them()
    {
        string cache = Path.Join(_machine.Local, "streams");
        string file = MachineFixture.Allocate(Path.Join(cache, "file.bin"), 2 * Mb);
        using (var stream = new FileStream(file + ":extra", FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(Mb);
            stream.Write([1]);
        }

        ScanResult overview = await _machine.ScanAsync([Category.PackageCaches], userPack: TwoRules(@"{LocalAppData}\streams", null), mode: SizeMode.Overview);
        Finding fast = Assert.Single(overview.Findings);
        Finding exact = await new FindingRemeasurer().RemeasureAsync(fast, cancellationToken: TestContext.Current.CancellationToken);

        output.WriteLine($"overview {fast.Size:N0} ({fast.Measurement}), exact {exact.Size:N0} ({exact.Measurement})");
        Assert.Equal(SizeMeasurement.Overview, fast.Measurement);
        Assert.Equal(2 * Mb, fast.Size);
        Assert.Equal(SizeMeasurement.Exact, exact.Measurement);
        Assert.Equal(3 * Mb, exact.Size);
        Assert.Equal(SizeConfidence.Exact, exact.Confidence);
    }

    private static string TwoRules(string first, string? second)
    {
        string Rule(string id, string path) => $"""
              - id: {id}
                title: {id}
                category: packageCaches
                risk: rebuilds
                minSizeMB: 1
                detector:
                  kind: paths
                  paths: ['{path}']
                actions:
                  - kind: delete
                explain:
                  what: "x"
                  why: "{"{{size}}"}"
                  ifDeleted: "y"
            """;
        return "rules:\n" + Rule("first", first) + (second is null ? string.Empty : "\n" + Rule("second", second));
    }
}
