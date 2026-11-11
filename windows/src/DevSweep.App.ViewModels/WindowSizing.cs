namespace DevSweep.App.ViewModels;

/// <summary>A rectangle in physical pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>
/// Where the window opens and how small it may get. The window is sized in device-independent pixels and fitted
/// to the screen's work area, so on a small or scaled display it is never taller than the screen (a window taller
/// than the screen pushed the bottom of the navigation, Settings, out of sight).
/// </summary>
public static class WindowSizing
{
    public const int DesiredWidth = 1240;
    public const int DesiredHeight = 820;
    public const int MinimumWidth = 1000;
    public const int MinimumHeight = 680;

    /// <summary>The window's bounds (centred in the work area, never bigger than it) and its minimum size, in pixels.</summary>
    public static (PixelRect Bounds, int MinWidth, int MinHeight) Fit(PixelRect workArea, double scale)
    {
        double factor = scale > 0 ? scale : 1;
        int width = Math.Min((int)Math.Round(DesiredWidth * factor), Math.Max(1, workArea.Width));
        int height = Math.Min((int)Math.Round(DesiredHeight * factor), Math.Max(1, workArea.Height));
        int x = workArea.X + ((workArea.Width - width) / 2);
        int y = workArea.Y + ((workArea.Height - height) / 2);
        int minWidth = Math.Min((int)Math.Round(MinimumWidth * factor), width);
        int minHeight = Math.Min((int)Math.Round(MinimumHeight * factor), height);
        return (new PixelRect(x, y, width, height), minWidth, minHeight);
    }
}
