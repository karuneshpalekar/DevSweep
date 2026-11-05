using DevSweep.Core.Health.Runtimes;
using DevSweep.Core.Inventory.Registry;
using DevSweep.Core.Locations;
using Microsoft.Win32;
using Xunit;

namespace DevSweep.Core.Tests.Health;

public sealed class PathAndAliasTests
{
    public const string StubFamily = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe";
    public const string StubTarget = @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.21.3482.0_x64__8wekyb3d8bbwe\AppInstallerPythonRedirector.exe";
    public const string StoreFamily = "PythonSoftwareFoundation.Python.3.12_qbz5n2kfra8p0";
    public const string StoreTarget = @"C:\Program Files\WindowsApps\PythonSoftwareFoundation.Python.3.12_3.12.2032.0_x64__qbz5n2kfra8p0\python3.12.exe";

    public static AliasInfo Stub => new(StubFamily, StubFamily + "!PythonRedirector", StubTarget, AliasKind.StoreInstallerStub);

    public static AliasInfo StorePython => new(StoreFamily, StoreFamily + "!Python", StoreTarget, AliasKind.StorePython);

    [Fact]
    public void The_store_installer_stub_is_recognised_from_its_reparse_data()
    {
        byte[] data = AppExecLink.Build(StubFamily, StubFamily + "!PythonRedirector", StubTarget);

        AliasInfo alias = Assert.IsType<AliasInfo>(AppExecLink.Parse(data));

        Assert.Equal(AliasKind.StoreInstallerStub, alias.Kind);
        Assert.Equal(StubTarget, alias.Target);
        Assert.Null(alias.PythonCycle);
        Assert.Contains("not a Python install", alias.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_store_python_alias_names_its_package_and_release_line()
    {
        AliasInfo alias = Assert.IsType<AliasInfo>(AppExecLink.Parse(AppExecLink.Build(StoreFamily, StoreFamily + "!Python", StoreTarget)));

        Assert.Equal(AliasKind.StorePython, alias.Kind);
        Assert.Equal(StoreFamily, alias.PackageFamily);
        Assert.Equal("3.12", alias.PythonCycle);
        Assert.Equal(StoreTarget, alias.Target);
    }

    [Fact]
    public void Other_reparse_data_is_not_an_alias()
    {
        byte[] data = AppExecLink.Build(StubFamily, "x", "y");
        data[0] = 0x0C; // a symlink tag
        Assert.Null(AppExecLink.Parse(data));
        Assert.Null(AppExecLink.Parse([1, 2, 3]));
        Assert.Equal(AliasKind.OtherPackage, AppExecLink.Classify("Microsoft.WindowsTerminal_8wekyb3d8bbwe", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", @"C:\x\wt.exe"));
    }

    [Fact]
    public void Path_is_built_like_explorer_system_then_user_with_variables_expanded()
    {
        var registry = new InMemoryRegistry();
        registry.Set(RegistryHive.LocalMachine, RegistryView.Registry64, SearchPath.MachineKey,
            ("Path", @"%SystemRoot%\system32;C:\Program Files\nodejs\;;%TOOLS%\bin"), ("SystemRoot", @"C:\Windows"), ("TOOLS", @"D:\tools"), ("PATHEXT", ".COM;.EXE;.BAT;.CMD;.PY"));
        registry.Set(RegistryHive.CurrentUser, RegistryView.Registry64, SearchPath.UserKey,
            ("Path", @"%USERPROFILE%\AppData\Local\Microsoft\WindowsApps;%NVM_HOME%"), ("NVM_HOME", @"%USERPROFILE%\AppData\Roaming\nvm"), ("TOOLS", @"E:\mine"));
        var environment = new FixedEnvironment { ReadRealFiles = false };
        environment.Variables["USERPROFILE"] = @"C:\Users\Dev";

        SearchPath path = SearchPath.FromRegistry(registry, environment);

        Assert.Equal(
            [@"C:\Windows\system32", @"C:\Program Files\nodejs\", @"D:\tools\bin", @"C:\Users\Dev\AppData\Local\Microsoft\WindowsApps", @"C:\Users\Dev\AppData\Roaming\nvm"],
            path.Entries.Select(e => e.Expanded));
        Assert.Equal([false, false, false, true, true], path.Entries.Select(e => e.FromUser));
        Assert.Equal([".COM", ".EXE", ".BAT", ".CMD", ".PY"], path.Extensions);
        Assert.Empty(path.Problems);
    }

    [Fact]
    public void Unknown_variables_stay_as_written()
        => Assert.Equal(@"%NOPE%\bin;C:\x", SearchPath.Expand(@"%NOPE%\bin;%X%", n => n == "X" ? @"C:\x" : null));

    [Fact]
    public void Find_lists_every_match_in_path_order_and_marks_aliases()
    {
        using var machine = new HealthMachine();
        string windowsApps = Path.Join(machine.LocalAppData, "Microsoft", "WindowsApps");
        string pythonOrg = Path.Join(machine.LocalAppData, "Programs", "Python", "Python312");
        machine.Alias(Path.Join(windowsApps, "python.exe"), Stub);
        machine.File(Path.Join(pythonOrg, "python.exe"));
        machine.File(Path.Join(pythonOrg, "Scripts", "pip.cmd"));
        machine.SetPath(machine: null, user: $"{windowsApps};{pythonOrg};{Path.Join(pythonOrg, "Scripts")}");

        SearchPath path = SearchPath.FromRegistry(machine.Registry, machine.Environment);
        IReadOnlyList<PathHit> python = path.Find("python", machine.Aliases);
        IReadOnlyList<PathHit> pip = path.Find("pip", machine.Aliases);

        Assert.Equal(2, python.Count);
        Assert.Equal(AliasKind.StoreInstallerStub, python[0].Alias?.Kind);
        Assert.Null(python[1].Alias);
        Assert.EndsWith("pip.cmd", Assert.Single(pip).Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_store_stub_is_never_an_install_and_its_shadowing_is_reported()
    {
        using var machine = new HealthMachine();
        string windowsApps = Path.Join(machine.LocalAppData, "Microsoft", "WindowsApps");
        string pythonOrg = Path.Join(machine.LocalAppData, "Programs", "Python", "Python312");
        machine.Alias(Path.Join(windowsApps, "python.exe"), Stub);
        machine.Alias(Path.Join(windowsApps, "python3.exe"), Stub);
        machine.Versions.Versions[machine.File(Path.Join(pythonOrg, "python.exe"))] = new FileVersion("3.12.1", "3.12.1150.1013", "Python", "Python Software Foundation");
        machine.SetPath(machine: null, user: $"{windowsApps};{pythonOrg}");

        RuntimeReport report = Report(machine, new PythonProbe());

        Installation only = Assert.Single(report.Installs);
        Assert.Equal(InstallSource.PythonOrg, only.Source);
        Assert.Equal("3.12.1", only.Version);
        Assert.Equal("version resource of python.exe", only.VersionFrom);
        Assert.False(only.IsDefault); // a new terminal runs the stub, not this
        Assert.DoesNotContain(report.Installs, i => i.Path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.NotInstalls, n => n.Contains("not a Python install", StringComparison.Ordinal));
        Assert.Contains(report.Issues, i => i.Text.Contains("opens the Microsoft Store instead of running Python 3.12.1", StringComparison.Ordinal));
    }

    [Fact]
    public void A_store_python_alias_is_reported_as_the_store_package()
    {
        using var machine = new HealthMachine();
        string windowsApps = Path.Join(machine.LocalAppData, "Microsoft", "WindowsApps");
        machine.Alias(Path.Join(windowsApps, "python.exe"), StorePython);
        machine.Alias(Path.Join(windowsApps, "python3.12.exe"), StorePython);
        machine.SetPath(machine: null, user: windowsApps);

        RuntimeReport report = Report(machine, new PythonProbe());

        Installation store = Assert.Single(report.Installs);
        Assert.Equal(InstallSource.StorePython, store.Source);
        Assert.Equal(StoreFamily, store.SourceDetail);
        Assert.Equal("3.12", store.Cycle);
        Assert.Contains("patch release not known", store.VersionFrom, StringComparison.Ordinal);
        Assert.True(store.IsDefault);
        Assert.Empty(report.NotInstalls);

        // With the package's own program readable, the exact version is used.
        machine.Versions.Versions[StoreTarget] = new FileVersion("3.12.7", "3.12.7150.1013", "Python", "Python Software Foundation");
        Assert.Equal("3.12.7", Assert.Single(Report(machine, new PythonProbe()).Installs).Version);
    }

    [Fact]
    public void Shims_and_links_are_followed_to_the_program_that_runs()
    {
        using var machine = new HealthMachine();
        string scoop = Path.Join(machine.Profile, "scoop");
        string real = machine.File(Path.Join(scoop, "apps", "nodejs-lts", "20.11.0", "node.exe"));
        string shim = machine.File(Path.Join(scoop, "shims", "node.exe"));
        machine.File(Path.Join(scoop, "shims", "node.shim"), $"path = \"{real}\"\r\n");

        (string runs, string? via) = ShimResolver.Resolve(new PathHit(shim, 0, true, null), machine.Environment);

        Assert.Equal(real, runs);
        Assert.Equal("Scoop shim", via);
    }

    [Fact]
    public void Volta_and_pyenv_shims_resolve_to_their_default_versions()
    {
        using var machine = new HealthMachine();
        string volta = Path.Join(machine.LocalAppData, "Volta");
        machine.File(Path.Join(volta, "tools", "user", "platform.json"), """{"node":{"runtime":"20.11.0","npm":null}}""");
        string pyenv = Path.Join(machine.Profile, ".pyenv", "pyenv-win");
        machine.File(Path.Join(pyenv, "version"), "3.11.7\n");

        Assert.Equal(Path.Join(volta, "tools", "image", "node", "20.11.0", "node.exe"),
            ShimResolver.Resolve(new PathHit(machine.File(Path.Join(volta, "bin", "node.exe")), 0, true, null), machine.Environment).Runs);
        Assert.Equal(Path.Join(pyenv, "versions", "3.11.7", "python.exe"),
            ShimResolver.Resolve(new PathHit(machine.File(Path.Join(pyenv, "shims", "python.bat")), 0, true, null), machine.Environment).Runs);
    }

    [Fact]
    public void Shell_start_up_files_are_read_not_run_and_never_quoted()
    {
        IReadOnlyList<ShellNote> notes = ShellProfiles.Scan("PowerShell 7", @"C:\Users\Dev\Documents\PowerShell\Microsoft.PowerShell_profile.ps1",
            "# fnm env is great\nfnm env --use-on-cd | Out-String | Invoke-Expression\n$env:GITHUB_TOKEN = 'ghp_secret'\n$env:Path = \"C:\\tools;$env:Path\"\n#region conda initialize\n");

        Assert.Equal([2, 4, 5], notes.Select(n => n.Line));
        Assert.Equal(["node"], notes[0].Tools);
        Assert.Empty(notes[1].Tools);
        Assert.DoesNotContain(notes, n => n.Describe().Contains("ghp_secret", StringComparison.Ordinal));
        Assert.Equal(2, ShellProfiles.MayDiffer(notes, "python").Count);
        Assert.Equal(2, ShellProfiles.MayDiffer(notes, "node").Count);
    }

    internal static RuntimeReport Report(HealthMachine machine, RuntimeProbe probe, IReadOnlyDictionary<string, EolData>? eol = null)
    {
        ProbeContext context = machine.Context();
        List<Installation> installs = ProbeKit.Dedupe(probe.Find(context));
        CommandResolution resolution = RuntimeHealth.Resolve(context, probe.Command, probe.Id, []);
        installs = probe.MarkDefault(context, installs, resolution);
        return RuntimeReporter.Build(new ReportInput
        {
            Probe = probe,
            Installs = installs,
            Eol = eol ?? new Dictionary<string, EolData>(),
            Resolution = resolution,
            Profile = machine.Profile,
            Today = new DateOnly(2026, 11, 4),
        });
    }
}
