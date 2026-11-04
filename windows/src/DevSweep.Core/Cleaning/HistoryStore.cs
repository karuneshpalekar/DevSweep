using System.Globalization;
using System.Text.Json;
using DevSweep.Core.Platform;
using DevSweep.Core.Platform.Quarantine;
using DevSweep.Core.Platform.Recycling;
using DevSweep.Core.Storage;

namespace DevSweep.Core.Cleaning;

/// <summary>What restoring a History entry did.</summary>
/// <param name="Restored">Items put back.</param>
/// <param name="Messages">One line per item.</param>
public sealed record HistoryRestoreResult(int Restored, IReadOnlyList<string> Messages)
{
    public bool Succeeded => Restored > 0 && Messages.Count == Restored;
}

/// <summary>
/// The log of everything DevSweep changed, newest first, kept as JSON in
/// <c>%LOCALAPPDATA%\DevSweep\history.json</c> (source-generated serializer, atomic
/// writes). Each item records where it went, so it can be put back.
/// </summary>
public sealed class HistoryStore
{
    private readonly Lock _lock = new();
    private List<HistoryEntry> _entries = [];

    public HistoryStore(string? path = null)
    {
        FilePath = path ?? DefaultPath;
        Load();
    }

    /// <summary><c>%LOCALAPPDATA%\DevSweep\history.json</c>.</summary>
    public static string DefaultPath => Path.Combine(KnownFolders.LocalAppData, "DevSweep", "history.json");

    public string FilePath { get; }

    /// <summary>Set when the file could not be read; it was kept aside under this name rather than overwritten.</summary>
    public string? SetAsidePath { get; private set; }

    public IReadOnlyList<HistoryEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries];
            }
        }
    }

    public void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(FilePath))
            {
                _entries = [];
                return;
            }

            byte[] data = File.ReadAllBytes(FilePath);
            try
            {
                _entries = JsonSerializer.Deserialize(data, CoreJsonContext.Default.ListHistoryEntry) ?? [];
            }
            catch (JsonException)
            {
                // Never silently lose History: keep the unreadable file beside it and start afresh.
                SetAsidePath = string.Create(CultureInfo.InvariantCulture, $"{FilePath}.unreadable-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");
                File.Move(FilePath, SetAsidePath);
                _entries = [];
            }
        }
    }

    public void Append(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            _entries.Insert(0, entry);
            Save();
        }
    }

    public HistoryEntry? Find(Guid id)
    {
        lock (_lock)
        {
            return _entries.Find(e => e.Id == id);
        }
    }

    /// <summary>Puts back every item of an entry that went to the Recycle Bin or the quarantine.</summary>
    public HistoryRestoreResult Restore(Guid id, QuarantineStore quarantine)
    {
        ArgumentNullException.ThrowIfNull(quarantine);
        HistoryEntry? entry = Find(id);
        if (entry is null)
        {
            return new HistoryRestoreResult(0, ["No History entry has that ID."]);
        }

        if (entry.RestoredAt is not null)
        {
            return new HistoryRestoreResult(0, ["This entry was already restored."]);
        }

        int restored = 0;
        var messages = new List<string>();
        foreach (MovedItem item in entry.Items)
        {
            (bool ok, string message) = item.Destination switch
            {
                ItemDestination.RecycleBin => FromRecycleBin(item, entry.Date),
                ItemDestination.Quarantine => FromQuarantine(item, quarantine),
                _ => (false, $"{item.Original} was deleted for good and cannot be restored."),
            };
            restored += ok ? 1 : 0;
            messages.Add(message);
        }

        if (restored > 0)
        {
            lock (_lock)
            {
                int index = _entries.FindIndex(e => e.Id == id);
                _entries[index] = _entries[index] with { RestoredAt = DateTimeOffset.UtcNow };
                Save();
            }
        }

        return new HistoryRestoreResult(restored, messages);
    }

    private static (bool, string) FromRecycleBin(MovedItem item, DateTimeOffset cleanedAt)
    {
        RecycledItem? recycled = item.RecycleInfo is { } info ? RecycleBinIndex.TryRead(info) : null;
        recycled ??= RecycleBinIndex.FindNewest(item.Original, cleanedAt.AddMinutes(-1));
        if (recycled is null)
        {
            return (false, $"{item.Original} is no longer in the Recycle Bin (it may have been emptied or restored already).");
        }

        RecycleRestoreResult result = RecycleBinRestore.Restore(recycled);
        return (result.Restored, result.Message);
    }

    private static (bool, string) FromQuarantine(MovedItem item, QuarantineStore quarantine)
    {
        if (item.QuarantineId is not { } id)
        {
            return (false, $"{item.Original}: the quarantine ID was not recorded.");
        }

        QuarantineRestoreResult result = quarantine.Restore(id);
        return (result.Restored, result.Message);
    }

    /// <summary>Caller holds the lock.</summary>
    private void Save()
        => AtomicFile.Write(FilePath, JsonSerializer.SerializeToUtf8Bytes(_entries, CoreJsonContext.Default.ListHistoryEntry));
}
