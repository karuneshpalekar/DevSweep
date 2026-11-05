using DevSweep.Core.Detectors;
using DevSweep.Core.Health.Common;

namespace DevSweep.Core.Health.Runtimes;

/// <summary>
/// Node.js: nvm-windows (<c>NVM_HOME</c>), fnm (<c>FNM_DIR</c>), Volta, mise, asdf, Scoop,
/// a CI tool cache, the nodejs.org MSI (also what winget and Chocolatey install) and
/// Visual Studio's own copy.
/// </summary>
public sealed class NodeProbe : RuntimeProbe
{
    public override string Id => "node";

    public override string Name => "Node.js";

    public override string Command => "node";

    public override string? Product => "nodejs";

    public override bool PreferLts => true;

    public override string Cycle(string version) => VersionText.Major(version);

    public override IReadOnlyList<Installation> Find(ProbeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var list = new List<Installation>();
        string? nvm = context.Variable("NVM_HOME") ?? context.InRoamingAppData("nvm");
        list.AddRange(VersionFolders(context, nvm, "node.exe", InstallSource.Nvm));

        foreach (string? fnm in (string?[])[context.Variable("FNM_DIR"), context.InRoamingAppData("fnm"), context.InLocalAppData("fnm")])
        {
            if (fnm is null)
            {
                continue;
            }

            foreach (ProbeEntry folder in ProbeKit.Folders(System.IO.Path.Join(fnm, "node-versions")))
            {
                string home = System.IO.Path.Join(folder.Path, "installation");
                if (VersionText.FromFolderName(folder.Name) is { } v && HealthFiles.Exists(System.IO.Path.Join(home, "node.exe")))
                {
                    list.Add(Make(v, home, System.IO.Path.Join(home, "node.exe"), InstallSource.Fnm, null, "folder name"));
                }
            }
        }

        string? volta = context.Variable("VOLTA_HOME") ?? context.InLocalAppData("Volta");
        list.AddRange(VersionFolders(context, volta is null ? null : System.IO.Path.Join(volta, "tools", "image", "node"), "node.exe", InstallSource.Volta));
        list.AddRange(MiseAndAsdf(context, "node", "nodejs", "node.exe"));
        list.AddRange(ScoopApps(context, "nodejs*", "node.exe"));
        list.AddRange(ToolCache(context, "node", "node.exe"));

        foreach (string programFiles in context.ProgramFilesFolders)
        {
            if (Program(context, System.IO.Path.Join(programFiles, "nodejs"), "node.exe", InstallSource.NodejsOrg, null, "--version") is { } official)
            {
                list.Add(ProbeKit.Refine(context, official, "nodejs", "nodejs.install", "nodejs-lts"));
            }

            foreach (ProbeEntry year in ProbeKit.Folders(System.IO.Path.Join(programFiles, "Microsoft Visual Studio")))
            {
                foreach (ProbeEntry edition in ProbeKit.Folders(year.Path))
                {
                    string home = System.IO.Path.Join(edition.Path, "MSBuild", "Microsoft", "VisualStudio", "NodeJs");
                    if (Program(context, home, "node.exe", InstallSource.IdeBundled, $"Visual Studio {year.Name} {edition.Name}") is { } vs)
                    {
                        list.Add(vs with { Source = InstallSource.IdeBundled, SourceDetail = $"Visual Studio {year.Name} {edition.Name}" });
                    }
                }
            }
        }

        return list;
    }
}
