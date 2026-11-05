using DevSweep.Core.Health.Runtimes;
using Microsoft.Win32;
using Xunit;

namespace DevSweep.Core.Tests.Health;

/// <summary>Every source of every tool, on a fixture machine whose registry, version resources and aliases are fakes.</summary>
public sealed class RuntimeDiscoveryTests
{
    [Fact]
    public void Node_is_found_from_every_manager_and_the_one_on_path_is_the_default()
    {
        using var machine = new HealthMachine();
        string nvm = Path.Join(machine.RoamingAppData, "nvm");
        machine.File(Path.Join(nvm, "v16.20.2", "node.exe"));
        machine.File(Path.Join(nvm, "v20.11.0", "node.exe"));
        machine.File(Path.Join(nvm, "settings.txt"));
        machine.File(Path.Join(machine.RoamingAppData, "fnm", "node-versions", "v22.3.0", "installation", "node.exe"));
        machine.File(Path.Join(machine.LocalAppData, "Volta", "tools", "image", "node", "18.19.0", "node.exe"));
        machine.File(Path.Join(machine.Profile, "scoop", "apps", "nodejs-lts", "20.10.0", "node.exe"));
        Directory.CreateDirectory(Path.Join(machine.Profile, "scoop", "apps", "nodejs-lts", "not-a-version"));
        string official = machine.File(Path.Join(machine.ProgramFiles, "nodejs", "node.exe"));
        machine.Versions.Versions[official] = new FileVersion("20.11.1", "20.11.1.0", "Node.js", "Node.js");
        string vs = machine.File(Path.Join(machine.ProgramFiles, "Microsoft Visual Studio", "2022", "Community", "MSBuild", "Microsoft", "VisualStudio", "NodeJs", "node.exe"));
        machine.Versions.Versions[vs] = new FileVersion("20.9.0", null, null, null);
        machine.SetPath(machine: Path.Join(machine.ProgramFiles, "nodejs"), user: null);

        RuntimeReport report = PathAndAliasTests.Report(machine, new NodeProbe());

        Assert.Equal(
            ["16.20.2 Nvm", "18.19.0 Volta", "20.10.0 Scoop", "20.11.0 Nvm", "20.11.1 NodejsOrg", "20.9.0 IdeBundled", "22.3.0 Fnm"],
            report.Installs.Select(i => $"{i.Version} {i.Source}").Order(StringComparer.Ordinal));
        Installation def = Assert.Single(report.Installs, i => i.IsDefault);
        Assert.Equal(InstallSource.NodejsOrg, def.Source);
        Assert.Equal("version resource of node.exe", def.VersionFrom);
        Assert.Equal("nodejs-lts", report.Installs.Single(i => i.Source == InstallSource.Scoop).SourceDetail);
        Assert.Contains(report.Issues, i => i.Text.StartsWith("Installed by 5 different tools", StringComparison.Ordinal));
        Assert.Contains(report.Issues, i => i.Text.Contains("even though 22.3.0 from fnm is installed", StringComparison.Ordinal));
    }

