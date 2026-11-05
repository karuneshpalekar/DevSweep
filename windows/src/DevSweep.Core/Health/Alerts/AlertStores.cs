using System.Text.Json;
using System.Text.Json.Serialization;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Storage;

namespace DevSweep.Core.Health.Alerts;

/// <summary>What a scan found, reduced to sizes, so later scans can say what grew.</summary>
public sealed record ScanSnapshot
{
    public DateTimeOffset Date { get; init; }

    /// <summary>Size per finding id.</summary>
    public Dictionary<string, long> Sizes { get; init; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Titles { get; init; } = new(StringComparer.Ordinal);

    public long? DiskFree { get; init; }
}

/// <summary>What grew (or is new and big) since an earlier snapshot.</summary>
public sealed record ScanChanges(DateTimeOffset Since, IReadOnlyList<ScanChange> Grew, IReadOnlyList<ScanChange> New);

/// <summary>Growth for items that grew; the whole size for new items.</summary>
public sealed record ScanChange(string Id, string Title, long Bytes);

/// <summary>
/// The last 40 scan snapshots in <c>%LOCALAPPDATA%\DevSweep\scans.json</c> (atomic writes; an
/// unreadable file is set aside, never overwritten). M2 keeps no snapshots, so this is the minimal
/// store the growth alert needs.
/// </summary>
public sealed class ScanSnapshotStore
{
    public const int Keep = 40;
    private readonly List<ScanSnapshot> _snapshots;

    public ScanSnapshotStore(string path)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        _snapshots = HealthStoreFile.Read(path, HealthJsonContext.Default.ListScanSnapshot) ?? [];
    }

    public string Path { get; }

    public IReadOnlyList<ScanSnapshot> Snapshots => _snapshots;

    /// <summary>
    /// Compares sizes with the newest snapshot at least <paramref name="days"/> old (or, failing that,
    /// the oldest one from an earlier day). Null on a first scan.
    /// </summary>
    public ScanChanges? Changes(IEnumerable<(string Id, string Title, long Size)> findings, DateTimeOffset now, int days = 7, long minimum = 200_000_000)
    {
        ArgumentNullException.ThrowIfNull(findings);
        DateTimeOffset cutoff = now.AddDays(-(days - 1));
        DateTimeOffset earlierDay = now.AddHours(-12);
        ScanSnapshot? baseline = _snapshots.LastOrDefault(s => s.Date <= cutoff) ?? _snapshots.FirstOrDefault(s => s.Date <= earlierDay);
        if (baseline is null)
        {
            return null;
        }

        var grew = new List<ScanChange>();
        var fresh = new List<ScanChange>();
        foreach ((string id, string title, long size) in findings)
        {
            if (baseline.Sizes.TryGetValue(id, out long before))
            {
                if (size - before >= minimum)
                {
                    grew.Add(new ScanChange(id, title, size - before));
                }
            }
            else if (size >= minimum)
            {
                fresh.Add(new ScanChange(id, title, size));
            }
        }

        return new ScanChanges(baseline.Date, grew.OrderByDescending(c => c.Bytes).ToList(), fresh.OrderByDescending(c => c.Bytes).ToList());
    }

    public ScanChanges? Changes(IReadOnlyList<Finding> findings, DateTimeOffset now, int days = 7)
    {
        ArgumentNullException.ThrowIfNull(findings);
        return Changes(findings.Select(f => (f.Id, f.Title, f.Size)), now, days);
    }

    public void Record(IEnumerable<(string Id, string Title, long Size)> findings, long? diskFree, DateTimeOffset date)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var snapshot = new ScanSnapshot { Date = date, DiskFree = diskFree };
        foreach ((string id, string title, long size) in findings)
        {
            snapshot.Sizes[id] = size;
            snapshot.Titles[id] = title;
        }

        _snapshots.Add(snapshot);
        if (_snapshots.Count > Keep)
        {
            _snapshots.RemoveRange(0, _snapshots.Count - Keep);
        }

        HealthStoreFile.Write(Path, _snapshots, HealthJsonContext.Default.ListScanSnapshot);
    }
}

/// <summary>What <c>alerts.json</c> holds: the last scheduled run, the ledger, and the secret files seen so far.</summary>
public sealed record AlertData
{
    public DateTimeOffset? LastScheduledRun { get; init; }

    public AlertLedger Ledger { get; init; } = new();

    /// <summary>Null until the first scheduled run has recorded a baseline.</summary>
    public List<string>? KnownSecretIds { get; init; }
}

/// <summary><c>%LOCALAPPDATA%\DevSweep\alerts.json</c>, written atomically after every change.</summary>
public sealed class AlertStore
{
    private readonly Lock _lock = new();

    public AlertStore(string path)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Data = HealthStoreFile.Read(path, HealthJsonContext.Default.AlertData) ?? new AlertData();
    }

    public string Path { get; }

    public AlertData Data { get; private set; }

    public void Update(Func<AlertData, AlertData> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_lock)
        {
            Data = change(Data);
            HealthStoreFile.Write(Path, Data, HealthJsonContext.Default.AlertData);
        }
    }
}

/// <summary>Reads and writes Health's JSON files; a file that cannot be parsed is renamed aside, never overwritten.</summary>
internal static class HealthStoreFile
{
    public static T? Read<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(path), type);
        }
        catch (JsonException)
        {
            try
            {
                File.Move(path, path + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (IOException)
            {
                // Leave it; it will be replaced by an atomic write only if it could be moved.
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Write<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
        => AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(value, type));
}

/// <summary>Source-generated JSON for Health's own files and results (AOT and trim safe).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<ScanSnapshot>))]
[JsonSerializable(typeof(AlertData))]
public sealed partial class HealthJsonContext : JsonSerializerContext;
