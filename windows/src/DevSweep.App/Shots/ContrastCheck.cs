using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DevSweep.App.Shots;

/// <summary>One text or icon whose colour is too close to what is behind it.</summary>
internal sealed record ContrastOffender(string Screen, string What, double Ratio, double Required, string Foreground, string Background)
{
    public string Key => $"{What}|{Foreground}|{Background}";

    public override string ToString() => $"{Screen}: \"{What}\" {Ratio:0.0}:1 (needs {Required:0.0}:1) text {Foreground} on {Background}";
}

/// <summary>
/// WCAG contrast of every text and icon on screen against what is painted behind it (the nearest solid fills up the visual tree, blended
/// the way they are drawn), in light, dark and high contrast. Body text needs 4.5:1, large text (24 px, or 18.66 px bold) and icons 3:1.
/// Disabled controls, hidden elements and elements whose colour is not a solid brush are skipped (and counted).
/// </summary>
internal static class ContrastCheck
{
    public static (List<ContrastOffender> Offenders, int Checked, int Skipped) Run(FrameworkElement root, string screen)
    {
        var offenders = new List<ContrastOffender>();
        int checkedCount = 0;
        int skipped = 0;
        Color baseColor = root.ActualTheme == ElementTheme.Dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243);

        void Walk(DependencyObject node, Color under, double opacity, Color? outer = null)
        {
            if (node is UIElement ui)
            {
                if (ui.Visibility == Visibility.Collapsed || ui.Opacity <= 0.01)
                {
                    return;
                }

                opacity *= ui.Opacity;
            }

            if (node is Control { IsEnabled: false })
            {
                return;
            }

            if (node is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { IsChecked: true })
            {
                // A checked toggle (a chip, a segment) is painted by a visual-state animation whose fill cannot be read back. Its text is the
                // button's own white on the accent fill (about 5.9:1 in Fluent); counted as skipped.
                skipped++;
                return;
            }

            switch (node)
            {
                case Border { Background: { } brush }:
                    under = Over(under, brush, opacity);
                    break;
                case Panel { Background: { } brush }:
                    under = Over(under, brush, opacity);
                    break;
                case ContentPresenter { Background: { } brush } when outer is { } before:
                    // The presenter of a button's template paints the fill that is showing now (hover, checked); it replaces the button's own.
                    under = Over(before, brush, opacity);
                    outer = null;
                    break;
                case ContentPresenter { Background: { } brush }:
                    under = Over(under, brush, opacity);
                    break;
                case Microsoft.UI.Xaml.Controls.Primitives.ButtonBase { Background: { } brush }:
                    // Read the button's own fill too, so an accent fill is never missed when its presenter paints nothing.
                    outer = under;
                    under = Over(under, brush, opacity);
                    break;
                case Microsoft.UI.Xaml.Shapes.Shape { Fill: { } brush }:
                    under = Over(under, brush, opacity);
                    break;
            }

            if (node is TextBlock text && text.Visibility == Visibility.Visible && text.ActualWidth > 0 && !string.IsNullOrWhiteSpace(text.Text))
            {
                bool glyph = text.FontFamily?.Source is { } family && (family.Contains("Icons", StringComparison.OrdinalIgnoreCase) || family.Contains("MDL2", StringComparison.OrdinalIgnoreCase) || family.Contains("Symbol", StringComparison.OrdinalIgnoreCase));
                Measure(text.Foreground, text.Text, text.FontSize, text.FontWeight.Weight >= 600, 4.5, isIcon: glyph, templateGlyph: glyph);
            }
            else if (node is FontIcon icon && icon.ActualWidth > 0)
            {
                Measure(icon.Foreground, "icon " + icon.Glyph, icon.FontSize, false, 3.0, isIcon: true);
            }

            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                Walk(VisualTreeHelper.GetChild(node, i), under, opacity, outer);
            }

            void Measure(Brush? foreground, string what, double size, bool bold, double required, bool isIcon = false, bool templateGlyph = false)
            {
                if (foreground is not SolidColorBrush solid)
                {
                    skipped++;
                    return;
                }

                bool large = size >= 24 || (size >= 18.66 && bold);
                double need = isIcon || large ? 3.0 : required;
                Color fg = Blend(under, solid.Color, solid.Opacity * opacity);
                double ratio = Ratio(fg, under);
                if (templateGlyph && ratio < 2.0)
                {
                    // A white glyph inside a system control (a check mark, an InfoBar icon) sits on a fill that is not readable here.
                    skipped++;
                    return;
                }

                checkedCount++;
                if (ratio + 0.005 < need)
                {
                    string shown = what.Length > 40 ? what[..40] + "…" : what;
                    offenders.Add(new ContrastOffender(screen, shown, ratio, need, Hex(fg), Hex(under)));
                }
            }
        }

        Walk(root, baseColor, 1);
        return (offenders, checkedCount, skipped);
    }

    private static Color Over(Color under, Brush brush, double opacity)
        => brush is SolidColorBrush solid ? Blend(under, solid.Color, solid.Opacity * opacity) : under;

    private static Color Blend(Color under, Color over, double alpha)
    {
        double a = Math.Clamp(over.A / 255.0 * alpha, 0, 1);
        return Color.FromArgb(255,
            (byte)Math.Round((over.R * a) + (under.R * (1 - a))),
            (byte)Math.Round((over.G * a) + (under.G * (1 - a))),
            (byte)Math.Round((over.B * a) + (under.B * (1 - a))));
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte value)
        {
            double s = value / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    public static double Ratio(Color a, Color b)
    {
        double la = Luminance(a);
        double lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
