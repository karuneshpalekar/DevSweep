using DevSweep.Core.Cleaning;
using DevSweep.Core.Platform;
using DevSweep.Core.Rules;
using DevSweep.Core.Scanning;
using Xunit;

namespace DevSweep.Accuracy.Rules;

/// <summary>
/// Every rule of the Windows packs that finds folders by path (<c>paths</c> and
/// <c>versionedSiblings</c>), on a fixture machine on real NTFS: found where its location says,
/// measured exactly (allocation), explained with every placeholder filled.
/// Files are written as <c>{Root}\relative|MiB</c>; roots are the fixture machine's folders.
/// </summary>
public sealed class PackPathRuleTests(ITestOutputHelper output) : IDisposable
{
    private const long Mb = 1024 * 1024;
    private readonly MachineFixture _machine = Windows(new MachineFixture("packs-paths"));

    public void Dispose() => _machine.Dispose();

    [Theory]
    [InlineData("npx-cache", @"{Local}\npm-cache\_npx\1a2b\node_modules\x.js|60", @"{Local}\npm-cache\_npx")]
    [InlineData("pnpm-store", @"{Local}\pnpm\store\v10\files\00\abc|210", @"{Local}\pnpm\store")]
    [InlineData("yarn-cache", @"{Local}\Yarn\Cache\v6\npm-a|30;{Local}\Yarn\Berry\cache\b.zip|30", @"{Local}\Yarn\Cache;{Local}\Yarn\Berry\cache")]
    [InlineData("uv-cache", @"{Local}\uv\cache\wheels-v1\a.whl|60", @"{Local}\uv\cache")]
    [InlineData("nuget-global-packages", @"{Profile}\.nuget\packages\newtonsoft.json\13.0.3\lib.dll|510", @"{Profile}\.nuget\packages")]
    [InlineData("nuget-other-caches", @"{Local}\NuGet\plugins-cache\a|25", @"{Local}\NuGet\plugins-cache")]
    [InlineData("gradle-daemon-logs", @"{Profile}\.gradle\daemon\8.5\daemon-1.out.log|110", @"{Profile}\.gradle\daemon")]
    [InlineData("maven-repository", @"{Profile}\.m2\repository\org\lib\1.0\lib.jar|510", @"{Profile}\.m2\repository")]
    [InlineData("cargo-cache", @"{Profile}\.cargo\registry\cache\index\a.crate|60;{Profile}\.cargo\registry\src\index\a\lib.rs|50", @"{Profile}\.cargo\registry\cache;{Profile}\.cargo\registry\src")]
    [InlineData("go-module-cache", @"{Profile}\go\pkg\mod\github.com\a\b.go|210", @"{Profile}\go\pkg\mod")]
    [InlineData("go-build-cache", @"{Local}\go-build\00\a-d|110", @"{Local}\go-build")]
    [InlineData("composer-cache", @"{Local}\Composer\files\a.zip|60", @"{Local}\Composer")]
    [InlineData("chocolatey-cache", @"{Temp}\chocolatey\git\2.47\git.exe|60", @"{Temp}\chocolatey")]
    [InlineData("scoop-cache", @"{Profile}\scoop\cache\git#2.47#a.7z|60", @"{Profile}\scoop\cache")]
    [InlineData("winget-downloads", @"{Temp}\WinGet\Git.Git.2.47\installer.exe|60", @"{Temp}\WinGet")]
    [InlineData("error-reports-user", @"{Local}\Microsoft\Windows\WER\ReportArchive\AppCrash_x\Report.wer|25", @"{Local}\Microsoft\Windows\WER\ReportArchive")]
    [InlineData("error-reports-machine", @"{ProgramData}\Microsoft\Windows\WER\ReportQueue\Kernel_x\dump.dmp|60", @"{ProgramData}\Microsoft\Windows\WER\ReportQueue")]
    [InlineData("thumbnail-cache", @"{Local}\Microsoft\Windows\Explorer\thumbcache_256.db|210;{Local}\Microsoft\Windows\Explorer\iconcache_16.db|10", @"{Local}\Microsoft\Windows\Explorer\thumbcache_256.db")]
    [InlineData("shader-caches", @"{Local}\D3DSCache\a\b.idx|110;{Local}\NVIDIA\DXCache\c.bin|110", @"{Local}\D3DSCache;{Local}\NVIDIA\DXCache")]
    [InlineData("windows-update-cleanup", @"{Root}\Windows\SoftwareDistribution\Download\abc\update.cab|210", @"{Root}\Windows\SoftwareDistribution\Download")]
    [InlineData("chrome-ai-model", @"{Local}\Google\Chrome\User Data\OptGuideOnDeviceModel\2024.9\weights.bin|210", @"{Local}\Google\Chrome\User Data\OptGuideOnDeviceModel")]
    [InlineData("edge-ai-model", @"{Local}\Microsoft\Edge\User Data\EdgeLLMOnDeviceModel\model.bin|210", @"{Local}\Microsoft\Edge\User Data\EdgeLLMOnDeviceModel")]
    [InlineData("huggingface-models", @"{Profile}\.cache\huggingface\hub\models--org--model\blobs\a|210", @"{Profile}\.cache\huggingface\hub\models--org--model")]
    [InlineData("torch-hub", @"{Profile}\.cache\torch\hub\checkpoints\resnet.pth|110", @"{Profile}\.cache\torch\hub")]
    [InlineData("whisper-models", @"{Profile}\.cache\whisper\base.pt|110", @"{Profile}\.cache\whisper")]
    [InlineData("lm-studio-models", @"{Profile}\.lmstudio\models\lmstudio-community\Qwen-GGUF\q4.gguf|210", @"{Profile}\.lmstudio\models\lmstudio-community\Qwen-GGUF")]
    [InlineData("vs-component-model-cache", @"{Local}\Microsoft\VisualStudio\17.0_1a2b3c4d\ComponentModelCache\Microsoft.VisualStudio.Default.cache|25", @"{Local}\Microsoft\VisualStudio\17.0_1a2b3c4d\ComponentModelCache")]
    [InlineData("vs-package-cache", @"{ProgramData}\Microsoft\VisualStudio\Packages\Microsoft.VisualCpp.Tools,version=17\payload.cab|510;{ProgramData}\Microsoft\VisualStudio\Packages\_Instances\1a2b3c4d\state.json|1", @"{ProgramData}\Microsoft\VisualStudio\Packages\Microsoft.VisualCpp.Tools,version=17")]
    [InlineData("resharper-caches", @"{Local}\JetBrains\Transient\ReSharperPlatformVs17\v241_1a2b3c4d\SolutionCaches\_App.1\a|110", @"{Local}\JetBrains\Transient\ReSharperPlatformVs17\v241_1a2b3c4d\SolutionCaches")]
    [InlineData("android-emulator-snapshots", @"{Profile}\.android\avd\Pixel_8.avd\snapshots\default_boot\ram.img|210", @"{Profile}\.android\avd\Pixel_8.avd\snapshots")]
    [InlineData("android-studio-caches", @"{Local}\Google\AndroidStudio2024.2\caches\a|210", @"{Local}\Google\AndroidStudio2024.2\caches")]
    [InlineData("vscode-caches", @"{Roaming}\Code\CachedData\abc123\a.js|210", @"{Roaming}\Code\CachedData")]
    [InlineData("cursor-caches", @"{Roaming}\Cursor\Cache\Cache_Data\a|210", @"{Roaming}\Cursor\Cache")]
    [InlineData("windsurf-caches", @"{Roaming}\Windsurf\CachedExtensionVSIXs\a.vsix|210", @"{Roaming}\Windsurf\CachedExtensionVSIXs")]
    [InlineData("jetbrains-caches", @"{Local}\JetBrains\Rider2024.3\caches\a|110;{Local}\JetBrains\Rider2024.3\index\b|110", @"{Local}\JetBrains\Rider2024.3\caches;{Local}\JetBrains\Rider2024.3\index")]
    [InlineData("gradle-wrappers", @"{Profile}\.gradle\wrapper\dists\gradle-7.6-bin\x\gradle.zip|60;{Profile}\.gradle\wrapper\dists\gradle-8.5-bin\x\a|1;{Profile}\.gradle\wrapper\dists\gradle-8.10-all\x\a|1", @"{Profile}\.gradle\wrapper\dists\gradle-7.6-bin")]
    [InlineData("android-build-tools", @"{Local}\Android\Sdk\build-tools\30.0.3\aapt.exe|60;{Local}\Android\Sdk\build-tools\33.0.0\a|1;{Local}\Android\Sdk\build-tools\34.0.0\a|1;{Local}\Android\Sdk\build-tools\35.0.0\a|1", @"{Local}\Android\Sdk\build-tools\30.0.3")]
    [InlineData("android-platforms", @"{Local}\Android\Sdk\platforms\android-30\android.jar|60;{Local}\Android\Sdk\platforms\android-33\a|1;{Local}\Android\Sdk\platforms\android-34\a|1;{Local}\Android\Sdk\platforms\android-35\a|1", @"{Local}\Android\Sdk\platforms\android-30")]
    [InlineData("android-ndk", @"{Local}\Android\Sdk\ndk\25.1.8937393\a|110;{Local}\Android\Sdk\ndk\26.1.10909125\a|1", @"{Local}\Android\Sdk\ndk\25.1.8937393")]
    [InlineData("windows-sdks", @"{ProgramFilesX86}\Windows Kits\10\Include\10.0.19041.0\um\a.h|60;{ProgramFilesX86}\Windows Kits\10\Lib\10.0.19041.0\um\a.lib|60;{ProgramFilesX86}\Windows Kits\10\Include\10.0.22621.0\a|1;{ProgramFilesX86}\Windows Kits\10\Include\10.0.26100.0\a|1", @"{ProgramFilesX86}\Windows Kits\10\Include\10.0.19041.0;{ProgramFilesX86}\Windows Kits\10\Lib\10.0.19041.0")]
    public async Task Rule_finds_exactly_its_folders(string ruleId, string files, string expected)
    {
        long bytes = 0;
        foreach (string spec in files.Split(';'))
        {
            string[] parts = spec.Split('|');
            long size = long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) * Mb;
            MachineFixture.Allocate(Resolve(parts[0]), size);
            bytes += size;
        }

