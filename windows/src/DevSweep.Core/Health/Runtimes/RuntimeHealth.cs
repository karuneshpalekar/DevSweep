using System.Globalization;
using DevSweep.Core.Health.Common;
using DevSweep.Core.Inventory.Registry;
using Microsoft.Win32;

namespace DevSweep.Core.Health.Runtimes;

/// <summary>
/// Health, "Tools and versions": every install of every tool, which one a new terminal
/// runs, support dates, what projects ask for, and suggested fixes. Read-only: it only
/// suggests commands.
/// </summary>
public sealed class RuntimeHealth
{
    public static IReadOnlyList<RuntimeProbe> DefaultProbes { get; } =
    [
        new NodeProbe(), new PythonProbe(), new JavaProbe(), new PostgresProbe(), new GoProbe(), new RubyProbe(),
        new PhpProbe(), new RustProbe(), new DotnetProbe(), new FlutterProbe(), new DenoProbe(), new BunProbe(),
    ];

    public RuntimeHealth(IReadOnlyList<RuntimeProbe>? probes = null) => Probes = probes ?? DefaultProbes;

    public IReadOnlyList<RuntimeProbe> Probes { get; }

    public async Task<VersionsResult> ScanAsync(
        ProbeContext context,
        EndOfLifeClient eol,
        IReadOnlyList<string>? projectRoots = null,
        DateTimeOffset? now = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(eol);
        DateTimeOffset date = now ?? DateTimeOffset.UtcNow;
        DateOnly today = DateOnly.FromDateTime(date.UtcDateTime);
        progress?.Report("Reading PATH and shell start-up files");
        IReadOnlyList<ShellNote> shell = ShellProfiles.Read(context.Environment, context.Registry);

        var reports = new List<RuntimeReport>();
        var cyclesByTool = new Dictionary<string, IReadOnlyList<ReleaseCycle>>(StringComparer.Ordinal);
        foreach (RuntimeProbe probe in Probes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(probe.Name);
            List<Installation> installs = ProbeKit.Dedupe(probe.Find(context));
            CommandResolution resolution = Resolve(context, probe.Command, probe.Id, shell);
            if (installs.Count == 0 && resolution.Hits.Count == 0)
            {
                continue;
            }

            installs = probe.MarkDefault(context, installs, resolution);
            var eolData = new Dictionary<string, EolData>(StringComparer.Ordinal);
            foreach (string product in installs.Select(i => i.Product).Append(probe.Product).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                eolData[product] = await eol.CyclesAsync(product, cancellationToken).ConfigureAwait(false);
            }

            if (probe.Product is { } main)
            {
                cyclesByTool[probe.Id] = eolData[main].Cycles;
            }

            reports.Add(RuntimeReporter.Build(new ReportInput
            {
                Probe = probe,
                Installs = installs,
                Eol = eolData,
                Resolution = resolution,
                JavaHome = probe.Id == "java" ? context.Variable("JAVA_HOME") : null,
                Profile = context.Profile,
                Today = today,
            }));
        }

        progress?.Report("Your projects");
        IReadOnlyList<string> roots = projectRoots ?? ProjectRequirementScanner.DefaultRoots(context.Environment);
        List<RawRequirement> raw = ProjectRequirementScanner.Projects(roots).SelectMany(ProjectRequirementScanner.Requirements).ToList();
        foreach (string toolId in raw.Select(r => r.Tool).Distinct(StringComparer.Ordinal).Where(t => !cyclesByTool.ContainsKey(t)))
        {
            if (Probes.FirstOrDefault(p => p.Id == toolId)?.Product is { } product)
            {
                cyclesByTool[toolId] = (await eol.CyclesAsync(product, cancellationToken).ConfigureAwait(false)).Cycles;
            }
        }

        Dictionary<string, string> names = Probes.ToDictionary(p => p.Id, p => p.Name, StringComparer.Ordinal);
        IReadOnlyList<ProjectRequirement> projects = ProjectRequirementScanner.Evaluate(raw, reports, cyclesByTool, names, today,
            (tool, cycle, via) => FixCommands.Install(tool, cycle, via)?.Commands);

        progress?.Report("Windows");
        RuntimeReport? windows = await WindowsReport(context, eol, today, cancellationToken).ConfigureAwait(false);
        return new VersionsResult
        {
            Runtimes = reports,
            Projects = projects,
            Windows = windows,
            Path = context.Path.Entries,
            ShellNotes = shell.Select(n => n.Describe()).ToList(),
            EolOffline = eol.UsedStaleData,
            Date = date,
        };
    }

