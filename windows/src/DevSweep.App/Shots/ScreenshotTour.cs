using System.Runtime.InteropServices;
using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Background;
using DevSweep.App.ViewModels.Settings;
using DevSweep.App.ViewModels.Sample;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Scanning;
using Microsoft.UI.Xaml;

namespace DevSweep.App.Shots;

/// <summary>
/// Screenshot mode (<c>DEVSWEEP_SHOTS=&lt;dir&gt;</c>, like the Mac app): walks every screen with
/// <see cref="SampleData"/> in light and dark and saves PNGs of the app's own window
/// (RenderTargetBitmap of its content, never the screen). With <c>DEVSWEEP_SHOTS_HIGH_CONTRAST=1</c>
/// it also turns Windows high contrast on for a few shots and back off afterwards.
/// </summary>
internal sealed partial class ScreenshotTour(MainWindow window, MainViewModel model, string folder)
{
    private int _saved;
    private readonly Dictionary<string, (double TitleBar, double Content)> _brightness = [];

    public async Task<int> RunAsync()
    {
        model.Scan.Show(new ScanResult(SampleData.Findings, SampleData.Report), SampleData.Changes, SampleData.Now);
        await model.Health.LoadAsync();
        await model.Projects.LoadAsync();
        model.History.Refresh();
        // The sidebar's update card and the tray menu's alerts, from the sample data (no network).
        await model.Background.Updates.CheckNowAsync();
        model.Background.Alerts.RefreshActive();
        foreach (TrayItem item in model.Background.Tray.Items())
        {
            Log.Write("tray menu: " + (item.IsSeparator ? "----" : item.Text + (item.Enabled ? string.Empty : " (not clickable)")));
        }

        await SmallestWindowAsync();

        // The title bar's theme button does the switching, exactly as a click does (System, Light, Dark, System).
        foreach ((AppearanceMode mode, string suffix) in new[] { (AppearanceMode.Light, "light"), (AppearanceMode.Dark, "dark") })
        {
            ClickThemeUntil(mode);
            await WalkAsync(suffix, full: true);
        }

        CheckThemesDiffer();
        await ThemeButtonShotsAsync();

        if (Environment.GetEnvironmentVariable("DEVSWEEP_SHOTS_HIGH_CONTRAST") == "1")
        {
            await HighContrastAsync();
        }

        return _saved;
    }

    /// <summary>Opens a place by selecting its navigation item (what a click does) and checks every place's item is on screen.</summary>
    private async Task GoAsync(string id)
    {
        await window.SelectPlaceAsync(id);
        IReadOnlyList<string> problems = window.VerifyNavigation(model.Places.Select(p => p.Id));
        if (problems.Count > 0)
        {
            throw new InvalidOperationException($"The navigation is wrong on {id}: " + string.Join("; ", problems));
        }
    }

    /// <summary>Clicks the title bar's theme button until it reaches <paramref name="mode"/> (at most three clicks).</summary>
    private void ClickThemeUntil(AppearanceMode mode)
    {
        for (int i = 0; i < 3 && model.SettingsPage.Appearance != mode; i++)
        {
            window.ClickThemeButton();
        }

        if (model.SettingsPage.Appearance != mode)
        {
            throw new InvalidOperationException($"The theme button did not reach {mode}: {window.DescribeTheme()}");
        }

        Log.Write("theme: " + window.DescribeTheme());
    }

    /// <summary>The navigation with the update card showing, at the smallest window: every place must still be on screen.</summary>
    private async Task SmallestWindowAsync()
    {
        string[] ids = [.. model.Places.Select(p => p.Id)];
        window.ResizeToMinimum();
        await window.WaitForRenderAsync();
        foreach (string id in ids)
        {
            await GoAsync(id);
        }

        Log.Write($"nav: all {ids.Length} places on screen at the minimum window {window.WindowSize.Width} x {window.WindowSize.Height} with the update card showing ({window.DescribeNavigation(ids)})");
        await GoAsync("home");
        window.ResizeTo(1280, 840);
        await window.WaitForRenderAsync();
    }

