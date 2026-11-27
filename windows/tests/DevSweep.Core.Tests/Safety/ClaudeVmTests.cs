using DevSweep.Core.Safety;
using Xunit;

namespace DevSweep.Core.Tests.Safety;

/// <summary>The Claude app's vm_bundles is never removed, whatever a rule or a user pack names (Mac 62-ai-editors).</summary>
public sealed class ClaudeVmTests
{
    private static readonly SafetyContext Context = TestContexts.Create();

    [Theory]
    [InlineData(@"C:\Users\Dev\AppData\Roaming\Claude\vm_bundles")]
    [InlineData(@"C:\Users\Dev\AppData\Roaming\Claude\vm_bundles\claudevm.bundle")]
    [InlineData(@"C:\Users\Dev\AppData\Roaming\claude\VM_BUNDLES\claudevm.bundle\rootfs.img")]
    [InlineData(@"C:\Users\Dev\AppData\Local\Packages\AnthropicPBC.Claude_abc\LocalCache\Roaming\Claude\vm_bundles\x")]
    public void Vm_bundles_and_anything_in_it_are_refused(string path)
    {
        Assert.True(SafePath.TryCanonicalize(path, out string? canonical, out string rejection), rejection);

        SafetyVerdict verdict = SafetyPolicy.Evaluate(Context, canonical);

        Assert.Equal(SafetyRefusal.Protected, verdict.Refusal);
        Assert.Contains("Claude app runs code in", verdict.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\Users\Dev\AppData\Roaming\Claude\Cache")]
    [InlineData(@"C:\Users\Dev\AppData\Roaming\Claude\Code Cache")]
    [InlineData(@"C:\Users\Dev\AppData\Roaming\Other\vm_bundles")]
    public void Claude_caches_and_other_apps_vm_bundles_folders_are_not_affected(string path)
    {
        Assert.True(SafePath.TryCanonicalize(path, out string? canonical, out string rejection), rejection);

        Assert.True(SafetyPolicy.Evaluate(Context, canonical).Allowed);
    }

    [Fact]
    public void The_folders_holding_vm_bundles_include_every_store_build()
    {
        string root = Path.Join(Path.GetTempPath(), "devsweep-claudevm-" + Guid.NewGuid().ToString("N"));
        try
        {
            string roaming = Path.Join(root, "Roaming");
            string local = Path.Join(root, "Local");
            Directory.CreateDirectory(Path.Join(local, "Packages", "AnthropicPBC.Claude_abc123"));
            Directory.CreateDirectory(Path.Join(local, "Packages", "Contoso.Other_xyz"));

            IReadOnlyList<string> paths = SafetyContextFactory.ClaudeVmBundles(roaming, local);

            Assert.Equal(
                [
                    Path.Combine(roaming, "Claude", "vm_bundles"),
                    Path.Combine(local, "Packages", "AnthropicPBC.Claude_abc123", "LocalCache", "Roaming", "Claude", "vm_bundles"),
                ],
                paths);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
