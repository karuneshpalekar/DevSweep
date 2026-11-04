using DevSweep.Core.Cleaning;
using DevSweep.Core.Health;
using DevSweep.Core.Health.Runtimes;
using DevSweep.Core.Inventory.Registry;
using DevSweep.Core.Locations;
using DevSweep.Core.Modules;
using Microsoft.Win32;
using Xunit;

namespace DevSweep.Accuracy.Health;

/// <summary>
/// Runtime discovery on the real machine (the CI runner has many Node, Python, Java, Go, Ruby, PHP and
/// .NET installs), checked against independent ground truth: every program <c>where.exe</c> finds on
/// PATH, every PEP 514 registration, <c>JAVA_HOME</c>, and what <c>py -0p</c> lists.
/// </summary>
public sealed class RuntimeGroundTruthTests(ITestOutputHelper output)
{
    private static readonly (string Tool, string Command)[] Commands =
    [
        ("node", "node"), ("python", "python"), ("java", "java"), ("go", "go"), ("ruby", "ruby"), ("php", "php"),
        ("dotnet", "dotnet"), ("postgres", "psql"), ("rust", "rustc"), ("deno", "deno"), ("bun", "bun"), ("flutter", "flutter"),
    ];

    private static async Task<VersionsResult> ScanAsync()
    {
        DevSweepEngine engine = DevSweepEngine.CreateDefault(FeatureFlags.Default.With("+module:health"));
        ProbeContext context = HealthChecks.CreateContext(engine);
        return await new RuntimeHealth().ScanAsync(context, new EndOfLifeClient(null, OfflineEolFetcher.Instance), [], cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Every_program_where_finds_belongs_to_a_discovered_install()
    {
        VersionsResult result = await ScanAsync();
        foreach (RuntimeReport report in result.Runtimes)
        {
            output.WriteLine($"{report.Name}: {report.Installs.Count} installs");
            foreach (Installation i in report.Installs)
            {
                output.WriteLine($"  {(i.IsDefault ? "*" : " ")} {i.Version,-12} {i.Source,-20} {i.SourceDetail,-40} {i.Path}  [{i.VersionFrom}]");
            }

            foreach (PathHit hit in report.Resolution?.Hits ?? [])
            {
                output.WriteLine($"    PATH: {hit.Path}{(hit.Alias is { } a ? $"  (alias: {a.Kind} {a.PackageFamily})" : string.Empty)}");
            }
        }

        string where = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe");
        var missed = new List<string>();
        int checkedPrograms = 0;
        foreach ((string tool, string command) in Commands)
        {
            CommandResult found = await CommandRunner.RunAsync([where, command], TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            foreach (string line in found.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string extension = Path.GetExtension(line).ToUpperInvariant();
                if (!File.Exists(line) || extension is ".CMD" or ".BAT" or ".PS1" or "" || line.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)
                    || line.Contains(@"\chocolatey\bin\", StringComparison.OrdinalIgnoreCase) || line.Contains(@"\scoop\shims\", StringComparison.OrdinalIgnoreCase))
                {
                    output.WriteLine($"where {command}: {line} (shim, script or alias: not an install)");
                    continue;
                }

                string real = ShimResolver.FollowLinks(line);
                RuntimeReport? report = result.Runtimes.FirstOrDefault(r => r.Id == tool);
                bool proxy = tool == "rust" && real.Contains(@"\.cargo\bin\", StringComparison.OrdinalIgnoreCase);
                bool covered = proxy ? report?.Installs.Count > 0 || !Directory.Exists(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".rustup"))
                    : report?.Installs.Any(i => ProbeKit.IsInside(real, ShimResolver.FollowLinks(i.Path))
                        || (tool == "dotnet" && ProbeKit.IsInside(i.Path, Path.GetDirectoryName(real)))) == true;
                checkedPrograms++;
                output.WriteLine($"where {command}: {line} -> {(covered ? "discovered" : "MISSED")}");
                if (!covered)
                {
                    missed.Add($"{command}: {line}");
                }
            }
        }

        output.WriteLine($"{checkedPrograms} programs on PATH checked, {missed.Count} missed");
        Assert.Empty(missed);
    }

    [Fact]
    public async Task Every_pep514_registration_java_home_and_py_launcher_entry_is_discovered()
    {
        VersionsResult result = await ScanAsync();
        List<Installation> python = result.Runtimes.FirstOrDefault(r => r.Id == "python")?.Installs.ToList() ?? [];
        List<Installation> java = result.Runtimes.FirstOrDefault(r => r.Id == "java")?.Installs.ToList() ?? [];
        var missed = new List<string>();

        foreach ((RegistryHive hive, RegistryView view) in new[] { (RegistryHive.CurrentUser, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32) })
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? pythonKey = root.OpenSubKey(@"Software\Python");
            foreach (string company in pythonKey?.GetSubKeyNames() ?? [])
            {
                using RegistryKey? companyKey = pythonKey!.OpenSubKey(company);
                foreach (string tag in companyKey?.GetSubKeyNames() ?? [])
                {
                    using RegistryKey? installPath = companyKey!.OpenSubKey(tag + @"\InstallPath");
                    if (installPath?.GetValue(string.Empty) is string home && File.Exists(Path.Join(home, "python.exe")))
                    {
                        bool ok = python.Any(i => ProbeKit.IsInside(home, i.Path) && ProbeKit.IsInside(i.Path, home));
                        output.WriteLine($"PEP 514 {hive}/{view} {company}\\{tag}: {home} -> {(ok ? "discovered" : "MISSED")}");
                        if (!ok)
                        {
                            missed.Add(home);
                        }
                    }
                }
            }
        }

        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } javaHome && File.Exists(Path.Join(javaHome, "release")))
        {
            bool ok = java.Any(i => ProbeKit.IsInside(javaHome, i.Path) || ProbeKit.IsInside(i.Path, javaHome));
            output.WriteLine($"JAVA_HOME {javaHome} -> {(ok ? "discovered" : "MISSED")}");
            if (!ok)
            {
                missed.Add(javaHome);
            }
        }

        foreach (string name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(k => k.StartsWith("JAVA_HOME_", StringComparison.OrdinalIgnoreCase)))
        {
            string home = Environment.GetEnvironmentVariable(name)!;
            if (File.Exists(Path.Join(home, "release")))
            {
                bool ok = java.Any(i => ProbeKit.IsInside(home, i.Path) || ProbeKit.IsInside(i.Path, home));
                output.WriteLine($"{name} {home} -> {(ok ? "discovered" : "MISSED")}");
                if (!ok)
                {
                    missed.Add(home);
                }
            }
        }

        ProbeContext context = new(WindowsEnvironment.Instance, WindowsRegistryReader.Instance, commands: new TrustedVersionCommand(WindowsEnvironment.Instance));
        foreach ((string exe, bool isDefault) in PythonProbe.Launcher(context))
        {
            bool ok = python.Any(i => ProbeKit.IsInside(exe, i.Path));
            output.WriteLine($"py -0p: {exe}{(isDefault ? " (py's default)" : string.Empty)} -> {(ok ? "discovered" : "MISSED")}");
            if (!ok)
            {
                missed.Add(exe);
            }
        }

        Assert.Empty(missed);
    }

    [Fact]
    public async Task Store_aliases_on_this_machine_are_never_reported_as_installs()
    {
        VersionsResult result = await ScanAsync();
        string apps = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
        foreach (string name in new[] { "python.exe", "python3.exe" })
        {
            string alias = Path.Join(apps, name);
            AliasInfo? info = File.Exists(alias) || Directory.Exists(apps) ? WindowsAliasReader.Instance.Read(alias) : null;
            output.WriteLine($"{alias}: {(info is null ? "none" : $"{info.Kind} {info.PackageFamily} -> {info.Target}")}");
        }

        IEnumerable<Installation> all = result.Runtimes.SelectMany(r => r.Installs);
        Assert.DoesNotContain(all, i => i.Source != InstallSource.StorePython && i.Path.Contains(@"\Microsoft\WindowsApps", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(all, i => i.Executable?.Contains("PythonRedirector", StringComparison.OrdinalIgnoreCase) == true);
        output.WriteLine($"PATH ({result.Path.Count} folders, system then user); shells: {string.Join("; ", result.ShellNotes)}");
        Assert.NotEmpty(result.Path);
    }
}
