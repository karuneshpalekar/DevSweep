using System.Globalization;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Detectors;
using DevSweep.Core.Inventory.Registry;
using DevSweep.Core.Packs.Tools;
using DevSweep.Core.Rules;
using DevSweep.Core.Scanning;
using Microsoft.Win32;

namespace DevSweep.Core.Packs.Detectors;

/// <summary>
/// <c>kind: vhdxDisks</c>. The virtual disks of WSL 2 distros and Docker Desktop, which grow
/// and never shrink on their own. Never offered for removal (they hold every file inside the
/// distro or every image and volume): the finding shows the disk file's size and guided
/// compaction steps. Its size is 0 because how much compaction frees cannot be known
/// before it runs; when a distro is running, the bytes its files use (<c>df</c> inside it)
/// give an upper bound, shown as such.
/// <list type="bullet">
/// <item><c>source</c>: <c>wsl</c> (distros registered under <c>HKCU\...\Lxss</c>) or <c>docker</c> (Docker Desktop's data disk);</item>
/// <item><c>fileMinMB</c>: smaller disk files are not shown (default 1024).</item>
/// </list>
/// </summary>
public sealed class VhdxDisksDetector(IMachineQueries machine) : Detector<VhdxDisksDetector.Options>
{
    public const string LxssKey = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

    public override string Kind => "vhdxDisks";

    /// <summary>A registered WSL distro.</summary>
    public sealed record Distro(string Name, string? BasePath, string VhdFileName, int Version);

    /// <summary>Every distro under <c>HKCU\...\Lxss\{guid}</c>; null when the key cannot be read.</summary>
    public static IReadOnlyList<Distro>? ReadDistros(IRegistryReader registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        try
        {
            using IRegistryKeyReader? lxss = registry.Open(RegistryHive.CurrentUser, RegistryView.Registry64, LxssKey);
            if (lxss is null)
            {
                return [];
            }

            var distros = new List<Distro>();
            foreach (string id in lxss.SubKeyNames())
            {
                using IRegistryKeyReader? key = lxss.OpenSubKey(id);
                if (key?.Value("DistributionName") is not string name)
                {
                    continue;
                }

                string? basePath = key.Value("BasePath") as string;
                string vhd = key.Value("VhdFileName") as string is { Length: > 0 } v ? v : "ext4.vhdx";
                int version = key.Value("Version") is int n ? n : 2;
                distros.Add(new Distro(name, basePath is null ? null : StripExtendedPrefix(basePath), vhd, version));
            }

            return distros;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    /// <summary><c>\\?\C:\x</c> to <c>C:\x</c> (WSL writes BasePath in both forms).</summary>
    public static string StripExtendedPrefix(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + path[8..]
            : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..]
            : path;
    }

    /// <summary>Names from <c>wsl --list --running --quiet</c> (UTF-16 output read as UTF-8 leaves NULs: removed).</summary>
    public static IReadOnlyList<string> ParseRunning(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return output.Replace("\0", string.Empty, StringComparison.Ordinal).Replace("\uFEFF", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    /// <summary>The number from <c>df -B1 --output=used /</c> ("Used\n123\n"), or null.</summary>
    public static long? ParseDfUsed(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length >= 2 && long.TryParse(lines[^1], NumberStyles.None, CultureInfo.InvariantCulture, out long used) ? used : null;
    }

    protected override Options? BindOptions(OptionReader options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string? source = options.Text("source", required: true);
        if (source is not null and not ("wsl" or "docker"))
        {
            options.Errors.Add(options.Node.Get("source"), "source should be wsl or docker");
        }

        int minMb = options.Number("fileMinMB", 0, 10_000_000) ?? 1024;
        return new Options(source ?? "wsl", minMb);
    }

    protected override IReadOnlyList<Candidate> Detect(DetectorRun run, Options options)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);
        List<(string Name, string Vhdx, string Origin)> disks = options.Source == "docker" ? DockerDisks(run) : WslDisks(run);
        if (disks.Count == 0)
        {
            return [];
        }

        string? wsl = ToolLocator.Find(run.Context.Locations.Environment, "wsl.exe",
            run.Context.Locations.Resolve("Windows").Path is { } windows ? Path.Join(windows, "System32", "wsl.exe") : null);
        IReadOnlyList<string> running = [];
        if (options.Source == "wsl" && wsl is not null)
        {
            CommandResult list = machine.Run([wsl, "--list", "--running", "--quiet"], TimeSpan.FromSeconds(15), run.CancellationToken);
            running = list.Succeeded ? ParseRunning(list.StandardOutput) : [];
        }

        var candidates = new List<Candidate>();
        foreach ((string name, string vhdx, string origin) in disks)
        {
            run.ThrowIfCancelled();
            long fileBytes = FileBytes(vhdx, run);
            if (fileBytes < options.FileMinMB * 1_000_000L)
            {
                continue;
            }

            var checks = new List<Check> { Check.Passed(origin), Check.Passed($"The disk file is {SizeText.Format(fileBytes)} on disk ({vhdx})") };
            string used = "unknown";
            string estimate = "unknown until it is compacted";
            if (wsl is not null && running.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                CommandResult df = machine.Run([wsl, "-d", name, "--exec", "df", "-B1", "--output=used", "/"], TimeSpan.FromSeconds(20), run.CancellationToken);
                if (df.Succeeded && ParseDfUsed(df.StandardOutput) is long inside)
                {
                    used = SizeText.Format(inside);
                    estimate = $"at most {SizeText.Format(Math.Max(0, fileBytes - inside))}";
                    checks.Add(Check.Passed($"{name} is running: its files use {used} (df inside the distro), so compacting can free at most {SizeText.Format(Math.Max(0, fileBytes - inside))}"));
                }
            }

            if (used == "unknown")
            {
                checks.Add(Check.Warning("How much compacting frees is unknown until it runs: DevSweep does not start a stopped distro or Docker to look inside, so the size shown is 0"));
            }

            checks.Add(Check.Passed("Never removed by DevSweep: it holds everything inside the distro or Docker"));
            candidates.Add(new Candidate
            {
                Key = vhdx,
                Size = 0,
                Subtitle = vhdx,
                Facts = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["name"] = name,
                    ["vhdx"] = vhdx,
                    ["vhdxPs"] = vhdx.Replace("'", "''", StringComparison.Ordinal),
                    ["fileSize"] = SizeText.Format(fileBytes),
                    ["used"] = used,
                    ["estimate"] = estimate,
                },
                Checks = checks,
            });
        }

        return candidates;
    }

