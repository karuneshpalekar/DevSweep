using DevSweep.Core.Cleaning;
using DevSweep.Core.Inventory.Collectors;
using DevSweep.Core.Packs.Detectors;
using DevSweep.Core.Packs.Tools;
using DevSweep.Core.Platform.Recycling;
using DevSweep.Core.Rules;
using DevSweep.Core.Scanning;
using DevSweep.Fixtures;
using Microsoft.Win32;
using Xunit;

namespace DevSweep.Accuracy.Rules;

/// <summary>
/// The Windows packs' own detectors on a fixture machine on real NTFS: what each one offers and
/// what it leaves alone, with exact sizes (allocation) or tool-reported ones, and explanations.
/// Tools are never run: the fixture machine answers for docker, wsl, dotnet and schtasks.
/// </summary>
public sealed class PackDetectorTests(ITestOutputHelper output) : IDisposable
{
    private const long Mb = 1024 * 1024;
    private static readonly string[] SdkVersions = ["6.0.428", "8.0.100", "8.0.300", "8.0.404", "9.0.100-rc.2.24474.11", "9.0.100"];
    private readonly MachineFixture _machine = PackPathRuleTests.Windows(new MachineFixture("packs"));

    public void Dispose() => _machine.Dispose();

    private string ProgramFiles => _machine.Environment.Folders["ProgramFiles"];

    [Fact]
    public async Task Docker_rows_come_from_docker_system_df()
    {
        string docker = Touch(Path.Join(ProgramFiles, "Docker", "Docker", "resources", "bin", "docker.exe"));
        _machine.Machine.Commands[docker + " system df --format {{json .}}"] = FixedMachineQueries.Output(
            """
            {"Active":"2","Reclaimable":"1.5GB (60%)","Size":"2.5GB","TotalCount":"7","Type":"Images"}
            {"Active":"1","Reclaimable":"80MB (100%)","Size":"80MB","TotalCount":"3","Type":"Containers"}
            {"Active":"0","Reclaimable":"0B","Size":"0B","TotalCount":"0","Type":"Local Volumes"}
            {"Active":"0","Reclaimable":"2.1GB","Size":"2.1GB","TotalCount":"40","Type":"Build Cache"}
            """);

        ScanResult result = await _machine.ScanAsync([Category.Docker]);
        Log(result);

        Finding images = Only(result, "docker-images");
        Assert.Equal(1_500_000_000L, images.Size);
        Assert.Equal(SizeMeasurement.Reported, images.Measurement);
        Assert.Empty(images.Paths);
        Assert.Equal([docker, "image", "prune", "-a", "-f"], images.Actions[0].Command);
        Assert.Contains("out of 7 images; 2 are in use", images.Explanation!.Why, StringComparison.Ordinal);
        Assert.Equal(2_100_000_000L, Only(result, "docker-build-cache").Size);
        Assert.Equal(80_000_000L, Only(result, "docker-containers").Size);
        Assert.DoesNotContain(result.Findings, f => f.RuleId == "docker-volumes");
        PackPathRuleTests.AssertExplained(images);
    }

