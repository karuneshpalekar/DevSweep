using System.Globalization;
using DevSweep.Core.Detectors;
using DevSweep.Core.Health.Common;
using DevSweep.Core.Inventory.Registry;
using Microsoft.Win32;

namespace DevSweep.Core.Health.Runtimes;

/// <summary>
/// PostgreSQL: the EDB installer's registrations (<c>SOFTWARE\PostgreSQL\Installations</c>: version,
/// base and data folders), Program Files, <c>PGROOT</c>, Scoop. Running is judged from the
/// <c>postgres.exe</c> processes whose program path Windows will give; unknown otherwise.
/// </summary>
public sealed class PostgresProbe : RuntimeProbe
{
    public override string Id => "postgres";

    public override string Name => "PostgreSQL";

    public override string Command => "psql";

    public override string? Product => "postgresql";

    /// <summary>10 and later: the major number; 9.6 and older: major.minor.</summary>
    public override string Cycle(string version)
    {
        IReadOnlyList<long> p = VersionText.Parts(version);
        return p.Count > 0 && p[0] >= 10 ? VersionText.Major(version) : VersionText.MajorMinor(version);
    }

    public override IReadOnlyList<Installation> Find(ProbeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var homes = new List<(string Home, string? Data, string? Version, string? Detail)>();
        try
        {
            using IRegistryKeyReader? installations = context.Registry.Open(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\PostgreSQL\Installations");
            foreach (string id in installations?.SubKeyNames() ?? [])
            {
                using IRegistryKeyReader? key = installations!.OpenSubKey(id);
                if (key?.Value("Base Directory") is string baseDir)
                {
                    homes.Add((baseDir.TrimEnd('\\'), key.Value("Data Directory") as string, key.Value("Version") as string, id));
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Folder scans below still find the usual places.
        }

        foreach (string programFiles in context.ProgramFilesFolders)
        {
            homes.AddRange(ProbeKit.Folders(System.IO.Path.Join(programFiles, "PostgreSQL")).Select(f => (f.Path, (string?)null, (string?)null, (string?)null)));
        }

        if (context.Variable("PGROOT") is { } pgRoot)
        {
            homes.Add((pgRoot, context.Variable("PGDATA"), null, "PGROOT"));
        }

        IReadOnlyList<ProcessPath> running = context.Processes.Snapshot().Where(p => p.Name.Equals("postgres", StringComparison.OrdinalIgnoreCase)).ToList();
        var list = new List<Installation>();
        foreach ((string home, string? data, string? registryVersion, string? detail) in homes)
        {
            string exe = System.IO.Path.Join(home, "bin", "postgres.exe");
            (string Version, string From)? v = context.ProgramVersion(exe, "--version")
                ?? (registryVersion is not null && HealthFiles.Exists(exe) ? (registryVersion, "registry (EDB installer)") : null);
            if (v is null)
            {
                continue;
            }

            bool? isRunning = running.Any(p => p.Path is not null && ProbeKit.IsInside(p.Path, home)) ? true
                : running.Any(p => p.Path is null) ? null
                : false;
            Owner? owner = ProbeKit.OwnerOf(context, home);
            Installation install = Make(v.Value.Version, home, exe, owner?.Source ?? InstallSource.PostgresInstaller, owner?.Detail ?? detail, v.Value.From) with
            {
                DataPath = data ?? (HealthFiles.IsFolder(System.IO.Path.Join(home, "data")) ? System.IO.Path.Join(home, "data") : null),
                IsRunning = isRunning,
            };
            list.Add(ProbeKit.Refine(context, install, "postgresql*"));
        }

        list.AddRange(ScoopApps(context, "postgresql*", @"bin\postgres.exe"));
        return list;
    }
}

/// <summary>Go: <c>Program Files\Go</c> (go.dev MSI, winget, Chocolatey), <c>GOROOT</c>, <c>~\sdk\go1.*</c>, Scoop, a CI tool cache, mise/asdf. Version from the <c>VERSION</c> file.</summary>
public sealed class GoProbe : RuntimeProbe
{
    public override string Id => "go";

    public override string Name => "Go";

    public override string Command => "go";

    public override string? Product => "go";

    public override IReadOnlyList<Installation> Find(ProbeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var list = new List<Installation>();
        foreach (string programFiles in context.ProgramFilesFolders)
        {
            if (Root(context, System.IO.Path.Join(programFiles, "Go"), InstallSource.GoOrg, null) is { } official)
            {
                list.Add(ProbeKit.Refine(context, official, "golang"));
            }
        }

        if (context.Variable("GOROOT") is { } goRoot && Root(context, goRoot, InstallSource.Unknown, "GOROOT") is { } fromVariable)
        {
            list.Add(fromVariable);
        }

        foreach (ProbeEntry sdk in ProbeKit.Folders(context.InProfile("sdk"), "go1.*"))
        {
            if (Root(context, sdk.Path, InstallSource.SdkFolder, "golang.org/dl") is { } dl)
            {
                list.Add(dl);
            }
        }

        list.AddRange(ScoopApps(context, "go", @"bin\go.exe"));
        list.AddRange(ToolCache(context, "go", @"bin\go.exe"));
        list.AddRange(MiseAndAsdf(context, "go", "golang", @"bin\go.exe"));
        return list;
    }

    private Installation? Root(ProbeContext context, string root, InstallSource source, string? detail)
    {
        string? text = HealthFiles.ReadText(System.IO.Path.Join(root, "VERSION"), 4096);
        if (text is null || VersionText.FirstVersion(HealthFiles.FirstLine(text)) is not { } v)
        {
            return null;
        }

        Owner? owner = ProbeKit.OwnerOf(context, root);
        string exe = System.IO.Path.Join(root, "bin", "go.exe");
        return Make(v, root, HealthFiles.Exists(exe) ? exe : null, owner?.Source ?? source, owner?.Detail ?? detail, "VERSION file");
    }
}

/// <summary>Ruby: RubyInstaller (<c>C:\Ruby*</c> and its registry keys), Scoop, a CI tool cache, mise/asdf.</summary>
public sealed class RubyProbe : RuntimeProbe
{
    public override string Id => "ruby";

    public override string Name => "Ruby";

    public override string Command => "ruby";

    public override string? Product => "ruby";

    public override IReadOnlyList<Installation> Find(ProbeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var homes = ProbeKit.Folders(context.SystemDrive, "Ruby*").Select(f => (f.Path, (string?)null)).ToList();
        foreach (RegistryHive hive in (RegistryHive[])[RegistryHive.CurrentUser, RegistryHive.LocalMachine])
        {
            try
            {
                using IRegistryKeyReader? mri = context.Registry.Open(hive, RegistryView.Registry64, @"Software\RubyInstaller\MRI");
                foreach (string version in mri?.SubKeyNames() ?? [])
                {
                    using IRegistryKeyReader? key = mri!.OpenSubKey(version);
                    if (key?.Value("InstallLocation") is string location)
                    {
                        homes.Add((location.TrimEnd('\\'), version));
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                // Folder scans still find C:\Ruby*.
            }
        }

        var list = new List<Installation>();
        foreach ((string home, string? registryVersion) in homes)
        {
            string exe = System.IO.Path.Join(home, "bin", "ruby.exe");
            (string Version, string From)? v = context.ProgramVersion(exe, "--version")
                ?? (registryVersion is not null && HealthFiles.Exists(exe) ? (registryVersion, "registry (RubyInstaller)") : null);
            if (v is not null)
            {
                Owner? owner = ProbeKit.OwnerOf(context, home);
                list.Add(ProbeKit.Refine(context, Make(v.Value.Version, home, exe, owner?.Source ?? InstallSource.RubyInstaller, owner?.Detail, v.Value.From), "ruby*"));
            }
        }

        list.AddRange(ScoopApps(context, "ruby*", @"bin\ruby.exe"));
        list.AddRange(ToolCache(context, "Ruby", @"bin\ruby.exe"));
        list.AddRange(MiseAndAsdf(context, "ruby", "ruby", @"bin\ruby.exe"));
        return list;
    }
}

/// <summary>PHP: Scoop, Chocolatey's tools folder (<c>C:\tools\php*</c>), <c>C:\php</c>, XAMPP, winget portable packages, Program Files, mise/asdf.</summary>
public sealed class PhpProbe : RuntimeProbe
{
    public override string Id => "php";

    public override string Name => "PHP";

    public override string Command => "php";

    public override string? Product => "php";

    public override IReadOnlyList<Installation> Find(ProbeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var homes = new List<string>();
        string tools = context.Variable("ChocolateyToolsLocation") ?? System.IO.Path.Join(context.SystemDrive, "tools");
        homes.AddRange(ProbeKit.Folders(tools, "php*").Select(f => f.Path));
        homes.AddRange(ProbeKit.Folders(context.SystemDrive, "php*").Select(f => f.Path));
        homes.Add(System.IO.Path.Join(context.SystemDrive, "xampp", "php"));
        foreach (string root in (string[])["wingetPackages", "wingetMachinePackages"])
        {
            homes.AddRange(ProbeKit.Folders(context.Location(root), "PHP.PHP*").Select(f => f.Path));
        }

        foreach (string programFiles in context.ProgramFilesFolders)
        {
            homes.AddRange(ProbeKit.Folders(System.IO.Path.Join(programFiles, "PHP")).Select(f => f.Path));
            homes.Add(System.IO.Path.Join(programFiles, "PHP"));
        }

        var list = new List<Installation>();
        foreach (string home in homes)
        {
            if (Program(context, home, "php.exe", InstallSource.Unknown, null, "--version") is { } php)
            {
                bool chocoTools = ProbeKit.IsInside(home, tools);
                list.Add(chocoTools ? ProbeKit.Refine(context, php with { Source = InstallSource.Unknown }, "php*") : php);
            }
        }

        list.AddRange(ScoopApps(context, "php*", "php.exe", readProgramVersion: true));
        list.AddRange(MiseAndAsdf(context, "php", "php", "php.exe"));
        return list;
    }
}

/// <summary>
/// Rust: rustup toolchains (<c>RUSTUP_HOME</c> or <c>~\.rustup</c>), versioned from each toolchain's
/// channel manifest. The default is rustup's (<c>settings.toml</c>), because <c>rustc</c> on PATH is rustup's proxy.
/// </summary>
public sealed class RustProbe : RuntimeProbe
{
    public override string Id => "rust";

    public override string Name => "Rust";

    public override string Command => "rustc";

    public override string? Product => "rust";

    public override IReadOnlyList<Installation> Find(ProbeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var list = new List<Installation>();
        foreach (ProbeEntry toolchain in ProbeKit.Folders(Home(context) is { } home ? System.IO.Path.Join(home, "toolchains") : null))
        {
            string manifest = System.IO.Path.Join(toolchain.Path, "lib", "rustlib", "multirust-channel-manifest.toml");
            string exe = System.IO.Path.Join(toolchain.Path, "bin", "rustc.exe");
            (string Version, string From)? v = ManifestVersion(HealthFiles.ReadText(manifest, 4 * 1024 * 1024)) is { } fromManifest
                ? (fromManifest, "channel manifest")
                : context.ProgramVersion(exe, "--version");
            if (v is not null)
            {
                list.Add(Make(v.Value.Version, toolchain.Path, exe, InstallSource.Rustup, toolchain.Name, v.Value.From));
            }
        }

        list.AddRange(ScoopApps(context, "rust", @"bin\rustc.exe", readProgramVersion: true));
        return list;
    }

    public override List<Installation> MarkDefault(ProbeContext context, List<Installation> installs, CommandResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(installs);
        ArgumentNullException.ThrowIfNull(resolution);
        bool proxy = resolution.Runs is { } runs && (System.IO.Path.GetDirectoryName(runs)?.EndsWith(System.IO.Path.Join(".cargo", "bin"), StringComparison.OrdinalIgnoreCase) == true
            || ProbeKit.IsInside(runs, context.Variable("CARGO_HOME")));
        if (!proxy)
        {
            return base.MarkDefault(context, installs, resolution);
        }

        string? settings = Home(context) is { } home ? HealthFiles.ReadText(System.IO.Path.Join(home, "settings.toml"), 64 * 1024) : null;
        string? name = settings is null ? null : Locations.ConfigText.TomlTopLevel(settings, "default_toolchain");
        int index = name is null ? -1 : installs.FindIndex(i => i.Source == InstallSource.Rustup && string.Equals(i.SourceDetail, name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            installs[index] = installs[index] with { IsDefault = true };
        }

        return installs;
    }

    /// <summary>"1.75.0" from <c>[pkg.rustc] version = "1.75.0 (82e1608df 2023-12-21)"</c>.</summary>
    public static string? ManifestVersion(string? manifest)
    {
        if (manifest is null)
        {
            return null;
        }

        int section = manifest.IndexOf("[pkg.rustc]", StringComparison.Ordinal);
        if (section < 0)
        {
            return null;
        }

        foreach (string line in manifest[section..].Split('\n').Skip(1))
        {
            string t = line.Trim();
            if (t.StartsWith('['))
            {
                break;
            }

            if (t.StartsWith("version", StringComparison.Ordinal))
            {
                return VersionText.FirstVersion(t);
            }
        }

        return null;
    }

    private static string? Home(ProbeContext context) => context.Variable("RUSTUP_HOME") ?? context.InProfile(".rustup");
}

/// <summary>
/// .NET SDKs: every <c>sdk\&lt;version&gt;</c> under <c>DOTNET_ROOT</c>, Program Files (x64 and x86),
/// <c>%LOCALAPPDATA%\Microsoft\dotnet</c> (dotnet-install), <c>~\.dotnet</c> and Scoop. The default is
/// the newest SDK of the <c>dotnet</c> on PATH (a project's <c>global.json</c> can pick another).
/// </summary>
public sealed class DotnetProbe : RuntimeProbe
{
    public override string Id => "dotnet";

    public override string Name => ".NET SDK";

    public override string Command => "dotnet";

    public override string? Product => "dotnet";

    public override bool PreferLts => true;

    public override IReadOnlyList<Installation> Find(ProbeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var roots = new List<(string Root, InstallSource Source)>();
        if (context.Variable("DOTNET_ROOT") is { } dotnetRoot)
        {
            roots.Add((dotnetRoot, InstallSource.SdkFolder));
        }

        roots.AddRange(context.ProgramFilesFolders.Select(pf => (System.IO.Path.Join(pf, "dotnet"), InstallSource.DotnetInstaller)));
        foreach (string? root in (string?[])[context.InLocalAppData("Microsoft", "dotnet"), context.InProfile(".dotnet")])
        {
            if (root is not null)
            {
                roots.Add((root, InstallSource.SdkFolder));
            }
        }

        foreach (string scoopRoot in (string[])["scoop", "scoopGlobal"])
        {
            string? apps = context.Location(scoopRoot) is { } r ? System.IO.Path.Join(r, "apps") : null;
            foreach (ProbeEntry app in ProbeKit.Folders(apps, "dotnet*sdk*"))
            {
                roots.AddRange(ProbeKit.Folders(app.Path).Select(v => (v.Path, InstallSource.Scoop)));
            }
        }

        var list = new List<Installation>();
        foreach ((string root, InstallSource source) in roots)
        {
            Owner? owner = ProbeKit.OwnerOf(context, root);
            foreach (ProbeEntry sdk in ProbeKit.Folders(System.IO.Path.Join(root, "sdk")))
            {
                if (VersionText.Parts(sdk.Name).Count >= 3 && char.IsAsciiDigit(sdk.Name[0]) && HealthFiles.Exists(System.IO.Path.Join(sdk.Path, "dotnet.dll")))
                {
                    Installation install = Make(sdk.Name, sdk.Path, System.IO.Path.Join(root, "dotnet.exe"), owner?.Source ?? source, owner?.Detail ?? root, "SDK folder name");
                    list.Add(source == InstallSource.DotnetInstaller ? Refine(context, install) : install);
                }
            }
        }

        return list;
    }

    public override List<Installation> MarkDefault(ProbeContext context, List<Installation> installs, CommandResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(installs);
        ArgumentNullException.ThrowIfNull(resolution);
        if (resolution.Runs is not { } runs || System.IO.Path.GetDirectoryName(runs) is not { } root)
        {
            return installs;
        }

        int newest = -1;
        for (int i = 0; i < installs.Count; i++)
        {
            if (ProbeKit.IsInside(installs[i].Path, root) && (newest < 0 || VersionText.Less(installs[newest].Version, installs[i].Version)))
            {
                newest = i;
            }
        }

        if (newest >= 0)
        {
            installs[newest] = installs[newest] with { IsDefault = true };
        }

        return installs;
    }

    /// <summary>The SDK's own Uninstall entry ("Microsoft .NET SDK 8.0.100 (x64)") gives its product code and winget id.</summary>
    private static Installation Refine(ProbeContext context, Installation install)
    {
        Inventory.AppRecord? entry = context.Inventory?.Records.FirstOrDefault(r => r.Source.ToString().StartsWith("Uninstall", StringComparison.Ordinal)
            && r.Name.StartsWith("Microsoft .NET SDK " + install.Version, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return install;
        }

        return install with
        {
            ProductCode = entry.ProductCode,
            WingetId = entry.PackageId,
            SourceDetail = string.Create(CultureInfo.InvariantCulture, $"{entry.Name}"),
        };
    }
}
