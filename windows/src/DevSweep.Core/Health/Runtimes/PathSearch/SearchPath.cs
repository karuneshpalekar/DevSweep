using System.Text;
using DevSweep.Core.Detectors;
using DevSweep.Core.Inventory.Registry;
using DevSweep.Core.Locations;
using Microsoft.Win32;

namespace DevSweep.Core.Health.Runtimes;

/// <summary>One folder of the PATH a new program gets.</summary>
/// <param name="Raw">As stored in the registry.</param>
/// <param name="Expanded">With <c>%VARIABLES%</c> expanded.</param>
/// <param name="FromUser">From <c>HKCU\Environment</c> (otherwise the system PATH).</param>
public sealed record PathEntry(string Raw, string Expanded, bool FromUser);

/// <summary>
/// PATH as Explorer builds it for every program it starts (a new terminal included):
/// the system <c>Path</c> (<c>HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment</c>)
/// then the user's (<c>HKCU\Environment</c>), <c>REG_EXPAND_SZ</c> references expanded with
/// the registry's own variables (user before system, then the process environment), and
/// <c>PATHEXT</c> from the system environment. This is not this process's PATH, which may be
/// stale or changed by whoever started it.
/// </summary>
public sealed class SearchPath
{
    public const string MachineKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    public const string UserKey = "Environment";
    private static readonly string[] DefaultExtensions = [".COM", ".EXE", ".BAT", ".CMD"];

    public SearchPath(IReadOnlyList<PathEntry> entries, IReadOnlyList<string> extensions, IReadOnlyList<string>? problems = null)
    {
        Entries = entries ?? throw new ArgumentNullException(nameof(entries));
        Extensions = extensions ?? throw new ArgumentNullException(nameof(extensions));
        Problems = problems ?? [];
    }

    public IReadOnlyList<PathEntry> Entries { get; }

    /// <summary><c>PATHEXT</c>, upper case with dots (".COM", ".EXE"...).</summary>
    public IReadOnlyList<string> Extensions { get; }

    /// <summary>What could not be read (then PATH may be incomplete).</summary>
    public IReadOnlyList<string> Problems { get; }

    public static SearchPath FromRegistry(IRegistryReader registry, ISystemEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(environment);
        var problems = new List<string>();
        Dictionary<string, string> machine = Values(registry, RegistryHive.LocalMachine, RegistryView.Registry64, MachineKey, problems);
        Dictionary<string, string> user = Values(registry, RegistryHive.CurrentUser, RegistryView.Registry64, UserKey, problems);

        string? Lookup(string name)
            => user.TryGetValue(name, out string? u) && !name.Equals("Path", StringComparison.OrdinalIgnoreCase) ? Expand(u, n => machine.GetValueOrDefault(n) ?? environment.GetVariable(n))
                : machine.TryGetValue(name, out string? m) ? Expand(m, environment.GetVariable)
                : environment.GetVariable(name);

        var entries = new List<PathEntry>();
        foreach ((Dictionary<string, string> values, bool fromUser) in (ReadOnlySpan<(Dictionary<string, string>, bool)>)[(machine, false), (user, true)])
        {
            if (!values.TryGetValue("Path", out string? path))
            {
                continue;
            }

            foreach (string raw in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string expanded = Expand(raw, Lookup).Trim('"');
                if (expanded.Length > 0)
                {
                    entries.Add(new PathEntry(raw, expanded, fromUser));
                }
            }
        }

        string? pathExt = machine.GetValueOrDefault("PATHEXT");
        IReadOnlyList<string> extensions = pathExt is null
            ? DefaultExtensions
            : pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(e => (e.StartsWith('.') ? e : "." + e).ToUpperInvariant()).ToList();
        return new SearchPath(entries, extensions, problems);
    }

    /// <summary>Expands <c>%NAME%</c>; unknown names stay as written (as Windows does).</summary>
    public static string Expand(string text, Func<string, string?> lookup)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(lookup);
        var output = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            int start = text.IndexOf('%', i);
            int end = start < 0 ? -1 : text.IndexOf('%', start + 1);
            if (start < 0 || end < 0)
            {
                output.Append(text, i, text.Length - i);
                break;
            }

            output.Append(text, i, start - i);
            string name = text[(start + 1)..end];
            if (name.Length > 0 && lookup(name) is { } value)
            {
                output.Append(value);
                i = end + 1;
            }
            else
            {
                output.Append('%').Append(name);
                i = end;
            }
        }

        return output.ToString();
    }

    /// <summary>
    /// Every match for <paramref name="command"/> in PATH order, trying each <c>PATHEXT</c>
    /// extension (or the command as given when it has one). Aliases are recognised, never run.
    /// </summary>
    public IReadOnlyList<PathHit> Find(string command, IAliasReader aliases)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(aliases);
        string[] names = Path.HasExtension(command) ? [command] : Extensions.Select(e => command + e.ToLowerInvariant()).ToArray();
        var hits = new List<PathHit>();
        for (int i = 0; i < Entries.Count; i++)
        {
            foreach (string name in names)
            {
                string candidate;
                try
                {
                    candidate = Path.Join(Entries[i].Expanded, name);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (FileProbe.Stat(candidate) is not { IsDirectory: false } entry)
                {
                    continue;
                }

                AliasInfo? alias = entry.IsReparsePoint ? aliases.Read(candidate) : null;
                hits.Add(new PathHit(candidate, i, Entries[i].FromUser, alias));
                break;
            }
        }

        return hits;
    }

    private static Dictionary<string, string> Values(IRegistryReader registry, RegistryHive hive, RegistryView view, string key, List<string> problems)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using IRegistryKeyReader? reader = registry.Open(hive, view, key);
            if (reader is null)
            {
                return values;
            }

            foreach (string name in (string[])["Path", "PATHEXT", "SystemRoot", "ProgramFiles", "USERPROFILE", "LOCALAPPDATA", "APPDATA",
                         "NVM_HOME", "NVM_SYMLINK", "PYENV", "PYENV_HOME", "PYENV_ROOT", "JAVA_HOME", "GOROOT", "GOPATH", "CARGO_HOME", "RUSTUP_HOME",
                         "VOLTA_HOME", "FNM_DIR", "SCOOP", "ChocolateyInstall", "ChocolateyToolsLocation", "DENO_INSTALL", "BUN_INSTALL", "PGBIN", "PGROOT",
                         "CONDA", "DOTNET_ROOT", "UV_PYTHON_INSTALL_DIR", "MISE_DATA_DIR", "ProgramData", "SystemDrive"])
            {
                if (reader.Value(name) is string text)
                {
                    values[name] = text;
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            problems.Add($"{hive}\\{key} could not be read ({ex.Message})");
        }

        return values;
    }

    /// <summary>A variable from the registry environment (user first, then system), as a new program would see it; null when unset.</summary>
    public static string? RegistryVariable(IRegistryReader registry, ISystemEnvironment environment, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var problems = new List<string>();
        Dictionary<string, string> user = Values(registry, RegistryHive.CurrentUser, RegistryView.Registry64, UserKey, problems);
        Dictionary<string, string> machine = Values(registry, RegistryHive.LocalMachine, RegistryView.Registry64, MachineKey, problems);
        string? raw = user.GetValueOrDefault(name) ?? machine.GetValueOrDefault(name);
        return raw is null ? null : Expand(raw, n => user.GetValueOrDefault(n) ?? machine.GetValueOrDefault(n) ?? environment.GetVariable(n));
    }
}
