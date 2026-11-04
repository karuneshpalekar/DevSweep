using System.Net;
using System.Text.RegularExpressions;
using DevSweep.Core.Health.Common;

namespace DevSweep.Core.Health.Ports;

/// <summary>How a port's project folder was worked out.</summary>
public enum FolderSource
{
    /// <summary>Not known.</summary>
    Unknown,

    /// <summary>The process's working directory (read from its parameters; reliable for 64-bit processes you own).</summary>
    WorkingDirectory,

    /// <summary>A path in its command line that sits in a project.</summary>
    CommandLine,

    /// <summary>Its program sits inside a project (a built binary).</summary>
    ProgramPath,
}

/// <summary>Who can connect.</summary>
public enum Exposure
{
    /// <summary>Only this PC (127.0.0.1 or ::1).</summary>
    Loopback,

    /// <summary>Every network interface (0.0.0.0 or ::): other devices can try to connect.</summary>
    AllInterfaces,

    /// <summary>One specific address of this PC.</summary>
    SpecificAddress,
}

/// <summary>A port something on this PC is listening on.</summary>
public sealed record ListeningPort
{
    public string Id => $"{Pid}:{Protocol}:{Port}";

    public required int Port { get; init; }

    public required PortProtocol Protocol { get; init; }

    public required int Pid { get; init; }

    /// <summary>Process name without ".exe".</summary>
    public required string ProcessName { get; init; }

    public string? ProgramPath { get; init; }

    /// <summary>The command line with secret-looking values masked.</summary>
    public string? CommandLine { get; init; }

    /// <summary>"Vite dev server in my-app", "PostgreSQL".</summary>
    public required string Label { get; init; }

    public string? Folder { get; init; }

    public FolderSource FolderSource { get; init; }

    /// <summary>Local addresses it listens on ("0.0.0.0", "::1").</summary>
    public IReadOnlyList<string> Addresses { get; init; } = [];

    public Exposure Exposure { get; init; }

    /// <summary>Listening on all interfaces (or a specific non-loopback address).</summary>
    public bool ReachableFromNetwork => Exposure != Exposure.Loopback;

    /// <summary>What Windows Firewall would do with connections from other devices (null for loopback-only).</summary>
    public FirewallVerdict? Firewall { get; init; }

    /// <summary>Started by you (null when Windows would not say).</summary>
    public bool? OwnedByYou { get; init; }

    public bool IsDevelopment { get; init; }

    public DateTimeOffset? Started { get; init; }
}

/// <summary>What the Ports tab shows.</summary>
public sealed record PortsResult
{
    public IReadOnlyList<ListeningPort> Ports { get; init; } = [];

    /// <summary>Listeners not shown because another user (or Windows) owns them.</summary>
    public int OthersHidden { get; init; }

    /// <summary>Why firewall reachability is unknown, when it is.</summary>
    public string? FirewallProblem { get; init; }

    public DateTimeOffset Date { get; init; }
}

