using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using DevSweep.Cli.Output;
using DevSweep.Core.Inventory;
using DevSweep.Core.Modules;

namespace DevSweep.Cli.Commands;

/// <summary><c>devsweep inventory [--json]</c>: every installed app from every source, and what each source contributed.</summary>
internal static class InventoryCommand
{
    public static Command Create()
    {
        var json = new Option<bool>("--json") { Description = "Print the inventory as JSON." };
        var command = new Command("inventory", "List installed apps from every source (registry, Store, Scoop, Chocolatey, winget, App Paths, Start menu, Program Files).");
        command.Options.Add(json);
        command.SetAction(parse => Run(parse.GetValue(json)));
        return command;
    }

    private static int Run(bool asJson)
    {
        DevSweepEngine engine = EngineFactory.Create();
        AppInventory inventory = engine.BuildInventory();
        if (asJson)
        {
            var output = new InventoryJson(inventory.Sources, inventory.DisabledSources, inventory.Records.Count, inventory.Apps);
            Console.Out.WriteLine(JsonSerializer.Serialize(output, CliJsonContext.Default.InventoryJson));
            return 0;
        }

        Console.Out.WriteLine("Sources:");
        foreach (SourceReport source in inventory.Sources)
        {
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {source.Count,6} {source.Title,-55} {source.Milliseconds,8:0} ms  {(source.Complete ? "complete" : "INCOMPLETE: " + string.Join("; ", source.Problems))}"));
        }

        foreach (InventorySource source in inventory.DisabledSources)
        {
            Console.Out.WriteLine($"  turned off: {InventorySources.Title(source)}");
        }

        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{inventory.Apps.Count} apps from {inventory.Records.Count} records:"));
        foreach (InstalledApp app in inventory.Apps)
        {
            string ids = string.Join(", ", app.ProductCodes.Select(c => "{" + c + "}").Concat(app.PackageFamilyNames).Concat(app.PackageIds));
            Console.Out.WriteLine($"  {app.Name}{(app.Publishers.Count > 0 ? $" ({app.Publishers[0]})" : string.Empty)}  [{string.Join(", ", app.Sources)}]{(ids.Length > 0 ? "  " + ids : string.Empty)}");
        }

        return 0;
    }
}
