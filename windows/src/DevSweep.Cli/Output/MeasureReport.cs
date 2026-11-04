using DevSweep.Core.Platform;

namespace DevSweep.Cli.Output;

/// <summary>The JSON shape of <c>devsweep measure --json</c>. Field names are a public contract.</summary>
internal sealed record MeasureReport(
    string Path,
    long Bytes,
    long LogicalBytes,
    long FreeableBytes,
    long Files,
    long Directories,
    bool Complete,
    SizeConfidence Confidence,
    long HardLinkDuplicates,
    long SharedLinkBytes,
    long SharedLinkFiles,
    long CloudPlaceholderCount,
    long CloudPlaceholderLogicalBytes,
    long CloudDirectories,
    long AlternateStreamCount,
    long AlternateStreamBytes,
    long AccessDenied,
    long Unreadable,
    long ReparsePointCount,
    IReadOnlyList<ReparsePointReport> ReparsePoints,
    IReadOnlyList<ProblemReport> Problems,
    ListingUsed Listing,
    long StaleDirectoryEntries,
    double ElapsedSeconds)
{
    public static MeasureReport From(WalkResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new MeasureReport(
            result.Path,
            result.Bytes,
            result.LogicalBytes,
            result.FreeableBytes,
            result.Files,
            result.Directories,
            result.Complete,
            result.Confidence,
            result.HardLinkDuplicates,
            result.SharedLinkBytes,
            result.SharedLinkFiles,
            result.CloudPlaceholderCount,
            result.CloudPlaceholderLogicalBytes,
            result.CloudDirectories,
            result.AlternateStreamCount,
            result.AlternateStreamBytes,
            result.AccessDenied,
            result.Unreadable,
            result.ReparsePointCount,
            [.. result.ReparsePoints.Select(r => new ReparsePointReport(r.Path, r.Kind, $"0x{r.Tag:X8}", r.IsDirectory))],
            [.. result.Problems.Select(p => new ProblemReport(p.Path, p.Win32Error))],
            result.Listing,
            result.StaleDirectoryEntries,
            Math.Round(result.Elapsed.TotalSeconds, 3));
    }
}

internal sealed record ReparsePointReport(string Path, ReparseKind Kind, string Tag, bool IsDirectory);

internal sealed record ProblemReport(string Path, int Win32Error);
