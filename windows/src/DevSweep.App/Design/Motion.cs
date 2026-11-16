using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;

namespace DevSweep.App.Design;

/// <summary>The easing a tween uses.</summary>
internal enum Ease
{
    /// <summary>A soft, critically damped spring: fast start, settles without bounce (the Mac's panel spring).</summary>
    Spring,

    /// <summary>Ease in and out (the Mac's swap and screen timings).</summary>
    InOut,
}

/// <summary>A running tween; <see cref="Cancel"/> stops it where it is.</summary>
internal sealed class MotionHandle
{
    internal bool Cancelled { get; private set; }

    public void Cancel() => Cancelled = true;
}

/// <summary>
/// DevSweep's one set of animation timings (the Mac's <c>Motion</c>), and the tween that plays them. A tween is driven by
/// the render loop (one <see cref="CompositionTarget.Rendering"/> subscription while any tween runs), so layout properties
/// such as a width animate too, with no XAML storyboards and no reflection. When motion is off (Windows "animation effects"
/// off, screenshot mode, or <see cref="Force"/> false) every tween jumps to its end value at once and calls its completion
/// synchronously, so shots and tests are deterministic.
/// </summary>
internal static partial class Motion
{
    /// <summary>The side panel sliding open or closed.</summary>
    public const int PanelMs = 380;

    /// <summary>Content changing in place (panel item, filter, sheet step).</summary>
    public const int SwapMs = 200;

    /// <summary>Moving between sidebar screens.</summary>
    public const int ScreenMs = 220;

    /// <summary>Selected-item highlight (the navigation's own indicator slides; custom indicators use this).</summary>
    public const int SlideMs = 350;

    private const uint SpiGetClientAreaAnimation = 0x1042;

    private static readonly List<Running> Active = [];
    private static bool _hooked;
    private static bool? _systemAnimations;

    /// <summary>Screenshot mode: no animation at all, so every shot shows the settled state.</summary>
    public static bool Screenshots { get; set; }

    /// <summary>For tests: true or false overrides everything (including screenshot mode); null follows Windows.</summary>
    public static bool? Force { get; set; }

    /// <summary>Whether tweens animate right now.</summary>
    public static bool Enabled => Force ?? (!Screenshots && SystemAnimations());

    /// <summary>Tweens running right now (tests).</summary>
    public static int RunningCount => Active.Count;

    /// <summary>
    /// Plays <paramref name="step"/> with values 0 to 1 over <paramref name="milliseconds"/>; <paramref name="done"/> runs after the last
    /// step. With motion off, runs step(1) and done at once.
    /// </summary>
    public static MotionHandle Run(int milliseconds, Ease ease, Action<double> step, Action? done = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        var handle = new MotionHandle();
        if (!Enabled || milliseconds <= 0)
        {
            step(1);
            done?.Invoke();
            return handle;
        }

        step(0);
        Active.Add(new Running(handle, Stopwatch.StartNew(), milliseconds, ease, step, done));
        if (!_hooked)
        {
            _hooked = true;
            CompositionTarget.Rendering += OnRendering;
        }

        return handle;
    }

    /// <summary>A value between <paramref name="from"/> and <paramref name="to"/>, for <c>Run(..., t => x = Lerp(a, b, t))</c>.</summary>
    public static double Lerp(double from, double to, double t) => from + ((to - from) * t);

    /// <summary>The eased progress for a linear 0 to 1 time.</summary>
    public static double Eased(Ease ease, double t)
    {
        t = Math.Clamp(t, 0, 1);
        if (ease == Ease.InOut)
        {
            return t * t * (3 - (2 * t));
        }

        const double omega = 10;
        double raw = 1 - ((1 + (omega * t)) * Math.Exp(-omega * t));
        double end = 1 - ((1 + omega) * Math.Exp(-omega));
        return raw / end;
    }

    private static void OnRendering(object? sender, object e)
    {
        foreach (Running running in Active.ToArray())
        {
            double t = running.Clock.Elapsed.TotalMilliseconds / running.Milliseconds;
            if (running.Handle.Cancelled)
            {
                Active.Remove(running);
                continue;
            }

            running.Step(Eased(running.Ease, t));
            if (t >= 1)
            {
                Active.Remove(running);
                running.Step(1);
                running.Done?.Invoke();
            }
        }

        if (Active.Count == 0)
        {
            CompositionTarget.Rendering -= OnRendering;
            _hooked = false;
        }
    }

    private static bool SystemAnimations()
    {
        if (_systemAnimations is { } known)
        {
            return known;
        }

        int on = 1;
        bool ok = SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref on, 0);
        _systemAnimations = !ok || on != 0;
        return _systemAnimations.Value;
    }

    private sealed record Running(MotionHandle Handle, Stopwatch Clock, int Milliseconds, Ease Ease, Action<double> Step, Action? Done);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);
}
