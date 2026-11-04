using System.CommandLine;
using DevSweep.Core.Modules;
using DevSweep.Core.Rules;

namespace DevSweep.Cli.Commands;

/// <summary>
/// <c>devsweep rules validate [path]</c>: checks a pack file, every pack in a folder,
/// or (no path) the bundled packs and the user's. Exit code 1 when any pack has a problem.
/// </summary>
internal static class RulesCommand
{
    public static Command Create()
    {
        var path = new Argument<string?>("path") { Description = "A .yml pack or a folder of packs. Default: the bundled packs and %LOCALAPPDATA%\\DevSweep\\Rules.", Arity = ArgumentArity.ZeroOrOne };
        var validate = new Command("validate", "Check rule packs against the schema: keys, categories, detector kinds and options, roots, explanations, actions.");
        validate.Arguments.Add(path);
        validate.SetAction(parse => Validate(parse.GetValue(path)));

        var command = new Command("rules", "Rule packs.");
        command.Subcommands.Add(validate);
        return command;
    }

    private static int Validate(string? path)
    {
        DevSweepEngine engine = EngineFactory.Create();
        var loader = new RuleLoader(engine.CreateSchema());
        List<RulePack> packs;
        if (path is null)
        {
            packs = engine.BundledPacks().Select(loader.Parse).ToList();
            if (engine.DefaultUserRulesDirectory is { } user && Directory.Exists(user))
            {
                packs.AddRange(Files(user).Select(loader.ParseFile));
            }
        }
        else if (Directory.Exists(path))
        {
            packs = Files(path).Select(loader.ParseFile).ToList();
        }
        else if (File.Exists(path))
        {
            packs = [loader.ParseFile(path)];
        }
        else
        {
            Console.Error.WriteLine($"devsweep: nothing exists at {path}");
            return 2;
        }

        int problems = 0;
        foreach (RulePack pack in packs)
        {
            if (pack.IsValid)
            {
                Console.Out.WriteLine($"ok    {pack.Name}: {pack.Rules.Count} rule{(pack.Rules.Count == 1 ? "" : "s")} ({string.Join(", ", pack.Rules.Select(r => r.Id))})");
                continue;
            }

            Console.Out.WriteLine($"FAIL  {pack.Name}: {pack.Errors.Count} problem{(pack.Errors.Count == 1 ? "" : "s")} (this pack would be skipped)");
            foreach (RuleError error in pack.Errors)
            {
                Console.Out.WriteLine($"      {error}");
            }

            problems += pack.Errors.Count;
        }

        Console.Out.WriteLine(packs.Count == 0 ? "No rule packs found." : $"{packs.Count} pack{(packs.Count == 1 ? "" : "s")}, {problems} problem{(problems == 1 ? "" : "s")}.");
        return problems == 0 ? 0 : 1;
    }

    private static IEnumerable<string> Files(string folder)
        => Directory.GetFiles(folder)
            .Where(f => Path.GetExtension(f).Equals(".yml", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(f).Equals(".yaml", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase);
}
