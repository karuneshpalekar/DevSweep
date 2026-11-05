using System.Globalization;
using System.IO.Enumeration;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Detectors;
using DevSweep.Core.Inventory.Registry;
using DevSweep.Core.Locations;
using DevSweep.Core.Rules;
using Microsoft.Win32;

namespace DevSweep.Core.Packs.Detectors;

/// <summary>
/// <c>kind: largeOldFiles</c> (the Mac detector, ported). Big files not used for a while.
/// "Used" is the later of when the file was last changed or put there and, only when NTFS
/// records it (<c>NtfsDisableLastAccessUpdate</c> says last-access updates are on), when it was
/// last opened. Every finding says which date was used. Cloud-only files are never looked at.
/// <list type="bullet">
/// <item><c>roots</c>: folders to look in (four levels deep; <c>node_modules</c>, <c>.git</c> and links not entered);</item>
/// <item><c>extensions</c>: only these file types (default: any);</item>
/// <item><c>fileMinMB</c>: smallest file (default 200);</item>
/// <item><c>unusedDays</c>: default 120.</item>
/// </list>
/// </summary>
public sealed class LargeOldFilesDetector : Detector<LargeOldFilesDetector.Options>
{
    public const string FileSystemKey = @"SYSTEM\CurrentControlSet\Control\FileSystem";
    private const int MaxDepth = 4;

    private static readonly HashSet<string> NeverEnter = new(StringComparer.OrdinalIgnoreCase) { "node_modules", ".git", "AppData", "$RECYCLE.BIN" };

    public override string Kind => "largeOldFiles";

    /// <summary>Whether NTFS updates last-access times: true, false, or null when the setting cannot be read.</summary>
    /// <remarks>Low bit set (1, 3, 0x80000001, 0x80000003) means updates are off; 0x80000000 marks "system managed".</remarks>
    public static bool? LastAccessRecorded(IRegistryReader registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        try
        {
            using IRegistryKeyReader? key = registry.Open(RegistryHive.LocalMachine, RegistryView.Registry64, FileSystemKey);
            return key?.Value("NtfsDisableLastAccessUpdate") is int value ? (value & 1) == 0 : null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    /// <summary>"3 days ago", "4 months ago", "2 years ago".</summary>
    public static string Ago(DateTime whenUtc, DateTime nowUtc)
    {
        int days = Math.Max(0, (int)(nowUtc - whenUtc).TotalDays);
        return days switch
        {
            < 1 => "today",
            1 => "yesterday",
            < 60 => string.Create(CultureInfo.InvariantCulture, $"{days} days ago"),
            < 730 => string.Create(CultureInfo.InvariantCulture, $"{days / 30} months ago"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{days / 365} years ago"),
        };
    }

    protected override Options? BindOptions(OptionReader options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new Options(
            options.Paths("roots", required: true),
            options.TextList("extensions").Select(e => e.TrimStart('.').ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase),
            options.Number("fileMinMB", 1, 1_000_000) ?? 200,
            options.Number("unusedDays", 1, 3650) ?? 120);
    }

    protected override IReadOnlyList<Candidate> Detect(DetectorRun run, Options options)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);
        DateTime now = run.Context.Now.UtcDateTime;
        DateTime cutoff = now.AddDays(-options.UnusedDays);
        long minBytes = options.FileMinMB * 1_000_000L;
        bool? lastAccess = LastAccessRecorded(run.Context.Registry);
        string dateKind = lastAccess == true ? "last opened" : "last changed";
        string dateNote = lastAccess switch
        {
            true => "NTFS records when files are opened on this PC (last-access updates are on), so the later of the last-opened and last-changed dates is used",
            false => "NTFS does not record when files are opened on this PC (last-access updates are off), so the date is when the file was last changed or put here",
            _ => "Whether NTFS records when files are opened could not be read, so the date is when the file was last changed or put here",
        };

        var candidates = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PathTemplate template in options.Roots)
        {
            foreach (ProbeEntry root in run.Expand(template).Matches.Where(m => m.CanEnter))
            {
                var pending = new Stack<(string Folder, int Depth)>();
                pending.Push((root.Path, 0));
                while (pending.Count > 0)
                {
                    run.ThrowIfCancelled();
                    (string folder, int depth) = pending.Pop();
                    foreach (Entry entry in List(folder, run))
                    {
                        if (entry.IsDirectory)
                        {
                            if (depth < MaxDepth && !NeverEnter.Contains(entry.Name) && !entry.Name.StartsWith('.'))
                            {
                                pending.Push((entry.Path, depth + 1));
                            }

                            continue;
                        }

                        string extension = Path.GetExtension(entry.Name).TrimStart('.');
                        if (entry.Length < minBytes || (options.Extensions.Count > 0 && !options.Extensions.Contains(extension)) || !seen.Add(entry.Path))
                        {
                            continue;
                        }

                        DateTime used = entry.ChangedUtc;
                        if (lastAccess == true && entry.AccessedUtc > used)
                        {
                            used = entry.AccessedUtc;
                        }

                        if (used >= cutoff)
                        {
                            continue;
                        }

                        string ago = Ago(used, now);
                        string where = Path.GetFileName(folder);
                        candidates.Add(new Candidate
                        {
                            Key = entry.Path,
                            Paths = [entry.Path],
                            Title = entry.Name,
                            Subtitle = $"{where}, {dateKind} {ago}",
                            Facts = new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["name"] = entry.Name,
                                ["folder"] = folder,
                                ["ago"] = ago,
                                ["dateKind"] = dateKind,
                                ["date"] = used.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                                ["dateNote"] = dateNote,
                                ["days"] = options.UnusedDays.ToString(CultureInfo.InvariantCulture),
                            },
                            Checks =
                            [
                                Check.Passed($"{char.ToUpperInvariant(dateKind[0])}{dateKind[1..]} {used:yyyy-MM-dd} ({ago}), more than {options.UnusedDays} days ago"),
                                lastAccess == true ? Check.Passed(dateNote) : Check.Warning(dateNote),
                            ],
                        });
                    }
                }
            }
        }

        return candidates;
    }

    private static List<Entry> List(string folder, DetectorRun run)
    {
        try
        {
            return new FileSystemEnumerable<Entry>(
                folder,
                (ref FileSystemEntry e) => new Entry(e.ToFullPath(), e.FileName.ToString(), e.Attributes, e.Length,
                    e.LastWriteTimeUtc.UtcDateTime > e.CreationTimeUtc.UtcDateTime ? e.LastWriteTimeUtc.UtcDateTime : e.CreationTimeUtc.UtcDateTime,
                    e.LastAccessTimeUtc.UtcDateTime),
                new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 })
            {
                // Links, cloud files and hidden or system items are never looked at.
                ShouldIncludePredicate = (ref FileSystemEntry e)
                    => (e.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.Hidden | FileAttributes.System | (FileAttributes)0x0040_0000 | (FileAttributes)0x0004_0000)) == 0,
            }.ToList();
        }
        catch (UnauthorizedAccessException)
        {
            run.Denied(folder);
            return [];
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return [];
        }
    }

    private readonly record struct Entry(string Path, string Name, FileAttributes Attributes, long Length, DateTime ChangedUtc, DateTime AccessedUtc)
    {
        public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
    }

    /// <summary>Bound options of a <c>largeOldFiles</c> rule.</summary>
    public sealed record Options(IReadOnlyList<PathTemplate> Roots, IReadOnlySet<string> Extensions, int FileMinMB, int UnusedDays);
}
