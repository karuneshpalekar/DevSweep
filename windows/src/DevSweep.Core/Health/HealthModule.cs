using DevSweep.Core.Health.Common;
using DevSweep.Core.Health.Ports;
using DevSweep.Core.Health.Runtimes;
using DevSweep.Core.Health.Security;
using DevSweep.Core.Modules;

namespace DevSweep.Core.Health;

/// <summary>
/// Which Health tabs are on. Each tab has its own flag under <c>module:health</c>, so it can ship or be
/// hidden on its own: <c>module:health.runtimes</c>, <c>module:health.security</c>, <c>module:health.ports</c>.
/// </summary>
public sealed record HealthFeatures(bool Runtimes, bool Security, bool Ports)
{
    public const string RuntimesFlag = "module:health.runtimes";
    public const string SecurityFlag = "module:health.security";
    public const string PortsFlag = "module:health.ports";

    /// <summary>Flags that turned a tab off.</summary>
    public IReadOnlyList<string> Disabled
        => ((string?[])[Runtimes ? null : RuntimesFlag, Security ? null : SecurityFlag, Ports ? null : PortsFlag]).OfType<string>().ToList();
}

/// <summary>
/// Health (<c>module:health</c>): tools and versions, security, ports. On by default (since 0.1.1, after it passed its
/// accuracy gate); turn it off with <c>-module:health</c> in <c>DEVSWEEP_FEATURES</c> or
/// <c>features.json</c>, or in Settings, Modules. Registers <see cref="HealthFeatures"/> and <see cref="HealthChecks"/>.
/// </summary>
public sealed class HealthModule : IDevSweepModule
{
    public string Id => "health";

    public string Title => "Health";

    public bool EnabledByDefault => true;

    public void Register(EngineBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var features = new HealthFeatures(
            builder.Flags.IsEnabled(HealthFeatures.RuntimesFlag),
            builder.Flags.IsEnabled(HealthFeatures.SecurityFlag),
            builder.Flags.IsEnabled(HealthFeatures.PortsFlag));
        builder.AddService(features);
        builder.AddService(new HealthChecks(features));
    }
}

/// <summary>Runs Health's checks against the engine's machine (the entry point for the CLI and the app). A tab turned off by its flag refuses to run.</summary>
public sealed class HealthChecks(HealthFeatures features)
{
    public HealthFeatures Features { get; } = features ?? throw new ArgumentNullException(nameof(features));

    /// <summary>What probes read, from the engine's environment, registry, locations and inventory.</summary>
    public static ProbeContext CreateContext(DevSweepEngine engine, bool runPrograms = true)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return new ProbeContext(
            engine.Environment,
            engine.Registry,
            engine.CreateLocations(),
            () => engine.BuildInventory(),
            WindowsFileVersionReader.Instance,
            runPrograms ? new TrustedVersionCommand(engine.Environment) : NoVersionCommand.Instance,
            WindowsAliasReader.Instance,
            WindowsProcessPaths.Instance);
    }

    /// <summary><c>%LOCALAPPDATA%\DevSweep\endoflife</c>.</summary>
    public static string? EolCacheDirectory(DevSweepEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return engine.Environment.KnownFolder("LocalAppData") is { } local ? Path.Join(local, "DevSweep", "endoflife") : null;
    }

    public Task<VersionsResult> RuntimesAsync(DevSweepEngine engine, IEolFetcher fetcher, IReadOnlyList<string>? projectRoots = null,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        Require(Features.Runtimes, HealthFeatures.RuntimesFlag);
        var eol = new EndOfLifeClient(EolCacheDirectory(engine), fetcher);
        return new RuntimeHealth().ScanAsync(CreateContext(engine), eol, projectRoots, null, progress, cancellationToken);
    }

    public SecurityResult Security(DevSweepEngine engine, IReadOnlyList<string>? projectRoots = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        Require(Features.Security, HealthFeatures.SecurityFlag);
        ProbeContext context = CreateContext(engine, runPrograms: false);
        WindowsGitRunner? git = WindowsGitRunner.Find(context, out string? whyNot);
        return SecurityScanner.Scan(engine.Environment, projectRoots ?? ProjectRequirementScanner.DefaultRoots(engine.Environment), git, whyNot);
    }

    public PortsResult Ports(DevSweepEngine engine, bool includeOthers = false)
    {
        ArgumentNullException.ThrowIfNull(engine);
        Require(Features.Ports, HealthFeatures.PortsFlag);
        Firewall firewall = Firewall.Load(WindowsFirewallConfig.Instance, text => Runtimes.SearchPath.Expand(text, engine.Environment.GetVariable));
        return PortScanner.Scan(WindowsSocketTable.Instance, WindowsProcessDetails.Instance, firewall, engine.Environment.KnownFolder("Profile"), includeOthers);
    }

    private static void Require(bool enabled, string flag)
    {
        if (!enabled)
        {
            throw new InvalidOperationException($"This Health tab is turned off ({flag}).");
        }
    }
}
