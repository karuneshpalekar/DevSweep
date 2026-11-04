using DevSweep.Core.Detectors;
using DevSweep.Core.Inventory.Registry;
using DevSweep.Core.Locations;
using DevSweep.Core.Packs.Detectors;
using DevSweep.Core.Packs.Locations;
using DevSweep.Core.Packs.Tools;
using Microsoft.Win32;
using Xunit;

namespace DevSweep.Core.Tests.Packs;

/// <summary>The parsers behind the Windows packs' detectors: tool output, config files, versions.</summary>
public sealed class PackParsingTests
{
    [Theory]
    [InlineData("1.2GB", 1_200_000_000L)]
    [InlineData("512.3MB (45%)", 512_300_000L)]
    [InlineData("0B", 0L)]
    [InlineData("4.096kB", 4_096L)]
    [InlineData("garbage", 0L)]
    public void Docker_sizes_are_powers_of_1000(string text, long bytes) => Assert.Equal(bytes, DockerDetector.Bytes(text));

    [Fact]
    public void Docker_system_df_rows_are_read_by_type_and_warnings_skipped()
    {
        string output = """
            {"Active":"2","Reclaimable":"1.5GB (60%)","Size":"2.5GB","TotalCount":"7","Type":"Images"}
            WARNING: something
            {"Active":"0","Reclaimable":"300MB","Size":"300MB","TotalCount":"12","Type":"Build Cache"}
            """;

        var rows = DockerDetector.ParseSystemDf(output);

        Assert.Equal(2, rows.Count);
        Assert.Equal("7", rows["Images"]["TotalCount"]);
        Assert.Equal(1_500_000_000L, DockerDetector.Bytes(rows["Images"]["Reclaimable"]));
        Assert.Equal(300_000_000L, DockerDetector.Bytes(rows["build cache"]["Reclaimable"]));
    }

    [Fact]
    public void Wsl_distros_come_from_lxss_with_their_disk_names()
    {
        var registry = new InMemoryRegistry()
            .Set(RegistryHive.CurrentUser, RegistryView.Registry64, VhdxDisksDetector.LxssKey + @"\{a}", ("DistributionName", "Ubuntu"), ("BasePath", @"\\?\C:\Users\Dev\AppData\Local\wsl\{a}"), ("Version", 2))
            .Set(RegistryHive.CurrentUser, RegistryView.Registry64, VhdxDisksDetector.LxssKey + @"\{b}", ("DistributionName", "Old"), ("BasePath", @"C:\wsl\old"), ("Version", 1), ("VhdFileName", "x.vhdx"))
            .Set(RegistryHive.CurrentUser, RegistryView.Registry64, VhdxDisksDetector.LxssKey + @"\{c}", ("BasePath", @"C:\nameless"));

        var distros = VhdxDisksDetector.ReadDistros(registry)!;

        Assert.Equal(2, distros.Count);
        Assert.Equal(new VhdxDisksDetector.Distro("Ubuntu", @"C:\Users\Dev\AppData\Local\wsl\{a}", "ext4.vhdx", 2), distros.Single(d => d.Name == "Ubuntu"));
        Assert.Equal("x.vhdx", distros.Single(d => d.Name == "Old").VhdFileName);
        Assert.Empty(VhdxDisksDetector.ReadDistros(new InMemoryRegistry())!);
        Assert.Null(VhdxDisksDetector.ReadDistros(new InMemoryRegistry().Deny(RegistryHive.CurrentUser, RegistryView.Registry64, VhdxDisksDetector.LxssKey)));
    }

    [Fact]
    public void Wsl_running_list_is_utf16_and_df_gives_used_bytes()
    {
        string utf16AsUtf8 = "\uFEFFU\0b\0u\0n\0t\0u\0\r\0\n\0d\0e\0v\0\r\0\n\0";
        Assert.Equal(["Ubuntu", "dev"], VhdxDisksDetector.ParseRunning(utf16AsUtf8));
        Assert.Equal(123_456_789L, VhdxDisksDetector.ParseDfUsed("    Used\n123456789\n"));
        Assert.Null(VhdxDisksDetector.ParseDfUsed("df: error"));
    }

    [Fact]
    public void Prerelease_versions_are_older_than_their_release()
    {
        string[] versions = ["9.0.100", "9.0.100-rc.2.24474.11", "8.0.404", "9.0.100-preview.7", "8.0.11", "10.0.100"];

        Assert.Equal(["8.0.11", "8.0.404", "9.0.100-preview.7", "9.0.100-rc.2.24474.11", "9.0.100", "10.0.100"], versions.Order(SemVerComparer.Instance));
        Assert.True(SemVerComparer.IsPrerelease("9.0.100-rc.1"));
        Assert.Equal("8", SemVerComparer.Prefix("v8.0.404", 1));
        Assert.Equal("3.12", SemVerComparer.Prefix("3.12.1", 2));
    }

