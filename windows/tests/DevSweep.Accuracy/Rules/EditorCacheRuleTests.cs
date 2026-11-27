using DevSweep.Core.Cleaning;
using DevSweep.Core.Inventory.Collectors;
using DevSweep.Core.Rules;
using DevSweep.Core.Scanning;
using DevSweep.Fixtures;
using Microsoft.Win32;
using Xunit;

namespace DevSweep.Accuracy.Rules;

/// <summary>
/// Editor and Claude app caches (Mac 62-ai-editors.yml): offered only while the app is installed
/// (<c>requireApps</c>, checked against the installed-apps list); once it's gone, nothing is offered
/// there and the leftovers check covers its folders.
/// </summary>
public sealed class EditorCacheRuleTests(ITestOutputHelper output) : IDisposable
{
    private const long Mb = 1024 * 1024;
    private readonly MachineFixture _machine = PackPathRuleTests.Windows(new MachineFixture("editor-caches"));

    public void Dispose() => _machine.Dispose();

    public static TheoryData<string, string, string> Editors() => new()
    {
        { "vscode-caches", @"Code\CachedData", "Microsoft Visual Studio Code (User)" },
        { "cursor-caches", @"Cursor\Cache", "Cursor" },
        { "windsurf-caches", @"Windsurf\CachedExtensionVSIXs", "Windsurf" },
        { "vscode-insiders-caches", @"Code - Insiders\Code Cache", "Microsoft Visual Studio Code - Insiders (User)" },
        { "antigravity-caches", @"Antigravity\GPUCache", "Antigravity" },
        { "kiro-caches", @"Kiro\CachedData", "Kiro" },
        { "trae-caches", @"Trae\Cache", "Trae" },
        { "void-caches", @"Void\CachedExtensionVSIXs", "Void" },
        { "claude-desktop-caches", @"Claude\Code Cache", "Claude" },
    };

    [Theory]
    [MemberData(nameof(Editors))]
    public async Task Caches_are_offered_while_the_app_is_installed(string ruleId, string cache, string displayName)
    {
        string folder = Path.Join(_machine.Roaming, cache);
        MachineFixture.Allocate(Path.Join(folder, "data", "a.bin"), 60 * Mb);
        Install(displayName);

        ScanResult result = await _machine.ScanAsync([Category.IdeVersions, Category.Leftovers]);

        Finding finding = Assert.Single(result.Findings, f => f.RuleId == ruleId);
        output.WriteLine($"{finding.Title}: {string.Join(", ", finding.Paths)}");
        Assert.Equal([folder], finding.Paths);
        Assert.Equal(Risk.Rebuilds, finding.Risk);
        Assert.Equal([CleanActionKind.Delete], finding.Actions.Select(a => a.Kind));
        Assert.NotEmpty(finding.Blockers);
        Assert.Contains(finding.Checks, c => c.Text.Contains("is installed", StringComparison.Ordinal));
        PackPathRuleTests.AssertExact(finding, 60 * Mb, output);
        Assert.DoesNotContain(result.Findings, f => f.RuleId != ruleId && f.Paths.Any(p => p.StartsWith(folder, StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [MemberData(nameof(Editors))]
    public async Task Nothing_is_offered_once_the_app_is_gone(string ruleId, string cache, string displayName)
    {
        _ = displayName;
        MachineFixture.Allocate(Path.Join(_machine.Roaming, cache, "data", "a.bin"), 60 * Mb);

        ScanResult result = await _machine.ScanAsync([Category.IdeVersions]);

        Assert.DoesNotContain(result.Findings, f => f.RuleId == ruleId);
        Assert.Contains(result.Report.Checks.Single(c => c.Id == ruleId).Notes, n => n.Contains("isn't installed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_claude_store_build_caches_are_found_but_never_its_vm_bundles()
    {
        string roaming = Path.Join(_machine.Local, "Packages", "AnthropicPBC.Claude_abc123", "LocalCache", "Roaming", "Claude");
        MachineFixture.Allocate(Path.Join(roaming, "Cache", "Cache_Data", "a"), 60 * Mb);
        MachineFixture.Allocate(Path.Join(roaming, "vm_bundles", "claudevm.bundle", "rootfs.img"), 120 * Mb);
        MachineFixture.Allocate(Path.Join(_machine.Roaming, "Claude", "vm_bundles", "claudevm.bundle", "rootfs.img"), 120 * Mb);
        Install("Claude");

        ScanResult result = await _machine.ScanAsync([Category.IdeVersions, Category.Leftovers]);

        Finding finding = Assert.Single(result.Findings, f => f.RuleId == "claude-desktop-caches");
        Assert.Equal([Path.Join(roaming, "Cache")], finding.Paths);
        Assert.DoesNotContain(result.Findings.SelectMany(f => f.Paths), p => p.Contains("vm_bundles", StringComparison.OrdinalIgnoreCase));
    }

    private void Install(string displayName)
        => _machine.Registry.Set(RegistryHive.CurrentUser, RegistryView.Registry64,
            UninstallCollector.KeyPath + @"\" + displayName.Replace(' ', '_'), ("DisplayName", displayName));
}
