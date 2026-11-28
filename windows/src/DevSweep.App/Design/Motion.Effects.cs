using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevSweep.App.Design;

/// <summary>Small effects built on the tween, for the Mac's quieter motion: things that appear, go and change in place.</summary>
internal static partial class Motion
{
    /// <summary>Hover and press fills easing in and out (the Mac's controls do this on their own).</summary>
    public const int HoverMs = 120;

    private static readonly ConditionalWeakTable<UIElement, MotionHandle> Fades = new();

    /// <summary>
    /// Shows or hides <paramref name="element"/> with a fade (<see cref="SwapMs"/>), as the Mac's <c>.transition(.opacity)</c>.
    /// Hiding fades out, then collapses. With <paramref name="animate"/> false, or motion off, it switches at once.
    /// </summary>
    public static void Reveal(UIElement element, bool show, bool animate = true)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (Fades.TryGetValue(element, out MotionHandle? running))
        {
            running.Cancel();
            Fades.Remove(element);
        }

        bool visible = element.Visibility == Visibility.Visible;
        if (!animate || !Enabled)
        {
            element.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            element.Opacity = 1;
            return;
        }

        if (show)
        {
            double from = visible ? element.Opacity : 0;
            element.Opacity = from;
            element.Visibility = Visibility.Visible;
            Fades.AddOrUpdate(element, Run(SwapMs, Ease.InOut, t => element.Opacity = Lerp(from, 1, t), () => Fades.Remove(element)));
        }
        else if (visible)
        {
            double from = element.Opacity;
            Fades.AddOrUpdate(element, Run(SwapMs, Ease.InOut, t => element.Opacity = Lerp(from, 0, t), () =>
            {
                element.Visibility = Visibility.Collapsed;
                element.Opacity = 1;
                Fades.Remove(element);
            }));
        }
    }

    /// <summary>Fades freshly changed content in from a little below full opacity (content changing in place).</summary>
    public static void FadeIn(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!Enabled)
        {
            element.Opacity = 1;
            return;
        }

        if (Fades.TryGetValue(element, out MotionHandle? running))
        {
            running.Cancel();
        }

        Fades.AddOrUpdate(element, Run(SwapMs, Ease.InOut, t => element.Opacity = Lerp(0.25, 1, t), () => Fades.Remove(element)));
    }

    /// <summary>A border whose fill eases between styles (hover, pressed, selected) instead of switching.</summary>
    public static void SmoothFill(Border border)
    {
        ArgumentNullException.ThrowIfNull(border);
        border.BackgroundTransition = Enabled ? new Microsoft.UI.Xaml.BrushTransition { Duration = TimeSpan.FromMilliseconds(HoverMs) } : null;
    }
}
