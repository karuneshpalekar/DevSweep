using DevSweep.Core.Cleaning;
using DevSweep.Core.Detectors;
using DevSweep.Core.Inventory;
using DevSweep.Core.Locations;
using DevSweep.Core.Platform;
using DevSweep.Core.Rules;
using DevSweep.Core.Rules.Yaml;
using DevSweep.Core.Scanning;

namespace DevSweep.Core.Tests.Scanning;

/// <summary>A detector whose candidates come from the rule's options (a function), for scanner tests.</summary>
internal sealed class FakeDetector : IDetector
{
    public string Kind => "fake";

    public object? Bind(OptionReader options) => null;

    public IReadOnlyList<Candidate> Detect(DetectorRun run)
        => ((Func<DetectorRun, IReadOnlyList<Candidate>>)run.Rule.Detector.Options)(run);
}

/// <summary>Sizes by path, per mode, with optional hard-link identities.</summary>
internal sealed class FakeMeasurer : IItemMeasurer
{
    public Dictionary<string, WalkResult> Overview { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, WalkResult> Exact { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Path, SizeMode Mode)> Calls { get; } = [];

    public void Set(string path, long bytes, long? exactBytes = null, params LinkedFile[] linked)
    {
        Overview[path] = Walk(path, bytes, linked);
        Exact[path] = Walk(path, exactBytes ?? bytes, linked);
    }

    public WalkResult Measure(string path, SizeMode mode, TimeSpan? timeLimit, CancellationToken cancellationToken)
    {
        lock (Calls)
        {
            Calls.Add((path, mode));
        }

        Dictionary<string, WalkResult> table = mode == SizeMode.Exact ? Exact : Overview;
        return table.TryGetValue(path, out WalkResult? result) ? result : throw new FileNotFoundException(path);
    }

    private static WalkResult Walk(string path, long bytes, LinkedFile[] linked)
        => new() { Path = path, Bytes = bytes, Files = 1 + linked.Length, Complete = true, LinkedFiles = linked };
}

internal static class ScanFakes
{
    public const long Mb = 1_000_000;

    public static readonly string Base = Path.Join(Path.GetTempPath(), "devsweep-scan-unit");

    public static string P(params string[] parts) => Path.Join([Base, .. parts]);

    public static Rule Rule(string id, Func<DetectorRun, IReadOnlyList<Candidate>> detect, int minSizeMB = 1, Category category = Category.PackageCaches,
        Risk risk = Risk.Rebuilds, string title = "Thing", IReadOnlyList<string>? blockers = null)
        => new()
        {
            Id = id,
            Title = title,
            Category = category,
            Risk = risk,
            Detector = new DetectorSpec("fake", new YamlMapping([], 1, 1), detect),
            Explain = new Explanation("{{title}} at {{path}}", "{{size}} in {{count}} places", "It comes back") { Undo = "Nothing to undo for {{title}}." },
            Actions = [new CleanAction(CleanActionKind.Delete), new CleanAction(CleanActionKind.Command, null, ["tool", "clean", "{{path}}"])],
            Blockers = blockers ?? [],
            MinSizeMB = minSizeMB,
        };

    public static Func<DetectorRun, IReadOnlyList<Candidate>> Finds(params Candidate[] candidates) => _ => candidates;

    public static Candidate Item(string key, params string[] paths) => new() { Key = key, Paths = paths };

    public static Scanner Scanner(FakeMeasurer measurer, IRunningProcesses? processes = null, Func<string, string?>? safety = null, params Rule[] rules)
    {
        var environment = FixedEnvironment.UnderRoot(Base, readRealFiles: false);
        var context = new ScanContext(new LocationRegistry(environment, BuiltInLocations.All()), null, processes ?? new FixedProcesses())
        {
            SafetyCheck = safety,
        };
        return new Scanner(rules, new DetectorRegistry([new FakeDetector()]), context, measurer);
    }

    public static LinkedFile Link(int id, long bytes, uint links, uint seen) => new(new FileIdentity(7, (UInt128)id), bytes, links, seen);
}
