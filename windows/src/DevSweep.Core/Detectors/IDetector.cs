using DevSweep.Core.Rules;

namespace DevSweep.Core.Detectors;

/// <summary>
/// One detector kind. Reads and checks its own options when a pack is loaded
/// (<see cref="Bind"/>), so a bad option is a load error with a line number, and
/// finds candidates when a scan runs (<see cref="Detect"/>).
/// </summary>
public interface IDetector
{
    /// <summary>The <c>kind:</c> value rules use.</summary>
    string Kind { get; }

    /// <summary>Reads the detector's options; returns them, or null after reporting errors to <see cref="OptionReader.Errors"/>.</summary>
    object? Bind(OptionReader options);

    /// <summary>Finds candidates. Runs on a worker thread under the check's time limit; honour the cancellation token.</summary>
    IReadOnlyList<Candidate> Detect(DetectorRun run);
}

/// <summary>Base for detectors with a typed options record.</summary>
/// <typeparam name="TOptions">What <see cref="BindOptions"/> returns.</typeparam>
public abstract class Detector<TOptions> : IDetector
    where TOptions : class
{
    public abstract string Kind { get; }

    public object? Bind(OptionReader options)
    {
        ArgumentNullException.ThrowIfNull(options);
        TOptions? bound = BindOptions(options);
        options.Finish();
        return options.Failed ? null : bound;
    }

    public IReadOnlyList<Candidate> Detect(DetectorRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Rule.Detector.Options is not TOptions options)
        {
            throw new RuleSchemaException($"Rule {run.Rule.Id} was not bound by the {Kind} detector.");
        }

        return Detect(run, options);
    }

    /// <summary>Reads every option (unread keys are reported as unknown afterwards).</summary>
    protected abstract TOptions? BindOptions(OptionReader options);

    protected abstract IReadOnlyList<Candidate> Detect(DetectorRun run, TOptions options);
}

/// <summary>The detectors an engine knows, by kind.</summary>
public sealed class DetectorRegistry
{
    private readonly Dictionary<string, IDetector> _detectors = new(StringComparer.Ordinal);

    public DetectorRegistry(IEnumerable<IDetector> detectors)
    {
        ArgumentNullException.ThrowIfNull(detectors);
        foreach (IDetector detector in detectors)
        {
            _detectors[detector.Kind] = detector;
        }
    }

    public IReadOnlyCollection<string> Kinds => _detectors.Keys;

    public bool TryGet(string kind, out IDetector detector) => _detectors.TryGetValue(kind, out detector!);
}
