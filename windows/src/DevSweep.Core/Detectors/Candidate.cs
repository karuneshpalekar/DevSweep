using DevSweep.Core.Cleaning;

namespace DevSweep.Core.Detectors;

/// <summary>What a detector found, before it is measured and its explanation filled in.</summary>
public sealed record Candidate
{
    /// <summary>Unique within the rule; the finding's id is <c>ruleId:key</c>.</summary>
    public required string Key { get; init; }

    /// <summary>Full paths of the items; empty for things that are not files (then <see cref="Size"/> is set).</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>Overrides the rule's title.</summary>
    public string? Title { get; init; }

    public string? Subtitle { get; init; }

    /// <summary>A size the owning tool reported; when null, the paths are measured.</summary>
    public long? Size { get; init; }

    /// <summary>Values for the explanation's <c>{{placeholders}}</c>.</summary>
    public IReadOnlyDictionary<string, string> Facts { get; init; } = new Dictionary<string, string>();

    /// <summary>What was checked (sources looked at, why it is safe).</summary>
    public IReadOnlyList<Check> Checks { get; init; } = [];

    /// <summary>Overrides the rule's actions.</summary>
    public IReadOnlyList<CleanAction>? Actions { get; init; }

    /// <summary>Overrides the rule's risk.</summary>
    public Risk? Risk { get; init; }
}
