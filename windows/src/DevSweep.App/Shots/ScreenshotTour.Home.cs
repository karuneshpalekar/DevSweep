using DevSweep.App.Background;
using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Background;
using DevSweep.App.ViewModels.Sample;
using DevSweep.Core.Health.Alerts;
using DevSweep.Core.Rules;
using DevSweep.Core.Scanning;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevSweep.App.Shots;

internal sealed partial class ScreenshotTour
{
    private void ShowSample(ScanReport? report, ScanChanges? changes, bool scanned = true)
    {
        ScanViewModel scan = model.Scan;
        scan.IsScanning = false;
        if (!scanned)
        {
            scan.Report = null;
            scan.Findings = [];
            scan.LastScan = null;
            scan.Changes = null;
            scan.Status = "Not scanned yet";
            return;
        }

        scan.Show(new ScanResult(SampleData.Findings, report ?? SampleData.Report), changes, SampleData.Now);
    }

    private static void RequireText(FrameworkElement? element, string what) => Require(element is not null, "home: " + what + " is not on screen");

    /// <summary>Home in every state (not scanned, scanning, first scan, changes, blocked folders, rule errors), the wide two-column layout and the compact sidebar.</summary>
    private async Task HomeStatesAsync(string suffix)
    {
        await GoAsync("home");
        model.Settings.Update(s => s with { Schedule = s.Schedule with { Enabled = true, Frequency = ScanFrequency.Weekly, Day = DayOfWeek.Monday, Hour = 9, Minute = 0, Since = SampleData.Now } });
        ShowSample(null, SampleData.Changes);
        await window.WaitForRenderAsync();
        Require(model.Home.Subtitle.Contains("next scan", StringComparison.Ordinal), $"home: the subtitle has no next scan ('{model.Home.Subtitle}')");
        Require(model.Sidebar.StatusLine.Contains("next", StringComparison.Ordinal), "sidebar: the status line has no next scan");
        string blocked = model.Home.BlockedFoldersText ?? throw new InvalidOperationException("home: no blocked-folders text for a report with a blocked folder");
        RequireText(window.Find<InfoBar>("Windows blocked some folders. " + blocked), "the blocked-folders banner");
        RequireText(window.Find<Button>("Rescan"), "the refresh button");
        await ShotAsync("home-blocked", suffix);

        // Wide layout (Since last week left, Needs attention right) and the compact sidebar.
        window.SetPaneOpen(false);
        await window.WaitForRenderAsync();
        await ShotAsync("home-wide", suffix);
        await GoAsync("cleanup");
        await ShotAsync("sidebar-compact", suffix);
        await GoAsync("home");
        window.SetPaneOpen(true);
        await window.WaitForRenderAsync();
        Require(model.Sidebar.CountFor("cleanup").Length > 0 && model.Sidebar.Cards.Count is > 0 and <= SidebarViewModel.MaxCards, "sidebar: no size on Clean up or no attention cards");
        await ShotAsync("sidebar-wide", suffix);

        // Rule pack errors.
        ShowSample(SampleData.Report with { AccessDenied = new List<string>(), RuleErrors = new List<RuleError> { new("community-pack.yml", "demo", 12, 3, "unknown field 'pathz'") } }, SampleData.Changes);
        await window.WaitForRenderAsync();
        RequireText(window.Find<InfoBar>("Some rule packs couldn't be read. " + model.Home.RuleErrorsText), "the rule-pack banner");
        Require(model.Home.BlockedFoldersText is null, "home: blocked-folders text stayed after the report had none");
        await ShotAsync("home-rule-errors", suffix);

        // First scan: no earlier scan to compare with.
        ShowSample(SampleData.Report with { AccessDenied = new List<string>() }, null);
        await window.WaitForRenderAsync();
        Require(model.Home.ChangesEmpty?.Contains("first scan", StringComparison.Ordinal) == true || model.Home.Changes.Count > 0, "home: the first scan text is missing");
        await ShotAsync("home-first-scan", suffix);

        // Not scanned yet, then the first scan running.
        ShowSample(null, null, scanned: false);
        await window.WaitForRenderAsync();
        Require(model.Home.State == HomeState.NotScanned, "home: not in the not-scanned state");
        Button scanNow = window.Find<Button>("Scan now") ?? throw new InvalidOperationException("home: no Scan now button when nothing was scanned");
        Require(scanNow.Visibility == Visibility.Visible, "home: the Scan now button is hidden before the first scan");
        await ShotAsync("home-not-scanned", suffix);
        model.Scan.IsScanning = true;
        model.Scan.OnProgress(new ScanProgress(ScanPhase.Checking, 8, 50, "check-1", true, new List<string> { "npm cache", "Gradle caches" }));
        await window.WaitForRenderAsync();
        Require(model.Home.State == HomeState.Scanning, "home: not in the scanning state");
        await ShotAsync("home-scanning-first", suffix);

        ShowSample(null, SampleData.Changes);
        model.Scan.Checks.Clear();
        await window.WaitForRenderAsync();
        Log.Write("home: states checked (blocked folders banner, rule errors banner, first scan, not scanned, scanning, wide layout, compact sidebar, next scan in the subtitle)");
    }

    /// <summary>The tray's left-click flyout, built as a real window with the sample data and saved as its own picture.</summary>
    private async Task FlyoutAsync(string suffix)
    {
        TrayFlyoutWindow? flyout = null;
        try
        {
            flyout = new TrayFlyoutWindow(model.Background.Tray, () => window.ActualTheme);
            flyout.ShowAt(500, 700);
            await Task.Delay(500);
            flyout.Reflow();
            await Task.Delay(300);
            flyout.Root.UpdateLayout();
            Require(Contains(flyout.Root, "Scan now") && Contains(flyout.Root, "Open DevSweep") && Contains(flyout.Root, "Settings"), "tray flyout: Scan now, Open DevSweep or Settings is missing");
            (int width, int height) = await PngCapture.SaveAsync(flyout.Root, Path.Join(folder, $"tray-flyout-{suffix}.png"));
            _saved++;
            Log.Write($"saved tray-flyout-{suffix}.png {width}x{height}");
            Log.Write("tray flyout: ok (a borderless window with free space, cleanable, alerts, recent projects and the buttons)");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException or NotSupportedException)
        {
            Log.Write("tray flyout: FAILED " + ex.Message);
            _flyoutFailure = new InvalidOperationException("The tray flyout failed: " + ex.Message, ex);
        }
        finally
        {
            flyout?.Close();
        }
    }

    private InvalidOperationException? _flyoutFailure;

    private static bool Contains(DependencyObject parent, string automationName)
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(child) == automationName || Contains(child, automationName))
            {
                return true;
            }
        }

        return false;
    }
}
