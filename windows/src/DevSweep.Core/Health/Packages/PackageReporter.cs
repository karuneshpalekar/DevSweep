using System.Globalization;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Health.Common;
using DevSweep.Core.Health.Runtimes;
using DevSweep.Core.Locations;
using DevSweep.Core.Platform;

namespace DevSweep.Core.Health.Packages;

/// <summary>Runs a manager's read-only query (the real one is <see cref="CommandRunner"/>; tests give recorded output).</summary>
public delegate Task<CommandResult> PackageRunner(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken cancellationToken);

/// <summary>Measures a cache folder: bytes and whether the whole folder was measured; null when it is not there.</summary>
public delegate (long Bytes, bool Exact)? FolderSizer(string path);

/// <summary>
/// The package-manager report (the Windows counterpart of the Mac's Homebrew report): for winget, Scoop and Chocolatey, each
/// only when installed, which packages are outdated or no longer offered, leftover folders, the manager's caches, and tools that
/// two owners installed. Queries are read-only with a hard time limit and no prompts (<c>--disable-interactivity</c>); Scoop is
/// answered from its own files (no program is run); what cannot be read is said so ("couldn't read the result"), not guessed.
/// Nothing here changes anything: fixes are commands the user runs in a visible window.
/// </summary>
public sealed class PackageReporter(ISystemEnvironment environment, PackageRunner? runner = null, FolderSizer? sizer = null)
{
    private static readonly TimeSpan QueryLimit = TimeSpan.FromSeconds(90);
    private readonly PackageRunner _runner = runner ?? ((argv, limit, token) => CommandRunner.RunAsync(argv, limit, token));
    private readonly FolderSizer _sizer = sizer ?? MeasureFolder;

    public async Task<PackagesResult> ReportAsync(IReadOnlyList<RuntimeReport>? tools, DateTimeOffset? now = null, CancellationToken cancellationToken = default)
    {
        var managers = new List<PackageManagerReport>();
        if (Winget() is { } winget)
        {
            managers.Add(await WingetReportAsync(winget, cancellationToken).ConfigureAwait(false));
        }

        if (ScoopRoot() is { } scoop)
        {
            managers.Add(ScoopReport(scoop));
        }

        if (Choco() is { } choco)
        {
            managers.Add(await ChocoReportAsync(choco, cancellationToken).ConfigureAwait(false));
        }

        return new PackagesResult { Managers = managers, Duplicates = Duplicates(tools ?? []), Date = now ?? DateTimeOffset.UtcNow };
    }

    /// <summary>Tools two owners installed (the same release line from, say, Scoop and nodejs.org).</summary>
    public static IReadOnlyList<DuplicateInstall> Duplicates(IReadOnlyList<RuntimeReport> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var found = new List<DuplicateInstall>();
        foreach (RuntimeReport tool in tools)
        {
            foreach (IGrouping<string, Installation> cycle in tool.Installs.Where(i => !i.IsSystem).GroupBy(i => i.Cycle, StringComparer.Ordinal))
            {
                string[] owners = [.. cycle.Select(i => i.SourceTitle).Distinct(StringComparer.Ordinal)];
                if (owners.Length > 1)
                {
                    found.Add(new DuplicateInstall(tool.Id, tool.Name, cycle.Key, owners));
                }
            }
        }

        return found;
    }

    private string? Winget()
    {
        string? local = environment.KnownFolder("LocalAppData");
        string? exe = local is null ? null : Path.Join(local, "Microsoft", "WindowsApps", "winget.exe");
        return exe is not null && environment.FileExists(exe) ? exe : null;
    }

    private string? Choco()
    {
        string? root = environment.GetVariable("ChocolateyInstall") ?? (environment.KnownFolder("ProgramData") is { } data ? Path.Join(data, "chocolatey") : null);
        string? exe = root is null ? null : Path.Join(root, "bin", "choco.exe");
        return exe is not null && TrustedPrograms.WhyNot(exe, environment) is null ? exe : null;
    }

    private string? ScoopRoot()
    {
        string? root = environment.GetVariable("SCOOP") ?? (environment.KnownFolder("Profile") is { } profile ? Path.Join(profile, "scoop") : null);
        return root is not null && environment.DirectoryExists(Path.Join(root, "apps")) ? root : null;
    }

