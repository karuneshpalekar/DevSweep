using DevSweep.Core.Modules;

namespace DevSweep.Core.Projects;

/// <summary>
/// Which Projects tabs are on (sub-flags of <c>module:projects</c>, like Health's):
/// <c>module:projects.accounts</c> (GitHub accounts, their repositories, downloads and publish),
/// <c>module:projects.cleanup</c> (the idle-projects list and removing), <c>module:projects.activity</c>.
/// Turning Accounts off makes Projects purely local: no gh, no network.
/// </summary>
public sealed record ProjectsFeatures(bool Accounts, bool Cleanup, bool Activity)
{
    public const string ModuleFlag = "module:projects";
    public const string AccountsFlag = "module:projects.accounts";
    public const string CleanupFlag = "module:projects.cleanup";
    public const string ActivityFlag = "module:projects.activity";

    public static ProjectsFeatures All { get; } = new(true, true, true);

    public static ProjectsFeatures From(FeatureFlags flags)
    {
        ArgumentNullException.ThrowIfNull(flags);
        return new ProjectsFeatures(flags.IsEnabled(AccountsFlag), flags.IsEnabled(CleanupFlag), flags.IsEnabled(ActivityFlag));
    }

    public IReadOnlyList<string> Disabled
        => [.. Accounts ? [] : (string[])[AccountsFlag], .. Cleanup ? [] : (string[])[CleanupFlag], .. Activity ? [] : (string[])[ActivityFlag]];
}

/// <summary>
/// Projects (PLAN M3c, <c>module:projects</c>, off by default until it passes its gate):
/// clones found by their origin, GitHub accounts without switching, safe removal, downloads,
/// publish, idle cleanup and the activity trail. Registers <see cref="ProjectsFeatures"/>;
/// the CLI and the app create a <see cref="ProjectsService"/> from the engine.
/// </summary>
public sealed class ProjectsModule : IDevSweepModule
{
    public string Id => "projects";

    public string Title => "Projects";

    public bool EnabledByDefault => false;

    public void Register(EngineBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddService(ProjectsFeatures.From(builder.Flags));
    }
}
