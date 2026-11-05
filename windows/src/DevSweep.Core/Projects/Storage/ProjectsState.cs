namespace DevSweep.Core.Projects.Storage;

/// <summary>A line of the Activity trail.</summary>
public enum ActivityKind
{
    Download,
    Remove,
    Publish,
    Push,
    AddRepo,
    Identity,
    SignIn,
    Fetch,
}

/// <summary>One line in the Activity trail: a download, removal, publish, push, added repo, identity change or sign-in.</summary>
public sealed record ActivityEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public DateTimeOffset Date { get; init; } = DateTimeOffset.UtcNow;

    public required ActivityKind Kind { get; init; }

    /// <summary><c>owner/name</c>, a folder, or an account.</summary>
    public required string Subject { get; init; }

    public string Detail { get; init; } = string.Empty;
}

/// <summary>A repository DevSweep remembers: added by URL, downloaded, or seen on disk (so a removed one can come back where it was).</summary>
public sealed record KnownRepo
{
    public required string NameWithOwner { get; init; }

    public string? Account { get; init; }

    public bool? IsPrivate { get; init; }

    public long? RemoteKB { get; init; }

    /// <summary>The folder it last lived in, so a download after removal lands in the same place.</summary>
    public string? LastPath { get; init; }

    /// <summary>Removed from this PC by DevSweep (it stays listed with Download).</summary>
    public DateTimeOffset? RemovedAt { get; init; }
}

/// <summary>Everything Projects remembers (<c>%LOCALAPPDATA%\DevSweep\projects.json</c>). Every field is optional on disk.</summary>
public sealed record ProjectsState
{
    /// <summary>Where new downloads go: <c>&lt;root&gt;\&lt;account&gt;\&lt;repo&gt;</c>. Null means <c>%USERPROFILE%\Code</c>.</summary>
    public string? WorkspaceRoot { get; init; }

    /// <summary>Extra folders searched for clones (Settings), besides the common ones.</summary>
    public List<string> ProjectRoots { get; init; } = [];

    /// <summary>Cleanup's "not opened or changed for" (7 to 90 days; 21 by default).</summary>
    public int IdleDays { get; init; } = IdleCleanup.DefaultDays;

    /// <summary>Commit identity per GitHub account.</summary>
    public Dictionary<string, GitIdentity> Identities { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, CloneStrategy> Strategies { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, DateTimeOffset> LastOpened { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public List<KnownRepo> Known { get; init; } = [];

    /// <summary>Newest first; the last <see cref="ProjectStore.MaxActivity"/> kept.</summary>
    public List<ActivityEntry> Activity { get; init; } = [];

    /// <summary>
    /// Fills what an older (or hand-edited) file left out: source-generated JSON sets a missing
    /// init-only property to null/0, not to its initializer, so every field is checked here.
    /// </summary>
    public ProjectsState Normalised() => this with
    {
        ProjectRoots = ProjectRoots ?? [],
        IdleDays = IdleDays == 0 ? IdleCleanup.DefaultDays : IdleCleanup.Clamp(IdleDays),
        Identities = new Dictionary<string, GitIdentity>(
            (Identities ?? []).Where(p => p.Value is not null).Select(p => KeyValuePair.Create(p.Key, new GitIdentity(p.Value.Name ?? string.Empty, p.Value.Email ?? string.Empty))),
            StringComparer.OrdinalIgnoreCase),
        Strategies = new Dictionary<string, CloneStrategy>(Strategies ?? [], StringComparer.OrdinalIgnoreCase),
        LastOpened = new Dictionary<string, DateTimeOffset>(LastOpened ?? [], StringComparer.OrdinalIgnoreCase),
        Known = (Known ?? []).Where(k => k?.NameWithOwner is { Length: > 0 }).ToList(),
        Activity = (Activity ?? []).Where(a => a?.Subject is not null).Select(a => a with { Detail = a.Detail ?? string.Empty }).ToList(),
    };
}
