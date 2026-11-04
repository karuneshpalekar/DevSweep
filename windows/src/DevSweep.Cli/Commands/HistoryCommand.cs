using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using DevSweep.Cli.Output;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Platform.Quarantine;

namespace DevSweep.Cli.Commands;

/// <summary><c>devsweep history [--json]</c> and <c>devsweep history restore &lt;id&gt;</c>.</summary>
internal static class HistoryCommand
{
    public static Command Create()
    {
        var json = new Option<bool>("--json") { Description = "Print History as JSON." };
        var command = new Command("history", "What DevSweep changed, newest first, and where each item went.");
        command.Options.Add(json);
        command.SetAction(parse => Show(parse.GetValue(json)));
        command.Subcommands.Add(Restore());
        return command;
    }

    private static int Show(bool asJson)
    {
        var store = new HistoryStore();
        if (store.SetAsidePath is { } aside)
        {
            Console.Error.WriteLine($"devsweep: History could not be read; it was kept as {aside}.");
        }

        if (asJson)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize([.. store.Entries], CliJsonContext.Default.ListHistoryEntry));
            return 0;
        }

        if (store.Entries.Count == 0)
        {
            Console.Out.WriteLine("History is empty.");
            return 0;
        }

        foreach (HistoryEntry entry in store.Entries)
        {
            string restore = entry.RestoredAt is { } at
                ? string.Create(CultureInfo.InvariantCulture, $"restored {at.ToLocalTime():g}")
                : entry.CanRestore ? "restorable" : string.Empty;
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{entry.Id}  {entry.Date.ToLocalTime():g}  {entry.ActionLabel}: {entry.Title}  {MeasureText.Bytes(entry.Size)}  {restore}"));
            foreach (MovedItem item in entry.Items)
            {
                Console.Out.WriteLine($"    {item.Destination,-10} {item.Original}{(item.Location is { } l ? $" -> {l}" : string.Empty)}");
            }

            if (entry.Reconciliation is { } r)
            {
                Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"    volume {r.Volume}: freed {r.FreedBytes:N0} bytes, expected {r.ExpectedFreedBytes:N0} ({(r.WithinTolerance ? "within" : "OUTSIDE")} tolerance)"));
            }
        }

        return 0;
    }

    private static Command Restore()
    {
        var id = new Argument<string>("id") { Description = "The History entry's ID." };
        var restore = new Command("restore", "Put back everything of an entry that went to the Recycle Bin or the quarantine.");
        restore.Arguments.Add(id);
        restore.SetAction(parse =>
        {
            if (!Guid.TryParse(parse.GetValue(id), out Guid guid))
            {
                Console.Error.WriteLine("devsweep: that is not a History ID.");
                return 2;
            }

            HistoryRestoreResult result = new HistoryStore().Restore(guid, new QuarantineStore());
            foreach (string message in result.Messages)
            {
                Console.Out.WriteLine(message);
            }

            return result.Succeeded ? 0 : 1;
        });
        return restore;
    }
}
