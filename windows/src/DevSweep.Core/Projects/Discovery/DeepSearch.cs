using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;

namespace DevSweep.Core.Projects.Discovery;

/// <summary>What a deep search is to look at.</summary>
public sealed record DeepSearchOptions
{
    /// <summary>The folders to walk (drive roots, or any folder).</summary>
    public required IReadOnlyList<string> Roots { get; init; }

    /// <summary>Stop after this long and return what was found (flagged partial). Null means no limit.</summary>
    public TimeSpan? TimeBudget { get; init; }

    /// <summary>Folders listed at once. A slow file system (WSL's 9P) wants fewer.</summary>
    public int Parallelism { get; init; } = Math.Clamp(Environment.ProcessorCount, 2, 8);

    /// <summary>How many levels inside a found repository are still searched for nested repositories (0 = none).</summary>
    public int NestedDepth { get; init; } = 2;

    public IProgress<DeepSearchProgress>? Progress { get; init; }
}

/// <summary>Where a deep search is now.</summary>
public sealed record DeepSearchProgress(long Folders, int Repos, string Current);

/// <summary>Why a deep search ended.</summary>
public enum DeepSearchStop
{
    Finished,
    Cancelled,
    TimeBudget,
}

/// <summary>What a deep search found and what it could not look at.</summary>
public sealed record DeepSearchResult
{
    public IReadOnlyList<FoundRepo> Repos { get; init; } = [];

    public IReadOnlyList<string> Roots { get; init; } = [];

    public long FoldersVisited { get; init; }

    public TimeSpan Elapsed { get; init; }

    public DeepSearchStop Stopped { get; init; }

    public bool Complete => Stopped == DeepSearchStop.Finished;

    /// <summary>Folders that could not be listed (access denied or an error).</summary>
    public int Unreadable { get; init; }

    public IReadOnlyList<string> UnreadableSample { get; init; } = [];

    /// <summary>Links (junctions, symlinks, mount points) seen and not entered.</summary>
    public int LinksNotFollowed { get; init; }

    /// <summary>OneDrive folders that are only in the cloud (listing them would download them).</summary>
    public int CloudOnly { get; init; }

    public IReadOnlyList<string> CloudOnlySample { get; init; } = [];

    /// <summary>Bare repositories (no working files): not projects, not entered.</summary>
    public int BareRepos { get; init; }

    /// <summary>Dependency, build and system folders passed over by name.</summary>
    public int Pruned { get; init; }

    public double FoldersPerSecond => Elapsed.TotalSeconds > 0 ? FoldersVisited / Elapsed.TotalSeconds : 0;
}

