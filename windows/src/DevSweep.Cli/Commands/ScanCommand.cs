using System.CommandLine;
using System.Text.Json;
using DevSweep.Cli.Output;
using DevSweep.Core.Locations;
using DevSweep.Core.Modules;
using DevSweep.Core.Rules;
using DevSweep.Core.Safety;
using DevSweep.Core.Scanning;

namespace DevSweep.Cli.Commands;

/// <summary>
/// <c>devsweep scan [--json] [--explain] [--exact] [--category x]... [--project-root dir]... [--rules-dir dir]</c>:
/// runs every rule and lists what could be cleaned. Changes nothing.
/// </summary>
internal static class ScanCommand
{
    public static Command Create()
    {
        var json = new Option<bool>("--json") { Description = "Print findings and the scan report as JSON." };
        var explain = new Option<bool>("--explain") { Description = "Show each finding's explanation and what was checked." };
        var exact = new Option<bool>("--exact") { Description = "Count alternate data streams (one handle per file: slower, exact). Default: overview sizes." };
        var category = new Option<string[]>("--category") { Description = "Only these categories (packageCaches, projects, leftovers...). Repeatable." };
        var projectRoot = new Option<string[]>("--project-root") { Description = "Look for idle projects here instead of the rules' default folders. Repeatable." };
        var rulesDir = new Option<string?>("--rules-dir") { Description = "Read user rule packs from this folder instead of %LOCALAPPDATA%\\DevSweep\\Rules (\"\" for none)." };
        var checkTimeout = new Option<double?>("--check-timeout") { Description = "Seconds before a slow check is skipped (default 90)." };

        var command = new Command("scan", "Find what can be cleaned and explain it. Nothing is changed.");
        foreach (Option option in (Option[])[json, explain, exact, category, projectRoot, rulesDir, checkTimeout])
        {
            command.Options.Add(option);
        }

        command.SetAction(parse => Run(
            parse.GetValue(json),
            parse.GetValue(explain),
            parse.GetValue(exact),
            parse.GetValue(category) ?? [],
            parse.GetValue(projectRoot) ?? [],
            parse.GetValue(rulesDir),
            parse.GetValue(checkTimeout)));
        return command;
    }

    private static int Run(bool asJson, bool explain, bool exact, string[] categories, string[] projectRoots, string? rulesDir, double? checkTimeout)
    {
        var chosen = new HashSet<Category>();
        foreach (string name in categories.SelectMany(c => c.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (!Categories.TryParse(name, out Category parsed))
            {
                Console.Error.WriteLine($"devsweep: unknown category \"{name}\" (known: {Categories.AllYamlNames})");
                return 2;
            }

            chosen.Add(parsed);
        }

        DevSweepEngine engine = EngineFactory.Create();
        List<string>? roots = projectRoots.Length == 0 ? null : projectRoots.Select(Path.GetFullPath).ToList();
        SafetyGuard guard = SafetyGuard.ForCurrentUser(devRoots: roots);
        var request = new ScanRequest
        {
            Options = new ScanOptions
            {
                Mode = exact ? SizeMode.Exact : SizeMode.Overview,
                Categories = chosen.Count == 0 ? null : chosen,
                CheckTimeout = checkTimeout is { } seconds ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(90),
                Progress = asJson ? null : new ConsoleProgress(),
            },
            ProjectRoots = roots,
            UserRulesDirectory = rulesDir,
            SafetyCheck = path => guard.CheckLexically(path) is { Allowed: false } verdict ? verdict.Reason : null,
        };

        ScanResult result = engine.ScanAsync(request).GetAwaiter().GetResult();
        if (asJson)
        {
            IReadOnlyList<ResolvedLocation> locations = engine.CreateLocations().ResolveAll();
            var output = new ScanJson(result.Findings, result.Report, locations);
            Console.Out.WriteLine(JsonSerializer.Serialize(output, CliJsonContext.Default.ScanJson));
        }
        else
        {
            ScanText.Write(Console.Out, result, explain);
        }

        return 0;
    }

    /// <summary>Reports each finished check on stderr (stdout stays the result).</summary>
    private sealed class ConsoleProgress : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value)
        {
            if (value.Phase == ScanPhase.Checking && value.Finished)
            {
                Console.Error.WriteLine($"  {value.Text}");
            }
        }
    }
}
