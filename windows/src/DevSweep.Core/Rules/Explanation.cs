namespace DevSweep.Core.Rules;

/// <summary>
/// The text behind the ⓘ button (same fields as the Mac app). Rule packs write
/// it with <c>{{fact}}</c> placeholders; the scanner fills them with what it found.
/// </summary>
/// <param name="What">What the item is.</param>
/// <param name="Why">Why it is listed (usually with its size).</param>
/// <param name="IfDeleted">What happens after it is removed.</param>
public sealed record Explanation(string What, string Why, string IfDeleted)
{
    /// <summary>What the user keeps.</summary>
    public string? WontLose { get; init; }

    /// <summary>Something to do first.</summary>
    public string? Before { get; init; }

    /// <summary>How to get it back.</summary>
    public string? Undo { get; init; }

    /// <summary>A better long-term fix.</summary>
    public string? Better { get; init; }

    /// <summary>The same explanation with every placeholder filled.</summary>
    public Explanation Rendered(IReadOnlyDictionary<string, string> facts)
    {
        string R(string text) => Template.Render(text, facts);
        return new Explanation(R(What), R(Why), R(IfDeleted))
        {
            WontLose = WontLose is null ? null : R(WontLose),
            Before = Before is null ? null : R(Before),
            Undo = Undo is null ? null : R(Undo),
            Better = Better is null ? null : R(Better),
        };
    }
}
