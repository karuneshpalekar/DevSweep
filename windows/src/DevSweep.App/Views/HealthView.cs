using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Text;
using DevSweep.Core.Health.Ports;
using DevSweep.Core.Health.Runtimes;
using DevSweep.Core.Health.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DevSweep.App.Views;

/// <summary>Health: Tools and versions, Security, Ports, bound to M3b's result types. Tabs follow their flags.</summary>
internal static class HealthView
{
    public static UIElement Build(MainViewModel model, Subscriptions subs, MainWindow window)
    {
        HealthViewModel vm = model.Health;
        TextBlock subtitle = Ui.Secondary("Your tools, files that look like secrets, and what's listening.");
        var tabs = new SelectorBar();
        AutomationProperties.SetName(tabs, "Health tabs");
        foreach (HealthTab tab in vm.Tabs)
        {
            var item = new SelectorBarItem { Text = HealthViewModel.TabTitle(tab), Tag = tab, IsSelected = tab == vm.Tab };
            AutomationProperties.SetName(item, HealthViewModel.TabTitle(tab));
            tabs.Items.Add(item);
        }

        tabs.SelectionChanged += (_, _) =>
        {
            if (tabs.SelectedItem?.Tag is HealthTab tab)
            {
                vm.Tab = tab;
            }
        };
        var body = new StackPanel { Spacing = 16 };
        Button refresh = Ui.Button("Check again", () => _ = vm.LoadAsync());
        StackPanel page = Ui.V(16, Ui.PageHeader("Health", subtitle, refresh), tabs, body);

        void Render()
        {
            body.Children.Clear();
            foreach (SelectorBarItem item in tabs.Items)
            {
                item.IsSelected = item.Tag is HealthTab t && t == vm.Tab;
                string count = item.Tag is HealthTab tab ? vm.Count(tab) : string.Empty;
                item.Text = HealthViewModel.TabTitle((HealthTab)item.Tag) + (count.Length > 0 ? $" ({count})" : string.Empty);
            }

            Ui.Show(tabs, vm.State != PlaceState.Unavailable);
            Ui.Show(refresh, vm.State is PlaceState.Ready or PlaceState.Failed);
            switch (vm.State)
            {
                case PlaceState.Unavailable:
                    body.Children.Add(Ui.Card(Ui.V(8, Ui.Text("Health is off", "DsSubtitle"), Ui.Secondary(HealthViewModel.UnavailableText), Ui.Button("Open Settings", () => model.Go("settings")))));
                    return;
                case PlaceState.NotChecked:
                    body.Children.Add(Ui.Card(Ui.V(8, Ui.Text("Not checked yet", "DsSubtitle"), Ui.Button("Check now", () => _ = vm.LoadAsync(), accent: true))));
                    return;
                case PlaceState.Loading:
                    body.Children.Add(Ui.H(10, new ProgressRing { IsActive = true, Width = 20, Height = 20 }, Ui.Secondary("Checking your tools and files…")));
                    return;
                case PlaceState.Failed:
                    body.Children.Add(Ui.Text("Health couldn't finish: " + vm.Error, "DsCriticalText"));
                    return;
            }

            if (vm.Error is { } error)
            {
                body.Children.Add(Ui.Text(error, "DsWarningText"));
            }

            switch (vm.Tab)
            {
                case HealthTab.Tools:
                    Tools(vm, body);
                    break;
                case HealthTab.Security:
                    Security(vm, body);
                    break;
                default:
                    Ports(vm, body);
                    break;
            }
        }

        subs.On(vm, null, Render);
        return Ui.Scroll(page);
    }

