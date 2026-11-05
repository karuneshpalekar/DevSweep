using System.CommandLine;
using DevSweep.Cli.Commands;

namespace DevSweep.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        var root = new RootCommand("DevSweep: a disk cleaner for developer machines that explains every item before it goes.");
        Commands.Projects.ProjectsCommand.AddTo(root); // Projects (M3c, module:projects)
        root.Subcommands.Add(ScanCommand.Create());
        root.Subcommands.Add(RulesCommand.Create());
        root.Subcommands.Add(InventoryCommand.Create());
        root.Subcommands.Add(MeasureCommand.Create());
        root.Subcommands.Add(QuarantineCommand.Create());
        root.Subcommands.Add(HistoryCommand.Create());

        // Health (M3b): runtimes, security, ports (module:health).
        Commands.Health.HealthCommands.AddTo(root);
        return root.Parse(args).Invoke();
    }
}
