namespace DevSweep.Core.Platform;

/// <summary>The few Win32 error codes the platform layer branches on.</summary>
internal static class Win32Errors
{
    public const int FileNotFound = 2;
    public const int PathNotFound = 3;
    public const int AccessDenied = 5;

    public static bool IsNotFound(int error) => error is FileNotFound or PathNotFound;
}
