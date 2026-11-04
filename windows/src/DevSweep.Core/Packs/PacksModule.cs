using DevSweep.Core.Detectors;
using DevSweep.Core.Locations;
using DevSweep.Core.Modules;
using DevSweep.Core.Packs.Detectors;
using DevSweep.Core.Packs.Locations;
using DevSweep.Core.Packs.Tools;
using DevSweep.Core.Rules;

namespace DevSweep.Core.Packs;

/// <summary>
/// The Windows rule packs (PLAN 4.5, milestone M3a; <c>module:packs</c>): their detectors,
/// their named roots and the packs in <c>windows/rules/packs</c>. Turned off, Clean up keeps
/// only its first eight rules.
/// </summary>
/// <param name="machine">Tools and the Recycle Bin (the real machine by default; a fake in tests).</param>
public sealed class PacksModule(IMachineQueries? machine = null) : IDevSweepModule
{
    /// <summary>The resource prefix of this module's packs.</summary>
    public const string ResourcePrefix = "DevSweep.Packs.";

    public string Id => "packs";

    public string Title => "Windows rule packs";

    public bool EnabledByDefault => true;

    public IMachineQueries Machine { get; } = machine ?? WindowsMachineQueries.Instance;

    /// <summary>Every detector this module adds.</summary>
    public IReadOnlyList<IDetector> Detectors() =>
    [
        new DockerDetector(Machine),
        new VhdxDisksDetector(Machine),
        new DotnetInstallsDetector(Machine),
        new VisualStudioInstancesDetector(),
        new EditorExtensionsDetector(),
        new AndroidSystemImagesDetector(),
        new OldJdksDetector(),
        new ToolVersionsDetector(),
        new RustToolchainsDetector(),
        new LargeOldFilesDetector(),
        new BrowserCachesDetector(),
        new BrowserProfilesDetector(),
        new OllamaModelsDetector(),
        new OrphanedScheduledTasksDetector(Machine),
        new OrphanedRunEntriesDetector(),
        new RecycleBinDetector(Machine),
    ];

    public void Register(EngineBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (ILocationResolver resolver in PackLocations.All())
        {
            builder.AddLocation(resolver);
        }

        foreach (IDetector detector in Detectors())
        {
            builder.AddDetector(detector);
        }

        builder.AddService(Machine)
            .AddRulePacks(() => EmbeddedRulePacks.From(typeof(PacksModule).Assembly, ResourcePrefix));
    }
}
