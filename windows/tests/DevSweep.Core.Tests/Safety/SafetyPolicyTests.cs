using DevSweep.Core.Safety;
using Xunit;

namespace DevSweep.Core.Tests.Safety;

public sealed class SafetyPolicyTests
{
    private static readonly SafetyContext Context = TestContexts.Create();

    private static SafetyVerdict Evaluate(string path)
    {
        Assert.True(SafePath.TryCanonicalize(path, out string? canonical, out string rejection), rejection);
        return SafetyPolicy.Evaluate(Context, canonical);
    }

    [Theory]
    [InlineData(@"C:\Users\Dev\AppData\Local\npm-cache\_cacache")]
    [InlineData(@"C:\Users\Dev\.gradle\caches\8.5")]
    [InlineData(@"C:\Users\Dev\.nuget\packages")]
    [InlineData(@"C:\Users\Dev\.konan")]
    [InlineData(@"c:\users\dev\APPDATA\local\Temp\old")]
    [InlineData(@"E:\src\app\node_modules")]
    [InlineData(@"C:\Users\Dev\AppData\Local\DevSweep\Quarantine\20260101-000000-abcdef12")]
    public void Allowed(string path) => Assert.True(Evaluate(path).Allowed, Evaluate(path).Reason);

    [Theory]
    [InlineData(@"C:\Users\Dev", SafetyRefusal.Protected)]
    [InlineData(@"C:\Users\Dev\", SafetyRefusal.Protected)]
    [InlineData(@"C:\USERS\DEV\appdata", SafetyRefusal.Protected)]
    [InlineData(@"C:\Users\Dev\AppData\Local\Microsoft", SafetyRefusal.Protected)]
    [InlineData(@"C:\Users\Dev\AppData\Local\Packages", SafetyRefusal.Protected)]
    [InlineData(@"C:\Users\Dev\.ssh", SafetyRefusal.Protected)]
    [InlineData(@"C:\Users\Dev\scoop", SafetyRefusal.Protected)]
    [InlineData(@"D:\OneDrive - Contoso\Documents", SafetyRefusal.Protected)]
    [InlineData(@"D:\OneDrive - Contoso", SafetyRefusal.Protected)]
    [InlineData(@"E:\src", SafetyRefusal.Protected)]
    [InlineData(@"C:\Users\Dev\AppData\Local\DevSweep\Quarantine", SafetyRefusal.Protected)]
    [InlineData(@"C:\Users\Dev\AppData\Local\DevSweep", SafetyRefusal.ContainsProtected)]
    [InlineData(@"C:\Users", SafetyRefusal.ContainsProtected)]
    [InlineData(@"C:\", SafetyRefusal.ContainsProtected)]
    [InlineData(@"D:\", SafetyRefusal.ContainsProtected)]
    [InlineData(@"C:\Users\Dev\Projects", SafetyRefusal.TooShallow)]
    [InlineData(@"C:\Windows\Temp\x", SafetyRefusal.OutsideAllowedRoots)]
    [InlineData(@"C:\Program Files\App", SafetyRefusal.OutsideAllowedRoots)]
    [InlineData(@"C:\Users\Other\AppData\Local\x", SafetyRefusal.OutsideAllowedRoots)]
    [InlineData(@"C:\Users\Dev2\x\y", SafetyRefusal.OutsideAllowedRoots)]
    [InlineData(@"\\server\share\x\y", SafetyRefusal.OutsideAllowedRoots)]
    [InlineData(@"D:\OneDrive - Contoso\Documents\project\node_modules", SafetyRefusal.OutsideAllowedRoots)]
    public void Refused(string path, SafetyRefusal expected) => Assert.Equal(expected, Evaluate(path).Refusal);

    [Fact]
    public void A_sibling_with_a_common_prefix_is_not_inside()
        => Assert.Equal(SafetyRefusal.OutsideAllowedRoots, Evaluate(@"E:\src-old\x").Refusal);
}
