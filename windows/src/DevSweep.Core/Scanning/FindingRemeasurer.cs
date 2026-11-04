using System.Globalization;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Rules;

namespace DevSweep.Core.Scanning;

/// <summary>
/// The "exact re-measure" API (for the app's Review sheet and the cleaner):
/// measures a finding's paths again with alternate data streams counted and no
/// overview shortcuts, and returns the finding with its exact size, confidence,
/// facts and explanation. Call it when an item is selected for review or cleaning,
/// for every finding whose <see cref="Finding.Measurement"/> is <see cref="SizeMeasurement.Overview"/>
/// (calling it on an exact one refreshes it). A tool-reported size is returned unchanged.
/// </summary>
public sealed class FindingRemeasurer(IItemMeasurer? measurer = null, Func<string, Rule?>? rules = null)
{
    private readonly IItemMeasurer _measurer = measurer ?? WalkerMeasurer.Instance;

    /// <summary>Re-measures on a worker thread.</summary>
    /// <param name="finding">The finding to measure.</param>
    /// <param name="timeLimit">Stop after this long (the size is then partial); null for no limit.</param>
    /// <param name="cancellationToken">Stops the walk; the result is then partial.</param>
    public Task<Finding> RemeasureAsync(Finding finding, TimeSpan? timeLimit = null, CancellationToken cancellationToken = default)
        => Task.Run(() => Remeasure(finding, timeLimit, cancellationToken), cancellationToken);

    public Finding Remeasure(Finding finding, TimeSpan? timeLimit = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (finding.Measurement == SizeMeasurement.Reported || finding.Paths.Count == 0)
        {
            return finding;
        }

        FindingSize size = Scanner.Measure(finding.Paths, SizeMode.Exact, timeLimit, _measurer, cancellationToken);
        var facts = new Dictionary<string, string>(finding.Facts, StringComparer.Ordinal)
        {
            ["size"] = SizeText.Format(size.Bytes),
            ["count"] = size.MeasuredPaths.Count.ToString(CultureInfo.InvariantCulture),
        };
        Rule? rule = rules?.Invoke(finding.RuleId);
        var checks = finding.Checks.Where(c => !c.Text.StartsWith("At least this size", StringComparison.Ordinal)).ToList();
        if (!size.Complete)
        {
            checks.Add(Check.Warning(string.Create(CultureInfo.InvariantCulture,
                $"At least this size. Measuring stopped with {size.Files:N0} files counted, so the real size is larger.")));
        }

        return finding with
        {
            Paths = size.MeasuredPaths,
            Size = size.Bytes,
            PathSizes = size.PathBytes,
            FreeableBytes = size.FreeableBytes,
            Files = size.Files,
            Confidence = size.Confidence,
            Measurement = SizeMeasurement.Exact,
            Facts = facts,
            Checks = checks,
            Explanation = rule is null ? finding.Explanation : rule.Explain.Rendered(facts),
        };
    }
}
