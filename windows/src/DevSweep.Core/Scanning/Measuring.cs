using DevSweep.Core.Platform;

namespace DevSweep.Core.Scanning;

/// <summary>
/// How a scan measures sizes (the walker policy). Overview scans count no
/// alternate data streams (no handle per file: fast, Defender-friendly); review
/// and cleaning re-measure exactly, streams included.
/// </summary>
public enum SizeMode
{
    /// <summary>Alternate streams off. Findings carry <see cref="Cleaning.SizeMeasurement.Overview"/>.</summary>
    Overview,

    /// <summary>Alternate streams on: the size the cleaner reconciles against.</summary>
    Exact,
}

/// <summary>Measures one path for a scan (the walker; faked in tests).</summary>
public interface IItemMeasurer
{
    /// <exception cref="FileNotFoundException">The item no longer exists.</exception>
    WalkResult Measure(string path, SizeMode mode, TimeSpan? timeLimit, CancellationToken cancellationToken);
}

/// <summary><see cref="DirectoryWalker"/> with hard-link identities recorded, so sizes can be deduplicated across paths and findings.</summary>
public sealed class WalkerMeasurer : IItemMeasurer
{
    public static WalkerMeasurer Instance { get; } = new();

    public WalkResult Measure(string path, SizeMode mode, TimeSpan? timeLimit, CancellationToken cancellationToken)
        => DirectoryWalker.Measure(path, Options(mode, timeLimit), cancellationToken);

    /// <summary>The walker options for a mode.</summary>
    public static WalkOptions Options(SizeMode mode, TimeSpan? timeLimit) => new()
    {
        AlternateStreams = mode == SizeMode.Exact,
        RecordLinkedFiles = true,
        TimeLimit = timeLimit,
        MaxRecordedReparsePoints = 50,
        MaxRecordedProblems = 50,
    };
}

/// <summary>A finding's size, worked out from its paths' walks (<see cref="FindingSizer"/>).</summary>
/// <param name="Bytes">On-disk bytes, every file counted once across all paths.</param>
/// <param name="PathBytes">Each path's own bytes, in order.</param>
/// <param name="FreeableBytes">Bytes removing every path frees (files linked from outside the finding left out).</param>
/// <param name="Files">Distinct files.</param>
/// <param name="Confidence">How far the size can be trusted.</param>
/// <param name="Complete">Every path was measured to the end.</param>
/// <param name="AccessDenied">Folders or files that could not be read.</param>
/// <param name="LinkedFiles">Hard-linked files across the finding, merged (for deduplication across findings).</param>
public sealed record FindingSize(
    long Bytes,
    IReadOnlyList<long> PathBytes,
    long FreeableBytes,
    long Files,
    SizeConfidence Confidence,
    bool Complete,
    long AccessDenied,
    IReadOnlyList<LinkedFile> LinkedFiles)
{
    /// <summary>The paths that were measured (vanished ones left out), in the order of <see cref="PathBytes"/>.</summary>
    public IReadOnlyList<string> MeasuredPaths { get; init; } = [];

    /// <summary>How long measuring took.</summary>
    public double Seconds { get; init; }
}

/// <summary>
/// Combines the walks of a finding's paths: a file hard-linked into two of its
/// paths is counted once, and only files with links outside every path count
/// as shared (not freed).
/// </summary>
public static class FindingSizer
{
    public static FindingSize Combine(IReadOnlyList<WalkResult> walks)
    {
        ArgumentNullException.ThrowIfNull(walks);
        long bytes = walks.Sum(w => w.Bytes);
        long files = walks.Sum(w => w.Files);
        var merged = new Dictionary<FileIdentity, LinkedFile>();
        foreach (LinkedFile file in walks.SelectMany(w => w.LinkedFiles))
        {
            if (merged.TryGetValue(file.Identity, out LinkedFile? earlier))
            {
                bytes -= file.Bytes;
                files--;
                merged[file.Identity] = earlier with { NamesSeen = earlier.NamesSeen + file.NamesSeen, Bytes = Math.Max(earlier.Bytes, file.Bytes) };
            }
            else
            {
                merged[file.Identity] = file;
            }
        }

        long shared = merged.Values.Where(f => f.NamesSeen < f.LinkCount).Sum(f => f.Bytes);
        bool anyLinkedFiles = walks.Any(w => w.LinkedFiles.Count > 0);

        // Without identities (a walker that did not record them), trust each walk's own shared count.
        if (!anyLinkedFiles)
        {
            shared = walks.Sum(w => w.SharedLinkBytes);
        }

        bool complete = walks.All(w => w.Complete);
        long denied = walks.Sum(w => w.AccessDenied);
        SizeConfidence confidence = SizeConfidenceRules.From(
            complete, walks.Sum(w => w.Unreadable), denied, walks.Sum(w => w.CloudPlaceholderCount), shared);
        return new FindingSize(bytes, walks.Select(w => w.Bytes).ToList(), bytes - shared, files, confidence, complete, denied, merged.Values.ToList())
        {
            MeasuredPaths = walks.Select(w => w.Path).ToList(),
        };
    }
}