/// <summary>
/// Ports: TCP listeners and UDP sockets (IPv4 and IPv6) with their owning process, its program
/// and command line, the project folder it runs from (with how sure that is), whether it listens
/// beyond this PC, and whether Windows Firewall would let other devices in. By default only
/// processes you own are listed (like the Mac app); <c>includeOthers</c> lists everything.
/// </summary>
public static partial class PortScanner
{
    private static readonly HashSet<string> ProjectMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "package.json", "pyproject.toml", "manage.py", "go.mod", "Cargo.toml", "Gemfile", "composer.json", "pom.xml", "build.gradle", "build.gradle.kts",
        "requirements.txt", "deno.json", "bun.lockb", "global.json",
    };

    private static readonly (string Prefix, string Label)[] Known =
    [
        ("node", "Node.js"), ("bun", "Bun"), ("deno", "Deno"), ("python", "Python"), ("pythonw", "Python"), ("ruby", "Ruby"), ("java", "Java"), ("javaw", "Java"),
        ("php", "PHP"), ("redis-server", "Redis"), ("mysqld", "MySQL"), ("mongod", "MongoDB"), ("com.docker", "Docker"), ("docker", "Docker"),
        ("wslrelay", "WSL port forwarding"), ("ollama", "Ollama"), ("nginx", "nginx"), ("caddy", "Caddy"), ("hugo", "Hugo"), ("dotnet", ".NET"),
        ("iisexpress", "IIS Express"), ("go", "Go"), ("cloudflared", "Cloudflare Tunnel"), ("ngrok", "ngrok"), ("code", "VS Code"), ("rider64", "Rider"),
        ("idea64", "IntelliJ IDEA"), ("devenv", "Visual Studio"), ("postgres", "PostgreSQL"), ("sqlservr", "SQL Server"),
    ];

    private static readonly Dictionary<string, string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["System"] = "Windows (System)", ["svchost"] = "Windows service host", ["lsass"] = "Windows security (LSASS)", ["wininit"] = "Windows start-up",
        ["services"] = "Windows services", ["spoolsv"] = "Windows print spooler", ["Idle"] = "Windows", ["OneDrive"] = "OneDrive",
    };

    public static PortsResult Scan(ISocketTable sockets, IProcessDetails processes, Firewall? firewall, string? profile, bool includeOthers = false, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(sockets);
        ArgumentNullException.ThrowIfNull(processes);
        var info = new Dictionary<int, ProcessInfo?>();
        var ports = new List<ListeningPort>();
        int hidden = 0;
        foreach (IGrouping<(int Pid, PortProtocol Protocol, int Port), SocketEntry> group in sockets.Listening().GroupBy(e => (e.Pid, e.Protocol, e.Port)).OrderBy(g => g.Key.Port))
        {
            (int pid, PortProtocol protocol, int port) = group.Key;
            if (!info.TryGetValue(pid, out ProcessInfo? process))
            {
                process = processes.Read(pid);
                info[pid] = process;
            }

            if (process?.OwnedByYou != true && !includeOthers)
            {
                hidden++;
                continue;
            }

            List<IPAddress> addresses = group.Select(e => e.Address).Distinct().ToList();
            Exposure exposure = addresses.Any(a => a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any)) ? Exposure.AllInterfaces
                : addresses.All(IPAddress.IsLoopback) ? Exposure.Loopback
                : Exposure.SpecificAddress;
            (string? folder, FolderSource source) = FolderOf(process, profile);
            string name = process?.Name ?? (pid == 4 ? "System" : $"PID {pid}");
            (string label, bool dev) = Describe(name, process?.CommandLine ?? string.Empty, folder, profile);
            ports.Add(new ListeningPort
            {
                Port = port,
                Protocol = protocol,
                Pid = pid,
                ProcessName = name,
                ProgramPath = process?.Path,
                CommandLine = process?.CommandLine is { } cmd ? Redact(cmd) : null,
                Label = label,
                Folder = folder,
                FolderSource = source,
                Addresses = addresses.Select(a => a.ToString()).ToList(),
                Exposure = exposure,
                Firewall = exposure == Exposure.Loopback ? null : firewall?.Judge(process?.Path, port, protocol) ?? FirewallVerdict.Unknown("not checked"),
                OwnedByYou = process?.OwnedByYou,
                IsDevelopment = dev,
                Started = process?.Started,
            });
        }

        return new PortsResult
        {
            Ports = ports.OrderBy(p => p.IsDevelopment ? 0 : 1).ThenBy(p => p.Port).ThenBy(p => p.Protocol).ToList(),
            OthersHidden = hidden,
            FirewallProblem = firewall?.Problem,
            Date = now ?? DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// The project a process belongs to: its working directory when that is a folder in the profile
    /// (not the profile itself), else a path in its command line inside a project, else its program
    /// inside a project. Each answer says which it was.
    /// </summary>
    public static (string? Folder, FolderSource Source) FolderOf(ProcessInfo? process, string? profile)
    {
        if (process is null || profile is null)
        {
            return (null, FolderSource.Unknown);
        }

        if (process.WorkingDirectory is { } cwd && Under(cwd, profile) && !Same(cwd, profile))
        {
            return (Path.TrimEndingDirectorySeparator(cwd), FolderSource.WorkingDirectory);
        }

        foreach (Match m in CommandLinePath().Matches(process.CommandLine ?? string.Empty))
        {
            string candidate = m.Value.Trim('"');
            if (Under(candidate, profile) && ProjectAbove(candidate, profile) is { } project)
            {
                return (project, FolderSource.CommandLine);
            }
        }

        if (process.Path is { } program && Under(program, profile) && ProjectAbove(program, profile) is { } built)
        {
            return (built, FolderSource.ProgramPath);
        }

        return (null, FolderSource.Unknown);
    }

    /// <summary>Turns a process into something a person recognises (the Mac app's names, Windows' own services).</summary>
    public static (string Label, bool IsDevelopment) Describe(string processName, string commandLine, string? folder, string? profile)
    {
        ArgumentNullException.ThrowIfNull(processName);
        ArgumentNullException.ThrowIfNull(commandLine);
        string project = folder is null ? string.Empty : " in " + Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        if (SystemNames.TryGetValue(processName, out string? system))
        {
            return (system, false);
        }

        (string Prefix, string Label) known = Known.FirstOrDefault(k => processName.Equals(k.Prefix, StringComparison.OrdinalIgnoreCase)
            || processName.StartsWith(k.Prefix + "-", StringComparison.OrdinalIgnoreCase) || processName.StartsWith(k.Prefix + ".", StringComparison.OrdinalIgnoreCase));
        if (known.Label is not null)
        {
            string label = known.Label;
            string c = commandLine;
            if (c.Contains("GradleDaemon", StringComparison.Ordinal))
            {
                label = "Gradle daemon";
            }
            else if (c.Contains("vite", StringComparison.OrdinalIgnoreCase))
            {
                label = "Vite dev server";
            }
            else if (c.Contains("next", StringComparison.OrdinalIgnoreCase) && c.Contains("dev", StringComparison.OrdinalIgnoreCase))
            {
                label = "Next.js dev server";
            }
            else if (c.Contains("astro", StringComparison.OrdinalIgnoreCase))
            {
                label = "Astro dev server";
            }
            else if (c.Contains("webpack", StringComparison.OrdinalIgnoreCase))
            {
                label = "webpack dev server";
            }
            else if (c.Contains("manage.py", StringComparison.OrdinalIgnoreCase))
            {
                label = "Django dev server";
            }
            else if (c.Contains("uvicorn", StringComparison.OrdinalIgnoreCase) || c.Contains("gunicorn", StringComparison.OrdinalIgnoreCase) || c.Contains("flask", StringComparison.OrdinalIgnoreCase))
            {
                label = "Python web server";
            }

            return (label + project, true);
        }

        return (processName + project, folder is not null && profile is not null && Under(folder, profile));
    }

    /// <summary>Masks values that look like secrets (<c>--password=x</c>, <c>TOKEN=x</c>, <c>-p x</c> after a secret-looking flag).</summary>
    public static string Redact(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        return SecretArgument().Replace(commandLine, m => m.Groups["k"].Value + m.Groups["sep"].Value + "***");
    }

    private static string? ProjectAbove(string path, string profile)
    {
        string? folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        for (int i = 0; folder is not null && i < 8 && Under(folder, profile) && !Same(folder, profile); i++)
        {
            if (HealthFiles.List(folder).Any(e => ProjectMarkers.Contains(e.Name)))
            {
                return Path.TrimEndingDirectorySeparator(folder);
            }

            folder = Path.GetDirectoryName(folder);
        }

        return null;
    }

    private static bool Under(string path, string folder)
        => Path.IsPathFullyQualified(path) && Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool Same(string a, string b)
        => string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"""[A-Za-z]:\\[^""]+""|[A-Za-z]:\\[^\s""]+")]
    private static partial Regex CommandLinePath();

    [GeneratedRegex(@"(?<k>[-\w]*(password|passwd|pwd|secret|token|apikey|api-key|api_key)[-\w]*)(?<sep>[=:]|\s+)(?<v>""[^""]*""|\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretArgument();
}
