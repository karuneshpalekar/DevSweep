using DevSweep.Core.Locations;
using DevSweep.Core.Rules;
using DevSweep.Core.Scanning;

namespace DevSweep.Core.Detectors;

/// <summary>One rule's check in one scan: the rule, the shared context, and what to report.</summary>
public sealed class DetectorRun(Rule rule, ScanContext context, CancellationToken cancellationToken)
{
    private readonly List<string> _denied = [];
    private readonly List<string> _notes = [];
    private readonly List<ResolvedLocation> _locations = [];

    public Rule Rule { get; } = rule ?? throw new ArgumentNullException(nameof(rule));

    public ScanContext Context { get; } = context ?? throw new ArgumentNullException(nameof(context));

    public CancellationToken CancellationToken { get; } = cancellationToken;

    /// <summary>Folders this check could not list (access denied); they go into the scan report.</summary>
    public IReadOnlyList<string> AccessDenied => _denied;

    /// <summary>Things worth knowing that are not findings ("not checked: ..."), for the scan report.</summary>
    public IReadOnlyList<string> Notes => _notes;

    /// <summary>Every location this check resolved, for the scan report.</summary>
    public IReadOnlyList<ResolvedLocation> Locations => _locations;

    public void Denied(string path) => _denied.Add(path);

    public void Note(string note) => _notes.Add(note);

    /// <summary>Expands a rule path against the disk (wildcards included, links never followed).</summary>
    public PathExpansion Expand(PathTemplate template)
    {
        PathExpansion expansion = PathExpander.Expand(template, this);
        if (!_locations.Contains(expansion.Root))
        {
            _locations.Add(expansion.Root);
        }

        return expansion;
    }

    public void ThrowIfCancelled() => CancellationToken.ThrowIfCancellationRequested();
}