/// <summary>
/// Finds git working copies anywhere on a drive (the "Find all repos on this PC" search): metadata only
/// (names and attributes from the directory listing, no file is opened), several folders at a time, never
/// through links or cloud-only folders. A folder with <c>.git</c> is a project; <c>.git</c> itself is never
/// entered, nor are dependency and build folders (<c>node_modules</c>, <c>target</c>, <c>.venv</c>...),
/// so a huge tree costs nothing once its folder name is seen. Submodules (a <c>.git</c> file that points into
/// <c>modules</c>) belong to their parent and are not listed; bare repositories are counted, not listed.
/// Cancelling or running out of time returns what was found so far, flagged.
/// </summary>
public static class DeepSearch
{
    /// <summary>Passed over wherever they are.</summary>
    private static readonly HashSet<string> PrunedAnywhere = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".venv", "venv", "target", "bin", "obj", ".gradle", ".next", ".cache", "__pycache__", "site-packages",
        ".nuget", ".m2", ".cargo", "$Recycle.Bin", "System Volume Information", "WinSxS", "Windows.old", ".git",
    };

    /// <summary>Passed over only directly under a drive root.</summary>
    private static readonly HashSet<string> PrunedAtRoot = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Recovery", "$WinREAgent", "Config.Msi", "$SysReset", "Documents and Settings",
    };

    private const int MaxSamples = 8;

    private static readonly EnumerationOptions ListOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Simple,
    };

    /// <summary>The roots a deep search starts from: every fixed drive, and removable drives when asked.</summary>
    public static IReadOnlyList<string> DriveRoots(bool includeRemovable)
    {
        var roots = new List<string>();
        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                bool wanted = drive.DriveType == DriveType.Fixed || (includeRemovable && drive.DriveType == DriveType.Removable);
                if (wanted && drive.IsReady)
                {
                    roots.Add(drive.RootDirectory.FullName);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Whatever was listed so far is used.
        }

        return roots;
    }

    /// <summary>True when a folder of this name is never entered (see the class summary).</summary>
    public static bool IsPruned(string name, string? parentName, bool parentIsDriveRoot)
        => PrunedAnywhere.Contains(name)
            || (parentIsDriveRoot && PrunedAtRoot.Contains(name))
            || (name.Equals("pkg", StringComparison.OrdinalIgnoreCase) && string.Equals(parentName, "go", StringComparison.OrdinalIgnoreCase));

    public static Task<DeepSearchResult> RunAsync(DeepSearchOptions options, CancellationToken cancellationToken = default)
        => Task.Run(() => Run(options, cancellationToken), CancellationToken.None);

    public static DeepSearchResult Run(DeepSearchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new Walk(options, cancellationToken).Execute();
    }

    private readonly record struct Entry(string Name, bool IsDirectory, FileAttributes Attributes)
    {
        public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;

        public bool IsCloudPlaceholder => (Attributes & (FileAttributes.Offline | (FileAttributes)0x0004_0000 | (FileAttributes)0x0040_0000)) != 0;
    }

    /// <param name="Left">Levels still to list inside a repository (only when <paramref name="Inside"/>).</param>
    private readonly record struct Item(string Path, string? ParentName, bool ParentIsRoot, bool Inside, int Left, bool Cloud);

    private sealed class Walk(DeepSearchOptions options, CancellationToken cancellationToken)
    {
        private readonly ConcurrentStack<Item> _work = new();
        private readonly ConcurrentDictionary<string, FoundRepo> _repos = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<string> _unreadable = new();
        private readonly ConcurrentQueue<string> _cloud = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _folders;
        private int _pending;
        private int _unreadableCount;
        private int _cloudCount;
        private int _links;
        private int _bare;
        private int _pruned;
        private volatile int _stop; // 0 running, 1 cancelled, 2 time budget

        public DeepSearchResult Execute()
        {
            var roots = new List<string>();
            foreach (string raw in options.Roots)
            {
                string root = System.IO.Path.GetFullPath(raw);
                if (!Directory.Exists(root))
                {
                    continue;
                }

                roots.Add(root);
                Interlocked.Increment(ref _pending);
                _work.Push(new Item(root, null, false, false, 0, false));
            }

            int workers = Math.Max(1, options.Parallelism);
            var tasks = new Task[workers];
            for (int i = 0; i < workers; i++)
            {
                tasks[i] = Task.Factory.StartNew(Work, TaskCreationOptions.LongRunning);
            }

            Task.WaitAll(tasks);
            _clock.Stop();
            options.Progress?.Report(new DeepSearchProgress(Interlocked.Read(ref _folders), _repos.Count, string.Empty));
            return new DeepSearchResult
            {
                Repos = [.. _repos.Values.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase)],
                Roots = roots,
                FoldersVisited = Interlocked.Read(ref _folders),
                Elapsed = _clock.Elapsed,
                Stopped = _stop switch { 1 => DeepSearchStop.Cancelled, 2 => DeepSearchStop.TimeBudget, _ => DeepSearchStop.Finished },
                Unreadable = _unreadableCount,
                UnreadableSample = [.. _unreadable.Take(MaxSamples)],
                LinksNotFollowed = _links,
                CloudOnly = _cloudCount,
                CloudOnlySample = [.. _cloud.Take(MaxSamples)],
                BareRepos = _bare,
                Pruned = _pruned,
            };
        }

        private void Work()
        {
            var spin = new SpinWait();
            while (true)
            {
                if (_stop == 0)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _stop = 1;
                    }
                    else if (options.TimeBudget is { } budget && _clock.Elapsed > budget)
                    {
                        _stop = 2;
                    }
                }

                if (_stop != 0)
                {
                    return;
                }

                if (_work.TryPop(out Item item))
                {
                    spin.Reset();
                    try
                    {
                        Visit(item);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _pending);
                    }

                    continue;
                }

                if (Volatile.Read(ref _pending) == 0)
                {
                    return;
                }

                spin.SpinOnce(sleep1Threshold: 20);
            }
        }

        private void Visit(Item item)
        {
            long count = Interlocked.Increment(ref _folders);
            if (count % 256 == 0)
            {
                options.Progress?.Report(new DeepSearchProgress(count, _repos.Count, item.Path));
            }

            List<Entry>? entries = List(item.Path);
            if (entries is null)
            {
                Interlocked.Increment(ref _unreadableCount);
                if (_unreadable.Count < MaxSamples)
                {
                    _unreadable.Enqueue(item.Path);
                }

                return;
            }

            bool inside = item.Inside;
            int left = item.Left;
            Entry git = entries.Find(e => e.Name.Equals(".git", StringComparison.OrdinalIgnoreCase));
            if (git.Name is not null)
            {
                if (!git.IsDirectory && !IsLinkedWorktree(item.Path, git))
                {
                    return; // a submodule (or an unreadable .git file): part of its parent, not a project of its own
                }

                _repos.TryAdd(item.Path, new FoundRepo(item.Path, git.IsCloudPlaceholder, item.Cloud));
                inside = true;
                left = options.NestedDepth;
            }
            else if (IsBare(item.Path, entries))
            {
                Interlocked.Increment(ref _bare);
                return;
            }

            if (inside && left <= 0)
            {
                return;
            }

            string name = System.IO.Path.GetFileName(item.Path);
            bool thisIsRoot = IsDriveRoot(item.Path);
            foreach (Entry entry in entries)
            {
                if (!entry.IsDirectory)
                {
                    continue;
                }

                if (IsPruned(entry.Name, name.Length == 0 ? null : name, thisIsRoot))
                {
                    Interlocked.Increment(ref _pruned);
                    continue;
                }

                string child = System.IO.Path.Join(item.Path, entry.Name);
                bool cloud = item.Cloud;
                if (entry.IsCloudPlaceholder)
                {
                    NoteCloud(child);
                    continue;
                }

                if (entry.IsReparsePoint)
                {
                    if (ProjectFinder.IsEnterableCloudFolder(child, out bool placeholder))
                    {
                        cloud = true;
                    }
                    else
                    {
                        if (placeholder)
                        {
                            NoteCloud(child);
                        }
                        else
                        {
                            Interlocked.Increment(ref _links);
                        }

                        continue;
                    }
                }

                Interlocked.Increment(ref _pending);
                _work.Push(new Item(child, entry.Name, thisIsRoot, inside, inside ? left - 1 : 0, cloud));
            }
        }

        private void NoteCloud(string path)
        {
            Interlocked.Increment(ref _cloudCount);
            if (_cloud.Count < MaxSamples)
            {
                _cloud.Enqueue(path);
            }
        }
    }

    private static bool IsDriveRoot(string path)
        => System.IO.Path.GetPathRoot(path) is { Length: > 0 } root
            && string.Equals(System.IO.Path.TrimEndingDirectorySeparator(root), System.IO.Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase);

    /// <summary>Folder entries and the two file names that matter (<c>.git</c>, <c>HEAD</c>); null when the folder can't be listed.</summary>
    private static List<Entry>? List(string directory)
    {
        try
        {
            var listing = new FileSystemEnumerable<Entry>(directory, Transform, ListOptions) { ShouldIncludePredicate = Include };
            return [.. listing];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static Entry Transform(ref FileSystemEntry entry) => new(entry.FileName.ToString(), entry.IsDirectory, entry.Attributes);

    private static bool Include(ref FileSystemEntry entry)
        => entry.IsDirectory || entry.FileName.Equals(".git", StringComparison.OrdinalIgnoreCase) || entry.FileName.Equals("HEAD", StringComparison.Ordinal);

    /// <summary>A <c>.git</c> file ("gitdir: ...") of a linked worktree is a project; one that points into <c>modules</c> is a submodule.</summary>
    private static bool IsLinkedWorktree(string folder, Entry git)
    {
        if (git.IsCloudPlaceholder)
        {
            return true; // listed as a project; the inspector does not run git on it
        }

        try
        {
            using var reader = new StreamReader(System.IO.Path.Join(folder, ".git"));
            string? line = reader.ReadLine();
            if (line is null || !line.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !line[7..].Trim().Replace('\\', '/').Contains("/modules/", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsBare(string folder, List<Entry> entries)
        => entries.Exists(e => !e.IsDirectory && e.Name == "HEAD")
            && Directory.Exists(System.IO.Path.Join(folder, "objects"))
            && Directory.Exists(System.IO.Path.Join(folder, "refs"));
}
