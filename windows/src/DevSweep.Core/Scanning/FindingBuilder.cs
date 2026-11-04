using System.Globalization;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Detectors;
using DevSweep.Core.Rules;

namespace DevSweep.Core.Scanning;

/// <summary>
/// Turns a measured candidate into a <see cref="Finding"/>: the facts for the
/// templates (size, count, path, title), the running blockers, the actions with
/// their commands filled in, the safety guard's verdict, and partial-size warnings.
/// </summary>
internal sealed class FindingBuilder(ScanContext context, ScanOptions options)
{
    public Finding Build(Rule rule, Candidate candidate, FindingSize? size)
    {
        long bytes = size?.Bytes ?? candidate.Size ?? 0;
        var facts = new Dictionary<string, string>(candidate.Facts, StringComparer.Ordinal)
        {
            ["size"] = SizeText.Format(bytes),
            ["count"] = candidate.Paths.Count.ToString(CultureInfo.InvariantCulture),
        };
        if (candidate.Paths.Count > 0)
        {
            facts["path"] = candidate.Paths[0];
        }

        var checks = new List<Check>(candidate.Checks);
        List<string> blocking = Blocking(rule.Blockers);
        if (blocking.Count > 0)
        {
            checks.Add(Check.Warning($"{Words.Join(blocking)} {(blocking.Count == 1 ? "is" : "are")} running. It has to quit first."));
        }
        else if (rule.Blockers.Count > 0)
        {
            checks.Add(Check.Passed($"{Words.Join(rule.Blockers)} {(rule.Blockers.Count == 1 ? "is" : "are")} not running"));
        }

        if (size is { Complete: false })
        {
            checks.Add(Check.Warning(string.Create(CultureInfo.InvariantCulture,
                $"At least this size. Measuring stopped after {options.MeasureTimeout.TotalSeconds:0} s with {size.Files:N0} files counted, so the real size is larger.")));
        }
        else if (size is { AccessDenied: > 0 })
        {
            checks.Add(Check.Warning(string.Create(CultureInfo.InvariantCulture,
                $"At least this size: {size.AccessDenied:N0} folders or files could not be read.")));
        }

        string title = candidate.Title ?? Template.Render(rule.Title, facts);
        facts["title"] = title;
        string subtitle = candidate.Subtitle ?? Subtitle(candidate.Paths);
        IReadOnlyList<CleanAction> actions = (candidate.Actions ?? rule.Actions).Select(a => Render(a, facts)).ToList();
        actions = ApplySafety(candidate.Paths, actions, checks);

        return new Finding
        {
            Id = $"{rule.Id}:{candidate.Key}",
            RuleId = rule.Id,
            Title = title,
            Subtitle = subtitle,
            Category = rule.Category,
            Risk = candidate.Risk ?? rule.Risk,
            Paths = candidate.Paths,
            Size = bytes,
            PathSizes = size?.PathBytes ?? [],
            FreeableBytes = size?.FreeableBytes ?? bytes,
            Files = size?.Files ?? 0,
            Confidence = size?.Confidence ?? Platform.SizeConfidence.Exact,
            Measurement = size is null ? SizeMeasurement.Reported : options.Mode == SizeMode.Exact ? SizeMeasurement.Exact : SizeMeasurement.Overview,
            Explanation = rule.Explain.Rendered(facts),
            Checks = checks,
            Facts = facts,
            Actions = actions,
            Blockers = rule.Blockers,
            BlockingApps = blocking,
            UndoNote = rule.Explain.Undo is { } undo ? Template.Render(undo, facts) : null,
        };
    }

    private List<string> Blocking(IReadOnlyList<string> blockers)
    {
        if (blockers.Count == 0)
        {
            return [];
        }

        var running = context.Processes.Names().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return blockers.Where(running.Contains).ToList();
    }

    /// <summary>Paths the guard would refuse cannot be removed: drop Delete and Trash, and say why.</summary>
    private IReadOnlyList<CleanAction> ApplySafety(IReadOnlyList<string> paths, IReadOnlyList<CleanAction> actions, List<Check> checks)
    {
        if (context.SafetyCheck is null || paths.Count == 0)
        {
            return actions;
        }

        string? refusal = paths.Select(context.SafetyCheck).FirstOrDefault(reason => reason is not null);
        if (refusal is null)
        {
            checks.Add(Check.Passed("Every path is one DevSweep's safety rules allow it to remove"));
            return actions;
        }

        checks.Add(Check.Warning($"DevSweep will not remove this itself: {refusal}"));
        return actions.Where(a => a.Kind is CleanActionKind.Command or CleanActionKind.Manual).ToList();
    }

    private static CleanAction Render(CleanAction action, IReadOnlyDictionary<string, string> facts)
        => action with
        {
            Command = action.Command?.Select(part => Template.Render(part, facts)).ToList(),
            Detail = action.Detail is null ? null : Template.Render(action.Detail, facts),
        };

    private static string Subtitle(IReadOnlyList<string> paths) => paths.Count switch
    {
        0 => string.Empty,
        1 => paths[0],
        _ => string.Create(CultureInfo.InvariantCulture, $"{paths[0]} and {paths.Count - 1} more"),
    };
}
