using System.Runtime.InteropServices;
using DevSweep.App.Design;
using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Background;
using DevSweep.App.ViewModels.Settings;
using DevSweep.App.ViewModels.Sample;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Projects;
using DevSweep.Core.Projects.GitHub;
using DevSweep.Core.Scanning;
using DevSweep.App.ViewModels.Places;
using DevSweep.App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

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
        InvalidOperationException? panelFailure = null;
        try
        {
            await SidePanelChecksAsync();
        }
        catch (InvalidOperationException ex)
        {
            // Keep going so the shots are still taken and published; the failure is raised at the end.
            panelFailure = ex;
            Log.Write("sidepanel: FAILED " + ex.Message);
        }


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

        FinishChecks();
        if (panelFailure is not null)
        {
            throw new InvalidOperationException("The side panel check failed: " + panelFailure.Message, panelFailure);
        }

        if (_flyoutFailure is not null)
        {
            throw _flyoutFailure;
        }

        return _saved;
    }

    /// <summary>Opens a place by selecting its navigation item (what a click does) and checks every place's item is on screen.</summary>
    /// <summary>The rows under the open section (the Mac's sidebar): Clean up's kinds open under it, picking one filters the list, other sections stay closed.</summary>
    private async Task SidebarSubRowsAsync(string suffix)
    {
        IReadOnlyList<string> rows = window.SubRowTags("cleanup");
        if (rows.Count < 2 || rows[0] != "cleanup:all")
        {
            throw new InvalidOperationException($"Clean up should open its kinds under it in the sidebar; it shows [{string.Join(", ", rows)}].");
        }

        if (window.SubRowTags("health").Count > 0 || window.SubRowTags("projects").Count > 0)
        {
            throw new InvalidOperationException("Only the open section shows its rows in the sidebar.");
        }

        string place = SidebarSubRows.Open(model, rows[1]);
        await window.WaitForRenderAsync();
        if (place != "cleanup" || model.CleanUp.SelectedKind is null)
        {
            throw new InvalidOperationException($"Picking {rows[1]} in the sidebar did not filter Clean up.");
        }

        await ShotAsync("sidebar-subrows", suffix);
        Log.Write($"sidebar: Clean up shows {rows.Count} rows ({string.Join(", ", rows)}); {rows[1]} filters the list");
        _ = SidebarSubRows.Open(model, "cleanup:all");
        await window.WaitForRenderAsync();
    }

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

        await GoAsync("cleanup");
        await ShotAsync("nav", "minimum");
        Log.Write($"nav: all {ids.Length} places on screen at the minimum window {window.WindowSize.Width} x {window.WindowSize.Height} with the update card showing ({window.DescribeNavigation(ids)})");
        await GoAsync("home");
        window.ResizeTo(1280, 840);
        await window.WaitForRenderAsync();
    }

    /// <summary>
    /// The side panel, driven the way a person drives it: focus the ⓘ button, open, press Esc, check focus came back; once with motion
    /// off (settles at once) and once with motion on (starts a tween, settles at the full width), and a cross-fade to another item.
    /// </summary>
    private async Task SidePanelChecksAsync()
    {
        await GoAsync("cleanup");
        model.CleanUp.Inspected = null;
        await window.WaitForRenderAsync();
        SidePanel panel = window.ActivePanel ?? throw new InvalidOperationException("Clean up has no side panel.");
        FindingViewModel first = model.CleanUp.All.First(f => f.Id == "pnpm-store");
        FindingViewModel second = model.CleanUp.All.First(f => f.Id == "npm-cache");

        Motion.Force = false;
        try
        {
            Button opener = window.Find<Button>($"Explain {first.Title}: what happens if I clean this?") ?? throw new InvalidOperationException("The info button of the first item is not on screen.");
            bool focusedOpener = opener.Focus(FocusState.Programmatic);
            Log.Write($"sidepanel: focus on the info button: {focusedOpener}, focused element: {Describe(FocusManager.GetFocusedElement(window.Root.XamlRoot))}");
            model.CleanUp.Inspect(first);
            await window.WaitForRenderAsync();
            Require(panel.IsOpen && panel.Selected == first.Id && Math.Abs(panel.PaneWidth - panel.OpenWidth) < 0.5 && panel.OpenWidth >= Tokens.PanelWidth, $"sidepanel: did not open at {panel.OpenWidth} px (open={panel.IsOpen}, width={panel.PaneWidth})");
            Log.Write($"sidepanel: opens on the item and settles at {panel.PaneWidth:0} px with motion off (overlay={panel.IsOverlay})");

            Require(panel.HandleKey(Windows.System.VirtualKey.Escape), "sidepanel: Esc was not handled while open");
            await window.WaitForRenderAsync();
            Require(!panel.IsOpen && panel.PaneWidth == 0 && model.CleanUp.Inspected is null && panel.Shown == first.Id, "sidepanel: Esc did not close it (or it forgot the last item)");
            object? focused = FocusManager.GetFocusedElement(window.Root.XamlRoot);
            Require(ReferenceEquals(focused, opener), $"sidepanel: focus did not return to the button that opened it (it is on {Describe(focused)})");
            Log.Write("sidepanel: Esc closes it, the last item stays until it is gone, focus returned to the info button");
            Log.Write($"motion: off path ok (enabled={Motion.Enabled}, running={Motion.RunningCount})");

            Motion.Force = true;
            opener.Focus(FocusState.Programmatic);
            model.CleanUp.Inspect(first);
            Require(Motion.RunningCount > 0 && panel.PaneWidth < panel.OpenWidth, $"motion: opening with motion on did not start an animation (running={Motion.RunningCount}, width={panel.PaneWidth})");
            await WaitForMotionAsync();
            Require(Math.Abs(panel.PaneWidth - panel.OpenWidth) < 0.5 && panel.IsOpen, $"motion: the panel did not settle open (width={panel.PaneWidth})");

            model.CleanUp.Inspect(second);
            Require(Motion.RunningCount > 0, "motion: switching items did not cross-fade");
            await WaitForMotionAsync();
            Require(panel.Shown == second.Id && panel.IsOpen, $"motion: the panel shows {panel.Shown}, not {second.Id}");

            model.CleanUp.Inspected = null;
            Require(!panel.IsOpen && Motion.RunningCount > 0 && panel.Shown == second.Id, "motion: closing did not animate or dropped the item at once");
            await WaitForMotionAsync();
            Require(panel.PaneWidth == 0, $"motion: the panel did not close (width={panel.PaneWidth})");
            Log.Write("motion: on path ok, the panel slid open, cross-faded to the next item and slid shut");
        }
        finally
        {
            Motion.Force = null;
            model.CleanUp.Inspected = null;
        }

        Require(!Motion.Enabled, "motion: screenshot mode should have motion off");
        Log.Write("sidepanel: ok");
    }

    private static async Task WaitForMotionAsync()
    {
        for (int i = 0; i < 60 && Motion.RunningCount > 0; i++)
        {
            await Task.Delay(50);
        }

        if (Motion.RunningCount > 0)
        {
            throw new InvalidOperationException("motion: an animation did not finish within 3 s");
        }
    }

    private static string Describe(object? element)
        => element is DependencyObject d ? $"{d.GetType().Name} '{Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(d)}'" : "nothing";

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
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
        if (full)
        {
            await HomeScanningAsync(suffix);
            await HomeStatesAsync(suffix);
            await FlyoutAsync(suffix);
            await GoAsync("home");
        }

        await GoAsync("cleanup");
        model.CleanUp.Inspected = null;
        await ShotAsync("cleanup", suffix);
        if (full)
        {
            await SidebarSubRowsAsync(suffix);
        }
        model.CleanUp.Inspect(model.CleanUp.All.First(f => f.Id == "pnpm-store"));
        await ShotAsync("inspector", suffix);
        RequireOnScreen("Select pnpm store for cleaning", "the pinned footer of the inspector");
        if (full)
        {
            model.CleanUp.Inspect(model.CleanUp.All.First(f => f.Id == "windows-update-cleanup"));
            await ShotAsync("inspector-admin", suffix);
            RequireOnScreen("Open the command in a PowerShell window that waits for Enter", "the pinned footer of the admin inspector");
            model.CleanUp.Inspect(model.CleanUp.All.First(f => f.Id == "chrome-cache"));
            await ShotAsync("inspector-blocked", suffix);
            Require(window.Find<Button>("Quit Chrome") is not null, "cleanup: a blocked item's inspector has no Quit Chrome button");
            RequireOnScreen("Select Chrome cache · Default profile for cleaning", "the pinned footer of the blocked item's inspector");
        }

        model.CleanUp.Inspected = null;
        foreach (FindingViewModel item in model.CleanUp.All.Where(f => f.CanSelect))
        {
            item.IsSelected = item.Id is "npm-cache" or "gradle-caches" or "pnpm-store" or "chrome-cache" or "jetbrains-old:IntelliJIdea2024.1" or "android-images:android-30" or "leftover:postman";
        }

        await ShotAsync("selection", suffix);
        await model.Review.OpenAsync(model.CleanUp.Selected.Select(f => new PlannedAction(f.Finding, f.ChosenAction)).ToList());
        await ShotAsync("review", suffix);
        Require(model.Review.Groups.SelectMany(g => g.Rows).Any(r => r.QuitApps.Contains("chrome", StringComparer.OrdinalIgnoreCase)), "review: the blocked item has no Quit button data");
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
            await ProjectsTourAsync(suffix);
        }

        if (model.Places.Any(p => p.Id == "health"))
        {
            await HealthWalkAsync(suffix, full);
        }

        if (full)
        {
            await WideAsync(suffix);
        }

        await GoAsync("history");
        await ShotAsync("history", suffix);
        await SettingsWalkAsync(suffix);
    }

    /// <summary>
    /// Every state of the Projects page with the sample data: the first-run question, the list with each filter, search with results and
    /// without, the detail panel for each kind of repo, each sheet, the GitHub cards, the deep search running and finished, a 5,000-project
    /// list (only the rows on screen are built), and the three other tabs. Also checks, the way UI Automation would, that every action in
    /// the panel is on screen and that typing in the search box filters the list.
    /// </summary>
    private async Task ProjectsTourAsync(string suffix)
    {
        ProjectsViewModel vm = model.Projects;
        vm.Tab = ProjectsTab.Projects;
        vm.Filter = ProjectFilter.All;
        vm.Search = string.Empty;
        vm.AccountFilter = null;
        vm.SelectedId = null;
        vm.CloseSheet();
        vm.Dismiss();
        vm.DeepState = DeepSearchState.Idle;
        vm.DeepResult = null;
        (model.Backend as SampleBackend)?.ResetProjectsConsent();
        vm.Report = new DevSweep.App.ViewModels.Places.ProjectsReport(SampleData.Projects, SampleData.Activity);
        await window.WaitForRenderAsync();

        if (vm.NeedsDeepSearchConsent)
        {
            await ShotAsync("projects-consent", suffix);
            await vm.AnswerDeepSearchAsync(false);
            vm.Dismiss();
        }

        // The full list, every filter, the account filter.
        await ShotAsync("projects-projects", suffix);
        vm.Filter = ProjectFilter.OnThisPc;
        await ShotAsync("projects-onpc", suffix);
        vm.Filter = ProjectFilter.OnGitHub;
        await ShotAsync("projects-github", suffix);
        vm.Filter = ProjectFilter.All;
        vm.AccountFilter = "alex-acme";
        await ShotAsync("projects-account", suffix);
        vm.AccountFilter = null;

        // Search, driven through the box itself.
        Microsoft.UI.Xaml.Controls.TextBox box = window.Find<Microsoft.UI.Xaml.Controls.TextBox>("Search projects") ?? throw new InvalidOperationException("projects: the search box is not on screen.");
        int all = vm.Visible.Count;
        box.Text = "PAY";
        await window.WaitForRenderAsync();
        Require(vm.Search == "PAY" && vm.Visible.Count == 1 && vm.Visible[0].NameWithOwner == "acme/payments", $"projects: typing PAY gave {vm.Visible.Count} projects (search '{vm.Search}')");
        Require(ProjectListTab.ListStats?.Invoke() is { Count: 1 }, "projects: the list on screen did not follow the search");
        await ShotAsync("projects-search", suffix);
        box.Text = "design";
        await window.WaitForRenderAsync();
        Require(vm.Visible.Count == 1 && vm.Visible[0].Name == "design-system", "projects: searching a description/name failed");
        box.Text = "storefront";
        await window.WaitForRenderAsync();
        Require(vm.Visible.Count == 1 && vm.Visible[0].Name == "web-shop", "projects: searching the description failed");
        box.Text = "zzz-nothing";
        await window.WaitForRenderAsync();
        await ShotAsync("projects-search-none", suffix);
        box.Text = string.Empty;
        await window.WaitForRenderAsync();
        Require(vm.Visible.Count == all, "projects: clearing the search did not bring everything back");
        Log.Write($"projects: search filters the list ({all} projects, PAY gives 1, nothing gives 0) through the search box");

        // The panel for each kind of repo; each action is on screen.
        (string Id, string Shot, string[] Buttons)[] panels =
        [
            ("alex/inventory-api", "projects-detail-safe", ["Open inventory-api in editor (Visual Studio Code)", "Show in File Explorer", "Open on GitHub", "Compare inventory-api with GitHub now", "Remove inventory-api from this PC"]),
            ("alex/web-shop", "projects-detail-unpushed", ["Open web-shop in editor (Visual Studio Code)", "Push 3 commits in a terminal window", "Remove web-shop from this PC"]),
            (@"local:C:\Users\alex\Documents\scratch-notes", "projects-detail-noremote", ["Publish scratch-notes to GitHub", "Remove scratch-notes from this PC", "Show in File Explorer"]),
            ("alex/dotfiles", "projects-detail-github", ["Download alex/dotfiles", "Open on GitHub"]),
            ("alex/ml-notebooks", "projects-detail-onedrive", ["Show in File Explorer", "Remove ml-notebooks from this PC"]),
            ("alex/lfs-assets", "projects-detail-lfs", ["Open lfs-assets in editor (Visual Studio Code)", "Compare lfs-assets with GitHub now"]),
            ("alex/api-experiments", "projects-detail-wsl", ["Show in File Explorer", "Open on GitHub"]),
            (@"local:D:\clients\landing-page", "projects-detail-otherhost", ["Open landing-page in editor (Visual Studio Code)", "Compare landing-page with gitlab.com now", "Show in File Explorer"]),
        ];
        foreach ((string id, string shot, string[] buttons) in panels)
        {
            vm.SelectedId = id;
            await window.WaitForRenderAsync();
            foreach (string name in buttons)
            {
                Require(window.Find<Microsoft.UI.Xaml.Controls.Button>(name) is not null, $"projects: '{name}' is not reachable in the panel of {id}");
                if (name.StartsWith("Push ", StringComparison.Ordinal) || name.StartsWith("Remove ", StringComparison.Ordinal) || name.StartsWith("Publish ", StringComparison.Ordinal))
                {
                    RequireOnScreen(name, $"the pinned footer of the panel of {id}");
                }
            }

            if (id == "alex/api-experiments")
            {
                Require(window.Find<Microsoft.UI.Xaml.Controls.Button>("Remove api-experiments from this PC") is null && window.Find<Microsoft.UI.Xaml.Controls.Button>("Open api-experiments in editor (Visual Studio Code)") is null,
                    "projects: a WSL project offers removal or an editor");
            }

            await ShotAsync(shot, suffix);
        }

        // Invoking actions the way UI Automation does.
        vm.SelectedId = "alex/inventory-api";
        await window.WaitForRenderAsync();
        var fetch = window.Find<Microsoft.UI.Xaml.Controls.Button>("Compare inventory-api with GitHub now")!;
        new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(fetch).Invoke();
        for (int i = 0; i < 40 && vm.IsFetching; i++)
        {
            await Task.Delay(50);
        }

        Require(vm.Notice is { } n && n.Text.Contains("inventory-api", StringComparison.Ordinal), "projects: Fetch now did nothing");
        vm.SelectedId = null;
        vm.Dismiss();
        vm.SelectedId = "alex/dotfiles";
        await window.WaitForRenderAsync();
        new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(window.Find<Microsoft.UI.Xaml.Controls.Button>("Download alex/dotfiles")!).Invoke();
        await window.WaitForRenderAsync();
        Require(vm.Sheet == ProjectSheet.Clone && vm.SheetProject?.Id == "alex/dotfiles", "projects: Download did not open the Clone sheet");
        await ShotAsync("projects-sheet-clone", suffix);
        vm.CloseSheet();
        vm.SelectedId = null;
        Log.Write("projects: every action of the panel is on screen for each kind of repo; Fetch now and Download work through UI Automation");

        // The other sheets.
        vm.OpenSheet(ProjectSheet.Publish, vm.All.First(p => p.Id == @"local:C:\Users\alex\Documents\scratch-notes"));
        await Task.Delay(300);
        await ShotAsync("projects-sheet-publish", suffix);
        vm.CloseSheet();
        vm.OpenSheet(ProjectSheet.AddByUrl);
        await window.WaitForRenderAsync();
        await ShotAsync("projects-sheet-url", suffix);
        window.Find<Microsoft.UI.Xaml.Controls.TextBox>("Repository address")!.Text = "https://github.com/acme/handbook";
        await ShotAsync("projects-sheet-url-filled", suffix);
        vm.CloseSheet();
        vm.OpenSheet(ProjectSheet.AddAccount);
        await ShotAsync("projects-sheet-account", suffix);
        vm.CloseSheet();

        // GitHub not set up: every local clone is still listed.
        foreach ((GitHubStatus status, string shot) in new[] { (GitHubStatus.NotInstalled, "projects-gh-missing"), (GitHubStatus.NotSignedIn, "projects-gh-signedout") })
        {
            vm.Report = new DevSweep.App.ViewModels.Places.ProjectsReport(SampleData.ProjectsWith(status), []);
            await window.WaitForRenderAsync();
            Require(vm.Visible.Count > 0 && vm.Visible.All(p => p.OnDisk), $"projects: with {status}, local clones were not all listed");
            Require(window.Find<Microsoft.UI.Xaml.Controls.Button>(status == GitHubStatus.NotInstalled ? "Install GitHub CLI" : "Sign in to GitHub…") is not null, $"projects: the {status} card has no button");
            await ShotAsync(shot, suffix);
        }

        vm.Report = new DevSweep.App.ViewModels.Places.ProjectsReport(SampleData.Projects, SampleData.Activity);
        await window.WaitForRenderAsync();

        // Deep search, running and finished.
        vm.DeepState = DeepSearchState.Running;
        vm.DeepProgress = new DevSweep.Core.Projects.Discovery.DeepSearchProgress(184_231, 11, @"C:\Users\alex\AppData\Local\Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState");
        await ShotAsync("projects-deep-running", suffix);
        vm.DeepState = DeepSearchState.Done;
        vm.DeepResult = "Looked through 482,113 folders on C: and D: in 2 min 14 s and found 11 repos.";
        await ShotAsync("projects-deep-result", suffix);
        vm.DeepResult = null;
        vm.DeepState = DeepSearchState.Idle;

        // Thousands of projects: only the rows on screen exist.
        var many = new List<Project>(SampleData.Projects.Projects);
        for (int i = 0; i < 5000; i++)
        {
            many.Add(new Project
            {
                Id = $"bulk/repo-{i:0000}", Name = $"repo-{i:0000}", NameWithOwner = $"bulk/repo-{i:0000}", Owner = "bulk", OnGitHub = true, Account = "alex",
                Description = "Generated for the list test", RemoteKB = 1024 + i, LastActivity = SampleData.Now.AddDays(-i % 400),
            });
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        vm.Report = new DevSweep.App.ViewModels.Places.ProjectsReport(SampleData.Projects with { Projects = many }, SampleData.Activity);
        await window.WaitForRenderAsync();
        watch.Stop();
        (int count, int realized) = ProjectListTab.ListStats?.Invoke() ?? (0, 0);
        Require(count == many.Count && realized is > 5 and < 60, $"projects: a list of {many.Count} built {realized} rows");
        var filter = System.Diagnostics.Stopwatch.StartNew();
        int hits = ProjectListing.Apply(many, ProjectFilter.All, null, "repo-49").Count;
        filter.Stop();
        Log.Write($"projects: {count} projects listed with {realized} rows built ({watch.ElapsedMilliseconds} ms to show them); searching them took {filter.Elapsed.TotalMilliseconds:0.0} ms ({hits} hits)");
        await ShotAsync("projects-many", suffix);
        vm.Report = new DevSweep.App.ViewModels.Places.ProjectsReport(SampleData.Projects, SampleData.Activity);
        await window.WaitForRenderAsync();

        // The other tabs.
        vm.SelectedId = null;
        vm.Tab = ProjectsTab.Cleanup;
        await window.WaitForRenderAsync();
        await ShotAsync("projects-cleanup", suffix);
        vm.SelectedId = "alex/old-web";
        await ShotAsync("projects-cleanup-detail", suffix);
        vm.SelectedId = null;
        vm.Tab = ProjectsTab.Accounts;
        await ShotAsync("projects-accounts", suffix);
        vm.Tab = ProjectsTab.Activity;
        await ShotAsync("projects-activity", suffix);
        vm.Tab = ProjectsTab.Projects;
    }

    /// <summary>Home while a scan runs (the cleanable number gives way to the progress).</summary>
    private async Task HomeScanningAsync(string suffix)
    {
        var scan = model.Scan;
        string[] titles = ["npm cache"];
        scan.IsScanning = true;
        scan.OnProgress(new ScanProgress(ScanPhase.Checking, 21, 50, "check-1", true, titles));
        await ShotAsync("home-scanning", suffix);
        scan.IsScanning = false;
        scan.Status = SampleData.Report.Summary;
        scan.Checks.Clear();
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

    /// <summary>Projects in high contrast: the list, a panel, a sheet, the GitHub card, search with no result and the other three tabs.</summary>
    private async Task ProjectsHighContrastAsync()
    {
        ProjectsViewModel vm = model.Projects;
        vm.Tab = ProjectsTab.Projects;
        vm.Filter = ProjectFilter.All;
        vm.Search = string.Empty;
        vm.SelectedId = null;
        vm.CloseSheet();
        vm.Dismiss();
        vm.Report = new ProjectsReport(SampleData.Projects, SampleData.Activity);
        await ShotAsync("projects-projects", "highcontrast");
        vm.SelectedId = "alex/web-shop";
        await ShotAsync("projects-detail-unpushed", "highcontrast");
        vm.SelectedId = null;
        vm.Search = "zzz-nothing";
        await ShotAsync("projects-search-none", "highcontrast");
        vm.Search = string.Empty;
        vm.OpenSheet(ProjectSheet.Publish, vm.All.First(p => p.Id == @"local:C:\Users\alex\Documents\scratch-notes"));
        await Task.Delay(300);
        await ShotAsync("projects-sheet-publish", "highcontrast");
        vm.CloseSheet();
        vm.Report = new ProjectsReport(SampleData.ProjectsWith(GitHubStatus.NotInstalled), []);
        await ShotAsync("projects-gh-missing", "highcontrast");
        vm.Report = new ProjectsReport(SampleData.Projects, SampleData.Activity);
        foreach ((ProjectsTab tab, string shot) in new[] { (ProjectsTab.Cleanup, "projects-cleanup"), (ProjectsTab.Accounts, "projects-accounts"), (ProjectsTab.Activity, "projects-activity") })
        {
            vm.Tab = tab;
            await ShotAsync(shot, "highcontrast");
        }

        vm.Tab = ProjectsTab.Projects;
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
            model.OpenSettings("accounts");
            await window.WaitForRenderAsync();
            await ShotAsync("settings-github", "highcontrast");
            model.OpenSettings("permissions");
            await window.WaitForRenderAsync();
            await ShotAsync("settings-privacy", "highcontrast");
            await GoAsync("home");
            await GoAsync("cleanup");
            model.CleanUp.Inspect(model.CleanUp.All.First(f => f.Id == "pnpm-store"));
            await ShotAsync("cleanup", "highcontrast");
            model.CleanUp.Inspected = null;
            await model.Review.OpenAsync(model.CleanUp.All.Where(f => f.CanSelect).Take(5).Select(f => new PlannedAction(f.Finding, f.ChosenAction)).ToList());
            await ShotAsync("review", "highcontrast");
            model.Review.Close();
            model.CleanUp.Inspect(model.CleanUp.All.First(f => f.Id == "chrome-cache"));
            await ShotAsync("inspector-blocked", "highcontrast");
            model.CleanUp.Inspected = null;
            await GoAsync("history");
            await ShotAsync("history", "highcontrast");
            if (model.Places.Any(p => p.Id == "projects"))
            {
                await GoAsync("projects");
                await ProjectsHighContrastAsync();
            }

            await HealthHighContrastAsync();
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
        CheckScreen($"{name}-{suffix}");
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
