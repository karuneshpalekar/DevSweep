using DevSweep.Core.Cleaning;
using DevSweep.Core.Detectors;
using DevSweep.Core.Health;
using DevSweep.Core.Health.Packages;
using DevSweep.Core.Health.Ports;
using DevSweep.Core.Health.Runtimes;
using DevSweep.Core.Health.Security;
using DevSweep.Core.Modules;

namespace DevSweep.App.ViewModels.Places;

/// <summary>The real Health source: M3b's <see cref="HealthChecks"/>, each tab only when its flag is on.</summary>
public sealed class CoreHealthSource(DevSweepEngine engine, HealthChecks checks, Func<IReadOnlyList<string>?> projectRoots, Func<PlannedAction, Task<ActionOutcome>>? clean = null) : IHealthSource
{
    public HealthFeatures Features => checks.Features;

    /// <summary>The Health source when <c>module:health</c> is on; null otherwise.</summary>
    public static CoreHealthSource? From(DevSweepEngine engine, Func<IReadOnlyList<string>?> projectRoots, Func<PlannedAction, Task<ActionOutcome>>? clean = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return engine.Services<HealthChecks>() is [var first, ..] ? new CoreHealthSource(engine, first, projectRoots, clean) : null;
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

    public Task<PackagesResult?> PackagesAsync(VersionsResult? versions, CancellationToken cancellationToken)
        => Features.Runtimes ? Task.Run<PackagesResult?>(async () => await checks.PackagesAsync(engine, versions, cancellationToken).ConfigureAwait(false), cancellationToken) : Task.FromResult<PackagesResult?>(null);

    public Task<PortsResult?> PortsAsync(CancellationToken cancellationToken)
        => Features.Ports ? Task.Run<PortsResult?>(() => checks.Ports(engine), cancellationToken) : Task.FromResult<PortsResult?>(null);

    public StopResult? StopRefusal(ListeningPort port)
    {
        ArgumentNullException.ThrowIfNull(port);
        var stopper = new ProcessStopper(WindowsProcessDetails.Instance);
        return stopper.Refusal(port.Pid) ?? Changed(port);
    }

    public Task<StopResult> StopAsync(ListeningPort port, bool force, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(port);
        return Task.Run(() =>
        {
            var stopper = new ProcessStopper(WindowsProcessDetails.Instance);
            if (Changed(port) is { } changed)
            {
                return changed;
            }

            return force ? stopper.End(port.Pid) : stopper.AskToStop(port.Pid, TimeSpan.FromSeconds(3));
        }, cancellationToken);
    }

    /// <summary>The process id may have been reused since the list was read: only the program that was listed is stopped.</summary>
    private static StopResult? Changed(ListeningPort port)
        => WindowsProcessDetails.Instance.Read(port.Pid) is { } info && !string.Equals(info.Name, port.ProcessName, StringComparison.OrdinalIgnoreCase)
            ? new StopResult(StopOutcome.NotFound, $"Process {port.Pid} is now {info.Name}, not {port.ProcessName}: the list is out of date. Refresh it.")
            : null;

    public string? TrashBlockedReason(SecurityFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (clean is null)
        {
            return "Moving files to the Recycle Bin isn't available here.";
        }

        ProbeEntry? entry = FileProbe.Stat(finding.Path);
        return entry switch
        {
            null => "The file is already gone.",
            { IsCloudPlaceholder: true } => "It's online-only in OneDrive (or another cloud folder). Moving it would first download it, so DevSweep leaves it alone. Make it available on this PC, or delete it from the cloud folder yourself.",
            { IsReparsePoint: true } => "It's a link, not the file itself, so DevSweep doesn't move it.",
            _ => null,
        };
    }

    public async Task<HealthActionResult> TrashAsync(SecurityFinding finding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(finding);
        string? reason = TrashBlockedReason(finding);
        if (reason is not null || clean is null)
        {
            return new HealthActionResult(false, reason ?? "Moving files to the Recycle Bin isn't available here.");
        }

        long size = 0;
        try
        {
            size = new FileInfo(finding.Path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The size is only for the History line.
        }

        var item = new Finding
        {
            Id = finding.Id,
            Title = finding.Title,
            Subtitle = finding.Path,
            Risk = Risk.HoldsData,
            Paths = [finding.Path],
            Size = size,
            PathSizes = [size],
            Actions = [new CleanAction(CleanActionKind.Trash)],
            UndoNote = "Restore it from History or the Recycle Bin.",
        };
        ActionOutcome outcome = await clean(new PlannedAction(item, item.Actions[0])).ConfigureAwait(false);
        return new HealthActionResult(outcome.Succeeded, outcome.Succeeded ? "Moved to the Recycle Bin. You can put it back from History." : outcome.Message);
    }

    private async Task<VersionsResult?> Runtimes(IReadOnlyList<string>? roots, CancellationToken cancellationToken)
    {
        using var fetcher = new HttpEolFetcher();
        return await Task.Run(() => checks.RuntimesAsync(engine, fetcher, roots, null, cancellationToken), cancellationToken).ConfigureAwait(false);
    }
}