    /// <summary>A light shot and a dark shot of the same screen must differ a lot in brightness, in the content and in the title bar.</summary>
    private void CheckThemesDiffer()
    {
        int pairs = 0;
        foreach (string key in _brightness.Keys.Where(k => k.EndsWith("-light", StringComparison.Ordinal)).ToList())
        {
            string name = key[..^"-light".Length];
            if (!_brightness.TryGetValue(name + "-dark", out (double TitleBar, double Content) dark))
            {
                continue;
            }

            (double TitleBar, double Content) light = _brightness[key];
            pairs++;
            if (pairs <= 3 || light.Content - dark.Content < 60)
            {
                Log.Write($"theme check: {name}: brightness light content {light.Content:0} / title bar {light.TitleBar:0}, dark content {dark.Content:0} / title bar {dark.TitleBar:0}");
            }

            if (light.Content - dark.Content < 40 || light.TitleBar - dark.TitleBar < 40)
            {
                throw new InvalidOperationException($"The light and dark shots of {name} are not clearly different (content {light.Content:0} vs {dark.Content:0}, title bar {light.TitleBar:0} vs {dark.TitleBar:0}).");
            }
        }

        if (pairs < 10)
        {
            throw new InvalidOperationException($"Only {pairs} light/dark pairs were compared.");
        }

        Log.Write($"theme check: ok, {pairs} screens are clearly lighter in Light than in Dark (content and title bar)");
    }

    /// <summary>One shot per theme with the title bar's button showing, taken after clicking it (System, Light, Dark).</summary>
    private async Task ThemeButtonShotsAsync()
    {
        await GoAsync("home");
        foreach ((AppearanceMode mode, string name) in new[] { (AppearanceMode.System, "system"), (AppearanceMode.Light, "light"), (AppearanceMode.Dark, "dark") })
        {
            ClickThemeUntil(mode);
            await ShotAsync("theme-button", name);
        }

        ClickThemeUntil(AppearanceMode.System);
    }

    private async Task WalkAsync(string suffix, bool full)
    {
        model.ShowWelcome = true;
        await ShotAsync("welcome", suffix);
        model.ShowWelcome = false;

        await GoAsync("home");
        await ShotAsync("home", suffix);

        await GoAsync("cleanup");
        model.CleanUp.Inspected = null;
        await ShotAsync("cleanup", suffix);
        model.CleanUp.Inspect(model.CleanUp.All.First(f => f.Id == "pnpm-store"));
        await ShotAsync("inspector", suffix);
        if (full)
        {
            model.CleanUp.Inspect(model.CleanUp.All.First(f => f.Id == "windows-update-cleanup"));
            await ShotAsync("inspector-admin", suffix);
        }

        model.CleanUp.Inspected = null;
        foreach (FindingViewModel item in model.CleanUp.All.Where(f => f.CanSelect))
        {
            item.IsSelected = item.Id is "npm-cache" or "gradle-caches" or "pnpm-store" or "chrome-cache" or "jetbrains-old:IntelliJIdea2024.1" or "android-images:android-30" or "leftover:postman";
        }

        await ShotAsync("selection", suffix);
        await model.Review.OpenAsync(model.CleanUp.Selected.Select(f => new PlannedAction(f.Finding, f.ChosenAction)).ToList());
        await ShotAsync("review", suffix);
        if (full)
        {
            await model.Review.CleanAsync();
            await ShotAsync("review-done", suffix);
            model.Scan.Show(new ScanResult(SampleData.Findings, SampleData.Report), SampleData.Changes, SampleData.Now);
        }

        model.Review.Close();
        model.CleanUp.ClearSelection();

        if (full)
        {
            await ScanningAsync(suffix);
        }

        if (model.Places.Any(p => p.Id == "projects"))
        {
            await GoAsync("projects");
            foreach (ProjectsTab tab in model.Projects.Tabs)
            {
                model.Projects.Tab = tab;
                await ShotAsync("projects-" + tab.ToString().ToLowerInvariant(), suffix);
            }
        }

        if (model.Places.Any(p => p.Id == "health"))
        {
            await GoAsync("health");
            foreach (HealthTab tab in model.Health.Tabs)
            {
                model.Health.Tab = tab;
                await ShotAsync("health-" + tab.ToString().ToLowerInvariant(), suffix);
            }
        }

        await GoAsync("history");
        await ShotAsync("history", suffix);
        await GoAsync("settings");
        foreach (SettingsTab tab in Enum.GetValues<SettingsTab>())
        {
            model.SettingsPage.Tab = tab;
            await ShotAsync("settings-" + tab.ToString().ToLowerInvariant(), suffix);
        }

        model.SettingsPage.Tab = SettingsTab.General;
    }