        ScanResult result = await _machine.ScanAsync();
        Finding finding = Assert.Single(result.Findings, f => f.RuleId == ruleId);
        output.WriteLine($"{finding.Title}: {finding.Size:N0} bytes, {finding.Risk}; {finding.Explanation!.Why}");
        foreach (Check check in finding.Checks)
        {
            output.WriteLine($"  [{check.Status}] {check.Text}");
        }

        Assert.Equal(expected.Split(';').Select(Resolve).Order(StringComparer.OrdinalIgnoreCase), finding.Paths.Order(StringComparer.OrdinalIgnoreCase));
        long offered = files.Split(';').Where(spec => expected.Split(';').Any(e => Resolve(spec.Split('|')[0]).StartsWith(Resolve(e), StringComparison.OrdinalIgnoreCase)))
            .Sum(spec => long.Parse(spec.Split('|')[1], System.Globalization.CultureInfo.InvariantCulture) * Mb);
        AssertExact(finding, offered, output);
        Assert.True(bytes >= offered);
        Assert.Equal(1, result.Report.Checks.Single(c => c.Id == ruleId).Found);
        if (finding.Risk != Risk.Rebuilds)
        {
            Assert.DoesNotContain(finding.Actions, a => a.Kind == CleanActionKind.Delete);
        }

