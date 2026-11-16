using Microsoft.UI.Xaml;

namespace DevSweep.App.Design;

/// <summary>
/// Layout tokens for code-built views. Colours are never here: they are theme resources (Toolkit/Theme.cs) so light, dark and
/// high contrast stay live. Spacing is the 4/8/12/16/24/32 scale; every margin, padding and gap in a view uses one of these.
/// </summary>
internal static class Tokens
{
    public const double Space1 = 4;
    public const double Space2 = 8;
    public const double Space3 = 12;
    public const double Space4 = 16;
    public const double Space6 = 24;
    public const double Space8 = 32;

    public const double RadiusSmall = 4;
    public const double RadiusMedium = 8;
    public const double RadiusLarge = 12;

    /// <summary>The page's side gutter (and the top padding below the title bar).</summary>
    public const double PageGutter = 32;

    /// <summary>Width of the side panel (the Mac's 360, a little wider for Segoe UI).</summary>
    public const double PanelWidth = 380;

    /// <summary>A content width below which a pushed side panel would squeeze the list too hard, so it overlays instead.</summary>
    public const double PanelOverlayBelow = 1000;

    /// <summary>The title bar's height (4/8 grid).</summary>
    public const double TitleBarHeight = 48;

    public const string FontDisplay = "Segoe UI Variable Display";
    public const string FontText = "Segoe UI Variable Text";

    public static Thickness Pad(double all) => new(all);

    public static Thickness Pad(double horizontal, double vertical) => new(horizontal, vertical, horizontal, vertical);
}
