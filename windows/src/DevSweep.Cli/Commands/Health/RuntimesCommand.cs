using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using DevSweep.Core.Health;
using DevSweep.Core.Health.Runtimes;

namespace DevSweep.Cli.Commands.Health;

/// <summary>
/// <c>devsweep runtimes [--json] [--offline] [--project-root DIR]... [--script-step ID] [--open-step ID]</c>:
/// every install of every tool, which one a new terminal runs, support dates, project requirements and fixes.
/// </summary>
internal static class RuntimesCommand
{
    public static Command Create()
    {
        var json = new Option<bool>("--json") { Description = "Print the result as JSON." };
        var offline = new Option<bool>("--offline") { Description = "Don't download support dates; use the cache only." };
        var roots = new Option<string[]>("--project-root") { Description = "Project folders to check (default: the usual ones).", AllowMultipleArgumentsPerToken = true };
        var script = new Option<string?>("--script-step") { Description = "Write the PowerShell script for a suggested fix (by its id) and print its path; runs nothing." };
        var open = new Option<string?>("--open-step") { Description = "Open a suggested fix (by its id) in a PowerShell window that lists the commands and waits for Enter." };
        var command = new Command("runtimes", "Tools and versions: Node.js, Python, Java, PostgreSQL, Go, Ruby, PHP, Rust, .NET, Flutter, Deno, Bun.");
        command.Options.Add(json);
        command.Options.Add(offline);
        command.Options.Add(roots);
        command.Options.Add(script);
        command.Options.Add(open);
        command.SetAction((parse, token) => RunAsync(parse.GetValue(json), parse.GetValue(offline), parse.GetValue(roots), parse.GetValue(script), parse.GetValue(open), token));
        return command;
    }

    private static async Task<int> RunAsync(bool asJson, bool offline, string[]? roots, string? scriptStep, string? openStep, CancellationToken token)
    {
        if (HealthCli.Open(f => f.Runtimes, HealthFeatures.RuntimesFlag) is not { } health)
        {
            return HealthCli.TurnedOff;
        }

        using var http = new HttpEolFetcher();
        IEolFetcher fetcher = offline ? OfflineEolFetcher.Instance : http;
        IProgress<string>? progress = asJson ? null : new Progress<string>(text => Console.Error.WriteLine($"  checking {text}..."));
        VersionsResult result = await health.Checks.RuntimesAsync(health.Engine, fetcher, roots is { Length: > 0 } ? roots : null, progress, token).ConfigureAwait(false);

        if ((scriptStep ?? openStep) is { } id)
        {
            RuntimeStep? step = result.Runtimes.Append(result.Windows).OfType<RuntimeReport>().SelectMany(r => r.Steps).FirstOrDefault(s => s.Id == id);
            if (step is null)
            {
                Console.Error.WriteLine($"devsweep: no suggested fix with id {id}.");
                return 2;
            }

            string path = TerminalScript.Write(step, Path.GetTempPath());
            Console.Out.WriteLine(path);
            if (openStep is not null)
            {
                TerminalScript.Open(path);
            }

            return 0;
        }

        if (asJson)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(result, HealthCliJsonContext.Default.VersionsResult));
            return 0;
        }

        Print(result);
        return 0;
    }

    private static void Print(VersionsResult result)
    {
        var o = Console.Out;
        foreach (RuntimeReport report in result.Runtimes.Append(result.Windows).OfType<RuntimeReport>())
        {
            o.WriteLine();
            o.WriteLine($"{report.Name}{(report.Recommended is { } r ? $"  (recommended: {r})" : string.Empty)}");
            foreach (Installation i in report.Installs)
            {
                string support = i.Support switch
                {
                    SupportStatus.EndOfLife => "END OF LIFE",
                    SupportStatus.EndingSoon => "ending " + i.SupportEnds?.ToString("d MMM yyyy", CultureInfo.InvariantCulture),
                    SupportStatus.Supported => i.SupportEnds is { } e ? "supported until " + e.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : "supported",
                    _ => "not checked",
                };
                o.WriteLine($"  {(i.IsDefault ? "*" : " ")} {i.Version,-14} {i.SourceTitle + (i.SourceDetail is { } d ? $" ({d})" : string.Empty),-48} {support}{(i.IsRunning == true ? ", running" : string.Empty)}");
                o.WriteLine($"      {i.Path}   [version from {i.VersionFrom}]");
            }

            if (report.Resolution is { } res)
            {
                o.WriteLine(res.Hits.Count == 0 ? $"  `{res.Command}` is not on PATH."
                    : $"  `{res.Command}` in a new terminal: {res.Hits[0].Path}{(res.Runs is { } runs && !string.Equals(runs, res.Hits[0].Path, StringComparison.OrdinalIgnoreCase) ? $" -> {runs}" : string.Empty)}{(res.Via is { } via ? $" ({via})" : string.Empty)}");
                foreach (PathHit later in res.Hits.Skip(1))
                {
                    o.WriteLine($"      also on PATH (not used): {later.Path}");
                }
            }

            foreach (string note in report.NotInstalls)
            {
                o.WriteLine($"  not an install: {note}");
            }

            foreach (RuntimeIssue issue in report.Issues)
            {
                o.WriteLine($"  [{issue.Level}] {issue.Text}");
            }

            foreach (RuntimeStep step in report.Steps)
            {
                o.WriteLine($"  fix {step.Id}: {step.Title}{(step.NeedsAdmin ? " (needs an administrator)" : string.Empty)}");
                foreach (string c in step.Commands)
                {
                    o.WriteLine($"      > {c}");
                }
            }
        }

        if (result.Projects.Count > 0)
        {
            o.WriteLine();
            o.WriteLine("Projects:");
            foreach (ProjectRequirement p in result.Projects)
            {
                o.WriteLine($"  [{p.Status}] {p.Message}");
            }
        }

        foreach (string note in result.ShellNotes)
        {
            o.WriteLine($"Shell start-up changes PATH: {note}");
        }

        if (result.EolOffline)
        {
            o.WriteLine("Support dates: offline, using the last saved data (or none).");
        }

        o.WriteLine();
        o.WriteLine("Open a fix in a PowerShell window (it lists the commands and waits for Enter): devsweep runtimes --open-step <id>");
    }
}

/// <summary>Hidden helper: sends Ctrl+C to a console process from this (throwaway) process.</summary>
internal static class CtrlCCommand
{
    public const string Name = "__ctrl-c";

    public static Command Create()
    {
        var pid = new Argument<int>("pid");
        var command = new Command(Name, "Internal: send Ctrl+C to a process's console.") { Hidden = true };
        command.Arguments.Add(pid);
        command.SetAction(parse => DevSweep.Core.Health.Ports.ConsoleInterrupt.Send(parse.GetValue(pid)) ? 0 : 1);
        return command;
    }
}