    /// <summary>What a new terminal runs for <paramref name="command"/>: every PATH match, shims and links followed.</summary>
    public static CommandResolution Resolve(ProbeContext context, string command, string toolId, IReadOnlyList<ShellNote> shell)
    {
        ArgumentNullException.ThrowIfNull(context);
        IReadOnlyList<PathHit> hits = context.Path.Find(command, context.Aliases);
        string? runs = null;
        string? via = null;
        if (hits.Count > 0 && hits[0].Alias is not { Kind: AliasKind.StoreInstallerStub })
        {
            (runs, via) = ShimResolver.Resolve(hits[0], context.Environment);
        }

        return new CommandResolution
        {
            Command = command,
            Hits = hits,
            Runs = runs,
            Via = via,
            MayDiffer = ShellProfiles.MayDiffer(shell, toolId),
        };
    }

    /// <summary>
    /// Windows itself: the feature update (<c>DisplayVersion</c>), build and edition from
    /// <c>HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion</c>, checked against endoflife.date's
    /// <c>windows</c> (or <c>windows-server</c>) release lines.
    /// </summary>
    public static async Task<RuntimeReport?> WindowsReport(ProbeContext context, EndOfLifeClient eol, DateOnly today, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(eol);
        string? display, build, edition, product, installationType;
        int ubr;
        try
        {
            using IRegistryKeyReader? key = context.Registry.Open(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is null)
            {
                return null;
            }

            display = key.Value("DisplayVersion") as string;
            build = key.Value("CurrentBuild") as string;
            edition = key.Value("EditionID") as string;
            product = key.Value("ProductName") as string;
            installationType = key.Value("InstallationType") as string;
            ubr = key.Value("UBR") is int u ? u : 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }

        if (display is null || build is null)
        {
            return null;
        }

        bool server = string.Equals(installationType, "Server", StringComparison.OrdinalIgnoreCase);
        bool eleven = int.TryParse(build, NumberStyles.Integer, CultureInfo.InvariantCulture, out int b) && b >= 22000;
        string productId = server ? "windows-server" : "windows";
        bool enterprise = edition?.Contains("Enterprise", StringComparison.OrdinalIgnoreCase) == true || edition?.Contains("Education", StringComparison.OrdinalIgnoreCase) == true;
        string name = server ? (product ?? "Windows Server") : (eleven ? "Windows 11" : "Windows 10");
        string version = string.Create(CultureInfo.InvariantCulture, $"{display} (build {build}.{ubr})");
        EolData data = await eol.CyclesAsync(productId, cancellationToken).ConfigureAwait(false);
        string year = VersionText.FirstVersion(product) ?? (product?.Split(' ').FirstOrDefault(w => w.Length == 4 && w.All(char.IsAsciiDigit)) ?? display);
        string[] candidates = server
            ? [year.ToLowerInvariant(), display.ToLowerInvariant()]
            : [$"{(eleven ? "11" : "10")}-{display.ToLowerInvariant()}-{(enterprise ? "e" : "w")}", $"{(eleven ? "11" : "10")}-{display.ToLowerInvariant()}"];
        ReleaseCycle? cycle = candidates.Select(c => data.Cycles.FirstOrDefault(x => string.Equals(x.Cycle, c, StringComparison.OrdinalIgnoreCase))).OfType<ReleaseCycle>().FirstOrDefault();
        var install = new Installation
        {
            Version = version,
            Cycle = cycle?.Cycle ?? candidates[0],
            Path = context.Variable("SystemRoot") ?? @"C:\Windows",
            Source = InstallSource.Unknown,
            SourceDetail = edition,
            VersionFrom = @"registry (Windows NT\CurrentVersion)",
            IsDefault = true,
            Product = productId,
            Support = cycle?.Status(today) ?? SupportStatus.Unknown,
            SupportEnds = cycle?.EolDate,
            ReleaseDate = cycle?.ReleaseDate,
        };
        var issues = new List<RuntimeIssue>();
        if (install.Support == SupportStatus.EndOfLife)
        {
            issues.Add(new(IssueLevel.Critical, $"{name} {display} no longer gets security updates. Install the newest feature update."));
        }
        else if (install.Support == SupportStatus.EndingSoon)
        {
            issues.Add(new(IssueLevel.Warning, $"{name} {display}: security updates end {install.SupportEnds?.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}."));
        }

        return new RuntimeReport
        {
            Id = "windows",
            Name = name,
            Installs = [install],
            Issues = issues,
            Steps = [new RuntimeStep { Id = "windows:upgrade:1", Kind = StepKind.Upgrade, Title = "Open Windows Update", Detail = "Check for updates in Settings.", Commands = ["Start-Process 'ms-settings:windowsupdate'"] }],
            DataSource = $"endoflife.date/{productId}",
            CheckedAt = data.Fetched,
        };
    }
}
