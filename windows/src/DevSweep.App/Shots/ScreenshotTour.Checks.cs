using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DevSweep.App.Shots;

/// <summary>The checks run on every screenshot: text and icon contrast, and (on the main screens) the keyboard order.</summary>
internal sealed partial class ScreenshotTour
{
    /// <summary>When true, a contrast offender or an unnamed control fails the tour. Offenders are always logged.</summary>
    private static readonly bool StrictChecks = Environment.GetEnvironmentVariable("DEVSWEEP_STRICT_CHECKS") == "1";

    private readonly Dictionary<string, (ContrastOffender First, int Count)> _offenders = [];
    private readonly HashSet<string> _unnamed = [];
    private int _contrastChecked;
    private int _contrastSkipped;
    private int _tabDumps;

    private static readonly HashSet<string> TabOrderScreens =
    [
        "home-light", "cleanup-light", "inspector-light", "inspector-admin-light", "selection-light", "review-light", "history-light",
        "projects-projects-light", "projects-detail-unpushed-light", "projects-detail-noremote-light", "projects-sheet-publish-light", "projects-accounts-light", "projects-activity-light",
        "health-tools-light", "health-tools-node-eol-light", "health-security-env-light", "health-ports-selected-light", "settings-general-light",
    ];

    private void CheckScreen(string screen)
    {
        (List<ContrastOffender> offenders, int count, int skipped) = ContrastCheck.Run(window.Root, screen);
        _contrastChecked += count;
        _contrastSkipped += skipped;
        int fresh = 0;
        foreach (ContrastOffender offender in offenders)
        {
            if (_offenders.TryGetValue(offender.Key, out (ContrastOffender First, int Count) known))
            {
                _offenders[offender.Key] = (known.First, known.Count + 1);
                continue;
            }

            _offenders[offender.Key] = (offender, 1);
            if (fresh++ < 12)
            {
                Log.Write("contrast: " + offender);
            }
        }

        if (offenders.Count > 0)
        {
            Log.Write($"contrast: {screen}: {count} checked, {offenders.Count} below the minimum ({fresh} new)");
        }

        if (TabOrderScreens.Contains(screen))
        {
            DumpTabOrder(screen);
        }
    }

    /// <summary>The controls a Tab key visits, in order (tree order, as WinUI does), and whether each has a name a screen reader can say.</summary>
    private void DumpTabOrder(string screen)
    {
        var names = new List<string>();
        int unnamed = 0;

        void Walk(DependencyObject node)
        {
            if (node is UIElement { Visibility: Visibility.Collapsed })
            {
                return;
            }

            if (node is Control { IsTabStop: true, IsEnabled: true, ActualWidth: > 0 } control)
            {
                string name = AutomationProperties.GetName(control);
                if (name.Length == 0)
                {
                    name = control switch
                    {
                        ContentControl { Content: string text } => text,
                        TextBox { Header: string header } => header,
                        TextBox { PlaceholderText: { Length: > 0 } hint } => hint,
                        AutoSuggestBox { PlaceholderText: { Length: > 0 } hint } => hint,
                        _ => string.Empty,
                    };
                }

                if (name.Length == 0)
                {
                    unnamed++;
                    if (_unnamed.Add($"{control.GetType().Name}@{screen}"))
                    {
                        Log.Write($"tab order: {screen}: a {control.GetType().Name} has no name for a screen reader");
                    }

                    name = "(" + control.GetType().Name + ")";
                }

                names.Add(name);
            }

            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                Walk(VisualTreeHelper.GetChild(node, i));
            }
        }

