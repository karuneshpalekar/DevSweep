using System.Text.RegularExpressions;
using DevSweep.Core.Detectors;
using DevSweep.Core.Health.Common;
using DevSweep.Core.Inventory;
using DevSweep.Core.Inventory.Registry;
using Microsoft.Win32;

namespace DevSweep.Core.Health.Runtimes;

/// <summary>
/// Python: pyenv-win, uv, conda (base and every environment), the Python install
/// manager, Scoop, a CI tool cache, mise/asdf, python.org per-user and per-machine
/// installs, everything registered under PEP 514 (<c>Software\Python</c>), what the
/// <c>py</c> launcher lists (<c>py -0p</c>), Microsoft Store packages and Visual Studio's copy.
/// The Store's <c>python.exe</c> installer stub is never an install.
/// </summary>
public sealed partial class PythonProbe : RuntimeProbe
{
    public override string Id => "python";

    public override string Name => "Python";

    public override string Command => "python";

    public override string? Product => "python";

    public override IReadOnlyList<Installation> Find(ProbeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var list = new List<Installation>();

        string? pyenv = PyenvRoot(context);
        list.AddRange(VersionFolders(context, pyenv is null ? null : System.IO.Path.Join(pyenv, "versions"), "python.exe", InstallSource.Pyenv,
            version: name => UvOrPlainVersion(name)));

        string? uv = UvRoot(context);
        foreach (ProbeEntry folder in ProbeKit.Folders(uv, "cpython-*"))
        {
            if (UvOrPlainVersion(folder.Name) is { } v && HealthFiles.Exists(System.IO.Path.Join(folder.Path, "python.exe")))
            {
                list.Add(Make(v, folder.Path, System.IO.Path.Join(folder.Path, "python.exe"), InstallSource.Uv, folder.Name, "folder name"));
            }
        }

        list.AddRange(Conda(context));

        foreach (ProbeEntry folder in ProbeKit.Folders(context.InLocalAppData("Python"), "pythoncore-*"))
        {
            if (Home(context, folder.Path, InstallSource.PythonInstallManager, folder.Name, null) is { } managed)
            {
                list.Add(managed);
            }
        }

        list.AddRange(ScoopApps(context, "python*", "python.exe", readProgramVersion: true));
        list.AddRange(ToolCache(context, "Python", "python.exe"));
        list.AddRange(MiseAndAsdf(context, "python", "python", "python.exe"));

        var homes = new List<(string Path, InstallSource Source, string? Detail)>();
        foreach (ProbeEntry folder in ProbeKit.Folders(context.InLocalAppData("Programs", "Python"), "Python*"))
        {
            homes.Add((folder.Path, InstallSource.PythonOrg, "this user"));
        }

        foreach (string programFiles in context.ProgramFilesFolders)
        {
            homes.AddRange(ProbeKit.Folders(programFiles, "Python3*").Select(f => (f.Path, InstallSource.PythonOrg, (string?)"all users")));
            homes.AddRange(ProbeKit.Folders(System.IO.Path.Join(programFiles, "Microsoft Visual Studio", "Shared"), "Python3*")
                .Select(f => (f.Path, InstallSource.IdeBundled, (string?)"Visual Studio")));
        }

        homes.AddRange(ProbeKit.Folders(context.SystemDrive, "Python3*").Select(f => (f.Path, InstallSource.PythonOrg, (string?)"all users")));
        homes.AddRange(Pep514(context));
        foreach ((string path, InstallSource source, string? detail) in homes)
        {
            if (Home(context, path, source, detail, null) is { } found)
            {
                list.Add(found.Source == InstallSource.PythonOrg ? ProbeKit.Refine(context, found, "python*") : found);
            }
        }

        foreach ((string exe, _) in Launcher(context))
        {
            string home = System.IO.Path.GetDirectoryName(exe)!;
            if (!list.Any(i => ProbeKit.IsInside(exe, i.Path)) && Home(context, home, InstallSource.Unknown, "listed by the py launcher", null) is { } listed)
            {
                list.Add(listed);
            }
        }

        list.AddRange(Store(context, list));
        return list;
    }

