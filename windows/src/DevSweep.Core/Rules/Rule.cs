using DevSweep.Core.Cleaning;
using DevSweep.Core.Rules.Yaml;

namespace DevSweep.Core.Rules;

/// <summary>
/// A rule pack entry (same schema as the Mac app): what to look for, how risky
/// it is, how to clean it and how to explain it. Adding a tool is a YAML change;
/// only detector kinds are code.
/// </summary>
public sealed record Rule
{
    public required string Id { get; init; }

    /// <summary>The finding title; may hold <c>{{fact}}</c> placeholders.</summary>
    public required string Title { get; init; }

    public required Category Category { get; init; }

    public required Risk Risk { get; init; }

    public required DetectorSpec Detector { get; init; }

    public required Explanation Explain { get; init; }

    public IReadOnlyList<CleanAction> Actions { get; init; } = [];

    /// <summary>Process names (without .exe) that must not be running.</summary>
    public IReadOnlyList<string> Blockers { get; init; } = [];

    /// <summary>Findings smaller than this many MB (10^6 bytes) are not shown.</summary>
    public int MinSizeMB { get; init; } = 1;

    /// <summary>The pack this rule came from.</summary>
    public string Pack { get; init; } = string.Empty;

    /// <summary>The title, or for a per-item template like "{{name}}" the id in words ("Large old files").</summary>
    public string DisplayTitle
    {
        get
        {
            if (!Template.HasPlaceholders(Title))
            {
                return Title;
            }

            string words = Id.Replace('-', ' ');
            return string.Concat(char.ToUpperInvariant(words[0]).ToString(), words.AsSpan(1));
        }
    }
}

/// <summary>
/// A rule's <c>detector:</c> block: its kind and the options the detector of
/// that kind read and checked when the pack was loaded.
/// </summary>
/// <param name="Kind">The detector kind (<c>paths</c>, <c>versionedSiblings</c>...).</param>
/// <param name="Node">The YAML it came from.</param>
/// <param name="Options">What the detector bound from <paramref name="Node"/> (its own options type).</param>
public sealed record DetectorSpec(string Kind, YamlMapping Node, object Options);
