using System.Diagnostics;
using DevSweep.Core.Detectors;
using DevSweep.Core.Rules;

namespace DevSweep.Core.Scanning;

/// <summary>What one check produced.</summary>
internal sealed record CheckResult(Rule Rule, IReadOnlyList<Candidate> Candidates, CheckReport Report);

/// <summary>
/// Runs the checks (one per rule) in parallel, each on a worker thread with its own
/// time limit and cancellation, reporting progress as each starts and finishes.
/// A check that times out is cancelled and skipped; one that fails is reported.
/// </summary>
internal sealed class CheckRunner(DetectorRegistry detectors, ScanContext context, ScanOptions options)
{
    private readonly Lock _lock = new();
    private readonly List<(string Id, string Title, long Started)> _running = [];
    private int _done;

    public async Task<CheckResult[]> RunAsync(IReadOnlyList<Rule> rules, CancellationToken cancellationToken)
    {
        var results = new CheckResult[rules.Count];
        using var throttle = new SemaphoreSlim(options.MaxParallelism);
        await Task.WhenAll(rules.Select(async (rule, i) =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                results[i] = await RunOneAsync(rule, rules.Count, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                throttle.Release();
            }
        })).ConfigureAwait(false);
        return results;
    }

    private async Task<CheckResult> RunOneAsync(Rule rule, int total, CancellationToken cancellationToken)
    {
        using var checkCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var run = new DetectorRun(rule, context, checkCancel.Token);
        var clock = Stopwatch.StartNew();
        Report(rule, total, finished: false);
        IReadOnlyList<Candidate> candidates = [];
        CheckOutcome outcome;
        string? error = null;
        bool snapshotSafe = true;
        try
        {
            if (!detectors.TryGet(rule.Detector.Kind, out IDetector detector))
            {
                throw new RuleSchemaException($"No detector of kind {rule.Detector.Kind} is registered.");
            }

            candidates = await Task.Run(() => detector.Detect(run), checkCancel.Token)
                .WaitAsync(options.CheckTimeout, cancellationToken).ConfigureAwait(false);
            outcome = candidates.Count > 0 ? CheckOutcome.Found : CheckOutcome.NothingFound;
        }
        catch (TimeoutException)
        {
            await checkCancel.CancelAsync().ConfigureAwait(false);
            outcome = CheckOutcome.TimedOut;
            snapshotSafe = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            outcome = CheckOutcome.Failed;
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        Report(rule, total, finished: true);
        var report = new CheckReport
        {
            Id = rule.Id,
            Title = rule.DisplayTitle,
            Category = rule.Category.Title(),
            Kind = rule.Detector.Kind,
            Seconds = clock.Elapsed.TotalSeconds,
            Outcome = outcome,
            Error = error,

            // A timed-out detector may still be running; its lists are not read.
            AccessDenied = snapshotSafe ? run.AccessDenied.Distinct(StringComparer.OrdinalIgnoreCase).ToList() : [],
            Notes = snapshotSafe ? run.Notes.ToList() : [],
            Locations = snapshotSafe ? run.Locations.Select(l => l.Describe()).ToList() : [],
        };
        return new CheckResult(rule, candidates, report);
    }

    private void Report(Rule rule, int total, bool finished)
    {
        ScanProgress progress;
        lock (_lock)
        {
            if (finished)
            {
                _done++;
                _running.RemoveAll(r => r.Id == rule.Id);
            }
            else
            {
                _running.Add((rule.Id, rule.DisplayTitle, Stopwatch.GetTimestamp()));
            }

            progress = new ScanProgress(ScanPhase.Checking, _done, total, rule.Id, finished,
                _running.OrderBy(r => r.Started).Select(r => r.Title).ToList());
        }

        options.Progress?.Report(progress);
    }
}
