using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Platform;
using DevSweep.Core.Platform.Quarantine;
using DevSweep.Core.Platform.Recycling;
using DevSweep.Core.Safety;

namespace DevSweep.Cli.Commands.Verify;

/// <summary>
/// Test-only, hidden: <c>devsweep __verify-clean run|restore</c>. It exists for
/// <c>scripts/Verify-DevSweep.ps1 -RunCleanTest</c>, which needs the real Cleaner to move a
/// fixture the script itself created (no CLI verb removes an arbitrary folder, by design).
/// It refuses anything that is not (1) strictly inside a root that holds the fixture marker
/// file written by the script, and (2) inside the temporary-files folder. History goes to a file
/// inside that root, never to the user's own History. The quarantine and the Recycle Bin are the
/// real ones, because those are what is being verified.
/// </summary>
internal static class VerifyCleanCommand
{
    public const string Name = "__verify-clean";

    /// <summary>The marker the script writes into the fixture root. Its presence is the proof the script made the folder.</summary>
    public const string MarkerFile = ".devsweep-verify-fixture";

    public static Command Create()
    {
        var command = new Command(Name, "Internal: used by scripts/Verify-DevSweep.ps1 -RunCleanTest. Touches only a fixture the script created under %TEMP%.") { Hidden = true };
        command.Subcommands.Add(Run());
        command.Subcommands.Add(Restore());
        return command;
    }

    private static Command Run()
    {
        var path = new Argument<string>("path") { Description = "The fixture item to move." };
        var root = new Option<string>("--allow-root") { Description = "The fixture root (holds the marker file).", Required = true };
        var history = new Option<string>("--history") { Description = "History file to write (inside the fixture root).", Required = true };
        var delete = new Option<bool>("--delete") { Description = "Delete for good (the fixture is declared a rebuildable item) instead of Recycle Bin or quarantine." };
        var run = new Command("run", "Move or delete one fixture item through the real Cleaner and print what happened as JSON.");
        run.Arguments.Add(path);
        run.Options.Add(root);
        run.Options.Add(history);
        run.Options.Add(delete);
        run.SetAction(parse => RunCleaner(parse.GetValue(path)!, parse.GetValue(root)!, parse.GetValue(history)!, parse.GetValue(delete)));
        return run;
    }

    private static Command Restore()
    {
        var id = new Argument<string>("id") { Description = "The History entry ID printed by run." };
        var root = new Option<string>("--allow-root") { Description = "The fixture root (holds the marker file).", Required = true };
        var history = new Option<string>("--history") { Description = "The History file written by run.", Required = true };
        var restore = new Command("restore", "Put back a fixture item moved by run.");
        restore.Arguments.Add(id);
        restore.Options.Add(root);
        restore.Options.Add(history);
        restore.SetAction(parse =>
        {
            if (Refuse(parse.GetValue(root)!, null, parse.GetValue(history)!) is { } refusal)
            {
                return Print(new VerifyCleanResult(false, refusal, null, null, []));
            }

            if (!Guid.TryParse(parse.GetValue(id), out Guid guid))
            {
                return Print(new VerifyCleanResult(false, "That is not a History ID.", null, null, []));
            }

            HistoryRestoreResult result = new HistoryStore(parse.GetValue(history)).Restore(guid, new QuarantineStore());
            return Print(new VerifyCleanResult(result.Succeeded, string.Join(" | ", result.Messages), guid, null, result.Messages));
        });
        return restore;
    }

    private static int RunCleaner(string path, string root, string historyFile, bool delete)
    {
        if (Refuse(root, path, historyFile) is { } refusal)
        {
            return Print(new VerifyCleanResult(false, refusal, null, null, []));
        }

        string full = Canonical(path)!;
        WalkResult size = DirectoryWalker.Measure(full);
        var finding = new Finding
        {
            Id = "verify:" + Path.GetFileName(full),
            RuleId = "verify-clean",
            Title = Path.GetFileName(full),
            Risk = delete ? Risk.Rebuilds : Risk.Leftover,
            Paths = [full],
            Size = size.Bytes,
            FreeableBytes = size.FreeableBytes,
            Files = size.Files,
        };
        var action = new CleanAction(delete ? CleanActionKind.Delete : CleanActionKind.Trash);
        var store = new HistoryStore(historyFile);
        SafetyGuard guard = SafetyGuard.ForCurrentUser(devRoots: [Canonical(root)!]);
        var cleaner = new Cleaner(guard, store, new QuarantineStore(), new RecycleBin());
        ActionOutcome outcome = cleaner.PerformAsync(new PlannedAction(finding, action)).GetAwaiter().GetResult();
        HistoryEntry? entry = outcome.HistoryId is { } id ? store.Find(id) : null;
        return Print(new VerifyCleanResult(outcome.Succeeded, outcome.Message, outcome.HistoryId, entry, []));
    }

    /// <summary>A reason to refuse, or null when everything is the script's own fixture under %TEMP%.</summary>
    private static string? Refuse(string root, string? item, string historyFile)
    {
        string? rootFull = Canonical(root);
        string? temp = Canonical(Path.GetTempPath());
        if (rootFull is null || temp is null)
        {
            return "The fixture root or the temporary folder is not a plain full path.";
        }

        if (!IsStrictlyInside(rootFull, temp))
        {
            return $"{rootFull} is not inside the temporary-files folder ({temp}); the verification kit only works on what it created there.";
        }

        string? link = ReparseGuard.FindReparseAncestor(rootFull) ?? SelfIfLink(rootFull);
        if (link is not null)
        {
            return $"{rootFull} is reached through a junction or link ({link}).";
        }

        if (!File.Exists(Path.Combine(rootFull, MarkerFile)))
        {
            return $"{rootFull} has no {MarkerFile} marker: it was not created by Verify-DevSweep.ps1.";
        }

        if (Canonical(historyFile) is not { } historyFull || !IsStrictlyInside(historyFull, rootFull))
        {
            return "The History file must be inside the fixture root.";
        }

        if (item is not null && (Canonical(item) is not { } itemFull || !IsStrictlyInside(itemFull, rootFull)))
        {
            return $"{item} is not strictly inside the fixture root {rootFull}.";
        }

        return null;
    }

    private static string? SelfIfLink(string full)
    {
        FileAttributes attributes = File.GetAttributes(full);
        return (attributes & FileAttributes.ReparsePoint) != 0 ? full : null;
    }

    private static string? Canonical(string path)
    {
        if (!SafePath.TryCanonicalize(path, out string? canonical, out _))
        {
            return null;
        }

        return SafePath.TryCanonicalize(LongNames.Resolve(canonical), out string? resolved, out _) ? resolved : canonical;
    }

    private static bool IsStrictlyInside(string path, string folder)
    {
        string prefix = folder.EndsWith('\\') ? folder : folder + "\\";
        return path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static int Print(VerifyCleanResult result)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(result, VerifyJsonContext.Default.VerifyCleanResult));
        return result.Succeeded ? 0 : 1;
    }
}

/// <summary>What <c>__verify-clean</c> prints.</summary>
internal sealed record VerifyCleanResult(bool Succeeded, string Message, Guid? HistoryId, HistoryEntry? Entry, IReadOnlyList<string> Messages);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(VerifyCleanResult))]
internal sealed partial class VerifyJsonContext : JsonSerializerContext;
