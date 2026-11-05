using System.Globalization;
using DevSweep.Core.Detectors;
using DevSweep.Core.Platform;
using DevSweep.Core.Projects.Git;
using DevSweep.Core.Scanning;

namespace DevSweep.Core.Projects.Discovery;

/// <summary>A git working copy on this PC, inspected.</summary>
public sealed record LocalClone
{
    public required string Path { get; init; }

    /// <summary>On-disk bytes (allocation, hard links once), from the M1 walker.</summary>
    public long Size { get; init; }

    public bool SizeComplete { get; init; } = true;

    public string? OriginUrl { get; init; }

    /// <summary><c>owner/name</c> from a GitHub <c>origin</c>; null when there is none.</summary>
    public string? Slug { get; init; }

    public DateTimeOffset? LastCommit { get; init; }

    /// <summary>git's HEAD/index times (a checkout or staging counts as activity).</summary>
    public DateTimeOffset? GitActivity { get; init; }

    public required RemovalSafety Safety { get; init; }

    public bool InCloudFolder { get; init; }

    public bool HasRemote => Safety.HasRemote;
}

/// <summary>
/// Inspects one clone: its size (the M1 walker, metadata only, no streams for the overview),
/// its <c>origin</c>, last commit and git activity, and the removal-safety check. A clone with
/// files that are only in OneDrive (cloud placeholders) is measured but not checked: git would
/// read those files and so download them, and DevSweep never hydrates anything.
/// </summary>
public sealed class CloneInspector(IGit git, IItemMeasurer? measurer = null, IReadOnlySet<string>? sshAliases = null)
{
    public SafetyChecker Checker { get; } = new(git);

    public TimeSpan MeasureLimit { get; init; } = TimeSpan.FromSeconds(60);

    public async Task<LocalClone> InspectAsync(FoundRepo repo, SafetyCheckOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        WalkResult size = Measure(repo.Path, cancellationToken);
        bool inCloud = repo.ThroughCloudFolder || size.CloudDirectories > 0 || size.CloudPlaceholderCount > 0;
        if (repo.GitIsCloudOnly || size.CloudPlaceholderCount > 0)
        {
            long count = Math.Max(size.CloudPlaceholderCount, 1);
            string? origin = ReadOriginWithoutGit(repo.Path);
            return new LocalClone
            {
                Path = repo.Path,
                Size = size.Bytes,
                SizeComplete = size.Complete,
                OriginUrl = origin,
                Slug = origin is null ? null : RemoteUrls.GitHubSlug(origin, sshAliases),
                InCloudFolder = true,
                Safety = new RemovalSafety
                {
                    Path = repo.Path,
                    State = SafetyState.NotChecked,
                    NotCheckedReason = string.Create(CultureInfo.InvariantCulture,
                        $"In OneDrive, not checked: {count:N0} file{(count == 1 ? " is" : "s are")} only in the cloud, and checking would download them. Make the folder \"Always keep on this device\" to check it."),
                    CheckedAt = DateTimeOffset.UtcNow,
                },
            };
        }

        GitResult originResult = await git.RunAsync(repo.Path, ["config", "--get", "remote.origin.url"], cancellationToken: cancellationToken).ConfigureAwait(false);
        string? url = originResult.Ok && originResult.Output.Trim() is { Length: > 0 } u ? u : null;
        GitResult log = await git.RunAsync(repo.Path, ["log", "-1", "--format=%ct"], cancellationToken: cancellationToken).ConfigureAwait(false);
        DateTimeOffset? lastCommit = log.Ok && long.TryParse(log.Output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
        DateTime? gitTime = Detectors.Projects.ProjectActivity.GitTime(repo.Path);
        RemovalSafety safety = await Checker.CheckAsync(repo.Path, options, cancellationToken).ConfigureAwait(false);
        return new LocalClone
        {
            Path = repo.Path,
            Size = size.Bytes,
            SizeComplete = size.Complete,
            OriginUrl = url,
            Slug = url is null ? null : RemoteUrls.GitHubSlug(url, sshAliases),
            LastCommit = lastCommit,
            GitActivity = gitTime is { } t ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)) : null,
            Safety = safety,
            InCloudFolder = inCloud,
        };
    }

    private WalkResult Measure(string path, CancellationToken cancellationToken)
    {
        try
        {
            return (measurer ?? DefaultMeasurer).Measure(path, SizeMode.Overview, MeasureLimit, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new WalkResult { Path = path, Complete = false };
        }
    }

    private static IItemMeasurer DefaultMeasurer => OperatingSystem.IsWindows() ? WalkerMeasurer.Instance : ManagedMeasurer.Instance;

    /// <summary><c>[remote "origin"] url</c> from <c>.git\config</c>, read only when that file is on this disk (not a placeholder).</summary>
    private static string? ReadOriginWithoutGit(string repo)
    {
        string config = System.IO.Path.Join(repo, ".git", "config");
        if (FileProbe.Stat(config) is not { IsCloudPlaceholder: false, IsDirectory: false })
        {
            return null;
        }

        try
        {
            bool inOrigin = false;
            foreach (string raw in File.ReadLines(config))
            {
                string line = raw.Trim();
                if (line.StartsWith('['))
                {
                    inOrigin = line.Replace(" ", string.Empty, StringComparison.Ordinal).Equals("[remote\"origin\"]", StringComparison.Ordinal);
                }
                else if (inOrigin && line.StartsWith("url", StringComparison.Ordinal) && line.IndexOf('=', StringComparison.Ordinal) is > 0 and int eq)
                {
                    return line[(eq + 1)..].Trim();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }
}

/// <summary>Sums logical sizes with .NET's enumeration (non-Windows development runs only; Windows always uses the M1 walker).</summary>
internal sealed class ManagedMeasurer : IItemMeasurer
{
    public static ManagedMeasurer Instance { get; } = new();

    public WalkResult Measure(string path, SizeMode mode, TimeSpan? timeLimit, CancellationToken cancellationToken)
    {
        long bytes = 0, files = 0;
        foreach (FileInfo file in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }))
        {
            bytes += file.Length;
            files++;
        }

        return new WalkResult { Path = path, Bytes = bytes, LogicalBytes = bytes, Files = files, Complete = true };
    }
}