    private static void Tools(HealthViewModel vm, StackPanel body)
    {
        if (vm.Report?.Versions is not { } versions)
        {
            body.Children.Add(Ui.Secondary("Tools and versions is turned off."));
            return;
        }

        if (versions.EolOffline)
        {
            body.Children.Add(Ui.Caption("Support dates come from an old copy (endoflife.date couldn't be reached)."));
        }

        foreach (RuntimeReport tool in versions.Runtimes.Where(r => r.Installs.Count > 0 || r.NotInstalls.Count > 0))
        {
            var card = new StackPanel { Spacing = 8 };
            SupportStatus worst = tool.Worst;
            card.Children.Add(Ui.Row(Ui.Text(tool.Name, "DsSubtitle"), Support(worst)));
            if (tool.Resolution?.Runs is { } runs)
            {
                card.Children.Add(Ui.Caption($"A new terminal runs {runs}{(tool.Resolution.Via is { } via ? $" (via {via})" : string.Empty)}."));
            }

            foreach (Installation install in tool.Installs)
            {
                StackPanel tags = Ui.H(6, Support(install.Support));
                if (install.IsDefault)
                {
                    tags.Children.Insert(0, Ui.Badge("Default", Tone.Accent, "The one a new terminal runs."));
                }

                if (install.PatchAvailable)
                {
                    tags.Children.Add(Ui.Badge($"{install.LatestInCycle} available", Tone.Neutral));
                }

                card.Children.Add(Ui.Row(Ui.V(0, Ui.Text($"{install.Version} · {install.SourceTitle}", "DsStrong"), Ui.Caption(install.Path)), tags));
            }

            foreach (string notInstall in tool.NotInstalls)
            {
                card.Children.Add(Ui.Caption("Not an install: " + notInstall));
            }

            foreach (RuntimeIssue issue in tool.Issues)
            {
                card.Children.Add(Ui.Text(issue.Text, issue.Level == IssueLevel.Info ? "DsSecondary" : "DsWarningText"));
            }

            foreach (RuntimeStep step in tool.Steps)
            {
                card.Children.Add(Ui.Card(Ui.V(6,
                    Ui.Row(Ui.V(0, Ui.Text(step.Title, "DsStrong"), Ui.Caption(step.Detail)),
                        Ui.Button("Copy", () => Shell.Copy(string.Join(Environment.NewLine, step.Commands)), $"Copy the commands for {step.Title}"),
                        Ui.Button(step.NeedsAdmin ? "Run in PowerShell (asks for admin)" : "Run in PowerShell", () => vm.OpenFix(step), $"Open {step.Title} in PowerShell")),
                    Ui.Text(string.Join(Environment.NewLine, step.Commands), "DsMono")), "DsCodeBox"));
            }

            body.Children.Add(Ui.Card(card));
        }

        if (versions.Runtimes.Any(r => r.Steps.Count > 0))
        {
            body.Children.Add(Ui.Caption("Run in PowerShell opens a window that lists the commands and waits for you to press Enter. Nothing runs before that, and nothing is elevated without Windows asking you (UAC)."));
        }

        if (versions.Projects.Count > 0)
        {
            var projects = new StackPanel { Spacing = 6 };
            foreach (ProjectRequirement requirement in versions.Projects)
            {
                Tone tone = requirement.Status == RequirementStatus.Ok ? Tone.Good : Tone.Caution;
                string status = requirement.Status switch { RequirementStatus.Ok => "OK", RequirementStatus.NotInstalled => "Not installed", _ => "End of life" };
                projects.Children.Add(Ui.Row(Ui.V(0, Ui.Text($"{requirement.Project} asks for {requirement.ToolName} {requirement.Spec}", "DsStrong"), Ui.Caption($"{requirement.File} · {requirement.Message}")), Ui.Badge(status, tone)));
            }

            body.Children.Add(Ui.V(8, Ui.Text("Your projects", "DsStrong"),
                Ui.Caption("Versions your projects ask for, from files like .nvmrc, package.json engines, .python-version, global.json and Gradle toolchains."),
                Ui.Card(projects)));
        }
    }

    private static Border Support(SupportStatus status) => status switch
    {
        SupportStatus.EndOfLife => Ui.Badge("End of life", Tone.Critical, "No more security fixes from upstream."),
        SupportStatus.EndingSoon => Ui.Badge("Support ending soon", Tone.Caution, "Support ends within 120 days."),
        SupportStatus.Supported => Ui.Badge("Supported", Tone.Good),
        _ => Ui.Badge("Not checked", Tone.Neutral, "No support dates for it."),
    };

