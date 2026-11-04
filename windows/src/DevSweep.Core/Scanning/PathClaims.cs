using DevSweep.Core.Detectors;
using DevSweep.Core.Rules;

namespace DevSweep.Core.Scanning;

/// <summary>A candidate that kept at least one path (or has a reported size), with its rule.</summary>
internal sealed record ClaimedCandidate(int CheckIndex, Rule Rule, Candidate Candidate);

/// <summary>
/// A path belongs to the first rule that claims it (rules run in pack order, so
/// specific rules come before general ones like leftovers): a later candidate
/// loses any path already claimed or inside a claimed folder, so nothing is
/// listed or counted twice. A later folder that contains an earlier claim keeps
/// it; the overlap is subtracted from the scan total (<see cref="ScanTotals"/>).
/// </summary>
internal static class PathClaims
{
    public static List<ClaimedCandidate> Resolve(IReadOnlyList<CheckResult> checks)
    {
        var claimed = new List<string>();
        var result = new List<ClaimedCandidate>();
        for (int i = 0; i < checks.Count; i++)
        {
            foreach (Candidate candidate in checks[i].Candidates)
            {
                if (candidate.Paths.Count == 0)
                {
                    result.Add(new ClaimedCandidate(i, checks[i].Rule, candidate));
                    continue;
                }

                var kept = candidate.Paths
                    .Select(Normalize)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(path => !claimed.Any(c => IsSameOrInside(path, c)))
                    .ToList();
                if (kept.Count == 0)
                {
                    continue;
                }

                claimed.AddRange(kept);
                result.Add(new ClaimedCandidate(i, checks[i].Rule, candidate with { Paths = kept }));
            }
        }

        return result;
    }

    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(path);

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it (case-insensitive).</summary>
    public static bool IsSameOrInside(string path, string folder)
        => string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) || IsInside(path, folder);

    public static bool IsInside(string path, string folder)
        => path.Length > folder.Length + 1
            && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
            && (path[folder.Length] == '\\' || path[folder.Length] == '/' || folder.EndsWith('\\') || folder.EndsWith('/'));
}