    private async Task<PackageManagerReport> WingetReportAsync(string exe, CancellationToken cancellationToken)
    {
        CommandResult result = await _runner([exe, "list", "--upgrade-available", "--disable-interactivity", "--accept-source-agreements"], QueryLimit, cancellationToken).ConfigureAwait(false);
        string? problem = null;
        IReadOnlyList<OutdatedPackage> outdated = [];
        if (result.StartError is not null || result.TimedOut)
        {
            problem = result.TimedOut ? "winget didn't answer in time, so its packages weren't checked." : "winget couldn't be started, so its packages weren't checked.";
        }
        else
        {
            ParsedPackages parsed = PackageParsers.Winget(result.StandardOutput, result.ExitCode);
            if (parsed.Understood)
            {
                outdated = parsed.Outdated;
            }
            else
            {
                problem = "winget answered, but DevSweep couldn't read the result (it may be in another language or format), so it says nothing about updates.";
            }
        }

        var steps = new List<RuntimeStep>();
        if (outdated.Count > 0)
        {
            steps.Add(new RuntimeStep
            {
                Id = "winget:upgrade-all",
                Kind = StepKind.Upgrade,
                Title = $"Upgrade {Words(outdated.Count)} with winget",
                Detail = "winget upgrades each one with its own installer; some may ask Windows for permission.",
                Commands = ["winget upgrade --all --disable-interactivity"],
            });
        }

        string? local = environment.KnownFolder("LocalAppData");
        return new PackageManagerReport
        {
            Manager = PackageManager.Winget,
            Location = exe,
            Problem = problem,
            Outdated = outdated,
            Steps = steps,
            Caches = Caches([local is null ? null : (Path.Join(local, "Temp", "WinGet"), "winget downloads")]),
            Note = "winget checks its sources online each time.",
        };
    }

    private async Task<PackageManagerReport> ChocoReportAsync(string exe, CancellationToken cancellationToken)
    {
        CommandResult result = await _runner([exe, "outdated", "-r", "--no-color"], QueryLimit, cancellationToken).ConfigureAwait(false);
        string? problem = null;
        IReadOnlyList<OutdatedPackage> outdated = [];
        if (result.StartError is not null || result.TimedOut)
        {
            problem = result.TimedOut ? "Chocolatey didn't answer in time, so its packages weren't checked." : "Chocolatey couldn't be started, so its packages weren't checked.";
        }
        else
        {
            ParsedPackages parsed = PackageParsers.Chocolatey(result.StandardOutput, result.ExitCode);
            if (parsed.Understood)
            {
                outdated = parsed.Outdated;
            }
            else
            {
                problem = "Chocolatey answered, but DevSweep couldn't read the result, so it says nothing about updates.";
            }
        }

        string root = Path.GetDirectoryName(Path.GetDirectoryName(exe) ?? string.Empty) ?? string.Empty;
        var orphans = new List<OrphanFolder>();
        int count = 0;
        foreach (Detectors.ProbeEntry package in HealthFiles.Folders(Path.Join(root, "lib")))
        {
            count++;
            if (HealthFiles.List(package.Path).Count == 0)
            {
                orphans.Add(new OrphanFolder(package.Path, "An empty package folder: nothing is installed in it."));
            }
        }

        var steps = new List<RuntimeStep>();
        if (outdated.Any(p => !p.Pinned))
        {
            steps.Add(new RuntimeStep
            {
                Id = "choco:upgrade-all",
                Kind = StepKind.Upgrade,
                Title = $"Upgrade {Words(outdated.Count(p => !p.Pinned))} with Chocolatey",
                Detail = "Chocolatey changes programs for every user, so this needs an administrator window.",
                Commands = ["choco upgrade all -y"],
                NeedsAdmin = true,
            });
        }

        AddOrphanStep(steps, "choco", orphans);
        return new PackageManagerReport
        {
            Manager = PackageManager.Chocolatey,
            Location = root,
            Problem = problem,
            InstalledCount = count,
            Outdated = outdated,
            Orphans = orphans,
            Steps = steps,
            Caches = Caches([(Path.Join(environment.TempPath, "chocolatey"), "Chocolatey downloads")]),
        };
    }