    [Fact]
    public void Global_json_pins_and_feature_bands()
    {
        Assert.Equal("8.0.100", DotnetInstallsDetector.PinnedSdk("{ // pinned\n \"sdk\": { \"version\": \"8.0.100\", \"rollForward\": \"latestPatch\" }, }"));
        Assert.Null(DotnetInstallsDetector.PinnedSdk("""{ "msbuild-sdks": {} }"""));
        Assert.Null(DotnetInstallsDetector.PinnedSdk("not json"));
        Assert.Equal("8.0.4", DotnetInstallsDetector.FeatureBand("8.0.404"));
        Assert.Equal("8.0.4", DotnetInstallsDetector.FeatureBand("8.0.400"));
        Assert.NotEqual(DotnetInstallsDetector.FeatureBand("8.0.304"), DotnetInstallsDetector.FeatureBand("8.0.404"));
    }

    [Fact]
    public void Dotnet_list_output_is_read()
    {
        Assert.Equal(new HashSet<string> { "8.0.404", "9.0.100" }, DotnetInstallsDetector.ParseList("8.0.404 [C:\\Program Files\\dotnet\\sdk]\r\n9.0.100 [C:\\Program Files\\dotnet\\sdk]\r\n").ToHashSet());
        Assert.Contains("Microsoft.NETCore.App 8.0.11", DotnetInstallsDetector.ParseList("Microsoft.NETCore.App 8.0.11 [C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App]"));
    }

    [Theory]
    [InlineData("17.0_1a2b3c4d", true)]
    [InlineData("17.0_1a2b3c4dExp", true)]
    [InlineData("16.0_abcdef01_Config", true)]
    [InlineData("17.0", false)]
    [InlineData("17.0_1a2b3c4", false)]
    [InlineData("Packages", false)]
    public void Visual_studio_instance_folder_names(string name, bool matches) => Assert.Equal(matches, VisualStudioInstancesDetector.FolderName().IsMatch(name));

    [Fact]
    public void Visual_studio_state_json()
    {
        var instance = VisualStudioInstancesDetector.ParseState("1a2b3c4d", """{"installationName":"VisualStudio/17.11.5+35327.3","installationPath":"C:\\VS","installationVersion":"17.11.35327.3"}""");
        Assert.Equal("17.11.35327.3", instance!.Version);
        Assert.Null(VisualStudioInstancesDetector.ParseState("x", "[]"));
    }

    [Fact]
    public void Editor_extension_list_and_obsolete_file()
    {
        string json = """
            [{"identifier":{"id":"ms-python.python"},"version":"2024.2.0","location":{"$mid":1,"path":"/c:/Users/Dev/.vscode/extensions/ms-python.python-2024.2.0","scheme":"file"},"relativeLocation":"ms-python.python-2024.2.0"},
             {"identifier":{"id":"a.b"},"location":{"path":"/c:/Users/Dev/.vscode/extensions/a.b-1.0.0"}}]
            """;

        Assert.Equal(new HashSet<string> { "ms-python.python-2024.2.0", "a.b-1.0.0" }, EditorExtensionsDetector.Referenced(json)!.ToHashSet());
        Assert.Null(EditorExtensionsDetector.Referenced("{}"));
        Assert.Equal(["x.y-1.0.0"], EditorExtensionsDetector.Obsolete("""{"x.y-1.0.0":true,"z-2":false}"""));
        Assert.Empty(EditorExtensionsDetector.Obsolete("broken"));
        Assert.Equal("Cursor", EditorExtensionsDetector.Editor(@"C:\Users\Dev\.cursor\extensions"));
        Assert.Equal("VS Code", EditorExtensionsDetector.Editor(@"C:\Users\Dev\.vscode\extensions"));
    }

    [Theory]
    [InlineData("image.sysdir.1=system-images\\android-34\\google_apis\\x86_64\\", "android-34/google_apis/x86_64")]
    [InlineData("hw.ram=2048\nimage.sysdir.1 = system-images/android-30/default/x86/\n", "android-30/default/x86")]
    [InlineData("hw.ram=2048", null)]
    public void Avd_config_names_its_image(string config, string? image) => Assert.Equal(image, AndroidSystemImagesDetector.ImageOf(config));

    [Theory]
    [InlineData("JAVA_VERSION=\"1.8.0_392\"\nOS_NAME=Windows", 8)]
    [InlineData("IMPLEMENTOR=\"Eclipse Adoptium\"\nJAVA_VERSION=\"17.0.9\"", 17)]
    [InlineData("nothing", null)]
    public void Java_major_comes_from_the_release_file(string release, int? major) => Assert.Equal(major, OldJdksDetector.Major(release));

