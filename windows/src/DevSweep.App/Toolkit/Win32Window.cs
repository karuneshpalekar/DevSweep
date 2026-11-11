using System.Runtime.InteropServices;

namespace DevSweep.App.Toolkit;

/// <summary>The few window calls the icon and sizing code needs (source-generated P/Invokes, AOT and trim safe).</summary>
internal static partial class Win32Window
{
    public const uint WmSetIcon = 0x80;
    public const uint WmGetIcon = 0x7F;
    public const nuint IconSmall = 0;
    public const nuint IconBig = 1;
    public const int SmCxIcon = 11;
    public const int SmCxSmIcon = 49;

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial nint SendMessage(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    public static partial uint GetDpiForWindow(nint window);

    [LibraryImport("user32.dll", EntryPoint = "GetSystemMetrics")]
    public static partial int GetSystemMetrics(int index);

    /// <summary>The window's current small and large icon handles (0 when it has none).</summary>
    public static (nint Small, nint Big) GetIcons(nint window)
        => (SendMessage(window, WmGetIcon, IconSmall, 0), SendMessage(window, WmGetIcon, IconBig, 0));

    /// <summary>Gives the window the logo as its small (title bar) and big (Alt+Tab, taskbar) icons, loaded from the .ico at each size.</summary>
    public static void SetIcons(nint window, string iconPath)
    {
        const uint imageIcon = 1;
        const uint loadFromFile = 0x10;
        nint small = Background.TrayNative.LoadImage(0, iconPath, imageIcon, GetSystemMetrics(SmCxSmIcon), GetSystemMetrics(SmCxSmIcon), loadFromFile);
        nint big = Background.TrayNative.LoadImage(0, iconPath, imageIcon, GetSystemMetrics(SmCxIcon), GetSystemMetrics(SmCxIcon), loadFromFile);
        if (small != 0)
        {
            SendMessage(window, WmSetIcon, IconSmall, small);
        }

        if (big != 0)
        {
            SendMessage(window, WmSetIcon, IconBig, big);
        }
    }
}
