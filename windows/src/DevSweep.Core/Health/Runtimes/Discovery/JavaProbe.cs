using DevSweep.Core.Detectors;
using DevSweep.Core.Health.Common;
using DevSweep.Core.Inventory.Registry;
using Microsoft.Win32;

namespace DevSweep.Core.Health.Runtimes;

/// <summary>
/// Java: JDKs from Adoptium, Microsoft, Oracle, Zulu, Corretto, BellSoft and others in
/// Program Files and in the registry (JavaSoft and vendor keys), IntelliJ's <c>~\.jdks</c>,
/// Scoop, a CI tool cache, mise, <c>JAVA_HOME</c>, and IDE-bundled runtimes (JetBrains
/// Runtime, Android Studio). The version and vendor come from each JDK's <c>release</c> file.
/// </summary>
public sealed class JavaProbe : RuntimeProbe
{
    private static readonly string[] VendorFolders =
    [
        "Eclipse Adoptium", "AdoptOpenJDK", "Microsoft", "Java", "Zulu", "Amazon Corretto", "BellSoft", "Semeru", "IBM", "RedHat", "OpenJDK", "SapMachine", "Eclipse Foundation",
    ];

    public override string Id => "java";

    public override string Name => "Java";

    public override string Command => "java";

    public override string? Product => "eclipse-temurin";

    public override bool PreferLts => true;

    /// <summary>"1.8.0_402" is Java 8; "21.0.2" is 21.</summary>
    public override string Cycle(string version)
    {
        IReadOnlyList<long> parts = VersionText.Parts(version);
        return parts.Count > 1 && parts[0] == 1 ? parts[1].ToString(System.Globalization.CultureInfo.InvariantCulture) : VersionText.Major(version);
    }

    public override IReadOnlyList<Installation> Find(ProbeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var candidates = new List<(string Dir, InstallSource Source, string? Detail)>();
        foreach (string programFiles in context.ProgramFilesFolders)
        {
            foreach (string vendor in VendorFolders)
            {
                candidates.AddRange(ProbeKit.Folders(System.IO.Path.Join(programFiles, vendor)).Select(f => (f.Path, InstallSource.JdkInstaller, (string?)vendor)));
            }

            foreach (ProbeEntry ide in ProbeKit.Folders(System.IO.Path.Join(programFiles, "JetBrains")))
            {
                candidates.Add((System.IO.Path.Join(ide.Path, "jbr"), InstallSource.IdeBundled, ide.Name));
            }

            candidates.Add((System.IO.Path.Join(programFiles, "Android", "Android Studio", "jbr"), InstallSource.IdeBundled, "Android Studio"));
        }

        foreach (ProbeEntry app in ProbeKit.Folders(context.InLocalAppData("Programs")))
        {
            candidates.Add((System.IO.Path.Join(app.Path, "jbr"), InstallSource.IdeBundled, app.Name));
        }

        candidates.AddRange(ProbeKit.Folders(context.InProfile(".jdks")).Select(f => (f.Path, InstallSource.IdeDownloaded, (string?)f.Name)));
        candidates.AddRange(Registry(context).Select(p => (p, InstallSource.JdkInstaller, (string?)null)));
        foreach (string pattern in (string[])["*jdk*", "*java*", "temurin*", "corretto*", "zulu*", "openjdk*", "microsoft*jdk*"])
        {
            foreach (string root in (string[])["scoop", "scoopGlobal"])
            {
                string? apps = context.Location(root) is { } r ? System.IO.Path.Join(r, "apps") : null;
                foreach (ProbeEntry app in ProbeKit.Folders(apps, pattern))
                {
                    candidates.AddRange(ProbeKit.Folders(app.Path).Select(v => (v.Path, InstallSource.Scoop, (string?)app.Name)));
                }
            }
        }

        foreach (ProbeEntry tool in ProbeKit.Folders(context.ToolCache, "Java_*"))
        {
            candidates.AddRange(ProbeKit.Folders(tool.Path).Select(v => (System.IO.Path.Join(v.Path, "x64"), InstallSource.ToolCache, (string?)tool.Name)));
        }

        string? mise = context.Variable("MISE_DATA_DIR") ?? context.InLocalAppData("mise");
        if (mise is not null)
        {
            candidates.AddRange(ProbeKit.Folders(System.IO.Path.Join(mise, "installs", "java")).Select(f => (f.Path, InstallSource.Mise, (string?)f.Name)));
        }

        if (context.Variable("JAVA_HOME") is { } javaHome)
        {
            candidates.Add((javaHome, InstallSource.Unknown, "JAVA_HOME"));
        }

        var list = new List<Installation>();
        foreach ((string dir, InstallSource source, string? detail) in candidates)
        {
            if (Jdk(context, dir, source, detail) is { } jdk)
            {
                list.Add(jdk);
            }
        }

        return list;
    }

