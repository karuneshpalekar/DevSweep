using System.Diagnostics;
using System.Text.Json.Serialization;
using DevSweep.Core.Health;
using DevSweep.Core.Health.Ports;
using DevSweep.Core.Health.Runtimes;
using DevSweep.Core.Health.Security;
using DevSweep.Core.Modules;

namespace DevSweep.Cli.Commands.Health;

/// <summary>Shared pieces of the Health commands: the engine's Health service, and exit codes.</summary>
internal static class HealthCli
{
    /// <summary>Health is turned off (module or tab flag).</summary>
    public const int TurnedOff = 3;

    /// <summary>The Health service, or null after printing how to turn it on.</summary>
    public static (DevSweepEngine Engine, HealthChecks Checks)? Open(Func<HealthFeatures, bool> tab, string flag)
    {
        DevSweepEngine engine = EngineFactory.Create();
        HealthChecks? checks = engine.Services<HealthChecks>() is [var first, ..] ? first : null;
        if (checks is null)
        {
            Console.Error.WriteLine("devsweep: Health is turned off (module:health). Turn it on with DEVSWEEP_FEATURES=+module:health, or add \"module:health\" to \"enabled\" in %LOCALAPPDATA%\\DevSweep\\features.json.");
            return null;
        }

        if (!tab(checks.Features))
        {
            Console.Error.WriteLine($"devsweep: this Health tab is turned off ({flag}).");
            return null;
        }

        return (engine, checks);
    }

    /// <summary>The CLI sends Ctrl+C from a short-lived copy of itself, so its own console is never detached.</summary>
    public static bool SendCtrlCFromHelper(int pid)
    {
        string? self = Environment.ProcessPath;
        if (self is null)
        {
            return false;
        }

        var start = new ProcessStartInfo(self) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(CtrlCCommand.Name);
        start.ArgumentList.Add(pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using Process? helper = Process.Start(start);
        if (helper is null || !helper.WaitForExit(TimeSpan.FromSeconds(10)))
        {
            return false;
        }

        return helper.ExitCode == 0;
    }
}

/// <summary>Adds the Health commands to the CLI.</summary>
internal static class HealthCommands
{
    public static void AddTo(System.CommandLine.RootCommand root)
    {
        ArgumentNullException.ThrowIfNull(root);
        root.Subcommands.Add(RuntimesCommand.Create());
        root.Subcommands.Add(SecurityCommand.Create());
        root.Subcommands.Add(PortsCommand.Create());
        root.Subcommands.Add(CtrlCCommand.Create());
    }
}

/// <summary>Source-generated JSON for the Health commands (AOT and trim safe).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(VersionsResult))]
[JsonSerializable(typeof(SecurityResult))]
[JsonSerializable(typeof(PortsResult))]
[JsonSerializable(typeof(StopResult))]
internal sealed partial class HealthCliJsonContext : JsonSerializerContext;
