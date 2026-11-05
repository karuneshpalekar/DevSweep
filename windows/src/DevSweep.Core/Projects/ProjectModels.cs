using System.Globalization;
using DevSweep.Core.Projects.Git;

namespace DevSweep.Core.Projects;

/// <summary>
/// The commit identity written into a clone's own <c>.git\config</c>, so commits made
/// with one GitHub account are attributed to it whatever the global identity says.
/// </summary>
public sealed record GitIdentity(string Name = "", string Email = "")
{
    public bool IsBlank => string.IsNullOrWhiteSpace(Name) && string.IsNullOrWhiteSpace(Email);

    public override string ToString() => $"{Name} <{Email}>";
}

/// <summary>How much of a repository a download fetches.</summary>
public enum CloneStrategy
{
    /// <summary>Full history; file contents download when needed (<c>--filter=blob:none</c>). The default.</summary>
    Blobless,

    /// <summary>Latest commit only (<c>--depth=1</c>).</summary>
    Shallow,

    /// <summary>Everything.</summary>
    Full,
}

/// <summary>What each <see cref="CloneStrategy"/> means (the Mac app's text and size factors).</summary>
public static class CloneStrategies
{
    public static IReadOnlyList<CloneStrategy> All { get; } = [CloneStrategy.Blobless, CloneStrategy.Shallow, CloneStrategy.Full];

    public static string Title(this CloneStrategy strategy) => strategy switch
    {
        CloneStrategy.Blobless => "Blobless",
        CloneStrategy.Shallow => "Shallow",
        _ => "Full",
    };

    public static string Summary(this CloneStrategy strategy) => strategy switch
    {
        CloneStrategy.Blobless => "Full history; file contents download when needed. Best default.",
        CloneStrategy.Shallow => "Latest commit only. Smallest, but no history or blame.",
        _ => "Everything, offline forever. Largest.",
    };

    /// <summary>Extra arguments for <c>git clone</c>.</summary>
    public static IReadOnlyList<string> GitArguments(this CloneStrategy strategy) => strategy switch
    {
        CloneStrategy.Blobless => ["--filter=blob:none"],
        CloneStrategy.Shallow => ["--depth=1"],
        _ => [],
    };

    /// <summary>Rough fraction of the repository's size on GitHub that ends up on disk (the Mac app's factors).</summary>
    public static double SizeFactor(this CloneStrategy strategy) => strategy switch
    {
        CloneStrategy.Blobless => 0.35,
        CloneStrategy.Shallow => 0.12,
        _ => 1.0,
    };

    /// <summary>
    /// The download estimate from the size GitHub reports (KB), or null when GitHub did not say.
    /// An estimate only: a checkout also writes the working files.
    /// </summary>
    public static long? EstimateBytes(this CloneStrategy strategy, long remoteKB)
        => remoteKB > 0 ? (long)(remoteKB * 1024 * strategy.SizeFactor()) : null;

    public static CloneStrategy? Parse(string? text)
        => Enum.TryParse(text, ignoreCase: true, out CloneStrategy s) && Enum.IsDefined(s) ? s : null;
}

/// <summary>A GitHub account the GitHub CLI is signed in to.</summary>
/// <param name="Login">The GitHub user name.</param>
/// <param name="IsActive">The account <c>gh</c> and git use in a terminal (DevSweep never changes it).</param>
/// <param name="Host">Always github.com for now.</param>
/// <param name="Source">Where it was listed (<c>gh auth status</c> or hosts.yml).</param>
public sealed record GitHubAccount(string Login, bool IsActive, string Host = "github.com", string Source = "gh auth status");

/// <summary>A repository one signed-in account can read, from the GitHub API.</summary>
public sealed record RemoteRepo
{
    public required string Name { get; init; }

    public required string NameWithOwner { get; init; }

    public string Description { get; init; } = string.Empty;

    public bool IsPrivate { get; init; }

    public DateTimeOffset? PushedAt { get; init; }

    public string Url { get; init; } = string.Empty;

    /// <summary>GitHub's <c>size</c>, in KB.</summary>
    public long DiskUsageKB { get; init; }

