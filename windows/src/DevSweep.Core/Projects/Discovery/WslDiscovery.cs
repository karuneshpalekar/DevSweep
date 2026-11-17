using DevSweep.Core.Cleaning;
using DevSweep.Core.Inventory.Registry;
using Microsoft.Win32;

namespace DevSweep.Core.Projects.Discovery;

/// <summary>A WSL distribution: its name and whether it is running now.</summary>
public sealed record WslDistro(string Name, bool Running)
{
    /// <summary>The folder Windows sees its files under (reading it never starts a distribution that is already running).</summary>
    public string Root => @"\\wsl.localhost\" + Name;
}

/// <summary>What listing the WSL distributions found: each one, and the repositories in the running ones.</summary>
public sealed record WslSearchResult(IReadOnlyList<WslDistro> Distros, IReadOnlyList<FoundRepo> Repos, bool Complete, long Folders);

/// <summary>Where WSL's facts come from (the registry and <c>wsl.exe</c>); a fake in tests.</summary>
public interface IWslSource
{
    /// <summary>Every registered distribution's name.</summary>
    IReadOnlyList<string> RegisteredDistros();

    /// <summary>The names of distributions that are running now, or null when that could not be asked.</summary>
    Task<IReadOnlySet<string>?> RunningAsync(CancellationToken cancellationToken);
}

/// <summary>The real thing: <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss</c> and <c>wsl.exe --list --running</c>.</summary>
public sealed class WindowsWslSource(IRegistryReader? registry = null) : IWslSource
{
    private const string LxssKey = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

    public IReadOnlyList<string> RegisteredDistros()
    {
        var names = new List<string>();
        try
        {
            using IRegistryKeyReader? key = (registry ?? WindowsRegistryReader.Instance).Open(RegistryHive.CurrentUser, RegistryView.Registry64, LxssKey);
            if (key is null)
            {
                return names;
            }

            foreach (string sub in key.SubKeyNames())
            {
                using IRegistryKeyReader? distro = key.OpenSubKey(sub);
                if (distro?.Value("DistributionName") is string name && name.Length > 0)
                {
                    names.Add(name);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Treated as no distributions.
        }

        return names;
    }

    public async Task<IReadOnlySet<string>?> RunningAsync(CancellationToken cancellationToken)
    {
        string wsl = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
        if (!File.Exists(wsl))
        {
            return null;
        }

        var options = new CommandOptions { Environment = new Dictionary<string, string?> { ["WSL_UTF8"] = "1" } };
        CommandResult result = await CommandRunner.RunAsync([wsl, "--list", "--running", "--quiet"], TimeSpan.FromSeconds(8), options, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return null;
        }

        return WslDiscovery.ParseNames(result.StandardOutput);
    }
}

/// <summary>
/// Projects inside WSL distributions (D5): listed read-only, found under <c>\\wsl.localhost\&lt;distro&gt;\home\*</c> and
/// <c>\root</c>, and only for distributions that are already running: DevSweep never starts one (opening that path would).
/// A stopped distribution is reported so the page can say "Start it to include its projects". The 9P file system is slow,
/// so the search has a time budget, fewer folders at once, and is cancellable.
/// </summary>
public static class WslDiscovery
{
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(90);

    /// <summary>Docker Desktop's own distributions hold no projects.</summary>
    private static bool IsInfrastructure(string name) => name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for a path inside a WSL distribution (<c>\\wsl.localhost\...</c> or <c>\\wsl$\...</c>).</summary>
    public static bool IsWslPath(string? path)
        => path is not null && (path.StartsWith(@"\\wsl.localhost\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"\\wsl$\", StringComparison.OrdinalIgnoreCase));

    /// <summary>The distribution name of a WSL path, or null.</summary>
    public static string? DistroOf(string? path)
    {
        if (!IsWslPath(path))
        {
            return null;
        }

        string[] parts = path!.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[1] : null;
    }

    /// <summary>Names from <c>wsl.exe</c> output (any encoding: stray NULs from UTF-16 are dropped), one per line.</summary>
    public static IReadOnlySet<string> ParseNames(string output)
        => output.Replace("\0", string.Empty, StringComparison.Ordinal).Replace("\uFEFF", string.Empty, StringComparison.Ordinal)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static async Task<IReadOnlyList<WslDistro>> ListAsync(IWslSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        List<string> registered = source.RegisteredDistros().Where(n => !IsInfrastructure(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (registered.Count == 0)
        {
            return [];
        }

        IReadOnlySet<string>? running = await source.RunningAsync(cancellationToken).ConfigureAwait(false);
        return [.. registered.Select(n => new WslDistro(n, running?.Contains(n) ?? false))];
    }

    /// <summary>Searches the running distributions' home folders; stopped ones are listed in the result and left alone.</summary>
    public static async Task<WslSearchResult> SearchAsync(IWslSource source, TimeSpan? budget = null, IProgress<DeepSearchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<WslDistro> distros = await ListAsync(source, cancellationToken).ConfigureAwait(false);
        var repos = new List<FoundRepo>();
        long folders = 0;
        bool complete = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan total = budget ?? DefaultBudget;
        foreach (WslDistro distro in distros.Where(d => d.Running))
        {
            var roots = new List<string>();
            string home = Path.Join(distro.Root, "home");
            try
            {
                if (Directory.Exists(home))
                {
                    roots.AddRange(Directory.EnumerateDirectories(home));
                }

                string root = Path.Join(distro.Root, "root");
                if (Directory.Exists(root))
                {
                    roots.Add(root);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                complete = false;
                continue;
            }

            TimeSpan left = total - clock.Elapsed;
            if (left <= TimeSpan.Zero)
            {
                complete = false;
                break;
            }

            DeepSearchResult result = await DeepSearch.RunAsync(new DeepSearchOptions { Roots = roots, TimeBudget = left, Parallelism = 2, Progress = progress }, cancellationToken).ConfigureAwait(false);
            repos.AddRange(result.Repos);
            folders += result.FoldersVisited;
            complete &= result.Complete;
        }

        return new WslSearchResult(distros, repos, complete, folders);
    }
}