    private static long FileBytes(string vhdx, DetectorRun run)
    {
        try
        {
            return WalkerMeasurer.Instance.Measure(vhdx, SizeMode.Overview, TimeSpan.FromSeconds(10), run.CancellationToken).Bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return FileProbe.Stat(vhdx)?.Length ?? 0;
        }
    }

    private static List<(string, string, string)> WslDisks(DetectorRun run)
    {
        IReadOnlyList<Distro>? distros = ReadDistros(run.Context.Registry);
        if (distros is null)
        {
            run.Note($"HKCU\\{LxssKey} could not be read");
            return [];
        }

        var disks = new List<(string, string, string)>();
        foreach (Distro distro in distros.Where(d => d.Version == 2 && d.BasePath is not null))
        {
            if (distro.Name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))
            {
                continue; // Docker Desktop's own disks: the docker rule.
            }

            string vhdx = Path.Join(distro.BasePath, distro.VhdFileName);
            if (FileProbe.Stat(vhdx) is { IsDirectory: false, IsReparsePoint: false })
            {
                disks.Add((distro.Name, vhdx, $"WSL lists the distro {distro.Name} with its disk in {distro.BasePath} (HKCU\\{LxssKey})"));
            }
        }

        return disks;
    }

    private static List<(string, string, string)> DockerDisks(DetectorRun run)
    {
        var disks = new List<(string, string, string)>();
        if (run.Context.Locations.Resolve("LocalAppData").Path is not { } local)
        {
            return disks;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] known = [Path.Join(local, "Docker", "wsl", "disk", "docker_data.vhdx"), Path.Join(local, "Docker", "wsl", "data", "ext4.vhdx")];
        foreach (string vhdx in known)
        {
            if (seen.Add(vhdx) && FileProbe.Stat(vhdx) is { IsDirectory: false, IsReparsePoint: false })
            {
                disks.Add(("Docker Desktop", vhdx, "Docker Desktop keeps its images, containers, volumes and build cache in this disk"));
            }
        }

        foreach (Distro distro in ReadDistros(run.Context.Registry) ?? [])
        {
            if (distro.Name.Equals("docker-desktop-data", StringComparison.OrdinalIgnoreCase) && distro.BasePath is not null)
            {
                string vhdx = Path.Join(distro.BasePath, distro.VhdFileName);
                if (seen.Add(vhdx) && FileProbe.Stat(vhdx) is { IsDirectory: false, IsReparsePoint: false })
                {
                    disks.Add(("Docker Desktop", vhdx, $"WSL lists Docker Desktop's data disk in {distro.BasePath}"));
                }
            }
        }

        return disks;
    }

    /// <summary>Bound options of a <c>vhdxDisks</c> rule.</summary>
    public sealed record Options(string Source, int FileMinMB);
}
