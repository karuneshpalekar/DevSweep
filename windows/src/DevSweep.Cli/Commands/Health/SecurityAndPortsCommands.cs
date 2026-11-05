using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using DevSweep.Core.Health;
using DevSweep.Core.Health.Ports;
using DevSweep.Core.Health.Security;

namespace DevSweep.Cli.Commands.Health;

/// <summary><c>devsweep security [--json] [--project-root DIR]...</c>: secrets left in plain files. Values are never printed.</summary>
internal static class SecurityCommand
{
    public static Command Create()
    {
        var json = new Option<bool>("--json") { Description = "Print the result as JSON." };
        var roots = new Option<string[]>("--project-root") { Description = "Project folders to check for .env files (default: the usual ones).", AllowMultipleArgumentsPerToken = true };
        var command = new Command("security", "Secrets left in Downloads, Desktop, Documents and project folders (names and first kilobytes only; values never shown).");
        command.Options.Add(json);
        command.Options.Add(roots);
        command.SetAction(parse => Run(parse.GetValue(json), parse.GetValue(roots)));
        return command;
    }

    private static int Run(bool asJson, string[]? roots)
    {
        if (HealthCli.Open(f => f.Security, HealthFeatures.SecurityFlag) is not { } health)
        {
            return HealthCli.TurnedOff;
        }

        SecurityResult result = health.Checks.Security(health.Engine, roots is { Length: > 0 } ? roots : null);
        if (asJson)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(result, HealthCliJsonContext.Default.SecurityResult));
            return 0;
        }

        var o = Console.Out;
        o.WriteLine($"Looked in: {string.Join("; ", result.Looked)}");
        if (result.Findings.Count == 0)
        {
            o.WriteLine("Nothing that looks like a secret.");
        }

        foreach (SecurityFinding f in result.Findings)
        {
            o.WriteLine($"[{f.Level}] {f.Title}  ({f.Status})");
            o.WriteLine($"    {f.Path}");
            o.WriteLine($"    Why: {f.Why}");
            o.WriteLine($"    What to do: {f.WhatToDo}");
            foreach (string c in f.Fix ?? [])
            {
                o.WriteLine($"      > {c}");
            }
        }

        if (result.CloudFilesSkipped > 0)
        {
            o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{result.CloudFilesSkipped} cloud-only files were not opened (opening them would download them), so they were not checked."));
        }

        foreach (string folder in result.Unreadable)
        {
            o.WriteLine($"Could not be read: {folder}");
        }

        if (result.GitUnavailable is { } git)
        {
            o.WriteLine($"git: {git}; .env files' git status is unknown.");
        }

        return 0;
    }
}

/// <summary>
/// <c>devsweep ports [--json] [--all]</c>, and <c>devsweep ports --stop PID [--yes]</c>: asks the process to
/// stop (window close or Ctrl+C) and ends it only if it is still running and the user confirms.
/// </summary>
internal static class PortsCommand
{
    public static Command Create()
    {
        var json = new Option<bool>("--json") { Description = "Print the result as JSON." };
        var all = new Option<bool>("--all") { Description = "Include processes of other users and Windows services." };
        var stop = new Option<int?>("--stop") { Description = "Stop the process with this ID (yours only): asked first, ended only if you confirm." };
        var yes = new Option<bool>("--yes") { Description = "With --stop: end it without asking if it doesn't stop when asked." };
        var command = new Command("ports", "What is listening: dev servers, databases, Docker; project folder, network reachability and Windows Firewall.");
        command.Options.Add(json);
        command.Options.Add(all);
        command.Options.Add(stop);
        command.Options.Add(yes);
        command.SetAction(parse => parse.GetValue(stop) is { } pid ? Stop(pid, parse.GetValue(yes), parse.GetValue(json)) : List(parse.GetValue(json), parse.GetValue(all)));
        return command;
    }

    private static int List(bool asJson, bool all)
    {
        if (HealthCli.Open(f => f.Ports, HealthFeatures.PortsFlag) is not { } health)
        {
            return HealthCli.TurnedOff;
        }

        PortsResult result = health.Checks.Ports(health.Engine, all);
        if (asJson)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(result, HealthCliJsonContext.Default.PortsResult));
            return 0;
        }

        var o = Console.Out;
        foreach (ListeningPort p in result.Ports)
        {
            string reach = p.Exposure switch
            {
                Exposure.Loopback => "this PC only",
                _ => $"{p.Exposure}; {p.Firewall?.Summary ?? "firewall not checked"}",
            };
            o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{p.Protocol,-3} {p.Port,5}  {p.Label}  (PID {p.Pid}, {p.ProcessName})  [{string.Join(", ", p.Addresses)}] {reach}"));
            if (p.Folder is { } folder)
            {
                o.WriteLine($"           folder: {folder} (from {p.FolderSource})");
            }
        }

        if (result.OthersHidden > 0)
        {
            o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{result.OthersHidden} listeners of other users or Windows services not shown (--all shows them)."));
        }

        if (result.FirewallProblem is { } problem)
        {
            o.WriteLine($"Firewall: {problem}");
        }

        return 0;
    }

    private static int Stop(int pid, bool yes, bool asJson)
    {
        if (HealthCli.Open(f => f.Ports, HealthFeatures.PortsFlag) is null)
        {
            return HealthCli.TurnedOff;
        }

        var stopper = new ProcessStopper(WindowsProcessDetails.Instance, HealthCli.SendCtrlCFromHelper);
        StopResult result = stopper.AskToStop(pid, TimeSpan.FromSeconds(5));
        if (result.Outcome == StopOutcome.StillRunning)
        {
            Console.Error.WriteLine(result.Message);
            bool confirmed = yes;
            if (!yes && !Console.IsInputRedirected)
            {
                Console.Error.Write("End it now? [y/N] ");
                confirmed = string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
            }

            if (confirmed)
            {
                result = stopper.End(pid);
            }
        }

        if (asJson)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(result, HealthCliJsonContext.Default.StopResult));
        }
        else
        {
            Console.Out.WriteLine(result.Message);
        }

        return result.Outcome == StopOutcome.Stopped ? 0 : 1;
    }
}