    private static void Security(HealthViewModel vm, StackPanel body)
    {
        if (vm.Report?.Security is not { } security)
        {
            body.Children.Add(Ui.Secondary("Security is turned off."));
            return;
        }

        if (security.Findings.Count == 0)
        {
            body.Children.Add(Ui.Card(Ui.V(6, Ui.Text("No secrets lying around", "DsSubtitle"), Ui.Secondary("DevSweep looked for recovery codes, private keys, cloud keys, password exports and .env files, and found none."))));
        }

        AddSecurity(body, "Needs action", vm.NeedsAction);
        AddSecurity(body, "Looks fine", vm.LooksFine);
        body.Children.Add(Ui.Caption($"Looked in {string.Join(", ", security.Looked)}. Names and at most the first 64 KB are read; secrets are never shown or stored.{(security.CloudFilesSkipped > 0 ? $" {security.CloudFilesSkipped} online-only files were not opened." : string.Empty)}"));
        if (security.GitUnavailable is { } git)
        {
            body.Children.Add(Ui.Caption(git));
        }
    }

    private static void AddSecurity(StackPanel body, string title, IReadOnlyList<SecurityFinding> findings)
    {
        if (findings.Count == 0)
        {
            return;
        }

        var rows = new StackPanel { Spacing = 10 };
        foreach (SecurityFinding finding in findings)
        {
            if (rows.Children.Count > 0)
            {
                rows.Children.Add(Ui.Divider());
            }

            Tone tone = finding.Level switch { SecurityLevel.Critical => Tone.Critical, SecurityLevel.Warning => Tone.Caution, _ => Tone.Good };
            rows.Children.Add(Ui.V(4,
                Ui.Row(Ui.H(10, Ui.Badge(finding.Status, tone), Ui.Text(finding.Title, "DsStrong")), Ui.Button("Show in File Explorer", () => Shell.Reveal(finding.Path), $"Show {finding.Title} in File Explorer")),
                Ui.Caption(finding.Path),
                Ui.Secondary(finding.Why),
                Ui.Text(finding.WhatToDo)));
        }

        body.Children.Add(Ui.V(8, Ui.Text(title, "DsStrong"), Ui.Card(rows)));
    }

    private static void Ports(HealthViewModel vm, StackPanel body)
    {
        if (vm.Report?.Ports is not { } ports)
        {
            body.Children.Add(Ui.Secondary("Ports is turned off."));
            return;
        }

        if (ports.Ports.Count == 0)
        {
            body.Children.Add(Ui.Card(Ui.V(6, Ui.Text("Nothing is listening", "DsSubtitle"), Ui.Secondary("Dev servers and databases you start show up here."))));
        }

        AddPorts(body, "Development", vm.DevelopmentPorts);
        AddPorts(body, "Apps and Windows", vm.OtherPorts);
        if (ports.OthersHidden > 0)
        {
            body.Children.Add(Ui.Caption($"{ports.OthersHidden} listeners of other users and services are hidden."));
        }

        if (ports.FirewallProblem is { } problem)
        {
            body.Children.Add(Ui.Caption(problem));
        }
    }

    private static void AddPorts(StackPanel body, string title, IReadOnlyList<ListeningPort> list)
    {
        if (list.Count == 0)
        {
            return;
        }

        var rows = new StackPanel { Spacing = 10 };
        foreach (ListeningPort port in list)
        {
            if (rows.Children.Count > 0)
            {
                rows.Children.Add(Ui.Divider());
            }

            Border exposure = port.Exposure == Exposure.Loopback
                ? Ui.Badge("Only this PC", Tone.Good)
                : Ui.Badge("Every network interface", Tone.Caution, "Other devices on your network may be able to connect. Most dev servers can listen on localhost only.");
            string port1 = port.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            rows.Children.Add(Ui.V(4,
                Ui.Row(Ui.H(10, Ui.Text($":{port1}", "DsStrong"), Ui.Text($"{port.Label} ({port.ProcessName}, PID {port.Pid})")), exposure,
                    Ui.Button("Copy port", () => Shell.Copy(port1), $"Copy port {port1}")),
                port.Folder is { } folder ? Ui.Caption(folder) : null,
                port.Firewall is { } firewall ? Ui.Caption(firewall.Summary) : null));
        }

        body.Children.Add(Ui.V(8, Ui.Text(title, "DsStrong"), Ui.Card(rows)));
    }
}
