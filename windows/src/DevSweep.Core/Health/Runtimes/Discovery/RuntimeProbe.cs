using DevSweep.Core.Detectors;
using DevSweep.Core.Health.Common;

namespace DevSweep.Core.Health.Runtimes;

/// <summary>Finds every installation of one tool, whoever installed it.</summary>
public abstract class RuntimeProbe
{
    /// <summary>"node", "python"...</summary>
    public abstract string Id { get; }

    public abstract string Name { get; }

    /// <summary>The command a terminal runs ("node", "python", "psql").</summary>
    public abstract string Command { get; }

    /// <summary>endoflife.date product; null when no schedule is published.</summary>
    public abstract string? Product { get; }

    /// <summary>Prefer LTS release lines when recommending an upgrade.</summary>
    public virtual bool PreferLts => false;

    /// <summary>The release line of a version ("20" for Node, "3.12" for Python).</summary>
    public virtual string Cycle(string version) => VersionText.MajorMinor(version);

    /// <summary>Every installation found (not yet de-duplicated or marked).</summary>
    public abstract IReadOnlyList<Installation> Find(ProbeContext context);

    /// <summary>Marks the default install given what PATH runs; tools with their own rules (rustup, dotnet) override.</summary>
    public virtual List<Installation> MarkDefault(ProbeContext context, List<Installation> installs, CommandResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        return ProbeKit.MarkDefault(installs, resolution.Runs);
    }

    protected Installation Make(string version, string path, string? exe, InstallSource source, string? detail, string from)
        => new()
        {
            Version = version,
            Cycle = Cycle(version),
            Path = path,
            Executable = exe,
            Source = source,
            SourceDetail = detail,
            VersionFrom = from,
            Product = Product,
        };

    /// <summary>
    /// Version-named folders under <paramref name="root"/> ("v20.11.0", "3.12.1") that hold
    /// <paramref name="exe"/> (relative; null to skip the check). Links (like Scoop's <c>current</c>) are skipped.
    /// </summary>
    protected IEnumerable<Installation> VersionFolders(ProbeContext context, string? root, string? exe, InstallSource source, string? detail = null, Func<string, string?>? version = null)
    {
        foreach (ProbeEntry folder in ProbeKit.Folders(root))
        {
            string? v = (version ?? VersionText.FromFolderName)(folder.Name);
            string? program = exe is null ? null : System.IO.Path.Join(folder.Path, exe);
            if (v is null || (program is not null && !HealthFiles.Exists(program)))
            {
                continue;
            }

            Owner? owner = ProbeKit.OwnerOf(context, folder.Path);
            yield return Make(v, folder.Path, program, owner?.Source ?? source, detail ?? owner?.Detail, "folder name");
        }
    }

    /// <summary>Scoop apps whose name matches <paramref name="appPattern"/>: each version folder (never <c>current</c>, a link).</summary>
    protected IEnumerable<Installation> ScoopApps(ProbeContext context, string appPattern, string? exe, bool readProgramVersion = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (string root in (string[])["scoop", "scoopGlobal"])
        {
            string? apps = context.Location(root) is { } r ? System.IO.Path.Join(r, "apps") : null;
            foreach (ProbeEntry app in ProbeKit.Folders(apps, appPattern))
            {
                foreach (ProbeEntry folder in ProbeKit.Folders(app.Path))
                {
                    string? program = exe is null ? null : System.IO.Path.Join(folder.Path, exe);
                    if (program is not null && !HealthFiles.Exists(program))
                    {
                        continue;
                    }

                    (string Version, string From)? read = readProgramVersion ? context.ProgramVersion(program) : null;
                    string? v = read?.Version ?? VersionText.FirstVersion(folder.Name);
                    if (v is not null)
                    {
                        yield return Make(v, folder.Path, program, InstallSource.Scoop, app.Name, read?.From ?? "Scoop version folder");
                    }
                }
            }
        }
    }

    /// <summary>A CI runner's tool cache: <c>{cache}\&lt;tool&gt;\&lt;version&gt;\x64</c>.</summary>
    protected IEnumerable<Installation> ToolCache(ProbeContext context, string toolPattern, string? exe)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (ProbeEntry tool in ProbeKit.Folders(context.ToolCache, toolPattern))
        {
            foreach (ProbeEntry version in ProbeKit.Folders(tool.Path))
            {
                string home = System.IO.Path.Join(version.Path, "x64");
                string? program = exe is null ? null : System.IO.Path.Join(home, exe);
                if (!HealthFiles.IsFolder(home) || (program is not null && !HealthFiles.Exists(program)) || VersionText.FirstVersion(version.Name) is not { } v)
                {
                    continue;
                }

                yield return Make(v, home, program, InstallSource.ToolCache, tool.Name, "tool cache folder name");
            }
        }
    }

    /// <summary>mise and asdf installs of <paramref name="miseName"/> / <paramref name="asdfName"/>.</summary>
    protected IEnumerable<Installation> MiseAndAsdf(ProbeContext context, string miseName, string asdfName, string? exe)
    {
        ArgumentNullException.ThrowIfNull(context);
        string? mise = context.Variable("MISE_DATA_DIR") ?? context.InLocalAppData("mise");
        foreach (Installation i in VersionFolders(context, mise is null ? null : System.IO.Path.Join(mise, "installs", miseName), exe, InstallSource.Mise))
        {
            yield return i;
        }

        foreach (Installation i in VersionFolders(context, context.InProfile(".asdf", "installs", asdfName), exe, InstallSource.Asdf))
        {
            yield return i;
        }
    }

    /// <summary>A single program at a fixed place, versioned from its version resource (or, trusted only, by running it).</summary>
    protected Installation? Program(ProbeContext context, string? home, string exeRelative, InstallSource source, string? detail, params string[] versionArguments)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (home is null)
        {
            return null;
        }

        string exe = System.IO.Path.Join(home, exeRelative);
        if (context.ProgramVersion(exe, versionArguments) is not { } v)
        {
            return null;
        }

        Owner? owner = ProbeKit.OwnerOf(context, home);
        return Make(v.Version, home, exe, owner?.Source ?? source, owner?.Detail ?? detail, v.From);
    }
}