        if (finding.Risk == Risk.NeedsAdmin)
        {
            Assert.All(finding.Actions, a => Assert.Equal(CleanActionKind.Manual, a.Kind));
        }
    }

    [Fact]
    public async Task Crash_dumps_only_after_a_week()
    {
        string old = MachineFixture.Allocate(Path.Join(_machine.Local, "CrashDumps", "app.exe.1234.dmp"), 60 * Mb);
        MachineFixture.Age(old, DateTime.UtcNow.AddDays(-10));
        MachineFixture.Allocate(Path.Join(_machine.Local, "CrashDumps", "app.exe.5678.dmp"), 60 * Mb);

        Finding finding = Assert.Single((await _machine.ScanAsync([Category.Windows])).Findings, f => f.RuleId == "crash-dumps");

        Assert.Equal([old], finding.Paths);
        AssertExact(finding, 60 * Mb, output);
        Assert.Equal([CleanActionKind.Trash], finding.Actions.Select(a => a.Kind));
        Assert.Contains("7 days", finding.Explanation!.Why, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Turning_off_the_packs_module_turns_off_every_pack_rule()
    {
        MachineFixture.Allocate(Path.Join(_machine.Local, "npm-cache", "_npx", "a", "b"), 60 * Mb);
        MachineFixture.Allocate(Path.Join(_machine.Local, "npm-cache", "_cacache", "c"), 60 * Mb);

        ScanResult on = await _machine.ScanAsync();
        ScanResult off = await _machine.ScanAsync(flags: "-module:packs");

        Assert.Contains(on.Findings, f => f.RuleId == "npx-cache");
        Assert.DoesNotContain(off.Findings, f => f.RuleId == "npx-cache");
        Assert.Contains(off.Findings, f => f.RuleId == "npm-cache");
        Assert.Contains("module:packs", off.Report.Disabled);
        Assert.Equal(8, off.Report.Checks.Count);
        output.WriteLine($"checks with the packs: {on.Report.Checks.Count}, without: {off.Report.Checks.Count}");
    }

    /// <summary>The walker's size is the allocation: the bytes, rounded up to clusters, plus the small write.</summary>
    internal static void AssertExact(Finding finding, long allocated, ITestOutputHelper output)
    {
        output.WriteLine($"{finding.Id}: {finding.Size:N0} bytes, {finding.Files} files, {finding.Confidence}, {finding.Measurement}");
        Assert.InRange(finding.Size, allocated, allocated + (64 * 1024 * finding.Paths.Count) + (64 * 1024));
        Assert.Equal(SizeConfidence.Exact, finding.Confidence);
        Assert.Equal(SizeMeasurement.Exact, finding.Measurement);
        Assert.Equal(finding.Size, finding.FreeableBytes);
        AssertExplained(finding);
    }

    /// <summary>Every placeholder of the explanation, title and actions filled in.</summary>
    internal static void AssertExplained(Finding finding)
    {
        Explanation e = finding.Explanation!;
        string all = string.Join("\n", new[] { finding.Title, e.What, e.Why, e.IfDeleted, e.WontLose, e.Before, e.Undo, e.Better }
            .Concat(finding.Actions.SelectMany(a => (a.Command ?? []).Append(a.Detail ?? string.Empty))));
        Assert.DoesNotContain("{{", all, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(e.What));
        Assert.False(string.IsNullOrWhiteSpace(e.Why));
    }

    /// <summary>A machine with a Windows folder (<c>SystemRoot</c>) inside the fixture.</summary>
    internal static MachineFixture Windows(MachineFixture machine)
    {
        machine.Environment.Variables["SystemRoot"] = Path.Join(machine.Root, "Windows");
        return machine;
    }

    private string Resolve(string relative)
    {
        int close = relative.IndexOf('}', StringComparison.Ordinal);
        string root = relative[1..close] switch
        {
            "Local" => _machine.Local,
            "Roaming" => _machine.Roaming,
            "Profile" => _machine.Profile,
            "Temp" => _machine.Temp,
            "ProgramData" => _machine.ProgramData,
            "ProgramFilesX86" => _machine.Environment.Folders["ProgramFilesX86"],
            "Root" => _machine.Root,
            string other => throw new ArgumentException(other),
        };
        return root + relative[(close + 1)..];
    }
}