        Walk(window.Root);
        _tabDumps++;
        Log.Write($"tab order: {screen}: {names.Count} stops, {unnamed} unnamed: {string.Join(" > ", names.Take(30))}{(names.Count > 30 ? " > …" : string.Empty)}");
    }

    /// <summary>The button must be inside the window right now, with no scrolling (the pinned footers of the side panels).</summary>
    private void RequireOnScreen(string automationName, string where)
    {
        Microsoft.UI.Xaml.Controls.Button button = window.Find<Microsoft.UI.Xaml.Controls.Button>(automationName)
            ?? throw new InvalidOperationException($"pinned: '{automationName}' is not on screen ({where})");
        Windows.Foundation.Rect bounds = button.TransformToVisual(window.Root).TransformBounds(new Windows.Foundation.Rect(0, 0, button.ActualWidth, button.ActualHeight));
        double height = window.Root.ActualHeight;
        double width = window.Root.ActualWidth;
        if (bounds.Top < 0 || bounds.Bottom > height + 1 || bounds.Left < 0 || bounds.Right > width + 1 || button.ActualHeight < 10)
        {
            throw new InvalidOperationException($"pinned: '{automationName}' is outside the window ({where}): {bounds.Left:0},{bounds.Top:0} to {bounds.Right:0},{bounds.Bottom:0} in {width:0} x {height:0}");
        }

        Log.Write($"pinned: '{automationName}' is on screen at {bounds.Top:0}-{bounds.Bottom:0} of {height:0} ({where})");
    }

    /// <summary>
    /// The wide layout (1400 px and up, where the side panel pushes the list instead of covering it), when the display allows it: the CI
    /// virtual screen is about 1030 x 780, so the window usually cannot get that big and the log says so.
    /// </summary>
    private async Task WideAsync(string suffix)
    {
        window.ResizeTo(1400, 900);
        await window.WaitForRenderAsync();
        (int width, int height) = ((int)window.WindowSize.Width, (int)window.WindowSize.Height);
        if (window.Root.ActualWidth < 1100)
        {
            Log.Write($"wide: the window could only reach {width} x {height} (wanted 1400 x 900), so the wide layout was not shot; it is checked by reading the layout code (see p4-polish.md)");
            window.ResizeTo(1280, 840);
            await window.WaitForRenderAsync();
            return;
        }

        Log.Write($"wide: the window reached {width} x {height}");
        await GoAsync("cleanup");
        model.CleanUp.Inspected = null;
        await ShotAsync("cleanup-wide", suffix);
        model.CleanUp.Inspect(model.CleanUp.All.First(f => f.Id == "pnpm-store"));
        await ShotAsync("inspector-wide", suffix);
        Require(window.ActivePanel is { IsOverlay: false }, "wide: the side panel should push the list at this width");
        model.CleanUp.Inspected = null;
        await GoAsync("projects");
        model.Projects.Tab = DevSweep.App.ViewModels.ProjectsTab.Projects;
        model.Projects.SelectedId = "alex/web-shop";
        await ShotAsync("projects-detail-wide", suffix);
        model.Projects.SelectedId = null;
        window.ResizeTo(1280, 840);
        await window.WaitForRenderAsync();
    }

    /// <summary>Summarises the checks; throws in strict mode when anything is below the minimum.</summary>
    private void FinishChecks()
    {
        Log.Write($"contrast: {_contrastChecked} texts and icons checked on every shot (light, dark, high contrast), {_contrastSkipped} skipped (not a solid colour), {_offenders.Count} distinct offenders ({_offenders.Values.Sum(v => v.Count)} occurrences)");
        foreach (ContrastOffender offender in _offenders.Values.OrderBy(v => v.First.Ratio).Select(v => v.First).Take(25))
        {
            Log.Write($"contrast: worst: {offender} (seen {_offenders[offender.Key].Count}x)");
        }

        Log.Write($"tab order: {_tabDumps} screens dumped, {_unnamed.Count} unnamed controls");
        if (StrictChecks && (_offenders.Count > 0 || _unnamed.Count > 0))
        {
            throw new InvalidOperationException($"{_offenders.Count} contrast offenders and {_unnamed.Count} unnamed controls; see the log.");
        }
    }
}
