using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Places;
using DevSweep.App.ViewModels.Sample;
using DevSweep.Core.Health.Packages;
using DevSweep.Core.Health.Ports;
using DevSweep.Core.Health.Runtimes;
using DevSweep.Core.Health.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DevSweep.App.Shots;

/// <summary>Health's part of the tour: every tab, every panel, the states, and UI Automation checks that each panel action is reachable.</summary>
internal sealed partial class ScreenshotTour
{
    private int _healthChecks;

    private async Task HealthWalkAsync(string suffix, bool full)
    {
        HealthViewModel health = model.Health;
        await GoAsync("health");
        await health.PackagesTask;
        health.ToolsFilter = ToolsFilter.All;
        health.Tab = HealthTab.Tools;
        health.SelectedTool = null;
        await ShotAsync("health-tools", suffix);
        if (!full)
        {
            foreach (HealthTab tab in health.Tabs.Where(t => t != HealthTab.Tools))
            {
                health.Tab = tab;
                await ShotAsync("health-" + tab.ToString().ToLowerInvariant(), suffix);
            }

            return;
        }

        // Tools and versions: an end-of-life runtime with its fix step, several installs with a shadowed default and a Store alias, an OK runtime.
        await ToolPanelAsync(health, "node", "health-tools-node-eol", suffix, ["Show 22.21.0 from nvm-windows in File Explorer", "Copy the path of 22.21.0 from nvm-windows", "Copy the commands for Remove Node.js 18.20.4", "Open Remove Node.js 18.20.4 in PowerShell"]);
        await ToolPanelAsync(health, "python", "health-tools-python", suffix, []);
        ScrollPanel(1.0);
        await ShotAsync("health-tools-python-notes", suffix);
        await ToolPanelAsync(health, "postgresql", "health-tools-postgres", suffix, ["Open Update 16.4 to 16.10 in PowerShell"]);
        await ToolPanelAsync(health, "go", "health-tools-ok", suffix, []);
        await ToolPanelAsync(health, "packages", "health-tools-packages", suffix, ["Open Upgrade 4 packages with winget in PowerShell", "Open Upgrade 1 package with Chocolatey in PowerShell"]);
        ScrollPanel(0.55);
        await ShotAsync("health-tools-packages-scoop", suffix);
        ScrollPanel(1.0);
        await ShotAsync("health-tools-packages-more", suffix);
        await ToolPanelAsync(health, "projects", "health-tools-projects", suffix, []);
        health.SelectedTool = null;
        health.ToolsFilter = ToolsFilter.NeedsAttention;
        await ShotAsync("health-tools-filtered", suffix);
        health.ToolsFilter = ToolsFilter.All;

        // Security.
        health.Tab = HealthTab.Security;
        await ShotAsync("health-security", suffix);
        await SecurityPanelAsync(health, "AWS access keys", "health-security-aws", suffix, ["Show AWS access keys in File Explorer", "Ignore AWS access keys", "Move AWS access keys to the Recycle Bin"]);
        await SecurityPanelAsync(health, ".env.production with 4 keys is committed in old-web", "health-security-env", suffix, ["Copy the commands for Fix it in git", "Show .env.production with 4 keys is committed in old-web in File Explorer"]);
        ScrollPanel(1.0);
        await ShotAsync("health-security-env-actions", suffix);
        await SecurityPanelAsync(health, "Password manager export", "health-security-cloud", suffix, ["Show Password manager export in File Explorer"]);
        ScrollPanel(1.0);
        await ShotAsync("health-security-cloud-actions", suffix);
        Button blocked = window.Find<Button>("Move Password manager export to the Recycle Bin") ?? throw new InvalidOperationException("health: the Recycle Bin button of the OneDrive finding is not on screen");
        Require(!blocked.IsEnabled, "health: the Recycle Bin button should be blocked for an online-only OneDrive file");
        Log.Write("health: a OneDrive finding is blocked from the Recycle Bin with a plain reason");
        await SecurityPanelAsync(health, "SSH private key", "health-security-fine", suffix, []);
        health.SelectedSecurity = null;

        // Ports.
        health.Tab = HealthTab.Ports;
        await ShotAsync("health-ports", suffix);
        health.SelectedPort = health.DevelopmentPorts.First(p => p.Port == 5173).Id;
        await ShotAsync("health-ports-selected", suffix);
        Require(window.Find<Button>("Open http://localhost:5173 in your browser") is not null, "health: Open in browser is not reachable for a dev web port");
        Require(window.Find<Button>("Copy http://localhost:5173") is not null, "health: Copy URL is not reachable");
        Button stop = window.Find<Button>("Stop Vite dev server in web-shop") ?? throw new InvalidOperationException("health: Stop is not reachable on a development port");
        Invoke(stop);
        await window.WaitForRenderAsync();
        await ShotAsync("health-ports-stop-confirm", suffix);
        Require(window.Find<Button>("Stop Vite dev server in web-shop") is not null, "health: the Stop confirmation has no Stop button");
        health.Stop = StopPhase.ConfirmEnd;
        health.StopMessage = "node is still running after DevSweep sent Ctrl+C to its console.";
        await window.WaitForRenderAsync();
        await ShotAsync("health-ports-still-running", suffix);
        Require(window.Find<Button>("End node now") is not null && window.Find<Button>("Leave it running") is not null, "health: the still-running step has no End it / Leave it buttons");
        health.CancelStop();
        health.SelectedPort = health.OtherPorts.First(p => p.Port == 5040).Id;
        await ShotAsync("health-ports-refused", suffix);
        health.SelectedPort = null;

        await HealthStatesAsync(health, suffix);
        health.Tab = HealthTab.Tools;
        Log.Write($"health: {_healthChecks} panel actions found through UI Automation names");
    }

