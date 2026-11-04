using System.ComponentModel;
using System.Diagnostics;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Detectors;
using DevSweep.Core.Platform;
using DevSweep.Core.Rules;

namespace DevSweep.Core.Scanning;

/// <summary>
/// Runs a scan (PLAN 4.1, the Mac Scanner's shape): every rule's check in
/// parallel under a time limit, paths claimed by the first rule, every item
/// measured in parallel (alternate streams off in <see cref="SizeMode.Overview"/>),
/// findings built with their facts and checks, totals with nothing counted twice,
/// and a <see cref="ScanReport"/>. Nothing is changed on disk.
/// </summary>
public sealed class Scanner(IReadOnlyList<Rule> rules, DetectorRegistry detectors, ScanContext context, IItemMeasurer? measurer = null)
{
    private readonly IItemMeasurer _measurer = measurer ?? WalkerMeasurer.Instance;

    public async Task<ScanResult> ScanAsync(ScanOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ScanOptions();
        var clock = Stopwatch.StartNew();
        List<Rule> active = rules.Where(r => options.Categories is null || options.Categories.Contains(r.Category)).ToList();

        CheckResult[] checks = await new CheckRunner(detectors, context, options).RunAsync(active, cancellationToken).ConfigureAwait(false);
        List<ClaimedCandidate> work = PathClaims.Resolve(checks);

        var measureClock = Stopwatch.StartNew();
        FindingSize?[] sizes = await MeasureAsync(work, options, cancellationToken).ConfigureAwait(false);
        double measuring = measureClock.Elapsed.TotalSeconds;

        var builder = new FindingBuilder(context, options);
        var findings = new List<Finding>();
        var linked = new List<IReadOnlyList<LinkedFile>>();
        var found = new int[checks.Length];
        var partial = new int[checks.Length];
        for (int i = 0; i < work.Count; i++)
        {
            (int checkIndex, Rule rule, Candidate candidate) = work[i];
            FindingSize? size = sizes[i];
            if (size is not null && size.PathBytes.Count == 0)
            {
                continue; // Every path vanished before it was measured.
            }

            Candidate measured = size is null ? candidate : candidate with { Paths = SurvivingPaths(candidate, size) };
            Finding finding = builder.Build(rule, measured, size);
            if (finding.Size < rule.MinSizeMB * 1_000_000L || options.Ignored.Contains(finding.Id))
            {
                continue;
            }

            findings.Add(finding);
            linked.Add(size?.LinkedFiles ?? []);
            found[checkIndex]++;
            if (size is { Confidence: SizeConfidence.Partial or SizeConfidence.AccessDenied })
            {
                partial[checkIndex]++;
            }
        }

        (List<Finding> totalled, ScanTotal total) = ScanTotals.Apply(findings, linked);
        totalled.Sort((a, b) => a.Risk != b.Risk ? a.Risk.CompareTo(b.Risk) : b.Size.CompareTo(a.Size));

        double[] measureSeconds = MeasureSecondsPerCheck(work, sizes, checks.Length);
        List<CheckReport> reports = checks.Select((c, i) => c.Report with
        {
            Found = found[i],
            Outcome = c.Report.Outcome is CheckOutcome.Found && found[i] == 0 ? CheckOutcome.NothingFound : c.Report.Outcome,
            PartialSizes = partial[i],
            MeasureSeconds = measureSeconds[i],
        }).ToList();

        var report = new ScanReport
        {
            Date = context.Now,
            TotalSeconds = clock.Elapsed.TotalSeconds,
            MeasuringSeconds = measuring,
            CheckTimeoutSeconds = options.CheckTimeout.TotalSeconds,
            MeasureTimeoutSeconds = options.MeasureTimeout.TotalSeconds,
            Mode = options.Mode,
            Checks = reports,
            Unmeasured = totalled.Count(f => f.Confidence is SizeConfidence.Partial or SizeConfidence.AccessDenied),
            TotalBytes = total.TotalBytes,
            SharedBetweenFindingsBytes = total.SharedBetweenFindings,
            NestedBytes = total.Nested,
            AccessDenied = reports.SelectMany(r => r.AccessDenied).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList(),
            RuleErrors = options.RuleErrors,
            Inventory = context.InventoryIfBuilt?.Sources ?? [],
            Disabled = options.Disabled,
        };
        return new ScanResult(totalled, report);
    }

    private async Task<FindingSize?[]> MeasureAsync(List<ClaimedCandidate> work, ScanOptions options, CancellationToken cancellationToken)
    {
        var sizes = new FindingSize?[work.Count];
        int done = 0;
        await Parallel.ForEachAsync(
            Enumerable.Range(0, work.Count),
            new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallelism, CancellationToken = cancellationToken },
            (i, token) =>
            {
                Candidate candidate = work[i].Candidate;
                if (candidate.Size is not null && candidate.Paths.Count == 0)
                {
                    return ValueTask.CompletedTask;
                }

                sizes[i] = Measure(candidate.Paths, options.Mode, options.MeasureTimeout, _measurer, token);
                int finished = Interlocked.Increment(ref done);
                options.Progress?.Report(new ScanProgress(ScanPhase.Measuring, finished, work.Count, work[i].Rule.Id, true, []));
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
        return sizes;
    }

    /// <summary>Measures every path under one deadline and combines them; vanished paths are left out.</summary>
    internal static FindingSize Measure(IReadOnlyList<string> paths, SizeMode mode, TimeSpan? limit, IItemMeasurer measurer, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var walks = new List<WalkResult>(paths.Count);
        foreach (string path in paths)
        {
            TimeSpan? remaining = limit is { } l ? (l - clock.Elapsed > TimeSpan.Zero ? l - clock.Elapsed : TimeSpan.Zero) : null;
            try
            {
                walks.Add(measurer.Measure(path, mode, remaining, cancellationToken));
            }
            catch (FileNotFoundException)
            {
                // Removed since the check found it.
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
            {
                walks.Add(new WalkResult { Path = path, Complete = true, AccessDenied = 1 });
            }
        }

        FindingSize size = FindingSizer.Combine(walks);
        return size with { Seconds = clock.Elapsed.TotalSeconds };
    }

    private static List<string> SurvivingPaths(Candidate candidate, FindingSize size)
        => size.MeasuredPaths.Count > 0 ? size.MeasuredPaths.ToList() : candidate.Paths.ToList();

    private static double[] MeasureSecondsPerCheck(List<ClaimedCandidate> work, FindingSize?[] sizes, int checks)
    {
        double[] seconds = new double[checks];
        for (int i = 0; i < work.Count; i++)
        {
            seconds[work[i].CheckIndex] += sizes[i]?.Seconds ?? 0;
        }

        return seconds;
    }
}