    [Fact]
    public void Python_is_found_from_pyenv_uv_conda_the_install_manager_pep514_and_the_py_launcher()
    {
        using var machine = new HealthMachine();
        string pyenv = Path.Join(machine.Profile, ".pyenv", "pyenv-win");
        machine.File(Path.Join(pyenv, "versions", "3.8.10", "python.exe"));
        machine.File(Path.Join(machine.RoamingAppData, "uv", "python", "cpython-3.12.1-windows-x86_64-none", "python.exe"));
        string conda = Path.Join(machine.Profile, "miniconda3");
        machine.File(Path.Join(conda, "python.exe"));
        machine.File(Path.Join(conda, "conda-meta", "python-3.11.5-he1021f5_0.json"), "{}");
        machine.File(Path.Join(conda, "envs", "ml", "python.exe"));
        machine.File(Path.Join(conda, "envs", "ml", "conda-meta", "python-3.10.13-h4de0772_0.json"), "{}");
        string elsewhere = Path.Join(machine.Root, "D", "envs", "web");
        machine.File(Path.Join(elsewhere, "python.exe"));
        machine.File(Path.Join(elsewhere, "conda-meta", "python-3.9.18-h1234_0.json"), "{}");
        machine.File(Path.Join(machine.Profile, ".conda", "environments.txt"), elsewhere + "\r\n");
        string manager = Path.Join(machine.LocalAppData, "Python", "pythoncore-3.14-64");
        machine.File(Path.Join(manager, "include", "patchlevel.h"), "#define PY_VERSION \"3.14.0\"\n");
        machine.File(Path.Join(manager, "python.exe"));
        string registered = Path.Join(machine.Root, "C", "Python313");
        machine.Versions.Versions[machine.File(Path.Join(registered, "python.exe"))] = new FileVersion("3.13.9", "3.13.9150.1013", null, null);
        machine.Registry.Set(RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Python\PythonCore\3.13\InstallPath", (string.Empty, registered + "\\"));
        string listedOnly = Path.Join(machine.Root, "E", "Py39");
        machine.Versions.Versions[machine.File(Path.Join(listedOnly, "python.exe"))] = new FileVersion("3.9.13", null, null, null);
        string windows = Path.Join(machine.Root, "Windows");
        machine.Environment.Variables["SystemRoot"] = windows;
        string py = machine.File(Path.Join(windows, "py.exe"));
        machine.Commands.Outputs[py] = $" -V:3.13 *        {registered}\\python.exe\r\n -V:3.9           {listedOnly}\\python.exe\r\n";

        RuntimeReport report = PathAndAliasTests.Report(machine, new PythonProbe());

        Assert.Equal(
            ["3.10.13 Conda env ml", "3.11.5 Conda base", "3.12.1 Uv", "3.13.9 PythonOrg all users", "3.14.0 PythonInstallManager", "3.8.10 Pyenv", "3.9.13 Unknown listed by the py launcher", "3.9.18 Conda env web"],
            report.Installs.Select(i => $"{i.Version} {i.Source}{(i.Source is InstallSource.Conda or InstallSource.PythonOrg or InstallSource.Unknown ? " " + i.SourceDetail : string.Empty)}").Order(StringComparer.Ordinal));
        Assert.Equal(@"include\patchlevel.h", report.Installs.Single(i => i.Source == InstallSource.PythonInstallManager).VersionFrom);
        Assert.Equal("conda-meta", report.Installs.Single(i => i.SourceDetail == "env ml").VersionFrom);
        Assert.Contains(py, machine.Commands.Ran);
    }

    [Fact]
    public void Java_go_rust_dotnet_and_others_read_versions_from_files()
    {
        using var machine = new HealthMachine();
        string temurin = Path.Join(machine.ProgramFiles, "Eclipse Adoptium", "jdk-17.0.9.9-hotspot");
        machine.File(Path.Join(temurin, "release"), "IMPLEMENTOR=\"Eclipse Adoptium\"\nJAVA_VERSION=\"17.0.9\"\n");
        machine.File(Path.Join(temurin, "bin", "java.exe"));
        string corretto = Path.Join(machine.Profile, ".jdks", "corretto-1.8.0_392");
        machine.File(Path.Join(corretto, "release"), "JAVA_VERSION=\"1.8.0_392\"\nIMPLEMENTOR=\"Amazon.com Inc.\"\n");
        string jbr = Path.Join(machine.ProgramFiles, "JetBrains", "IntelliJ IDEA 2024.3", "jbr");
        machine.File(Path.Join(jbr, "release"), "IMPLEMENTOR=\"JetBrains s.r.o.\"\nJAVA_VERSION=\"21.0.5\"\n");
        string microsoft = Path.Join(machine.ProgramFiles, "Microsoft", "jdk-21.0.2.13-hotspot");
        machine.File(Path.Join(microsoft, "release"), "IMPLEMENTOR=\"Microsoft\"\nJAVA_VERSION=\"21.0.2\"\n");
        machine.Registry.Set(RegistryHive.CurrentUser, RegistryView.Registry64, SearchPath.UserKey, ("JAVA_HOME", temurin));

        RuntimeReport java = PathAndAliasTests.Report(machine, new JavaProbe());
        Assert.Equal(["1.8.0_392 IdeDownloaded 8 amazon-corretto", "17.0.9 JdkInstaller 17 eclipse-temurin", "21.0.2 JdkInstaller 21 microsoft-build-of-openjdk", "21.0.5 IdeBundled 21 eclipse-temurin"],
            java.Installs.Select(i => $"{i.Version} {i.Source} {i.Cycle} {i.Product}").Order(StringComparer.Ordinal));

        machine.File(Path.Join(machine.ProgramFiles, "Go", "VERSION"), "go1.24.2\ntime 2025-04-01T00:00:00Z\n");
        machine.File(Path.Join(machine.Profile, "sdk", "go1.22.5", "VERSION"), "go1.22.5\n");
        Assert.Equal(["1.22.5 SdkFolder", "1.24.2 GoOrg"], PathAndAliasTests.Report(machine, new GoProbe()).Installs.Select(i => $"{i.Version} {i.Source}").Order(StringComparer.Ordinal));

        string rustup = Path.Join(machine.Profile, ".rustup");
        machine.File(Path.Join(rustup, "toolchains", "stable-x86_64-pc-windows-msvc", "lib", "rustlib", "multirust-channel-manifest.toml"),
            "manifest-version = \"2\"\n[pkg.cargo]\nversion = \"0.88.0\"\n[pkg.rustc]\nversion = \"1.87.0 (17067e9ac 2025-05-09)\"\n");
        machine.File(Path.Join(rustup, "toolchains", "1.75.0-x86_64-pc-windows-msvc", "lib", "rustlib", "multirust-channel-manifest.toml"),
            "[pkg.rustc]\nversion = \"1.75.0 (82e1608df 2023-12-21)\"\n");
        machine.File(Path.Join(rustup, "settings.toml"), "default_toolchain = \"stable-x86_64-pc-windows-msvc\"\n");
        string cargoBin = Path.Join(machine.Profile, ".cargo", "bin");
        machine.File(Path.Join(cargoBin, "rustc.exe"));
        machine.SetPath(machine: null, user: cargoBin);
        RuntimeReport rust = PathAndAliasTests.Report(machine, new RustProbe());
        Assert.Equal("1.87.0", Assert.Single(rust.Installs, i => i.IsDefault).Version);
        Assert.Equal(2, rust.Installs.Count);

        string dotnet = Path.Join(machine.ProgramFiles, "dotnet");
        foreach (string sdk in (string[])["8.0.404", "9.0.101", "10.0.100"])
        {
            machine.File(Path.Join(dotnet, "sdk", sdk, "dotnet.dll"));
        }

        Directory.CreateDirectory(Path.Join(dotnet, "sdk", "NuGetFallbackFolder"));
        machine.File(Path.Join(dotnet, "dotnet.exe"));
        machine.SetPath(machine: dotnet, user: cargoBin);
        RuntimeReport net = PathAndAliasTests.Report(machine, new DotnetProbe());
        Assert.Equal(["10.0", "8.0", "9.0"], net.Installs.Select(i => i.Cycle).Order(StringComparer.Ordinal));
        Assert.Equal("10.0.100", Assert.Single(net.Installs, i => i.IsDefault).Version);

        string flutter = Path.Join(machine.Root, "C", "src", "flutter");
        machine.File(Path.Join(flutter, "bin", "cache", "flutter.version.json"), """{"frameworkVersion":"3.24.5","channel":"stable"}""");
        Assert.Equal("3.24.5", Assert.Single(PathAndAliasTests.Report(machine, new FlutterProbe()).Installs).Version);

        string ruby = Path.Join(machine.Root, "C", "Ruby32-x64");
        machine.Versions.Versions[machine.File(Path.Join(ruby, "bin", "ruby.exe"))] = new FileVersion("3.2.2p53", null, null, null);
        Installation rb = Assert.Single(PathAndAliasTests.Report(machine, new RubyProbe()).Installs);
        Assert.Equal(("3.2.2", InstallSource.RubyInstaller), (rb.Version, rb.Source));

        string pg = Path.Join(machine.ProgramFiles, "PostgreSQL", "16");
        machine.Versions.Versions[machine.File(Path.Join(pg, "bin", "postgres.exe"))] = new FileVersion("16.4", null, null, null);
        machine.Registry.Set(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\PostgreSQL\Installations\postgresql-x64-16",
            ("Base Directory", pg), ("Data Directory", Path.Join(pg, "data")), ("Version", "16.4"));
        Installation postgres = Assert.Single(PathAndAliasTests.Report(machine, new PostgresProbe()).Installs);
        Assert.Equal(("16", Path.Join(pg, "data")), (postgres.Cycle, postgres.DataPath));
    }

    [Fact]
    public void A_ci_tool_cache_is_its_own_source_and_never_offered_for_removal()
    {
        using var machine = new HealthMachine();
        string cache = Path.Join(machine.Root, "hostedtoolcache", "windows");
        machine.Environment.Variables["RUNNER_TOOL_CACHE"] = cache;
        machine.File(Path.Join(cache, "node", "18.20.8", "x64", "node.exe"));
        machine.File(Path.Join(cache, "node", "16.20.2", "x64", "node.exe"));

        RuntimeReport report = PathAndAliasTests.Report(machine, new NodeProbe(), new Dictionary<string, EolData> { ["nodejs"] = EolFixtures.Data(EolFixtures.NodeJs) });

        Assert.All(report.Installs, i => Assert.True(i.IsSystem));
        Assert.All(report.Installs, i => Assert.Equal(SupportStatus.EndOfLife, i.Support));
        Assert.Empty(report.Steps);
        Assert.All(report.Issues, i => Assert.Equal(IssueLevel.Info, i.Level));
    }

    [Fact]
    public void The_py_launcher_list_is_parsed_in_both_formats()
    {
        IReadOnlyList<(string Exe, bool IsDefault)> modern = PythonProbe.ParseLauncherList(" -V:3.12 *        C:\\Python312\\python.exe\r\n -V:ContinuumAnalytics/Anaconda39-64 C:\\Users\\Dev\\anaconda3\\python.exe\r\n");
        IReadOnlyList<(string Exe, bool IsDefault)> legacy = PythonProbe.ParseLauncherList(" -3.9-64        C:\\Program Files\\Python39\\python.exe *\n -2.7-32        C:\\Python27\\python.exe\n");

        Assert.Equal([("C:\\Python312\\python.exe", true), ("C:\\Users\\Dev\\anaconda3\\python.exe", false)], modern);
        Assert.Equal([("C:\\Program Files\\Python39\\python.exe", true), ("C:\\Python27\\python.exe", false)], legacy);
    }

    [Fact]
    public void Rust_versions_come_from_the_channel_manifest()
    {
        Assert.Equal("1.87.0", RustProbe.ManifestVersion("[pkg.cargo]\nversion = \"0.88.0\"\n[pkg.rustc]\nversion = \"1.87.0 (17067e9ac 2025-05-09)\"\n"));
        Assert.Null(RustProbe.ManifestVersion("[pkg.cargo]\nversion = \"0.88.0\"\n"));
    }
}
