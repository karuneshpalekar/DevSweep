namespace DevSweep.Core.Platform;

/// <summary>The few Win32 error codes the platform layer branches on.</summary>
internal static class Win32Errors
{
    public const int InvalidFunction = 1;
    public const int FileNotFound = 2;
    public const int PathNotFound = 3;
    public const int AccessDenied = 5;
    public const int NotSameDevice = 17;
    public const int NoMoreFiles = 18;
    public const int SharingViolation = 32;
    public const int HandleEof = 38;
    public const int NotSupported = 50;
    public const int FileExists = 80;
    public const int InvalidParameter = 87;
    public const int InvalidLevel = 124;
    public const int DirectoryNotEmpty = 145;
    public const int AlreadyExists = 183;
    public const int MoreData = 234;

    public static bool IsNotFound(int error) => error is FileNotFound or PathNotFound;

    /// <summary>The file system does not implement the requested information class.</summary>
    public static bool IsUnsupported(int error) => error is InvalidFunction or NotSupported or InvalidParameter or InvalidLevel;

    public static bool IsExists(int error) => error is FileExists or AlreadyExists;
}