    /// <summary>Health in high contrast: the tools list with a panel, and a security finding.</summary>
    private async Task HealthHighContrastAsync()
    {
        if (!model.Places.Any(p => p.Id == "health"))
        {
            return;
        }

        HealthViewModel health = model.Health;
        await GoAsync("health");
        health.Tab = HealthTab.Tools;
        health.SelectedTool = "python";
        await ShotAsync("health-tools", "highcontrast");
        health.SelectedTool = null;
        health.Tab = HealthTab.Security;
        health.SelectedSecurity = health.NeedsAction[0].Id;
        await ShotAsync("health-security", "highcontrast");
        health.SelectedSecurity = null;
        health.Tab = HealthTab.Ports;
        health.SelectedPort = health.DevelopmentPorts[0].Id;
        await ShotAsync("health-ports", "highcontrast");
        health.SelectedPort = null;
        health.Tab = HealthTab.Tools;
    }

    /// <summary>The loading, off, failed and empty states.</summary>
    private async Task HealthStatesAsync(HealthViewModel health, string suffix)
    {
        HealthReport? report = health.Report;
        health.Tab = HealthTab.Tools;
        health.State = PlaceState.Loading;
        await ShotAsync("health-loading", suffix);
        health.State = PlaceState.NotChecked;
        await ShotAsync("health-notchecked", suffix);
        health.Error = "git didn't answer in time.";
        health.State = PlaceState.Failed;
        await ShotAsync("health-failed", suffix);
        health.Error = null;
        health.State = PlaceState.Unavailable;
        await ShotAsync("health-off", suffix);
        Require(window.Find<Button>("Turn on Health") is not null, "health: the off state has no one-click enable");
        health.State = PlaceState.Ready;

        health.Report = new HealthReport(new VersionsResult { Date = SampleData.Now }, new SecurityResult { Date = SampleData.Now, Looked = new List<string> { @"C:\Users\alex\Downloads", @"D:\src" } }, new PortsResult { Date = SampleData.Now });
        await ShotAsync("health-tools-empty", suffix);
        health.Tab = HealthTab.Security;
        await ShotAsync("health-security-empty", suffix);
        health.Tab = HealthTab.Ports;
        await ShotAsync("health-ports-empty", suffix);
        health.Report = report;
        health.Tab = HealthTab.Tools;
    }

    private async Task ToolPanelAsync(HealthViewModel health, string id, string shot, string suffix, string[] reachable)
    {
        health.SelectedTool = id;
        await window.WaitForRenderAsync();
        ScrollPanel(0);
        await ShotAsync(shot, suffix);
        CheckReachable(reachable);
    }

    private async Task SecurityPanelAsync(HealthViewModel health, string title, string shot, string suffix, string[] reachable)
    {
        SecurityFinding finding = health.Report!.Security!.Findings.First(f => f.Title == title);
        health.SelectedSecurity = finding.Id;
        await window.WaitForRenderAsync();
        ScrollPanel(0);
        await ShotAsync(shot, suffix);
        CheckReachable(reachable);
    }

    private void CheckReachable(string[] names)
    {
        foreach (string name in names)
        {
            Require(window.Find<Button>(name) is not null, $"health: '{name}' is not reachable through UI Automation names");
            _healthChecks++;
        }
    }

    private static void Invoke(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }

    /// <summary>Scrolls the open side panel (the last scroll viewer under it) to a fraction of its height.</summary>
    private void ScrollPanel(double fraction)
    {
        if (window.ActivePanel?.Root is not { } root)
        {
            return;
        }

        ScrollViewer? last = null;
        void Walk(DependencyObject node)
        {
            if (node is ScrollViewer viewer)
            {
                last = viewer;
            }

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                Walk(VisualTreeHelper.GetChild(node, i));
            }
        }

        Walk(root);
        last?.ChangeView(null, last.ScrollableHeight * fraction, null, disableAnimation: true);
    }
}
