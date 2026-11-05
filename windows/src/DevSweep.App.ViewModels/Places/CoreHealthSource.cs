using DevSweep.Core.Health;
using DevSweep.Core.Health.Ports;
using DevSweep.Core.Health.Runtimes;
using DevSweep.Core.Health.Security;
using DevSweep.Core.Modules;

namespace DevSweep.App.ViewModels.Places;

/// <summary>The real Health source: M3b's <see cref="HealthChecks"/>, each tab only when its flag is on.</summary>
public sealed class CoreHealthSource(DevSweepEngine engine, HealthChecks checks, Func<IReadOnlyList<string>?> projectRoots) : IHealthSource
{
    public HealthFeatures Features => checks.Features;

    /// <summary>The Health source when <c>module:health</c> is on; null otherwise.</summary>
    public static CoreHealthSource? From(DevSweepEngine engine, Func<IReadOnlyList<string>?> projectRoots)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return engine.Services<HealthChecks>() is [var first, ..] ? new CoreHealthSource(engine, first, projectRoots) : null;
    }

    public async Task<HealthReport> LoadAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? roots = projectRoots();
        Task<VersionsResult?> versions = Features.Runtimes ? Runtimes(roots, cancellationToken) : Task.FromResult<VersionsResult?>(null);
        Task<SecurityResult?> security = Features.Security
            ? Task.Run<SecurityResult?>(() => checks.Security(engine, roots), cancellationToken)
            : Task.FromResult<SecurityResult?>(null);
        Task<PortsResult?> ports = Features.Ports
            ? Task.Run<PortsResult?>(() => checks.Ports(engine), cancellationToken)
            : Task.FromResult<PortsResult?>(null);
        await Task.WhenAll(versions, security, ports).ConfigureAwait(false);
        return new HealthReport(await versions.ConfigureAwait(false), await security.ConfigureAwait(false), await ports.ConfigureAwait(false));
    }

    public void OpenFix(RuntimeStep fix)
    {
        ArgumentNullException.ThrowIfNull(fix);
        using System.Diagnostics.Process? process = TerminalScript.Open(TerminalScript.Write(fix, Path.GetTempPath()));
    }

    private async Task<VersionsResult?> Runtimes(IReadOnlyList<string>? roots, CancellationToken cancellationToken)
    {
        using var fetcher = new HttpEolFetcher();
        return await Task.Run(() => checks.RuntimesAsync(engine, fetcher, roots, null, cancellationToken), cancellationToken).ConfigureAwait(false);
    }
}
