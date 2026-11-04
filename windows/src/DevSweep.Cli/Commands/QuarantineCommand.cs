using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using DevSweep.Cli.Output;
using DevSweep.Core.Platform;
using DevSweep.Core.Platform.Quarantine;

namespace DevSweep.Cli.Commands;

/// <summary><c>devsweep quarantine list|restore &lt;id&gt;|empty</c>: DevSweep's own undo for items the Recycle Bin would not keep.</summary>
internal static class QuarantineCommand
{
    public static Command Create()
    {
        var command = new Command("quarantine", "Items DevSweep moved aside because the Recycle Bin would not have kept them.");
        command.Subcommands.Add(List());
        command.Subcommands.Add(Restore());
        command.Subcommands.Add(Empty());
        return command;
    }

    private static Command List()
    {
        var json = new Option<bool>("--json") { Description = "Print the list as JSON." };
        var list = new Command("list", "List quarantined items with their size and age.");
        list.Options.Add(json);
        list.SetAction(parse =>
        {
            IReadOnlyList<QuarantineEntry> entries = new QuarantineStore().List();
            if (parse.GetValue(json))
            {
                Console.Out.WriteLine(JsonSerializer.Serialize([.. entries], CliJsonContext.Default.ListQuarantineEntry));
                return 0;
            }

            if (entries.Count == 0)
            {
                Console.Out.WriteLine("The quarantine is empty.");
                return 0;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (QuarantineEntry entry in entries)
            {
                Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{entry.Id}  {Age(entry.AgeAt(now)),-8} {MeasureText.Bytes(entry.Bytes),-34} {entry.OriginalPath}"));
            }

            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{entries.Count} items, {MeasureText.Bytes(entries.Sum(e => e.Bytes))}"));
            return 0;
        });
        return list;
    }

    private static Command Restore()
    {
        var id = new Argument<string>("id") { Description = "The ID shown by `devsweep quarantine list`." };
        var restore = new Command("restore", "Put a quarantined item back exactly where it was.");
        restore.Arguments.Add(id);
        restore.SetAction(parse =>
        {
            QuarantineRestoreResult result = new QuarantineStore().Restore(parse.GetValue(id)!);
            (result.Restored ? Console.Out : Console.Error).WriteLine(result.Message);
            return result.Restored ? 0 : 1;
        });
        return restore;
    }

    private static Command Empty()
    {
        var olderThan = new Option<double?>("--older-than") { Description = "Only items quarantined at least this many days ago." };
        var yes = new Option<bool>("--yes") { Description = "Really delete them for good (without it, only lists what would go)." };
        var empty = new Command("empty", "Delete quarantined items for good.");
        empty.Options.Add(olderThan);
        empty.Options.Add(yes);
        empty.SetAction(parse =>
        {
            TimeSpan? age = parse.GetValue(olderThan) is { } days ? TimeSpan.FromDays(days) : null;
            var store = new QuarantineStore();
            if (!parse.GetValue(yes))
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                var due = store.List().Where(e => age is null || e.AgeAt(now) >= age).ToList();
                Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{due.Count} items ({MeasureText.Bytes(due.Sum(e => e.Bytes))}) would be deleted for good. Add --yes to do it."));
                return 0;
            }

            int failures = 0;
            foreach ((QuarantineEntry entry, DeleteReport report) in store.Empty(age))
            {
                failures += report.Succeeded ? 0 : 1;
                Console.Out.WriteLine(report.Succeeded
                    ? $"Deleted {entry.Id} ({entry.OriginalPath})"
                    : $"Could not delete {entry.Id}: {string.Join("; ", report.Failures.Select(f => $"{f.Path} (error {f.Win32Error})"))}");
            }

            return failures == 0 ? 0 : 1;
        });
        return empty;
    }

    private static string Age(TimeSpan age) => age.TotalDays >= 1
        ? string.Create(CultureInfo.InvariantCulture, $"{age.TotalDays:0}d")
        : string.Create(CultureInfo.InvariantCulture, $"{age.TotalHours:0}h");
}
