using System.CommandLine;
using DevSweep.Cli.Commands;

namespace DevSweep.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        var root = new RootCommand("DevSweep: a disk cleaner for developer machines that explains every item before it goes.");
        root.Subcommands.Add(MeasureCommand.Create());
        return root.Parse(args).Invoke();
    }
}
