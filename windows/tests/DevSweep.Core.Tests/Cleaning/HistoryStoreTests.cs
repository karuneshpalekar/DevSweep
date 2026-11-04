using System.Text.Json;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Storage;
using Xunit;

namespace DevSweep.Core.Tests.Cleaning;

public sealed class HistoryStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DevSweepUnit", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Entries_round_trip_through_the_file_newest_first()
    {
        string path = Path.Combine(_folder, "history.json");
        var store = new HistoryStore(path);
        HistoryEntry first = Sample("first");
        HistoryEntry second = Sample("second") with
        {
            Items =
            [
                new MovedItem { Original = @"C:\Users\dev\a", Destination = ItemDestination.RecycleBin, Location = @"C:\$Recycle.Bin\S-1\$RABC", RecycleInfo = @"C:\$Recycle.Bin\S-1\$IABC", Bytes = 10 },
                new MovedItem { Original = @"C:\Users\dev\b", Destination = ItemDestination.Quarantine, Location = @"C:\q\id\b", QuarantineId = "id", Bytes = 20, Note = "too big" },
            ],
            Reconciliation = new FreedSpace { Volume = @"C:\", ShownBytes = 30, ExpectedFreedBytes = 0, FreeBefore = 100, FreeAfter = 100 },
        };
        store.Append(first);
        store.Append(second);

        var reloaded = new HistoryStore(path);

        Assert.Equal([second.Id, first.Id], reloaded.Entries.Select(e => e.Id));
        HistoryEntry read = reloaded.Entries[0];
        Assert.Equal(second.Title, read.Title);
        Assert.Equal(second.Items, read.Items);
        Assert.Equal(second.Reconciliation, read.Reconciliation);
        Assert.Equal(CleanActionKind.Trash, read.ActionKind);
        Assert.Null(reloaded.SetAsidePath);
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    [Fact]
    public void Stored_json_uses_camel_case_and_enum_names()
    {
        string path = Path.Combine(_folder, "history.json");
        new HistoryStore(path).Append(Sample("x"));

        string json = File.ReadAllText(path);
        Assert.Contains("\"actionKind\": \"Trash\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("canRestore", json, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreadable_file_is_set_aside_not_overwritten()
    {
        string path = Path.Combine(_folder, "history.json");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(path, "{ not json");

        var store = new HistoryStore(path);

        Assert.Empty(store.Entries);
        Assert.NotNull(store.SetAsidePath);
        Assert.Equal("{ not json", File.ReadAllText(store.SetAsidePath));
        store.Append(Sample("after"));
        Assert.Single(new HistoryStore(path).Entries);
    }

    [Fact]
    public void Atomic_write_replaces_existing_contents()
    {
        string path = Path.Combine(_folder, "file.json");
        AtomicFile.Write(path, "one"u8);
        AtomicFile.Write(path, "two"u8);

        Assert.Equal("two", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public void Freed_space_tolerance_is_one_percent_or_ten_megabytes()
    {
        var small = new FreedSpace { Volume = @"C:\", ExpectedFreedBytes = 1_000_000, FreeBefore = 0, FreeAfter = 1_000_000 + (9 * 1024 * 1024) };
        var big = new FreedSpace { Volume = @"C:\", ExpectedFreedBytes = 10_000_000_000, FreeBefore = 0, FreeAfter = 9_850_000_000 };

        Assert.True(small.WithinTolerance);
        Assert.Equal(FreedSpace.MinimumTolerance, small.Tolerance);
        Assert.Equal(100_000_000, big.Tolerance);
        Assert.False(big.WithinTolerance);
        Assert.Equal(-150_000_000, big.Difference);
    }

    [Fact]
    public void Quarantine_entry_serialises_with_the_source_generator()
    {
        var entry = new DevSweep.Core.Platform.Quarantine.QuarantineEntry
        {
            Id = "20260101-000000-abcdef12",
            OriginalPath = @"C:\Users\dev\x",
            StoredPath = @"C:\q\20260101-000000-abcdef12\x",
            Bytes = 4096,
            QuarantinedAt = DateTimeOffset.UnixEpoch,
        };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(entry, CoreJsonContext.Default.QuarantineEntry);
        Assert.Equal(entry, JsonSerializer.Deserialize(json, CoreJsonContext.Default.QuarantineEntry));
    }

    private static HistoryEntry Sample(string title) => new()
    {
        Title = title,
        ActionKind = CleanActionKind.Trash,
        ActionLabel = "Move to Recycle Bin",
        Size = 30,
    };
}
