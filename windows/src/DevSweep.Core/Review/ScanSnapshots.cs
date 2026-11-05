using System.Text.Json;
using System.Text.Json.Serialization;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Platform;
using DevSweep.Core.Storage;

namespace DevSweep.Core.Review;

/// <summary>What a scan found, reduced to sizes, so later scans can say what changed (the Mac ScanSnapshot).</summary>
public sealed record ScanSnapshot
{
    public DateTimeOffset Date { get; init; }

    /// <summary>Finding id to size.</summary>
    public IReadOnlyDictionary<string, long> Sizes { get; init; } = new Dictionary<string, long>();

    /// <summary>Finding id to title.</summary>
    public IReadOnlyDictionary<string, string> Titles { get; init; } = new Dictionary<string, string>();

    /// <summary>Free bytes on the profile's volume when the scan finished.</summary>
    public long? DiskFree { get; init; }
}

/// <summary>One item that grew or appeared.</summary>
/// <param name="Id">The finding id.</param>
/// <param name="Title">Its title.</param>
/// <param name="Bytes">Growth for an item that grew; its whole size for a new item.</param>
public sealed record ScanChange(string Id, string Title, long Bytes);

/// <summary>What grew and what is new since an earlier scan.</summary>
public sealed record ScanChanges(DateTimeOffset Since, IReadOnlyList<ScanChange> Grew, IReadOnlyList<ScanChange> New)
{
    public bool IsEmpty => Grew.Count == 0 && New.Count == 0;
}

/// <summary>
/// Keeps the last <see cref="Keep"/> scan snapshots in <c>%LOCALAPPDATA%\DevSweep\scans.json</c>
/// (source-generated JSON, atomic writes), and compares a new scan with them (Mac semantics).
/// </summary>
public sealed class ScanSnapshotStore
{
    public const int Keep = 40;

    /// <summary>Growth or size below this is not worth mentioning (200 MB, as on the Mac).</summary>
    public const long DefaultMinimum = 200_000_000;

    private readonly Lock _lock = new();
    private List<ScanSnapshot> _snapshots = [];

    public ScanSnapshotStore(string? path = null)
    {
        FilePath = path ?? DefaultPath;
        Load();
    }

    public static string DefaultPath => Path.Combine(KnownFolders.LocalAppData, "DevSweep", "scans.json");

    public string FilePath { get; }

    public IReadOnlyList<ScanSnapshot> Snapshots
    {
        get
        {
            lock (_lock)
            {
                return [.. _snapshots];
            }
        }
    }

    /// <summary>
    /// Compares findings with the most recent snapshot at least <paramref name="days"/> old, or failing
    /// that the oldest one from an earlier part of the day (12 hours or more). Null on a first scan.
    /// </summary>
    public ScanChanges? Changes(IReadOnlyList<Finding> findings, int days = 7, DateTimeOffset? now = null, long minimum = DefaultMinimum)
    {
        ArgumentNullException.ThrowIfNull(findings);
        List<ScanSnapshot> list;
        lock (_lock)
        {
            list = [.. _snapshots];
        }

        return Compare(list, findings, days, now ?? DateTimeOffset.UtcNow, minimum);
    }

    /// <summary>The comparison itself (pure).</summary>
    public static ScanChanges? Compare(IReadOnlyList<ScanSnapshot> snapshots, IReadOnlyList<Finding> findings, int days, DateTimeOffset now, long minimum)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(findings);
        DateTimeOffset cutoff = now.AddDays(-(days - 1));
        DateTimeOffset earlierDay = now.AddHours(-12);
        ScanSnapshot? baseline = snapshots.LastOrDefault(s => s.Date <= cutoff) ?? snapshots.FirstOrDefault(s => s.Date <= earlierDay);
        if (baseline is null)
        {
            return null;
        }

        var grew = new List<ScanChange>();
        var added = new List<ScanChange>();
        foreach (Finding finding in findings)
        {
            if (baseline.Sizes.TryGetValue(finding.Id, out long before))
            {
                if (finding.Size - before >= minimum)
                {
                    grew.Add(new ScanChange(finding.Id, finding.Title, finding.Size - before));
                }
            }
            else if (finding.Size >= minimum)
            {
                added.Add(new ScanChange(finding.Id, finding.Title, finding.Size));
            }
        }

        return new ScanChanges(baseline.Date, [.. grew.OrderByDescending(c => c.Bytes)], [.. added.OrderByDescending(c => c.Bytes)]);
    }

    /// <summary>Records a finished scan (keeps the newest <see cref="Keep"/>).</summary>
    public void Record(IReadOnlyList<Finding> findings, long? diskFree, DateTimeOffset? date = null)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var snapshot = new ScanSnapshot
        {
            Date = date ?? DateTimeOffset.UtcNow,
            Sizes = findings.GroupBy(f => f.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Size, StringComparer.Ordinal),
            Titles = findings.GroupBy(f => f.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Title, StringComparer.Ordinal),
            DiskFree = diskFree,
        };
        lock (_lock)
        {
            _snapshots.Add(snapshot);
            if (_snapshots.Count > Keep)
            {
                _snapshots.RemoveRange(0, _snapshots.Count - Keep);
            }

            AtomicFile.Write(FilePath, JsonSerializer.SerializeToUtf8Bytes(_snapshots, ReviewJsonContext.Default.ListScanSnapshot));
        }
    }

    private void Load()
    {
        lock (_lock)
        {
            try
            {
                _snapshots = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize(File.ReadAllBytes(FilePath), ReviewJsonContext.Default.ListScanSnapshot) ?? []
                    : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // A broken snapshot file only loses "what changed"; start again.
                _snapshots = [];
            }
        }
    }
}

/// <summary>Source-generated JSON for the Review types (snapshots).</summary>
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<ScanSnapshot>))]
internal sealed partial class ReviewJsonContext : JsonSerializerContext;