    [Fact]
    public void Version_manager_settings()
    {
        Assert.Equal(@"D:\nvm", ToolVersionsDetector.NvmSetting("root: D:\\nvm\r\npath: C:\\nvm4w\\nodejs\r\n", "root"));
        Assert.Equal(@"C:\nvm4w\nodejs", ToolVersionsDetector.NvmSetting("root: D:\\nvm\r\npath: C:\\nvm4w\\nodejs\r\n", "path"));
        Assert.Null(ToolVersionsDetector.NvmSetting("arch: 64", "root"));
        Assert.Equal("20.11.0", ToolVersionsDetector.VoltaDefault("""{"node":{"runtime":"20.11.0","npm":null}}"""));
        Assert.Null(ToolVersionsDetector.VoltaDefault("{}"));
    }

    [Fact]
    public void Rustup_settings_and_pins()
    {
        (string? def, IReadOnlyList<string> overrides) = RustToolchainsDetector.ReadSettings("""
            default_host_triple = "x86_64-pc-windows-msvc"
            default_toolchain = "stable-x86_64-pc-windows-msvc"
            version = "12"

            [overrides]
            "C:\\src\\old" = "1.70.0-x86_64-pc-windows-msvc"
            """);
        Assert.Equal("stable-x86_64-pc-windows-msvc", def);
        Assert.Equal(["1.70.0-x86_64-pc-windows-msvc"], overrides);
        Assert.Equal("1.75.0", RustToolchainsDetector.PinnedChannel("[toolchain]\nchannel = \"1.75.0\"\ncomponents = []"));
        Assert.Equal("nightly-2024-01-01", RustToolchainsDetector.PinnedChannel("nightly-2024-01-01\n"));
        Assert.True(RustToolchainsDetector.Matches("1.75.0-x86_64-pc-windows-msvc", "1.75.0"));
        Assert.False(RustToolchainsDetector.Matches("1.75.0-x86_64-pc-windows-msvc", "1.75"));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(unchecked((int)0x80000002), true)]
    [InlineData(unchecked((int)0x80000003), false)]
    [InlineData(unchecked((int)0x80000001), false)]
    public void Ntfs_last_access_setting(int value, bool recorded)
    {
        var registry = new InMemoryRegistry().Set(RegistryHive.LocalMachine, RegistryView.Registry64, LargeOldFilesDetector.FileSystemKey, ("NtfsDisableLastAccessUpdate", value));
        Assert.Equal(recorded, LargeOldFilesDetector.LastAccessRecorded(registry));
        Assert.Null(LargeOldFilesDetector.LastAccessRecorded(new InMemoryRegistry()));
    }

    [Fact]
    public void Ago_is_in_words()
    {
        var now = new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal("today", LargeOldFilesDetector.Ago(now, now));
        Assert.Equal("20 days ago", LargeOldFilesDetector.Ago(now.AddDays(-20), now));
        Assert.Equal("5 months ago", LargeOldFilesDetector.Ago(now.AddDays(-150), now));
        Assert.Equal("3 years ago", LargeOldFilesDetector.Ago(now.AddDays(-1100), now));
    }