    /// <summary>The scan progress panel, fed with progress events the way a real scan sends them.</summary>
    private async Task ScanningAsync(string suffix)
    {
        await GoAsync("cleanup");
        var scan = model.Scan;
        scan.Checks.Clear();
        scan.IsScanning = true;
        string[] titles = ["npm cache", "Gradle caches", "Browser caches", "Leftovers of removed apps", "Idle project dependencies", "WSL and Docker disks"];
        for (int i = 0; i < titles.Length; i++)
        {
            scan.OnProgress(new ScanProgress(ScanPhase.Checking, i, 50, "check-" + i, false, titles[..(i + 1)]));
        }

        scan.OnProgress(new ScanProgress(ScanPhase.Checking, 1, 50, "check-0", true, titles[1..]));
        scan.OnProgress(new ScanProgress(ScanPhase.Measuring, 1, 8, "check-0", true, []));
        scan.OnProgress(new ScanProgress(ScanPhase.Checking, 21, 50, "check-1", true, titles[2..]));
        scan.OnProgress(new ScanProgress(ScanPhase.Checking, 21, 50, "check-5", false, titles[2..]));
        await ShotAsync("scanning", suffix);
        scan.IsScanning = false;
        scan.Status = SampleData.Report.Summary;
        scan.Checks.Clear();
    }

    private async Task HighContrastAsync()
    {
        if (!HighContrast.Set(true))
        {
            Log.Write("high contrast: could not be turned on (SystemParametersInfo failed); skipped");
            return;
        }

        try
        {
            await Task.Delay(2500);
            ClickThemeUntil(AppearanceMode.System);
            bool on = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
            Log.Write($"high contrast: on={on}");
            await GoAsync("home");
            await ShotAsync("home", "highcontrast");
            await GoAsync("cleanup");
            model.CleanUp.Inspect(model.CleanUp.All.First(f => f.Id == "pnpm-store"));
            await ShotAsync("cleanup", "highcontrast");
            model.CleanUp.Inspected = null;
            await model.Review.OpenAsync(model.CleanUp.All.Where(f => f.CanSelect).Take(5).Select(f => new PlannedAction(f.Finding, f.ChosenAction)).ToList());
            await ShotAsync("review", "highcontrast");
            model.Review.Close();
        }
        finally
        {
            HighContrast.Set(false);
        }
    }

    private async Task ShotAsync(string name, string suffix)
    {
        await window.WaitForRenderAsync(350);
        string path = Path.Join(folder, $"{name}-{suffix}.png");
        (int width, int height, double titleBar, double content) = await window.CaptureWithBrightnessAsync(path);
        _saved++;
        _brightness[$"{name}-{suffix}"] = (titleBar, content);
        Log.Write($"saved {Path.GetFileName(path)} {width}x{height} brightness title bar {titleBar:0} content {content:0}");
    }
}

/// <summary>Turns Windows high contrast on or off for the current user (screenshot mode only).</summary>
internal static partial class HighContrast
{
    private const uint SpiSetHighContrast = 0x0043;
    private const uint HcfHighContrastOn = 0x00000001;
    private const uint SpifSendChange = 0x0002;

    public static bool Set(bool on)
    {
        var settings = new HighContrastSettings
        {
            Size = (uint)Marshal.SizeOf<HighContrastSettings>(),
            Flags = on ? HcfHighContrastOn : 0,
        };
        return SystemParametersInfo(SpiSetHighContrast, settings.Size, ref settings, SpifSendChange);
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, ref HighContrastSettings value, uint winIni);

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrastSettings
    {
        public uint Size;
        public uint Flags;
        public IntPtr DefaultScheme;
    }
}