    /// <summary>A JDK or JRE folder with a <c>release</c> file (or one level below, as some vendors nest).</summary>
    private Installation? Jdk(ProbeContext context, string dir, InstallSource source, string? detail)
    {
        string? home = HealthFiles.IsFile(System.IO.Path.Join(dir, "release")) ? dir
            : ProbeKit.Folders(dir).Select(f => f.Path).FirstOrDefault(p => HealthFiles.IsFile(System.IO.Path.Join(p, "release")));
        if (home is null || HealthFiles.ReadText(System.IO.Path.Join(home, "release"), 64 * 1024) is not { } release)
        {
            return null;
        }

        string? version = Field(release, "JAVA_VERSION");
        if (version is null || VersionText.Parts(version).Count == 0)
        {
            return null;
        }

        string? implementor = Field(release, "IMPLEMENTOR");
        (InstallSource s, string? d) = ProbeKit.OwnerOf(context, home) is { } owner ? (owner.Source, owner.Detail) : (source, detail);
        if (s == InstallSource.Unknown && context.ProgramFilesFolders.Any(pf => ProbeKit.IsInside(home, pf)))
        {
            s = InstallSource.JdkInstaller;
        }

        string exe = System.IO.Path.Join(home, "bin", "java.exe");
        Installation jdk = Make(version, home, HealthFiles.Exists(exe) ? exe : null, s, d ?? implementor, "release file") with
        {
            Product = ProductFor(implementor, s),
            SourceDetail = implementor is null ? d : d is null ? implementor : $"{d}, {implementor}",
        };
        return s == InstallSource.JdkInstaller ? ProbeKit.Refine(context, jdk, "temurin*", "microsoft-openjdk*", "openjdk*", "zulu*", "corretto*", "oracle*", "jdk*") : jdk;
    }

    /// <summary>The endoflife.date product for a vendor (support dates differ slightly between vendors).</summary>
    public static string ProductFor(string? implementor, InstallSource source)
    {
        string i = (implementor ?? string.Empty).ToUpperInvariant();
        return i.Contains("MICROSOFT", StringComparison.Ordinal) ? "microsoft-build-of-openjdk"
            : i.Contains("AMAZON", StringComparison.Ordinal) ? "amazon-corretto"
            : i.Contains("AZUL", StringComparison.Ordinal) ? "azul-zulu"
            : i.Contains("ORACLE", StringComparison.Ordinal) && source != InstallSource.IdeBundled ? "oracle-jdk"
            : "eclipse-temurin";
    }

    private static string? Field(string release, string name)
    {
        foreach (string line in release.Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith(name + "=", StringComparison.Ordinal))
            {
                return t[(name.Length + 1)..].Trim().Trim('"');
            }
        }

        return null;
    }

    /// <summary>JDK folders the registry names: JavaSoft (<c>JavaHome</c>) and vendor keys (<c>Path</c>, <c>InstallationPath</c>), four levels deep at most.</summary>
    private static List<string> Registry(ProbeContext context)
    {
        var found = new List<string>();
        foreach (string root in (string[])[@"SOFTWARE\JavaSoft", @"SOFTWARE\Eclipse Adoptium", @"SOFTWARE\Eclipse Foundation", @"SOFTWARE\Microsoft\JDK", @"SOFTWARE\Azul Systems", @"SOFTWARE\Amazon Corretto", @"SOFTWARE\BellSoft"])
        {
            foreach (RegistryView view in (RegistryView[])[RegistryView.Registry64, RegistryView.Registry32])
            {
                try
                {
                    using IRegistryKeyReader? key = context.Registry.Open(RegistryHive.LocalMachine, view, root);
                    if (key is not null)
                    {
                        Collect(key, 0, found);
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    // Not readable: the folder scans still find the usual places.
                }
            }
        }

        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Collect(IRegistryKeyReader key, int depth, List<string> found)
    {
        foreach (string value in (string[])["JavaHome", "Path", "InstallationPath"])
        {
            if (key.Value(value) is string path && path.Trim().Length > 0)
            {
                found.Add(System.IO.Path.TrimEndingDirectorySeparator(path.Trim().Trim('"')));
            }
        }

        if (depth >= 4)
        {
            return;
        }

        foreach (string name in key.SubKeyNames())
        {
            using IRegistryKeyReader? child = key.OpenSubKey(name);
            if (child is not null)
            {
                Collect(child, depth + 1, found);
            }
        }
    }
}
