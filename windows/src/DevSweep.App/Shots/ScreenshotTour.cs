using System.Runtime.InteropServices;
using DevSweep.App.ViewModels;
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

    public async Task<int> RunAsync()
    {
        model.Scan.Show(new ScanResult(SampleData.Findings, SampleData.Report), SampleData.Changes, SampleData.Now);
        await model.Health.LoadAsync();
        await model.Projects.LoadAsync();
        model.History.Refresh();
        foreach ((ElementTheme theme, string suffix) in new[] { (ElementTheme.Light, "light"), (ElementTheme.Dark, "dark") })
        {
            window.SetTheme(theme);
            await WalkAsync(suffix, full: true);
        }

        if (Environment.GetEnvironmentVariable("DEVSWEEP_SHOTS_HIGH_CONTRAST") == "1")
        {
            await HighContrastAsync();
        }

        return _saved;
    }

    private async Task WalkAsync(string suffix, bool full)
    {
        model.ShowWelcome = true;
        await ShotAsync("welcome", suffix);
        model.ShowWelcome = false;

        model.Go("home");
        await ShotAsync("home", suffix);

        model.Go("cleanup");
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
            model.Go("projects");
            foreach (ProjectsTab tab in Enum.GetValues<ProjectsTab>())
            {
                model.Projects.Tab = tab;
                await ShotAsync("projects-" + tab.ToString().ToLowerInvariant(), suffix);
            }
        }

        if (model.Places.Any(p => p.Id == "health"))
        {
            model.Go("health");
            foreach (HealthTab tab in model.Health.Tabs)
            {
                model.Health.Tab = tab;
                await ShotAsync("health-" + tab.ToString().ToLowerInvariant(), suffix);
            }
        }

        model.Go("history");
        await ShotAsync("history", suffix);
        model.Go("settings");
        await ShotAsync("settings", suffix);
    }

    /// <summary>The scan progress panel, fed with progress events the way a real scan sends them.</summary>
    private async Task ScanningAsync(string suffix)
    {
        model.Go("cleanup");
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
            window.SetTheme(ElementTheme.Default);
            bool on = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
            Log.Write($"high contrast: on={on}");
            model.Go("home");
            await ShotAsync("home", "highcontrast");
            model.Go("cleanup");
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
        (int width, int height) = await window.CaptureAsync(path);
        _saved++;
        Log.Write($"saved {Path.GetFileName(path)} {width}x{height}");
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
