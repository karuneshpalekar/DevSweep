using System.Text.Json;
using DevSweep.Core.Detectors;
using DevSweep.Core.Health.Common;

namespace DevSweep.Core.Health.Runtimes;

/// <summary>Flutter: SDK folders in the usual places (<c>~\flutter</c>, <c>C:\src\flutter</c>...), Scoop and FVM; version from the SDK's files.</summary>
public sealed class FlutterProbe : RuntimeProbe
{
    public override string Id => "flutter";

    public override string Name => "Flutter";

    public override string Command => "flutter";

    public override string? Product => null;

    public override IReadOnlyList<Installation> Find(ProbeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var roots = new List<(string Root, InstallSource Source, string? Detail)>();
        foreach (string? root in (string?[])[context.InProfile("flutter"), context.InProfile("dev", "flutter"), context.InProfile("development", "flutter"),
                     context.InProfile("sdk", "flutter"), context.InProfile("src", "flutter")])
        {
            if (root is not null)
            {
                roots.Add((root, InstallSource.SdkFolder, "Flutter SDK"));
            }
        }

        roots.AddRange(((string[])["src", "flutter", "tools", "dev"]).Select(d => (System.IO.Path.Join(context.SystemDrive, d == "flutter" ? "flutter" : System.IO.Path.Join(d, "flutter")), InstallSource.SdkFolder, (string?)"Flutter SDK")));
        foreach (string? fvm in (string?[])[context.InProfile("fvm", "versions"), context.InLocalAppData("fvm", "versions")])
        {
            roots.AddRange(ProbeKit.Folders(fvm).Select(f => (f.Path, InstallSource.Fvm, (string?)f.Name)));
        }

        foreach (string scoop in (string[])["scoop", "scoopGlobal"])
        {
            string? apps = context.Location(scoop) is { } r ? System.IO.Path.Join(r, "apps", "flutter") : null;
            roots.AddRange(ProbeKit.Folders(apps).Select(f => (f.Path, InstallSource.Scoop, (string?)"flutter")));
        }

        var list = new List<Installation>();
        foreach ((string root, InstallSource source, string? detail) in roots)
        {
            if (Version(root) is { } v)
            {
                list.Add(Make(v.Version, root, System.IO.Path.Join(root, "bin", "flutter.bat"), source, detail, v.From));
            }
        }

        return list;
    }

    private static (string Version, string From)? Version(string root)
    {
        if (HealthFiles.ReadText(System.IO.Path.Join(root, "bin", "cache", "flutter.version.json"), 64 * 1024) is { } json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("frameworkVersion", out JsonElement v) && v.GetString() is { } s && VersionText.FirstVersion(s) is not null)
                {
                    return (s, "flutter.version.json");
                }
            }
            catch (JsonException)
            {
                // Fall back to the version file.
            }
        }

        return HealthFiles.ReadText(System.IO.Path.Join(root, "version"), 4096) is { } text && VersionText.FirstVersion(HealthFiles.FirstLine(text)) is not null
            ? (HealthFiles.FirstLine(text), "version file")
            : null;
    }
}

/// <summary>Deno: the official installer (<c>DENO_INSTALL</c> or <c>~\.deno</c>), Scoop and winget portable packages.</summary>
public sealed class DenoProbe : RuntimeProbe
{
    public override string Id => "deno";

    public override string Name => "Deno";

    public override string Command => "deno";

    public override string? Product => "deno";

    public override IReadOnlyList<Installation> Find(ProbeContext context) => SingleBinary(context, this, "DENO_INSTALL", ".deno", "deno*", "DenoLand.Deno*", InstallSource.DenoInstaller);

    internal static IReadOnlyList<Installation> SingleBinary(ProbeContext context, RuntimeProbe probe, string variable, string profileFolder, string scoopPattern, string wingetPattern, InstallSource own)
    {
        ArgumentNullException.ThrowIfNull(context);
        string exe = probe.Command + ".exe";
        var homes = new List<(string Home, string Relative, InstallSource Source)>();
        if ((context.Variable(variable) ?? context.InProfile(profileFolder)) is { } home)
        {
            homes.Add((home, System.IO.Path.Join("bin", exe), own));
        }

        foreach (string root in (string[])["wingetPackages", "wingetMachinePackages"])
        {
            homes.AddRange(ProbeKit.Folders(context.Location(root), wingetPattern).Select(f => (f.Path, exe, InstallSource.Winget)));
        }

        foreach (string root in (string[])["scoop", "scoopGlobal"])
        {
            string? apps = context.Location(root) is { } r ? System.IO.Path.Join(r, "apps") : null;
            foreach (ProbeEntry app in ProbeKit.Folders(apps, scoopPattern))
            {
                homes.AddRange(ProbeKit.Folders(app.Path).Select(v => (v.Path, exe, InstallSource.Scoop)));
            }
        }

        var list = new List<Installation>();
        foreach ((string folder, string relative, InstallSource source) in homes)
        {
            string program = System.IO.Path.Join(folder, relative);
            if (context.ProgramVersion(program, "--version") is { } v)
            {
                Owner? owner = ProbeKit.OwnerOf(context, folder);
                list.Add(new Installation
                {
                    Version = v.Version,
                    Cycle = probe.Cycle(v.Version),
                    Path = folder,
                    Executable = program,
                    Source = owner?.Source ?? source,
                    SourceDetail = owner?.Detail,
                    VersionFrom = v.From,
                    Product = probe.Product,
                });
            }
        }

        return list;
    }
}

/// <summary>Bun: the official installer (<c>BUN_INSTALL</c> or <c>~\.bun</c>), Scoop and winget portable packages.</summary>
public sealed class BunProbe : RuntimeProbe
{
    public override string Id => "bun";

    public override string Name => "Bun";

    public override string Command => "bun";

    public override string? Product => "bun";

    public override string Cycle(string version) => VersionText.MajorMinor(version);

    public override IReadOnlyList<Installation> Find(ProbeContext context) => DenoProbe.SingleBinary(context, this, "BUN_INSTALL", ".bun", "bun*", "Oven-sh.Bun*", InstallSource.BunInstaller);
}
