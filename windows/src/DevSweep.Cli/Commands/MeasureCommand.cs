using System.CommandLine;
using System.ComponentModel;
using System.Text.Json;
using DevSweep.Cli.Output;
using DevSweep.Core.Platform;

namespace DevSweep.Cli.Commands;

/// <summary>
/// <c>devsweep measure &lt;path&gt; [--json] [--time-limit seconds] [--no-streams] [--find-first-file]</c>:
/// prints what the walker (v2 by default) measured.
/// </summary>
internal static class MeasureCommand
{
    public static Command Create()
    {
        var path = new Argument<string>("path") { Description = "File or folder to measure." };
        var json = new Option<bool>("--json") { Description = "Print the result as JSON." };
        var timeLimit = new Option<double?>("--time-limit") { Description = "Stop after this many seconds and report a partial size." };
        var noStreams = new Option<bool>("--no-streams") { Description = "Leave alternate data streams out (faster: files are measured by name, with no handle)." };
        var findFirstFile = new Option<bool>("--find-first-file") { Description = "List folders with FindFirstFileExW only (the M0 walker), for comparison." };

        var command = new Command("measure", "Measure a file or folder's real size on disk (hard links once, links not followed, cloud-only files as 0).");
        command.Arguments.Add(path);
        command.Options.Add(json);
        command.Options.Add(timeLimit);
        command.Options.Add(noStreams);
        command.Options.Add(findFirstFile);
        command.SetAction(parse => Run(
            parse.GetValue(path)!,
            parse.GetValue(json),
            new WalkOptions
            {
                TimeLimit = parse.GetValue(timeLimit) is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
                AlternateStreams = !parse.GetValue(noStreams),
                Listing = parse.GetValue(findFirstFile) ? ListingMethod.FindFirstFile : ListingMethod.Auto,
            }));
        return command;
    }

    private static int Run(string path, bool asJson, WalkOptions options)
    {
        WalkResult result;
        try
        {
            result = DirectoryWalker.Measure(path, options);
        }
        catch (Exception ex) when (ex is FileNotFoundException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"devsweep: {ex.Message}");
            return 2;
        }

        MeasureReport report = MeasureReport.From(result);
        if (asJson)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(report, CliJsonContext.Default.MeasureReport));
        }
        else
        {
            MeasureText.Write(Console.Out, report);
        }

        return 0;
    }
}