    /// <summary>One Python home: version from python.exe's version resource, patchlevel.h, conda-meta or the registry; owner from its location.</summary>
    public Installation? Home(ProbeContext context, string home, InstallSource source, string? detail, string? registryVersion)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(home);
        string exe = System.IO.Path.Join(home, "python.exe");
        if (!HealthFiles.Exists(exe))
        {
            return null;
        }

        (string Version, string From)? version = context.Versions.Read(exe)?.Best is { } pe && VersionText.Parts(pe).Count >= 3 && VersionText.Parts(pe)[2] < 1000
            ? (pe, "version resource of python.exe")
            : PatchLevel(home) is { } patch ? (patch, @"include\patchlevel.h")
            : CondaMetaVersion(home) is { } meta ? (meta, "conda-meta")
            : registryVersion is not null && VersionText.Parts(registryVersion).Count >= 2 ? (registryVersion, "registry (PEP 514)")
            : context.ProgramVersion(exe, "--version") is { } ran ? ran
            : null;
        if (version is null)
        {
            return null;
        }

        (InstallSource s, string? d) = Classify(context, home, source, detail);
        return Make(version.Value.Version, home, exe, s, d, version.Value.From);
    }

    private static (InstallSource, string?) Classify(ProbeContext context, string home, InstallSource source, string? detail)
    {
        if (ProbeKit.OwnerOf(context, home) is { } owner)
        {
            return (owner.Source, owner.Detail);
        }

        if (ProbeKit.IsInside(home, PyenvRoot(context)))
        {
            return (InstallSource.Pyenv, null);
        }

        if (ProbeKit.IsInside(home, UvRoot(context)))
        {
            return (InstallSource.Uv, null);
        }

        if (ProbeKit.IsInside(home, context.InLocalAppData("Python")))
        {
            return (InstallSource.PythonInstallManager, System.IO.Path.GetFileName(home));
        }

        if (HealthFiles.IsFolder(System.IO.Path.Join(home, "conda-meta")))
        {
            return (InstallSource.Conda, detail ?? "base");
        }

        if (ProbeKit.IsInside(home, context.InLocalAppData("Programs", "Python")))
        {
            return (InstallSource.PythonOrg, "this user");
        }

        return (source, detail);
    }

    private static string? PyenvRoot(ProbeContext context)
    {
        foreach (string? candidate in (string?[])[context.Variable("PYENV_ROOT"), context.Variable("PYENV"), context.Variable("PYENV_HOME"), context.InProfile(".pyenv", "pyenv-win")])
        {
            if (candidate is null)
            {
                continue;
            }

            string trimmed = System.IO.Path.TrimEndingDirectorySeparator(candidate);
            string root = trimmed.EndsWith("pyenv-win", StringComparison.OrdinalIgnoreCase) ? trimmed : System.IO.Path.Join(trimmed, "pyenv-win");
            if (HealthFiles.IsFolder(System.IO.Path.Join(root, "versions")))
            {
                return root;
            }

            if (HealthFiles.IsFolder(System.IO.Path.Join(trimmed, "versions")))
            {
                return trimmed;
            }
        }

        return null;
    }

    private static string? UvRoot(ProbeContext context) => context.Variable("UV_PYTHON_INSTALL_DIR") ?? context.InRoamingAppData("uv", "python");

    private IEnumerable<Installation> Conda(ProbeContext context)
    {
        var roots = new List<string>();
        foreach (string? root in (string?[])[context.Variable("CONDA"), context.InProfile("miniconda3"), context.InProfile("anaconda3"), context.InProfile("miniforge3"),
                     context.InProfile("mambaforge"), context.InLocalAppData("miniconda3"), context.InLocalAppData("anaconda3")])
        {
            if (root is not null)
            {
                roots.Add(root);
            }
        }

        if (context.ProgramData is { } programData)
        {
            roots.AddRange(((string[])["miniconda3", "Anaconda3", "miniforge3"]).Select(n => System.IO.Path.Join(programData, n)));
        }

        roots.AddRange(ProbeKit.Folders(context.SystemDrive, "Miniconda*").Concat(ProbeKit.Folders(context.SystemDrive, "Anaconda*")).Select(f => f.Path));
        var envs = new List<string>();
        if (context.InProfile(".conda", "environments.txt") is { } listFile && HealthFiles.ReadText(listFile) is { } listed)
        {
            envs.AddRange(listed.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && System.IO.Path.IsPathFullyQualified(l)));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots.Where(r => seen.Add(System.IO.Path.TrimEndingDirectorySeparator(r)) && HealthFiles.IsFolder(System.IO.Path.Join(r, "conda-meta"))))
        {
            if (Home(context, root, InstallSource.Conda, "base", null) is { } baseInstall)
            {
                yield return baseInstall with { Source = InstallSource.Conda, SourceDetail = "base" };
            }

            envs.AddRange(ProbeKit.Folders(System.IO.Path.Join(root, "envs")).Select(e => e.Path));
        }

        foreach (string env in envs.Distinct(StringComparer.OrdinalIgnoreCase).Where(e => !seen.Contains(System.IO.Path.TrimEndingDirectorySeparator(e))))
        {
            if (HealthFiles.IsFolder(System.IO.Path.Join(env, "conda-meta")) && Home(context, env, InstallSource.Conda, null, null) is { } envInstall)
            {
                yield return envInstall with { Source = InstallSource.Conda, SourceDetail = "env " + System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(env)) };
            }
        }
    }

    /// <summary>PEP 514 registrations (HKCU, HKLM 64- and 32-bit): <c>Software\Python\&lt;Company&gt;\&lt;Tag&gt;\InstallPath</c>.</summary>
    private static List<(string Path, InstallSource Source, string? Detail)> Pep514(ProbeContext context)
    {
        var found = new List<(string, InstallSource, string?)>();
        foreach ((RegistryHive hive, RegistryView view) in (ReadOnlySpan<(RegistryHive, RegistryView)>)[
                     (RegistryHive.CurrentUser, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32)])
        {
            try
            {
                using IRegistryKeyReader? python = context.Registry.Open(hive, view, @"Software\Python");
                if (python is null)
                {
                    continue;
                }

                foreach (string company in python.SubKeyNames())
                {
                    if (company.Equals("PyLauncher", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    using IRegistryKeyReader? companyKey = python.OpenSubKey(company);
                    foreach (string tag in companyKey?.SubKeyNames() ?? [])
                    {
                        using IRegistryKeyReader? installPath = companyKey!.OpenSubKey(tag)?.OpenSubKey("InstallPath");
                        string? path = (installPath?.Value(string.Empty) as string)?.Trim().Trim('"');
                        if (string.IsNullOrEmpty(path) && installPath?.Value("ExecutablePath") is string executable)
                        {
                            path = System.IO.Path.GetDirectoryName(executable.Trim('"'));
                        }

                        if (!string.IsNullOrEmpty(path))
                        {
                            InstallSource source = company.Equals("PythonCore", StringComparison.OrdinalIgnoreCase) ? InstallSource.PythonOrg
                                : company.Equals("ContinuumAnalytics", StringComparison.OrdinalIgnoreCase) ? InstallSource.Conda
                                : InstallSource.Unknown;
                            string detail = source == InstallSource.PythonOrg ? (hive == RegistryHive.CurrentUser ? "this user" : "all users") : company;
                            found.Add((System.IO.Path.TrimEndingDirectorySeparator(path), source, detail));
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                // A key we may not read: the folder scans still find the usual places.
            }
        }

        return found;
    }

    /// <summary>What <c>py -0p</c> lists (program paths, and the launcher's own default); empty when there is no trusted launcher.</summary>
    public static IReadOnlyList<(string Exe, bool IsDefault)> Launcher(ProbeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string? windows = context.Variable("SystemRoot") ?? context.Variable("windir");
        foreach (string? py in (string?[])[windows is null ? null : System.IO.Path.Join(windows, "py.exe"), context.InLocalAppData("Programs", "Python", "Launcher", "py.exe")])
        {
            if (py is not null && HealthFiles.IsFile(py) && context.Commands.Run(py, ["-0p"]) is { } output)
            {
                return ParseLauncherList(output);
            }
        }

        return [];
    }

    /// <summary>Parses <c>py -0p</c> (" -V:3.12 *  C:\...\python.exe", or the older " -3.12-64  C:\...\python.exe *").</summary>
    public static IReadOnlyList<(string Exe, bool IsDefault)> ParseLauncherList(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var list = new List<(string, bool)>();
        foreach (string line in output.Split('\n'))
        {
            Match m = LauncherPath().Match(line);
            if (m.Success)
            {
                list.Add((m.Value.Trim(), line.Contains(" * ", StringComparison.Ordinal) || line.TrimEnd().EndsWith(" *", StringComparison.Ordinal)));
            }
        }

        return list;
    }

    /// <summary>
    /// Store Python packages: from the Store aliases (an alias to a real Python package) and the
    /// inventory's MSIX packages. The App Installer stub alias is not a package and is never reported.
    /// </summary>
    private IEnumerable<Installation> Store(ProbeContext context, List<Installation> already)
    {
        var families = new Dictionary<string, (string? Target, string? Version, string? Location)>(StringComparer.OrdinalIgnoreCase);
        foreach (ProbeEntry entry in HealthFiles.List(context.InLocalAppData("Microsoft", "WindowsApps")))
        {
            if (!entry.IsDirectory && entry.Name.StartsWith("python", StringComparison.OrdinalIgnoreCase) && entry.IsReparsePoint
                && context.Aliases.Read(entry.Path) is { Kind: AliasKind.StorePython } alias)
            {
                families.TryAdd(alias.PackageFamily, (alias.Target, null, null));
            }
        }

        foreach (AppRecord record in context.Inventory?.Records.Where(r => r.Source == InventorySource.StorePackages
                     && r.PackageFamilyName?.StartsWith("PythonSoftwareFoundation.Python.", StringComparison.OrdinalIgnoreCase) == true) ?? [])
        {
            (string? target, _, _) = families.GetValueOrDefault(record.PackageFamilyName!);
            families[record.PackageFamilyName!] = (target, record.Version, record.InstallLocation);
        }

        foreach ((string family, (string? target, string? packageVersion, string? location)) in families)
        {
            string cycle = new AliasInfo(family, string.Empty, string.Empty, AliasKind.StorePython).PythonCycle ?? "3";
            string? fromProgram = target is null ? null : context.Versions.Read(target)?.Best;
            bool exact = fromProgram is not null && VersionText.Parts(fromProgram).Count >= 3 && VersionText.Parts(fromProgram)[2] < 1000
                && VersionText.MajorMinor(fromProgram) == cycle;
            string home = location ?? (target is null ? family : System.IO.Path.GetDirectoryName(target)!);
            if (already.Any(i => i.Source == InstallSource.StorePython && string.Equals(i.SourceDetail, family, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            yield return Make(exact ? fromProgram! : cycle, home, target, InstallSource.StorePython, family,
                exact ? "version resource of the package's python.exe" : $"package name (patch release not known; package version {packageVersion ?? "unknown"})");
        }
    }

    private static string? PatchLevel(string home)
    {
        string? text = HealthFiles.ReadText(System.IO.Path.Join(home, "include", "patchlevel.h"), 64 * 1024);
        Match m = text is null ? Match.Empty : PyVersionDefine().Match(text);
        return m.Success ? m.Groups["v"].Value : null;
    }

    private static string? CondaMetaVersion(string home)
    {
        foreach (ProbeEntry entry in HealthFiles.List(System.IO.Path.Join(home, "conda-meta")))
        {
            Match m = CondaPython().Match(entry.Name);
            if (m.Success)
            {
                return m.Groups["v"].Value;
            }
        }

        return null;
    }

    private static string? UvOrPlainVersion(string name)
    {
        Match m = UvName().Match(name);
        return m.Success ? m.Groups["v"].Value : VersionText.FromFolderName(name);
    }

    [GeneratedRegex(@"#define\s+PY_VERSION\s+""(?<v>\d+\.\d+\.\d+)")]
    private static partial Regex PyVersionDefine();

    [GeneratedRegex(@"^python-(?<v>\d+\.\d+\.\d+)-.*\.json$", RegexOptions.IgnoreCase)]
    private static partial Regex CondaPython();

    [GeneratedRegex(@"^cpython-(?<v>\d+\.\d+\.\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex UvName();

    [GeneratedRegex(@"[A-Za-z]:\\[^*\r\n]*?\.exe", RegexOptions.IgnoreCase)]
    private static partial Regex LauncherPath();
}