    /// <summary>The signed-in account that can read it.</summary>
    public required string Account { get; init; }

    public string Owner => NameWithOwner.Split('/')[0];
}

/// <summary>How loud a project's status should be.</summary>
public enum StatusLevel
{
    Ok,
    Attention,
    Info,
}

/// <summary>
/// One row of the Projects list: a repository on GitHub, a clone on this PC (matched by
/// its <c>origin</c>), a folder with no GitHub origin, or a repository removed earlier.
/// </summary>
public sealed record Project
{
    /// <summary><c>owner/name</c>, or <c>local:&lt;path&gt;</c> for a folder with no GitHub origin.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string NameWithOwner { get; init; }

    public string Owner { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public bool? IsPrivate { get; init; }

    /// <summary>The folder on this PC; null when it is only on GitHub.</summary>
    public string? LocalPath { get; init; }

    /// <summary>On-disk bytes of the folder (overview measure; exact in the cleanup list).</summary>
    public long LocalSize { get; init; }

    /// <summary>True when <see cref="LocalSize"/> is complete (no time limit hit, nothing unreadable).</summary>
    public bool LocalSizeComplete { get; init; } = true;

    public long RemoteKB { get; init; }

    public bool OnGitHub { get; init; }

    /// <summary>The signed-in account that can fetch it; null for clones from owners nobody here is signed in as.</summary>
    public string? Account { get; init; }

    /// <summary>What removing it would lose; null when it is not on disk.</summary>
    public RemovalSafety? Safety { get; init; }

    public DateTimeOffset? LastOpened { get; init; }

    /// <summary>The newest of the last commit, the last push on GitHub and git's own HEAD/index times.</summary>
    public DateTimeOffset? LastActivity { get; init; }

    public CloneStrategy? Strategy { get; init; }

    /// <summary>The folder is inside a OneDrive (cloud) folder.</summary>
    public bool InCloudFolder { get; init; }

    public bool OnDisk => LocalPath is not null;

    /// <summary>How recently it was used: opened here, else its last activity.</summary>
    public DateTimeOffset? LastUsed => (LastOpened, LastActivity) switch
    {
        ({ } a, { } b) => a > b ? a : b,
        ({ } a, null) => a,
        (null, { } b) => b,
        _ => null,
    };

    /// <summary>The list's status text.</summary>
    public string StatusText => Status.Text;

    public StatusLevel StatusLevel => Status.Level;

    /// <summary>A short status for the list (the Mac wording, "this PC" for "this Mac").</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public (string Text, StatusLevel Level) Status
    {
        get
        {
            if (!OnDisk)
            {
                return ("Only on GitHub", StatusLevel.Info);
            }

            if (Safety is not { } s)
            {
                return ("Not checked", StatusLevel.Attention);
            }

            if (s.State is SafetyState.Unknown or SafetyState.NotChecked)
            {
                return (s.Headline, StatusLevel.Attention);
            }

            if (!s.HasRemote)
            {
                return ("Only on this PC", StatusLevel.Attention);
            }

            var parts = new List<string>();
            if (s.UnpushedCommits > 0)
            {
                parts.Add(Plural(s.UnpushedCommits, "commit") + " not pushed");
            }

            if (s.ChangedFiles > 0)
            {
                parts.Add(Plural(s.ChangedFiles, "changed file"));
            }

            if (s.UntrackedFiles > 0)
            {
                parts.Add(Plural(s.UntrackedFiles, "untracked file"));
            }

            if (parts.Count == 0 && s.Stashes > 0)
            {
                parts.Add(s.Stashes == 1 ? "1 stash" : $"{s.Stashes} stashes");
            }

            if (parts.Count == 0 && !s.IsSafeToRemove)
            {
                parts.Add(s.Headline);
            }

            return parts.Count == 0 ? ("Up to date", StatusLevel.Ok) : (string.Join(" · ", parts), StatusLevel.Attention);
        }
    }

    internal static string Plural(long count, string noun)
        => string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? string.Empty : "s")}");
}
