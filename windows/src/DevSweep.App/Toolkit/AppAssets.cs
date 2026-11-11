namespace DevSweep.App.Toolkit;

/// <summary>
/// DevSweep's logo files, which ship next to DevSweep.exe in an <c>Assets</c> folder (the portable zip and the
/// installer both carry it): the multi-size icon (window, taskbar, tray) and a PNG (title bar, toasts). The exe
/// also has the same icon embedded for Explorer and shortcuts.
/// </summary>
internal static class AppAssets
{
    public static string Folder => Path.Join(AppContext.BaseDirectory, "Assets");

    public static string IconPath => Path.Join(Folder, "DevSweep.ico");

    public static string LogoPath => Path.Join(Folder, "DevSweep.png");

    public static bool HasIcon => File.Exists(IconPath);

    public static bool HasLogo => File.Exists(LogoPath);
}
