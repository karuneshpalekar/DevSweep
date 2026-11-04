using DevSweep.Core.Modules;
using DevSweep.Core.Platform;

namespace DevSweep.Cli.Commands;

/// <summary>The engine for CLI commands: the built-in modules with the user's feature flags.</summary>
internal static class EngineFactory
{
    public static DevSweepEngine Create()
    {
        string? settings = KnownFolders.TryGetRoot("LocalAppData") is { } local ? Path.Join(local, "DevSweep", "features.json") : null;
        FeatureFlags flags = FeatureFlags.Load(settings, Environment.GetEnvironmentVariable("DEVSWEEP_FEATURES"));
        return DevSweepEngine.CreateDefault(flags);
    }
}