    [Fact]
    public async Task Docker_not_running_finds_nothing_and_says_so()
    {
        Touch(Path.Join(ProgramFiles, "Docker", "Docker", "resources", "bin", "docker.exe"));

        ScanResult result = await _machine.ScanAsync([Category.Docker]);

        Assert.Empty(result.Findings);
        Assert.Contains(result.Report.Checks.Single(c => c.Id == "docker-images").Notes, n => n.Contains("Docker is probably not running", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Wsl_disk_is_never_removable_and_its_saving_unknown_while_stopped()
    {
        string basePath = Path.Join(_machine.Local, "Packages", "CanonicalGroupLimited.Ubuntu_79rhkp1fndgsc", "LocalState");
        string vhdx = MachineFixture.Allocate(Path.Join(basePath, "ext4.vhdx"), 2100 * Mb);
        _machine.Registry.Set(RegistryHive.CurrentUser, RegistryView.Registry64, VhdxDisksDetector.LxssKey + @"\{11111111-2222}",
            ("DistributionName", "Ubuntu"), ("BasePath", @"\\?\" + basePath), ("Version", 2));
        _machine.Registry.Set(RegistryHive.CurrentUser, RegistryView.Registry64, VhdxDisksDetector.LxssKey + @"\{33333333-4444}",
            ("DistributionName", "Tiny"), ("BasePath", Path.Join(_machine.Root, "tiny")), ("Version", 2));
        MachineFixture.Allocate(Path.Join(_machine.Root, "tiny", "ext4.vhdx"), 10 * Mb);

        ScanResult result = await _machine.ScanAsync([Category.Wsl]);
        Log(result);

        Finding disk = Only(result, "wsl-disks");
        Assert.Equal(0, disk.Size);
        Assert.Equal(Risk.HoldsData, disk.Risk);
        Assert.Empty(disk.Paths);
        Assert.All(disk.Actions, a => Assert.Equal(CleanActionKind.Manual, a.Kind));
        Assert.Equal(["wsl", "--shutdown"], disk.Actions[0].Command);
        Assert.Equal($"Optimize-VHD -Path '{vhdx}' -Mode Full", disk.Actions[1].Command![^1]);
        Assert.Contains("unknown until it is compacted", disk.Explanation!.Why, StringComparison.Ordinal);
        Assert.Contains("2.1 GB", disk.Title, StringComparison.Ordinal);
        Assert.Contains(disk.Checks, c => c.Status == CheckStatus.Warning && c.Text.Contains("does not start a stopped distro", StringComparison.Ordinal));
        Assert.True(File.Exists(vhdx));
        PackPathRuleTests.AssertExplained(disk);
    }

    [Fact]
    public async Task Running_wsl_distro_gives_an_upper_bound_from_df()
    {
        string basePath = Path.Join(_machine.Root, "wsl", "Ubuntu");
        MachineFixture.Allocate(Path.Join(basePath, "ext4.vhdx"), 3000 * Mb);
        _machine.Registry.Set(RegistryHive.CurrentUser, RegistryView.Registry64, VhdxDisksDetector.LxssKey + @"\{a}",
            ("DistributionName", "Ubuntu"), ("BasePath", basePath), ("Version", 2));
        string wsl = Touch(Path.Join(_machine.Root, "Windows", "System32", "wsl.exe"));
        _machine.Machine.Commands[wsl + " --list --running --quiet"] = FixedMachineQueries.Output("U\0b\0u\0n\0t\0u\0\r\0\n\0");
        _machine.Machine.Commands[wsl + " -d Ubuntu --exec df -B1 --output=used /"] = FixedMachineQueries.Output("Used\n1000000000\n");

        Finding disk = Only(await _machine.ScanAsync([Category.Wsl]), "wsl-disks");

        Assert.Equal(0, disk.Size);
        Assert.Contains($"the files inside use {SizeText.Format(1_000_000_000)}", disk.Explanation!.Why, StringComparison.Ordinal);
        Assert.Contains("at most", disk.Explanation.Why, StringComparison.Ordinal);
        output.WriteLine(disk.Explanation.Why);
    }

    [Fact]
    public async Task Docker_desktop_disk()
    {
        string vhdx = MachineFixture.Allocate(Path.Join(_machine.Local, "Docker", "wsl", "disk", "docker_data.vhdx"), 2100 * Mb);

        Finding disk = Only(await _machine.ScanAsync([Category.Docker]), "docker-disk");

        Assert.Equal(0, disk.Size);
        Assert.Contains(vhdx, disk.Actions[1].Command![^1], StringComparison.Ordinal);
        Assert.Contains("Docker doesn't report", disk.Explanation!.Why, StringComparison.Ordinal);
        PackPathRuleTests.AssertExplained(disk);
    }

    [Fact]
    public async Task Dotnet_sdks_keep_the_newest_of_each_major_and_pinned_bands()
    {
        string dotnet = Path.Join(ProgramFiles, "dotnet");
        MachineFixture.Allocate(Path.Join(dotnet, "sdk", "8.0.100", "dotnet.dll"), 60 * Mb);
        MachineFixture.Allocate(Path.Join(dotnet, "sdk", "8.0.300", "dotnet.dll"), 60 * Mb);
        MachineFixture.Allocate(Path.Join(dotnet, "sdk", "8.0.404", "dotnet.dll"), 1 * Mb);
        MachineFixture.Allocate(Path.Join(dotnet, "sdk", "6.0.428", "dotnet.dll"), 60 * Mb);
        MachineFixture.Allocate(Path.Join(dotnet, "sdk", "9.0.100-rc.2.24474.11", "dotnet.dll"), 60 * Mb);
        MachineFixture.Allocate(Path.Join(dotnet, "sdk", "9.0.100", "dotnet.dll"), 1 * Mb);
        Directory.CreateDirectory(Path.Join(dotnet, "sdk", "NuGetFallbackFolder"));
        string exe = Touch(Path.Join(dotnet, "dotnet.exe"));
        _machine.Machine.Commands[exe + " --list-sdks"] = FixedMachineQueries.Output(string.Join("\n",
            SdkVersions.Select(v => $"{v} [{dotnet}\\sdk]")));
        MachineFixture.Write(Path.Join(_machine.Profile, "source", "legacy", "global.json"), """{ "sdk": { "version": "8.0.301" } }""");

        ScanResult result = await _machine.ScanAsync([Category.Dotnet]);
        Log(result);

        var sdks = result.Findings.Where(f => f.RuleId == "dotnet-sdks").OrderBy(f => f.Title, StringComparer.Ordinal).ToList();
        Assert.Equal([".NET SDK 8.0.100", ".NET SDK 9.0.100-rc.2.24474.11"], sdks.Select(f => f.Title));
        Finding old = sdks[0];
        Assert.Equal(Risk.NeedsAdmin, old.Risk);
        Assert.All(old.Actions, a => Assert.Equal(CleanActionKind.Manual, a.Kind));
        Assert.Equal(["dotnet-core-uninstall", "remove", "--sdk", "8.0.100"], old.Actions[1].Command);
        Assert.Contains("SDK 8.0.404 is newer", old.Explanation!.Why, StringComparison.Ordinal);
        Assert.Contains(old.Checks, c => c.Text.Contains("--list-sdks lists these versions too", StringComparison.Ordinal));
        Assert.Contains(old.Checks, c => c.Text.Contains("none is in the 8.0.1xx band", StringComparison.Ordinal));
        PackPathRuleTests.AssertExact(old, 60 * Mb, output);
    }

    [Fact]
    public async Task Dotnet_runtimes_user_sdks_and_orphaned_workload_packs()
    {
        string dotnet = Path.Join(ProgramFiles, "dotnet");
        MachineFixture.Allocate(Path.Join(dotnet, "shared", "Microsoft.NETCore.App", "8.0.10", "coreclr.dll"), 30 * Mb);
        MachineFixture.Allocate(Path.Join(dotnet, "shared", "Microsoft.NETCore.App", "8.0.11", "coreclr.dll"), 1 * Mb);
        MachineFixture.Allocate(Path.Join(dotnet, "shared", "Microsoft.NETCore.App", "9.0.0", "coreclr.dll"), 30 * Mb);
        MachineFixture.Allocate(Path.Join(dotnet, "packs", "Microsoft.Android.Sdk.Windows", "34.0.95", "a.dll"), 30 * Mb);
        MachineFixture.Allocate(Path.Join(dotnet, "packs", "Microsoft.Android.Sdk.Windows", "34.0.113", "a.dll"), 1 * Mb);
        MachineFixture.Allocate(Path.Join(dotnet, "packs", "Microsoft.NETCore.App.Ref", "8.0.0", "a.dll"), 30 * Mb);
        MachineFixture.Write(Path.Join(_machine.ProgramData, "dotnet", "workloads", "x64", "8.0.400", "InstalledPacks", "v1", "Microsoft.Android.Sdk.Windows", "34.0.113", "8.0.400"), string.Empty);
        string userDotnet = Path.Join(_machine.Local, "Microsoft", "dotnet");
        MachineFixture.Allocate(Path.Join(userDotnet, "sdk", "7.0.100", "dotnet.dll"), 60 * Mb);
        MachineFixture.Allocate(Path.Join(userDotnet, "sdk", "7.0.410", "dotnet.dll"), 1 * Mb);

        ScanResult result = await _machine.ScanAsync([Category.Dotnet]);
        Log(result);

        Finding runtime = Only(result, "dotnet-runtimes");
        Assert.Equal("Microsoft.NETCore.App 8.0.10", runtime.Title);
        PackPathRuleTests.AssertExact(runtime, 30 * Mb, output);
        Finding pack = Only(result, "dotnet-workload-packs");
        Assert.Equal("Workload pack Microsoft.Android.Sdk.Windows 34.0.95", pack.Title);
        Assert.Equal(["dotnet", "workload", "clean"], pack.Actions[0].Command);
        PackPathRuleTests.AssertExact(pack, 30 * Mb, output);
        Finding user = Only(result, "dotnet-sdks-user");
        Assert.Equal(Risk.OldVersion, user.Risk);
        Assert.Equal([CleanActionKind.Trash], user.Actions.Select(a => a.Kind));
        PackPathRuleTests.AssertExact(user, 60 * Mb, output);
    }

    [Fact]
    public async Task Visual_studio_folders_of_uninstalled_instances_only()
    {
        string instances = Path.Join(_machine.ProgramData, "Microsoft", "VisualStudio", "Packages", "_Instances");
        MachineFixture.Write(Path.Join(instances, "aaaa1111", "state.json"), """{"installationName":"VisualStudio/17.11.5","installationVersion":"17.11.35327.3"}""");
        string vs = Path.Join(_machine.Local, "Microsoft", "VisualStudio");
        MachineFixture.Allocate(Path.Join(vs, "17.0_aaaa1111", "privateregistry.bin"), 20 * Mb);
        MachineFixture.Allocate(Path.Join(vs, "17.0_bbbb2222", "privateregistry.bin"), 20 * Mb);
        MachineFixture.Allocate(Path.Join(vs, "17.0_bbbb2222Exp", "privateregistry.bin"), 5 * Mb);

        Finding finding = Only(await _machine.ScanAsync([Category.VisualStudio]), "vs-old-instances");

        Assert.Equal([Path.Join(vs, "17.0_bbbb2222"), Path.Join(vs, "17.0_bbbb2222Exp")], finding.Paths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Contains("VisualStudio/17.11.5 (aaaa1111, 17.11.35327.3)", finding.Explanation!.Why, StringComparison.Ordinal);
        PackPathRuleTests.AssertExact(finding, 25 * Mb, output);
    }

    [Fact]
    public async Task Visual_studio_folders_are_left_alone_without_setup_records()
    {
        MachineFixture.Allocate(Path.Join(_machine.Local, "Microsoft", "VisualStudio", "17.0_bbbb2222", "privateregistry.bin"), 20 * Mb);

        ScanResult result = await _machine.ScanAsync([Category.VisualStudio]);

        Assert.DoesNotContain(result.Findings, f => f.RuleId == "vs-old-instances");
        Assert.Contains(result.Report.Checks.Single(c => c.Id == "vs-old-instances").Notes, n => n.Contains("no Visual Studio folder is called a leftover", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Editor_extensions_not_in_the_list_or_marked_obsolete()
    {
        string root = Path.Join(_machine.Profile, ".vscode", "extensions");
        MachineFixture.Allocate(Path.Join(root, "ms-python.python-2024.1.0", "a.js"), 15 * Mb);
        MachineFixture.Allocate(Path.Join(root, "ms-python.python-2024.2.0", "a.js"), 15 * Mb);
        MachineFixture.Allocate(Path.Join(root, "acme.tool-1.0.0", "a.js"), 5 * Mb);
        MachineFixture.Write(Path.Join(root, "extensions.json"),
            """[{"identifier":{"id":"ms-python.python"},"relativeLocation":"ms-python.python-2024.2.0"},{"identifier":{"id":"acme.tool"},"relativeLocation":"acme.tool-1.0.0"}]""");
        MachineFixture.Write(Path.Join(root, ".obsolete"), """{"acme.tool-1.0.0":true}""");
        MachineFixture.Allocate(Path.Join(_machine.Profile, ".cursor", "extensions", "x.y-1.0.0", "a"), 15 * Mb); // no list: left alone

        Finding finding = Only(await _machine.ScanAsync([Category.IdeVersions]), "editor-extensions");

        Assert.Equal("Old VS Code extension versions", finding.Title);
        Assert.Equal([Path.Join(root, "acme.tool-1.0.0"), Path.Join(root, "ms-python.python-2024.1.0")], finding.Paths.Order(StringComparer.OrdinalIgnoreCase));
        PackPathRuleTests.AssertExact(finding, 20 * Mb, output);
    }

    [Fact]
    public async Task Android_images_no_emulator_uses()
    {
        string images = Path.Join(_machine.Local, "Android", "Sdk", "system-images");
        MachineFixture.Allocate(Path.Join(images, "android-30", "google_apis", "x86_64", "system.img"), 110 * Mb);
        MachineFixture.Allocate(Path.Join(images, "android-34", "google_apis", "x86_64", "system.img"), 110 * Mb);
        MachineFixture.Allocate(Path.Join(images, "android-34", "default", "x86_64", "system.img"), 110 * Mb);
        string avds = Path.Join(_machine.Profile, ".android", "avd");
        MachineFixture.Write(Path.Join(avds, "Pixel_8.avd", "config.ini"), "hw.ram=2048\r\nimage.sysdir.1=system-images\\android-34\\google_apis\\x86_64\\\r\n");
        MachineFixture.Write(Path.Join(avds, "Pixel_8.ini"), $"avd.ini.encoding=UTF-8\r\npath={Path.Join(avds, "Pixel_8.avd")}\r\n");

        ScanResult result = await _machine.ScanAsync([Category.Android]);
        Log(result);

        var findings = result.Findings.Where(f => f.RuleId == "android-system-images").OrderBy(f => f.Title, StringComparer.Ordinal).ToList();
        Assert.Equal(["Android API 30 system image", "Android API 34 system image"], findings.Select(f => f.Title));
        Assert.Equal([Path.Join(images, "android-30")], findings[0].Paths);
        Assert.Equal([Path.Join(images, "android-34", "default", "x86_64")], findings[1].Paths);
        Assert.Contains(findings[0].Checks, c => c.Text.Contains("Your emulators (Pixel_8) use API 34", StringComparison.Ordinal));
        PackPathRuleTests.AssertExact(findings[0], 110 * Mb, output);
        PackPathRuleTests.AssertExact(findings[1], 110 * Mb, output);
    }

    [Fact]
    public async Task Old_jdks_by_their_release_file_user_and_machine()
    {
        string user8 = Path.Join(_machine.Profile, ".jdks", "corretto-1.8.0_392");
        MachineFixture.Allocate(Path.Join(user8, "lib", "rt.jar"), 60 * Mb);
        MachineFixture.Write(Path.Join(user8, "release"), "JAVA_VERSION=\"1.8.0_392\"\n");
        string user17 = Path.Join(_machine.Profile, ".jdks", "corretto-17.0.9");
        MachineFixture.Allocate(Path.Join(user17, "lib", "modules"), 60 * Mb);
        MachineFixture.Write(Path.Join(user17, "release"), "JAVA_VERSION=\"17.0.9\"\n");
        string machine8 = Path.Join(ProgramFiles, "Java", "jre-1.8");
        MachineFixture.Allocate(Path.Join(machine8, "lib", "rt.jar"), 60 * Mb);
        MachineFixture.Write(Path.Join(machine8, "release"), "JAVA_VERSION=\"1.8.0_401\"\n");
        MachineFixture.Allocate(Path.Join(ProgramFiles, "Java", "no-release", "lib", "x"), 60 * Mb);
        _machine.Environment.Variables["JAVA_HOME"] = machine8;

        ScanResult result = await _machine.ScanAsync([Category.Toolchains]);
        Log(result);

        Finding user = Only(result, "old-jdks-user");
        Assert.Equal("Java 8 (corretto-1.8.0_392)", user.Title);
        Assert.Contains(user.Checks, c => c.Text == "JAVA_HOME doesn't point to it");
        PackPathRuleTests.AssertExact(user, 60 * Mb, output);
        Finding machine = Only(result, "old-jdks-machine");
        Assert.Equal(Risk.NeedsAdmin, machine.Risk);
        Assert.Contains(machine.Checks, c => c.Status == CheckStatus.Warning && c.Text.Contains("JAVA_HOME points to it", StringComparison.Ordinal));
        Assert.Contains("Java 17", machine.Explanation!.Why, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nvm_keeps_the_active_version_and_never_touches_its_link()
    {
        string nvm = Path.Join(_machine.Roaming, "nvm");
        foreach ((string v, long mb) in new[] { ("v18.17.0", 40L), ("v18.19.0", 40L), ("v18.20.4", 1L), ("v20.11.0", 1L) })
        {
            MachineFixture.Allocate(Path.Join(nvm, v, "node.exe"), mb * Mb);
        }

        string link = Path.Join(ProgramFiles, "nodejs");
        Directory.CreateDirectory(ProgramFiles);
        Ntfs.CreateJunction(link, Path.Join(nvm, "v18.17.0"));

        ScanResult result = await _machine.ScanAsync([Category.Toolchains]);
        Log(result);

        Finding finding = Only(result, "nvm-old-node");
        Assert.Equal([Path.Join(nvm, "v18.19.0")], finding.Paths);
        Assert.Contains(finding.Checks, c => c.Text.Contains("The active version is v18.17.0", StringComparison.Ordinal));
        Assert.True(new DirectoryInfo(link).LinkTarget is not null);
        PackPathRuleTests.AssertExact(finding, 40 * Mb, output);
    }

    [Fact]
    public async Task Fnm_volta_and_pyenv_keep_what_is_in_use()
    {
        string fnm = Path.Join(_machine.Roaming, "fnm");
        foreach (string v in new[] { "v20.1.0", "v20.3.0", "v20.5.0" })
        {
            MachineFixture.Allocate(Path.Join(fnm, "node-versions", v, "installation", "node.exe"), 40 * Mb);
        }

        Directory.CreateDirectory(Path.Join(fnm, "aliases"));
        Ntfs.CreateJunction(Path.Join(fnm, "aliases", "default"), Path.Join(fnm, "node-versions", "v20.1.0", "installation"));

        string volta = Path.Join(_machine.Local, "Volta");
        foreach (string v in new[] { "18.0.0", "18.2.0", "18.5.0" })
        {
            MachineFixture.Allocate(Path.Join(volta, "tools", "image", "node", v, "node.exe"), 40 * Mb);
        }

        MachineFixture.Write(Path.Join(volta, "tools", "user", "platform.json"), """{"node":{"runtime":"18.0.0","npm":null}}""");

        string pyenv = Path.Join(_machine.Profile, ".pyenv", "pyenv-win");
        foreach (string v in new[] { "3.11.1", "3.11.2", "3.11.4", "3.11.9", "3.12.1" })
        {
            MachineFixture.Allocate(Path.Join(pyenv, "versions", v, "python.exe"), 40 * Mb);
        }

        MachineFixture.Write(Path.Join(pyenv, "version"), "3.11.1\n");
        MachineFixture.Write(Path.Join(_machine.Profile, "source", "app", ".python-version"), "3.11.4\n");

        ScanResult result = await _machine.ScanAsync([Category.Toolchains]);
        Log(result);

        Assert.Equal([Path.Join(fnm, "node-versions", "v20.3.0")], Only(result, "fnm-old-node").Paths);
        Assert.Equal([Path.Join(volta, "tools", "image", "node", "18.2.0")], Only(result, "volta-old-node").Paths);
        Finding python = Only(result, "pyenv-old-python");
        Assert.Equal([Path.Join(pyenv, "versions", "3.11.2")], python.Paths);
        Assert.Contains("3.11.9", python.Explanation!.Why, StringComparison.Ordinal);
        PackPathRuleTests.AssertExact(python, 40 * Mb, output);
    }

    [Fact]
    public async Task Rustup_offers_only_unpinned_numbered_toolchains()
    {
        string rustup = Path.Join(_machine.Profile, ".rustup");
        foreach (string t in new[] { "stable-x86_64-pc-windows-msvc", "nightly-x86_64-pc-windows-msvc", "1.70.0-x86_64-pc-windows-msvc", "1.75.0-x86_64-pc-windows-msvc" })
        {
            MachineFixture.Allocate(Path.Join(rustup, "toolchains", t, "bin", "rustc.exe"), 110 * Mb);
        }

        MachineFixture.Write(Path.Join(rustup, "settings.toml"), "default_toolchain = \"stable-x86_64-pc-windows-msvc\"\nversion = \"12\"\n\n[overrides]\n");
        MachineFixture.Write(Path.Join(_machine.Profile, "source", "svc", "rust-toolchain.toml"), "[toolchain]\nchannel = \"1.75.0\"\n");

        Finding finding = Only(await _machine.ScanAsync([Category.Toolchains]), "rustup-toolchains");

        Assert.Equal([Path.Join(rustup, "toolchains", "1.70.0-x86_64-pc-windows-msvc")], finding.Paths);
        Assert.Equal(["rustup", "toolchain", "uninstall", "1.70.0-x86_64-pc-windows-msvc"], finding.Actions[0].Command);
        PackPathRuleTests.AssertExact(finding, 110 * Mb, output);
    }

    [Fact]
    public async Task Large_files_say_which_date_was_used_last_access_off()
    {
        DateTime now = DateTime.UtcNow;
        string installer = MachineFixture.Allocate(Path.Join(_machine.Environment.Folders["Downloads"], "setup-old.exe"), 30 * Mb);
        MachineFixture.Age(installer, now.AddDays(-20));
        MachineFixture.Allocate(Path.Join(_machine.Environment.Folders["Downloads"], "fresh.msi"), 30 * Mb);
        string iso = MachineFixture.Allocate(Path.Join(_machine.Environment.Folders["Desktop"], "ubuntu.iso"), 110 * Mb);
        MachineFixture.Age(iso, now.AddDays(-40));
        string video = MachineFixture.Allocate(Path.Join(_machine.Environment.Folders["Documents"], "talks", "talk.mp4"), 210 * Mb);
        MachineFixture.Age(video, now.AddDays(-200));
        string hidden = MachineFixture.Allocate(Path.Join(_machine.Environment.Folders["Documents"], "hidden.bin"), 210 * Mb);
        MachineFixture.Age(hidden, now.AddDays(-200));
        File.SetAttributes(hidden, FileAttributes.Hidden);
        _machine.Registry.Set(RegistryHive.LocalMachine, RegistryView.Registry64, Core.Packs.Detectors.LargeOldFilesDetector.FileSystemKey,
            ("NtfsDisableLastAccessUpdate", unchecked((int)0x80000003)));

        ScanResult result = await _machine.ScanAsync([Category.LargeFiles]);
        Log(result);

        Finding exe = Only(result, "old-installers");
        Assert.Equal([installer], exe.Paths);
        Assert.Contains("last changed 20 days ago", exe.Explanation!.Why, StringComparison.Ordinal);
        Assert.Contains("does not record when files are opened", exe.Explanation.Before, StringComparison.Ordinal);
        PackPathRuleTests.AssertExact(exe, 30 * Mb, output);
        Assert.Equal([iso], Only(result, "old-disk-images").Paths);
        Finding big = Only(result, "large-old-files");
        Assert.Equal([video], big.Paths);
        Assert.Equal(Risk.HoldsData, big.Risk);
        Assert.Equal([CleanActionKind.Trash], big.Actions.Select(a => a.Kind));
    }

    [Fact]
    public async Task Large_files_opened_recently_stay_when_last_access_is_recorded()
    {
        string downloads = _machine.Environment.Folders["Downloads"];
        string opened = MachineFixture.Allocate(Path.Join(downloads, "opened.exe"), 30 * Mb);
        string untouched = MachineFixture.Allocate(Path.Join(downloads, "untouched.exe"), 30 * Mb);
        MachineFixture.Age(opened, DateTime.UtcNow.AddDays(-30));
        MachineFixture.Age(untouched, DateTime.UtcNow.AddDays(-30));
        File.SetLastAccessTimeUtc(opened, DateTime.UtcNow.AddDays(-2));
        File.SetLastAccessTimeUtc(untouched, DateTime.UtcNow.AddDays(-30));
        _machine.Registry.Set(RegistryHive.LocalMachine, RegistryView.Registry64, Core.Packs.Detectors.LargeOldFilesDetector.FileSystemKey,
            ("NtfsDisableLastAccessUpdate", unchecked((int)0x80000002)));

        Finding finding = Only(await _machine.ScanAsync([Category.LargeFiles]), "old-installers");

        Assert.Equal([untouched], finding.Paths);
        Assert.Contains("last opened", finding.Explanation!.Why, StringComparison.Ordinal);
        Assert.Contains("records when files are opened", finding.Explanation.Before, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Browser_caches_per_profile_and_unused_profiles()
    {
        string chrome = Path.Join(_machine.Local, "Google", "Chrome", "User Data");
        long longAgo = DateTimeOffset.UtcNow.AddDays(-400).ToUnixTimeSeconds();
        long recent = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
        string localState = "{\"profile\":{\"last_used\":\"Default\",\"info_cache\":{"
            + "\"Default\":{\"name\":\"Work\",\"user_name\":\"me@example.com\",\"active_time\":" + recent.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".5},"
            + "\"Profile 2\":{\"name\":\"Old\",\"active_time\":" + longAgo.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}}}";
        MachineFixture.Write(Path.Join(chrome, "Local State"), localState);
        MachineFixture.Allocate(Path.Join(chrome, "Default", "Cache", "Cache_Data", "data_1"), 60 * Mb);
        MachineFixture.Allocate(Path.Join(chrome, "Default", "Code Cache", "js", "a"), 10 * Mb);
        MachineFixture.Allocate(Path.Join(chrome, "Default", "History"), 30 * Mb);
        MachineFixture.Allocate(Path.Join(chrome, "Profile 2", "Cache", "Cache_Data", "data_1"), 60 * Mb);
        MachineFixture.Allocate(Path.Join(chrome, "Profile 2", "History"), 25 * Mb);
        MachineFixture.Allocate(Path.Join(chrome, "Crashpad", "reports", "a"), 60 * Mb);
        string edge = Path.Join(_machine.Local, "Microsoft", "Edge", "User Data");
        MachineFixture.Allocate(Path.Join(edge, "Default", "Cache", "Cache_Data", "data_1"), 60 * Mb);
        string brave = Path.Join(_machine.Local, "BraveSoftware", "Brave-Browser", "User Data");
        MachineFixture.Allocate(Path.Join(brave, "Default", "GPUCache", "data_1"), 60 * Mb);
        _machine.Processes.Add("msedge");

        ScanResult result = await _machine.ScanAsync([Category.Browsers]);
        Log(result);

        var caches = result.Findings.Where(f => f.RuleId == "chrome-cache").OrderBy(f => f.Title, StringComparer.Ordinal).ToList();
        Assert.Equal(["Chrome cache (Old)", "Chrome cache (Work)"], caches.Select(f => f.Title));
        Assert.Equal([Path.Join(chrome, "Default", "Cache"), Path.Join(chrome, "Default", "Code Cache")], caches[1].Paths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Contains("the profile Chrome opens with", caches[1].Subtitle, StringComparison.Ordinal);
        PackPathRuleTests.AssertExact(caches[1], 70 * Mb, output);
        PackPathRuleTests.AssertExact(caches[0], 60 * Mb, output);

        Finding edgeCache = Only(result, "edge-cache");
        Assert.Equal(["msedge"], edgeCache.BlockingApps);
        Assert.Contains(edgeCache.Checks, c => c.Status == CheckStatus.Warning && c.Text.Contains("msedge is running", StringComparison.Ordinal));
        PackPathRuleTests.AssertExact(Only(result, "brave-cache"), 60 * Mb, output);

        Finding profile = Only(result, "chrome-profiles");
        Assert.Equal("Chrome profile \u201cOld\u201d", profile.Title);
        Assert.Equal([Path.Join(chrome, "Profile 2")], profile.Paths);
        Assert.Equal([CleanActionKind.Manual], profile.Actions.Select(a => a.Kind));
        Assert.Contains(profile.Checks, c => c.Status == CheckStatus.Warning && c.Text.Contains("Not signed in", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Findings, f => f.RuleId == "edge-profiles");
    }

    [Fact]
    public async Task Ollama_models_count_only_the_layers_they_alone_use()
    {
        string models = Path.Join(_machine.Profile, ".ollama", "models");
        string library = Path.Join(models, "manifests", "registry.ollama.ai", "library");
        MachineFixture.Write(Path.Join(library, "llama3", "latest"), """{"config":{"digest":"sha256:cfg1","size":1},"layers":[{"digest":"sha256:aaa"},{"digest":"sha256:bbb"}]}""");
        MachineFixture.Write(Path.Join(library, "llama3-tuned", "latest"), """{"config":{"digest":"sha256:cfg2","size":1},"layers":[{"digest":"sha256:aaa"},{"digest":"sha256:ccc"}]}""");
        MachineFixture.Allocate(Path.Join(models, "blobs", "sha256-aaa"), 150 * Mb);
        MachineFixture.Allocate(Path.Join(models, "blobs", "sha256-bbb"), 120 * Mb);
        MachineFixture.Allocate(Path.Join(models, "blobs", "sha256-ccc"), 110 * Mb);
        MachineFixture.Write(Path.Join(models, "blobs", "sha256-cfg1"), "{}");
        MachineFixture.Write(Path.Join(models, "blobs", "sha256-cfg2"), "{}");

        ScanResult result = await _machine.ScanAsync([Category.AiModels]);
        Log(result);

        var findings = result.Findings.Where(f => f.RuleId == "ollama-models").OrderBy(f => f.Title, StringComparer.Ordinal).ToList();
        Assert.Equal(["Ollama model llama3-tuned:latest", "Ollama model llama3:latest"], findings.Select(f => f.Title));
        Assert.Equal((120 * Mb) + 2, findings[1].Size);
        Assert.Equal((110 * Mb) + 2, findings[0].Size);
        Assert.Equal(SizeMeasurement.Reported, findings[1].Measurement);
        Assert.Equal(["ollama", "rm", "llama3:latest"], findings[1].Actions[0].Command);
        Assert.Contains($"{SizeText.Format(150 * Mb)} more is shared", findings[1].Explanation!.Why, StringComparison.Ordinal);
        Assert.Empty(findings[1].Paths);
    }

    [Fact]
    public async Task Scheduled_tasks_whose_program_is_gone_from_task_files()
    {
        string tasks = Path.Join(_machine.Root, "Windows", "System32", "Tasks");
        string here = Touch(Path.Join(ProgramFiles, "Here", "here.exe"));
        string gone = Path.Join(ProgramFiles, "Gone", "updater.exe");
        MachineFixture.Write(Path.Join(tasks, "GoneUpdater"), TaskXml(gone));
        MachineFixture.Write(Path.Join(tasks, "HereUpdater"), TaskXml(here));
        MachineFixture.Write(Path.Join(tasks, "Vendor", "UsesPath"), TaskXml("notepad.exe"));
        MachineFixture.Write(Path.Join(tasks, "Microsoft", "Windows", "Gone"), TaskXml(gone));

        ScanResult result = await _machine.ScanAsync([Category.Leftovers]);
        Log(result);

        Finding task = Only(result, "orphaned-scheduled-tasks");
        Assert.Equal(@"Scheduled task \GoneUpdater", task.Title);
        Assert.Equal(0, task.Size);
        Assert.Equal(["schtasks", "/delete", "/tn", @"\GoneUpdater", "/f"], task.Actions[0].Command);
        Assert.Contains(task.Checks, c => c.Text.Contains("the task files in", StringComparison.Ordinal));
        PackPathRuleTests.AssertExplained(task);
    }

    [Fact]
    public async Task Scheduled_tasks_from_schtasks_when_the_task_folder_cannot_be_read()
    {
        string schtasks = Touch(Path.Join(_machine.Root, "Windows", "System32", "schtasks.exe"));
        string gone = Path.Join(ProgramFiles, "Gone", "updater.exe");
        _machine.Machine.Commands[schtasks + " /query /xml ONE"] = FixedMachineQueries.Output(
            "<?xml version=\"1.0\"?>\n<Tasks>\n<!-- \\Gone -->\n" + TaskXml(gone).Replace("<?xml version=\"1.0\"?>", string.Empty, StringComparison.Ordinal)
            + "\n<!-- \\Microsoft\\X -->\n" + TaskXml(gone).Replace("<?xml version=\"1.0\"?>", string.Empty, StringComparison.Ordinal) + "\n</Tasks>");

        Finding task = Only(await _machine.ScanAsync([Category.Leftovers]), "orphaned-scheduled-tasks");

        Assert.Equal(@"\Gone", task.Facts["task"]);
        Assert.Contains(task.Checks, c => c.Text.Contains("schtasks /query", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Startup_entries_whose_program_is_gone()
    {
        string here = Touch(Path.Join(ProgramFiles, "Here", "here.exe"));
        string gone = Path.Join(ProgramFiles, "Gone App", "gone.exe");
        _machine.Registry.Set(RegistryHive.CurrentUser, RegistryView.Registry64, OrphanedRunEntriesDetector.RunKey,
            ("GoneApp", $"\"{gone}\" --tray"), ("HereApp", $"\"{here}\""), ("Helper", "rundll32.exe shell32.dll,Control_RunDLL"));

        Finding entry = Only(await _machine.ScanAsync([Category.Leftovers]), "orphaned-startup-entries");

        Assert.Equal("Startup entry \u201cGoneApp\u201d", entry.Title);
        Assert.Equal(gone, entry.Facts["program"]);
        Assert.Equal(["reg", "delete", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "/v", "GoneApp", "/f"], entry.Actions[0].Command);
    }

    [Fact]
    public async Task Program_data_leftovers_are_commands_only()
    {
        string folder = Path.Join(_machine.ProgramData, "VirtualBox");
        MachineFixture.Allocate(Path.Join(folder, "VBoxSDS.log"), 15 * Mb);

        Finding finding = Only(await _machine.ScanAsync([Category.Leftovers]), "orphaned-program-data");

        Assert.Equal(Risk.NeedsAdmin, finding.Risk);
        Assert.Equal([CleanActionKind.Manual], finding.Actions.Select(a => a.Kind));
        Assert.Equal($"Remove-Item -LiteralPath '{folder}' -Recurse", finding.Actions[0].Command![^1]);
        PackPathRuleTests.AssertExact(finding, 15 * Mb, output);
    }

    [Fact]
    public async Task Recycle_bin_size_is_what_windows_reports()
    {
        _machine.Machine.Drives.Add(@"C:\");
        _machine.Machine.Drives.Add(@"D:\");
        _machine.Machine.Bins[@"C:\"] = new RecycleBinUsage(300_000_000, 12);
        _machine.Machine.Bins[@"D:\"] = new RecycleBinUsage(0, 0);

        Finding bin = Only(await _machine.ScanAsync([Category.Windows]), "recycle-bin");

        Assert.Equal("Recycle Bin on C:", bin.Title);
        Assert.Equal(300_000_000, bin.Size);
        Assert.Equal(SizeMeasurement.Reported, bin.Measurement);
        Assert.Contains("12 items", bin.Explanation!.Why, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JetBrains_and_Android_Studio_versions_still_installed_side_by_side_stay()
    {
        string roaming = Path.Join(_machine.Roaming, "JetBrains");
        MachineFixture.Allocate(Path.Join(roaming, "IntelliJIdea2024.1", "options", "a.xml"), 12 * Mb);
        MachineFixture.Allocate(Path.Join(roaming, "IntelliJIdea2024.3", "options", "a.xml"), 12 * Mb);
        MachineFixture.Allocate(Path.Join(roaming, "PyCharm2023.3", "options", "a.xml"), 12 * Mb);
        MachineFixture.Allocate(Path.Join(roaming, "PyCharm2024.2", "options", "a.xml"), 12 * Mb);
        _machine.Registry.Set(RegistryHive.CurrentUser, RegistryView.Registry64, UninstallCollector.KeyPath + @"\IntelliJ IDEA 2024.1",
            ("DisplayName", "IntelliJ IDEA 2024.1.4"), ("DisplayVersion", "241.18034.62"));
        string google = Path.Join(_machine.Roaming, "Google");
        MachineFixture.Allocate(Path.Join(google, "AndroidStudio2023.3", "options", "a.xml"), 12 * Mb);
        MachineFixture.Allocate(Path.Join(google, "AndroidStudio2024.2", "options", "a.xml"), 12 * Mb);

        ScanResult result = await _machine.ScanAsync([Category.IdeVersions]);
        Log(result);

        Assert.Equal(["PyCharm 2023.3 settings and caches"], result.Findings.Where(f => f.RuleId == "jetbrains-old").Select(f => f.Title));
        Assert.Contains(result.Report.Checks.Single(c => c.Id == "jetbrains-old").Notes, n => n.Contains("IntelliJ IDEA 2024.1.4 is installed", StringComparison.Ordinal));
        Finding studio = Only(result, "android-studio-old");
        Assert.Equal([Path.Join(google, "AndroidStudio2023.3")], studio.Paths);
        PackPathRuleTests.AssertExact(studio, 12 * Mb, output);
    }

    [Fact]
    public async Task Project_build_output_only_when_confirmed()
    {
        string src = Path.Join(_machine.Root, "src");
        DateTime old = DateTime.UtcNow.AddDays(-60);
        string next = Path.Join(src, "site");
        MachineFixture.Write(Path.Join(next, "package.json"), "{}");
        MachineFixture.Write(Path.Join(next, "next.config.mjs"), "export default {}");
        MachineFixture.Allocate(Path.Join(next, ".next", "cache", "webpack", "a.pack"), 60 * Mb);
        MachineFixture.Write(Path.Join(next, ".next", "BUILD_ID"), "x");
        string vite = Path.Join(src, "app");
        MachineFixture.Write(Path.Join(vite, "vite.config.ts"), "export default {}");
        MachineFixture.Write(Path.Join(vite, "dist", "index.html"), "<html/>");
        MachineFixture.Allocate(Path.Join(vite, "dist", "assets", "index.js"), 60 * Mb);
        string notes = Path.Join(src, "notes");
        MachineFixture.Write(Path.Join(notes, "vite.config.ts"), "export default {}");
        MachineFixture.Allocate(Path.Join(notes, "dist", "manual.pdf"), 60 * Mb);
        string gradle = Path.Join(src, "android-app");
        MachineFixture.Write(Path.Join(gradle, "settings.gradle.kts"), "x");
        MachineFixture.Allocate(Path.Join(gradle, "build", "intermediates", "dex", "a.dex"), 60 * Mb);
        MachineFixture.Age(src, old);

        ScanResult result = await _machine.ScanAsync([Category.Projects], projectRoots: [src]);
        Log(result);

        Assert.Equal([".next in site", "build in android-app", "dist in app"], result.Findings.Select(f => f.Title).Order(StringComparer.Ordinal));
        Finding site = result.Findings.Single(f => f.Title == ".next in site");
        Assert.Equal("npm run build", site.Facts["reinstall"]);
        PackPathRuleTests.AssertExact(site, 60 * Mb, output);
    }

    private static string TaskXml(string command) => $"""
        <?xml version="1.0"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Actions Context="Author"><Exec><Command>"{command}"</Command></Exec></Actions>
        </Task>
        """;

    private static string Touch(string path) => MachineFixture.Write(path, "MZ");

    private static Finding Only(ScanResult result, string ruleId) => Assert.Single(result.Findings, f => f.RuleId == ruleId);

    private void Log(ScanResult result)
    {
        foreach (Finding f in result.Findings)
        {
            output.WriteLine($"{f.RuleId}: {f.Title} = {f.Size:N0} ({f.Risk}, {f.Measurement}) {string.Join(" | ", f.Paths)}");
            output.WriteLine($"    why: {f.Explanation?.Why}");
            foreach (Check c in f.Checks)
            {
                output.WriteLine($"    [{c.Status}] {c.Text}");
            }
        }

        foreach (CheckReport check in result.Report.Checks.Where(c => c.Notes.Count > 0))
        {
            output.WriteLine($"  notes {check.Id}: {string.Join(" | ", check.Notes)}");
        }
    }
}