    /// <summary>Scoop, from its own files: what is installed, its bucket's manifest version, and what the bucket no longer has. No program is run.</summary>
    private PackageManagerReport ScoopReport(string root)
    {
        var outdated = new List<OutdatedPackage>();
        var deprecated = new List<DeprecatedPackage>();
        var orphans = new List<OrphanFolder>();
        int count = 0;
        bool anyBucketMissing = false;
        foreach (Detectors.ProbeEntry app in HealthFiles.Folders(Path.Join(root, "apps")))
        {
            if (string.Equals(app.Name, "scoop", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            List<string> versions = [.. HealthFiles.Folders(app.Path).Where(e => !string.Equals(e.Name, "current", StringComparison.OrdinalIgnoreCase)).Select(e => e.Name)];
            if (versions.Count == 0)
            {
                orphans.Add(new OrphanFolder(app.Path, "An empty app folder: no version is installed in it."));
                continue;
            }

            count++;
            string installed = versions.OrderBy(v => v, Comparer<string>.Create((a, b) => VersionText.Less(a, b) ? -1 : VersionText.Less(b, a) ? 1 : 0)).Last();
            string versionDir = Path.Join(app.Path, installed);
            (string? bucket, bool held) = PackageParsers.ScoopInstall(HealthFiles.ReadText(Path.Join(versionDir, "install.json"), 16 * 1024));
            if (bucket is null)
            {
                continue;
            }

            string bucketDir = Path.Join(root, "buckets", bucket);
            if (!environment.DirectoryExists(bucketDir))
            {
                anyBucketMissing = true;
                continue;
            }

            string? manifest = new[] { Path.Join(bucketDir, "bucket", app.Name + ".json"), Path.Join(bucketDir, app.Name + ".json") }.FirstOrDefault(environment.FileExists);
            if (manifest is null)
            {
                deprecated.Add(new DeprecatedPackage(app.Name, installed, $"It is no longer in the {bucket} bucket, so it won't get updates."));
                continue;
            }

            if (PackageParsers.ScoopManifestVersion(environment.ReadAllText(manifest)) is { } latest && VersionText.Less(installed, latest))
            {
                outdated.Add(new OutdatedPackage(app.Name, app.Name, installed, latest, held));
            }
        }

        var steps = new List<RuntimeStep>();
        if (outdated.Any(p => !p.Pinned))
        {
            steps.Add(new RuntimeStep
            {
                Id = "scoop:upgrade-all",
                Kind = StepKind.Upgrade,
                Title = $"Update {Words(outdated.Count(p => !p.Pinned))} with Scoop",
                Detail = "Refreshes the buckets, then updates every app that isn't held back.",
                Commands = ["scoop update", "scoop update *"],
            });
        }

        if (deprecated.Count > 0)
        {
            steps.Add(new RuntimeStep
            {
                Id = "scoop:removed",
                Kind = StepKind.Remove,
                Title = $"Remove {Words(deprecated.Count)} Scoop no longer offers",
                Detail = "Only if you don't use them any more. Their data stays where the app keeps it.",
                Commands = [.. deprecated.Select(d => "scoop uninstall " + PowerShellText.Quote(d.Name))],
            });
        }

        string cache = Path.Join(root, "cache");
        List<PackageCache> caches = Caches([(cache, "Scoop downloads")]);
        if (caches.Count > 0)
        {
            steps.Add(new RuntimeStep
            {
                Id = "scoop:cache",
                Kind = StepKind.Remove,
                Title = "Clear Scoop's download cache",
                Detail = "Scoop downloads the installers again if it needs them.",
                Commands = ["scoop cache rm *"],
            });
        }

        AddOrphanStep(steps, "scoop", orphans);
        return new PackageManagerReport
        {
            Manager = PackageManager.Scoop,
            Location = root,
            InstalledCount = count,
            Outdated = outdated,
            Deprecated = deprecated,
            Orphans = orphans,
            Steps = steps,
            Caches = caches,
            Note = anyBucketMissing
                ? "Read from Scoop's own files; some apps come from buckets that aren't on this PC, so they weren't compared. Versions are as fresh as your last scoop update."
                : "Read from Scoop's own files (no program was run). Versions are as fresh as your last scoop update.",
        };
    }

    private static void AddOrphanStep(List<RuntimeStep> steps, string id, List<OrphanFolder> orphans)
    {
        if (orphans.Count > 0)
        {
            steps.Add(new RuntimeStep
            {
                Id = id + ":orphans",
                Kind = StepKind.Remove,
                Title = $"Move {Words(orphans.Count, "leftover folder")} to the Recycle Bin",
                Detail = "Empty folders nothing is installed in. You can restore them from the Recycle Bin.",
                Commands = [.. orphans.Select(o => PowerShellText.RecycleFolder(o.Path))],
            });
        }
    }

    private List<PackageCache> Caches(IEnumerable<(string Path, string Title)?> folders)
    {
        var caches = new List<PackageCache>();
        foreach ((string path, string title) in folders.OfType<(string, string)>())
        {
            if (environment.DirectoryExists(path) && _sizer(path) is { Bytes: > 0 } size)
            {
                caches.Add(new PackageCache(path, title, size.Bytes, size.Exact));
            }
        }

        return caches;
    }

    private static string Words(int count, string noun = "package") => string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? string.Empty : "s")}");

    private static (long, bool)? MeasureFolder(string path)
    {
        try
        {
            WalkResult walk = DirectoryWalker.Measure(path, new WalkOptions { TimeLimit = TimeSpan.FromSeconds(5) });
            return (walk.Bytes, walk.Complete);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
