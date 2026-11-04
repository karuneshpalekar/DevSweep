using DevSweep.Core.Safety;

namespace DevSweep.Core.Tests.Safety;

/// <summary>A fixed user layout with redirected folders, a dev root on another drive and a quarantine.</summary>
internal static class TestContexts
{
    public const string Profile = @"C:\Users\Dev";
    public const string RedirectedDocuments = @"D:\OneDrive - Contoso\Documents";
    public const string RedirectedDesktop = @"D:\OneDrive - Contoso\Desktop";
    public const string OneDrive = @"D:\OneDrive - Contoso";
    public const string DevRoot = @"E:\src";
    public const string Quarantine = @"C:\Users\Dev\AppData\Local\DevSweep\Quarantine";

    public static readonly string[] ProtectedPaths =
    [
        RedirectedDocuments,
        RedirectedDesktop,
        OneDrive,
        @"C:\Users\Dev\Downloads",
        @"C:\Users\Dev\AppData",
        @"C:\Users\Dev\AppData\Local",
        @"C:\Users\Dev\AppData\Roaming",
        @"C:\Users\Dev\AppData\LocalLow",
        @"C:\Users\Dev\AppData\Local\Microsoft",
        @"C:\Users\Dev\AppData\Local\Packages",
        @"C:\Users\Dev\AppData\Local\Temp",
        @"C:\Users\Dev\.ssh",
        @"C:\Users\Dev\.nuget",
        @"C:\Users\Dev\scoop",
    ];

    public static SafetyContext Create()
        => SafetyContext.Create(
            Profile,
            ProtectedPaths.Select(p => new ProtectedPath(p, "protected")),
            devRoots: [DevRoot],
            quarantineRoots: [Quarantine]);
}