    [Fact]
    public void Chromium_local_state()
    {
        var state = ChromiumProfiles.Parse("""
            {"profile":{"last_used":"Default","info_cache":{
              "Default":{"name":"Work","user_name":"me@example.com","active_time":1700000000.5},
              "Profile 2":{"name":"Old","active_time":1600000000}}}}
            """)!;

        Assert.Equal("Default", state.LastUsed);
        Assert.Equal("Work", state.NameOf("Default"));
        Assert.Equal("Guest", state.NameOf("Guest Profile"));
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(1600000000), state.Profiles["Profile 2"].ActiveUtc);
        Assert.Null(state.Profiles["Profile 2"].Account);
        Assert.True(BrowserCachesDetector.IsProfileFolder("Profile 7", ChromiumProfiles.Empty));
        Assert.False(BrowserCachesDetector.IsProfileFolder("Crashpad", ChromiumProfiles.Empty));
        Assert.False(BrowserCachesDetector.IsProfileFolder("Profile x", ChromiumProfiles.Empty));
        Assert.Null(ChromiumProfiles.Parse("{broken"));
    }

    [Fact]
    public void Ollama_manifests()
    {
        Assert.Equal(["sha256-c", "sha256-a", "sha256-b"], OllamaModelsDetector.Blobs("""{"config":{"digest":"sha256:c","size":1},"layers":[{"digest":"sha256:a"},{"digest":"sha256:b"}]}"""));
        Assert.Equal("llama3:latest", OllamaModelsDetector.ModelName(["registry.ollama.ai", "library", "llama3", "latest"]));
        Assert.Equal("me/tuned:v1", OllamaModelsDetector.ModelName(["registry.ollama.ai", "me", "tuned", "v1"]));
        Assert.Equal("hf.co/org/model:Q4", OllamaModelsDetector.ModelName(["hf.co", "org", "model", "Q4"]));
    }

    [Fact]
    public void Scheduled_task_xml_one_file_and_schtasks_listing()
    {
        string one = """
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Actions Context="Author"><Exec><Command>"C:\Program Files\Gone\updater.exe"</Command><Arguments>/silent</Arguments></Exec></Actions>
            </Task>
            """.Replace("encoding=\"UTF-16\"", string.Empty, StringComparison.Ordinal);
        var parsed = OrphanedScheduledTasksDetector.Parse(one, @"\GoneUpdater")!;
        Assert.Equal(@"\GoneUpdater", Assert.Single(parsed).Name);
        Assert.Equal("\"C:\\Program Files\\Gone\\updater.exe\"", Assert.Single(parsed[0].Commands));

        string all = """
            <?xml version="1.0"?>
            <Tasks>
              <!-- \A -->
              <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><Actions><Exec><Command>a.exe</Command></Exec><Exec><Command>b.exe</Command></Exec></Actions></Task>
              <!-- \Folder\B -->
              <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><Actions><ComHandler><ClassId>{x}</ClassId></ComHandler></Actions></Task>
            </Tasks>
            """;
        var tasks = OrphanedScheduledTasksDetector.Parse(all)!;
        Assert.Equal([@"\A", @"\Folder\B"], tasks.Select(t => t.Name));
        Assert.Equal(["a.exe", "b.exe"], tasks[0].Commands);
        Assert.Empty(tasks[1].Commands);
        Assert.Null(OrphanedScheduledTasksDetector.Parse("<Task"));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --minimized", "C:\\Program Files\\App\\app.exe")]
    [InlineData("C:\\Program Files\\App\\app.exe --minimized", "C:\\Program Files\\App\\app.exe")]
    [InlineData("%LOCALAPPDATA%\\App\\app.exe", "C:\\Users\\Dev\\AppData\\Local\\App\\app.exe")]
    [InlineData("%UNKNOWN%\\app.exe", null)]
    [InlineData("rundll32.exe C:\\x.dll,Run", null)]
    [InlineData("\\\\server\\share\\app.exe", null)]
    [InlineData("C:\\tools\\noext", null)]
    public void Program_of_a_command_line(string commandLine, string? program)
    {
        var env = new FixedEnvironment { ReadRealFiles = false };
        env.Variables["LOCALAPPDATA"] = @"C:\Users\Dev\AppData\Local";
        Assert.Equal(program, CommandLines.ProgramPath(commandLine, env));
    }

    [Fact]
    public void Chocolatey_cache_location_setting()
    {
        Assert.Equal(@"D:\choco-cache", PackLocations.ChocolateyCacheLocation("""<config><add key="cacheLocation" value="D:\choco-cache" description="x" /></config>"""));
        Assert.Null(PackLocations.ChocolateyCacheLocation("""<config><add key="cacheLocation" value="" /></config>"""));
    }

    [Fact]
    public void Tools_are_found_on_path_and_never_as_scripts()
    {
        var env = new FixedEnvironment { ReadRealFiles = false };
        env.Variables["PATH"] = @"relative\bin;C:\Tools;""C:\Quoted Dir""";
        env.Files[@"C:\Quoted Dir\docker.exe"] = string.Empty;
        env.Files[@"C:\Fallback\wsl.exe"] = string.Empty;

        Assert.Equal(@"C:\Quoted Dir\docker.exe", ToolLocator.Find(env, "docker.exe"));
        Assert.Equal(@"C:\Fallback\wsl.exe", ToolLocator.Find(env, "wsl.exe", null, @"C:\Fallback\wsl.exe"));
        Assert.Null(ToolLocator.Find(env, "go.exe"));
        Assert.Throws<ArgumentException>(() => ToolLocator.Find(env, "npm.cmd"));
    }

    [Fact]
    public void Fixed_machine_answers_only_what_it_was_given()
    {
        var machine = new FixedMachineQueries();
        machine.Commands[@"C:\docker.exe system df"] = FixedMachineQueries.Output("ok");

        Assert.True(machine.Run([@"C:\docker.exe", "system", "df"], TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken).Succeeded);
        Assert.NotNull(machine.Run(["other"], TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken).StartError);
        Assert.Equal(2, machine.Ran.Count);
    }

    [Fact]
    public void Version_comparer_used_by_guards()
        => Assert.True(VersionComparer.Instance.Compare("2024.10", "2024.9") > 0);
}
