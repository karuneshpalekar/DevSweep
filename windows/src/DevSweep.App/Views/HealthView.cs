using DevSweep.App.Design;
using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Text;
using DevSweep.Core.Health.Packages;
using DevSweep.Core.Health.Ports;
using DevSweep.Core.Health.Runtimes;
using DevSweep.Core.Health.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DevSweep.App.Views;

/// <summary>
/// Health: Tools and versions (master list and side panel, the Mac's RuntimesView), Security and Ports, each a list with a panel that
/// slides in. The tab bar carries counts; one panel serves all three tabs (its key says which kind of item it shows).
/// </summary>
internal static class HealthView
{
    /// <summary>How often the Ports tab looks again while it is on screen and the window is showing.</summary>
    public static readonly TimeSpan PortsRefresh = TimeSpan.FromSeconds(4);

    private static readonly string[] BodyProperties =
    [
        nameof(HealthViewModel.State), nameof(HealthViewModel.Tab), nameof(HealthViewModel.Report), nameof(HealthViewModel.Packages), nameof(HealthViewModel.IsLoadingPackages),
        nameof(HealthViewModel.ToolsFilter), nameof(HealthViewModel.Error), nameof(HealthViewModel.RestartNeeded), nameof(HealthViewModel.NeedsAction), nameof(HealthViewModel.LooksFine),
    ];

    public static UIElement Build(MainViewModel model, Subscriptions subs, MainWindow window)
    {
        HealthViewModel vm = model.Health;
        TextBlock subtitle = Ui.Secondary(string.Empty);
        Button refresh = Ui.Button("Check again", () => _ = vm.LoadAsync(), "Check again");
        refresh.Content = Ui.H(Tokens.Space2, Ui.Icon(Icons.Refresh, 14), Ui.Text("Check again"));
        SelectorBar tabs = Ds.Tabs(vm.Tabs.Select(HealthViewModel.TabTitle).ToList(), Math.Max(0, vm.Tabs.ToList().IndexOf(vm.Tab)), index =>
        {
            if (index >= 0 && index < vm.Tabs.Count)
            {
                vm.Tab = vm.Tabs[index];
            }
        }, "Health tabs");
        var body = new StackPanel { Spacing = Tokens.Space4 };
        var rows = new Dictionary<string, ListRow>(StringComparer.Ordinal);
        StackPanel page = Ui.V(Tokens.Space4, Ds.PageHeader("Health", subtitle, refresh), tabs, body);

        var panel = new SidePanel(Ds.PageScroll(page), (key, panelSubs) => HealthPanels.Build(model, key, panelSubs, window), "Health details")
        {
            Closed = () => ClearSelection(vm),
        };
        subs.Add(panel.Dispose);
        window.ActivePanel = panel;
        subs.Add(() =>
        {
            if (ReferenceEquals(window.ActivePanel, panel))
            {
                window.ActivePanel = null;
            }
        });

        void Header()
        {
            subtitle.Text = vm.CheckedText;
            for (int i = 0; i < tabs.Items.Count && i < vm.Tabs.Count; i++)
            {
                HealthTab tab = vm.Tabs[i];
                string count = vm.Count(tab);
                ((SelectorBarItem)tabs.Items[i]).Text = HealthViewModel.TabTitle(tab) + (count.Length > 0 ? " · " + count : string.Empty);
                AutomationProperties.SetName((SelectorBarItem)tabs.Items[i], HealthViewModel.TabTitle(tab) + (count.Length > 0 ? $", {count} to look at" : string.Empty));
            }

            Ui.Show(tabs, vm.State != PlaceState.Unavailable && vm.Tabs.Count > 1);
            Ui.Show(refresh, vm.State is PlaceState.Ready or PlaceState.Failed);
        }

        void Render()
        {
            Header();
            rows.Clear();
            body.Children.Clear();
            RenderBody(model, vm, body, rows, subs);
            SyncSelection(vm, panel, rows);
        }

        Render();
        void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is { } name && BodyProperties.Contains(name))
            {
                Render();
            }
            else if (e.PropertyName is nameof(HealthViewModel.CheckedText) or nameof(HealthViewModel.ToolsSummary))
            {
                Header();
            }
        }

        vm.PropertyChanged += OnChanged;
        subs.Add(() => vm.PropertyChanged -= OnChanged);

        void OnSelection(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(HealthViewModel.SelectedTool) or nameof(HealthViewModel.SelectedSecurity) or nameof(HealthViewModel.SelectedPort))
            {
                SyncSelection(vm, panel, rows);
            }
        }

        vm.PropertyChanged += OnSelection;
        subs.Add(() => vm.PropertyChanged -= OnSelection);

        // Ports are read again every few seconds while this tab is on screen and the window is showing; a hidden window pauses it.
        if (Environment.GetEnvironmentVariable("DEVSWEEP_SHOTS") is null)
        {
            var timer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = PortsRefresh };
            timer.Tick += (_, _) =>
            {
                if (vm.Tab == HealthTab.Ports && vm.State == PlaceState.Ready && window.IsShowing && !vm.IsRefreshingPorts)
                {
                    _ = vm.RefreshPortsAsync();
                }
            };
            timer.Start();
            subs.Add(timer.Stop);
        }

        return panel.Root;
    }

    private static void ClearSelection(HealthViewModel vm)
    {
        switch (vm.Tab)
        {
            case HealthTab.Tools:
                vm.SelectedTool = null;
                break;
            case HealthTab.Security:
                vm.SelectedSecurity = null;
                break;
            default:
                vm.SelectedPort = null;
                break;
        }
    }

    /// <summary>The panel key for the current tab's selection (null when nothing is selected).</summary>
    public static string? KeyFor(HealthViewModel vm) => vm.Tab switch
    {
        HealthTab.Tools => vm.SelectedTool is { } t && vm.AllToolRows.Any(r => r.Id == t) ? "tool:" + t : null,
        HealthTab.Security => vm.SelectedFinding is { } f ? "sec:" + f.Id : null,
        _ => vm.SelectedPortInfo is { } p ? "port:" + p.Id : null,
    };

    private static void SyncSelection(HealthViewModel vm, SidePanel panel, Dictionary<string, ListRow> rows)
    {
        string? key = vm.State == PlaceState.Ready ? KeyFor(vm) : null;
        panel.Select(key);
        foreach ((string id, ListRow row) in rows)
        {
            row.Selected = id == key;
        }
    }

    private static void RenderBody(MainViewModel model, HealthViewModel vm, StackPanel body, Dictionary<string, ListRow> rows, Subscriptions subs)
    {
        switch (vm.State)
        {
            case PlaceState.Unavailable:
                body.Children.Add(Ds.Card(Ds.EmptyState(Icons.Shield, "Health is off", HealthViewModel.UnavailableText, vm.RestartNeeded ? null : OffButtons(model, vm))));
                if (vm.RestartNeeded)
                {
                    body.Children.Add(Ds.Banner(BannerKind.Info, "Health is switched on. DevSweep applies it the next time it starts: close it from the tray (Quit) and open it again.", "Restart to finish"));
                }

                return;
            case PlaceState.NotChecked:
                body.Children.Add(Ds.Card(Ds.EmptyState(Icons.Shield, "Not checked yet", "DevSweep finds every Node, Python, Java and database on this PC and whether each is still supported, looks for files that look like secrets, and shows what's listening.",
                    Ui.Button("Check now", () => _ = vm.LoadAsync(), accent: true))));
                return;
            case PlaceState.Loading:
                body.Children.Add(Ds.Card(Ds.Skeleton(6)));
                return;
            case PlaceState.Failed:
                body.Children.Add(Ds.Banner(BannerKind.Critical, vm.Error ?? "Something went wrong.", "Health couldn't finish", "Try again", () => _ = vm.LoadAsync()));
                return;
        }

        if (vm.Error is { } error)
        {
            body.Children.Add(Ds.Banner(BannerKind.Warning, error));
        }

        switch (vm.Tab)
        {
            case HealthTab.Tools:
                ToolsTab(vm, body, rows);
                break;
            case HealthTab.Security:
                SecurityTab(model, vm, body, rows);
                break;
            default:
                PortsTab(vm, body, rows);
                break;
        }
    }

    private static UIElement OffButtons(MainViewModel model, HealthViewModel vm)
        => FlowButtons.Build(vm.CanEnable ? Ui.Button("Turn on Health", vm.Enable, "Turn on Health", accent: true) : null, Ui.Button("Open Settings", () => model.Go("settings"), "Open Settings, Modules"));

    // ---- Tools and versions -------------------------------------------------------------------------------------------------

    private static void ToolsTab(HealthViewModel vm, StackPanel body, Dictionary<string, ListRow> rows)
    {
        if (vm.Report?.Versions is not { } versions)
        {
            body.Children.Add(Ds.Card(Ds.EmptyState(Icons.Terminal, "Tools and versions is off", "Turn it on in Settings, Modules, to see every install of your languages and databases.")));
            return;
        }

        if (versions.EolOffline)
        {
            body.Children.Add(Ds.Banner(BannerKind.Info, "Support dates come from an old copy (endoflife.date couldn't be reached), so they may be out of date.", "Working offline"));
        }

        var filters = new List<UIElement>();
        foreach ((ToolsFilter filter, string title) in (ReadOnlySpan<(ToolsFilter, string)>)[(ToolsFilter.All, "All"), (ToolsFilter.NeedsAttention, "Needs attention"), (ToolsFilter.UpToDate, "Up to date")])
        {
            int count = vm.FilterCount(filter);
            filters.Add(Ds.Chip(title, count.ToString(System.Globalization.CultureInfo.InvariantCulture), vm.ToolsFilter == filter, $"Show {title}, {count}", () => vm.ToolsFilter = filter));
        }

        TextBlock summary = Ui.Secondary(vm.ToolsSummary);
        summary.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(summary, vm.ToolsSummary);
        body.Children.Add(Ds.CommandBar(FlowButtons.Build([.. filters]), null, summary));
        body.Children.Add(Legend());

        IReadOnlyList<ToolRow> list = vm.ToolRows;
        var items = new List<UIElement>();
        foreach (ToolRow row in list)
        {
            ListRow item = ToolListRow(vm, row);
            rows["tool:" + row.Id] = item;
            items.Add(item.Root);
        }

        if (vm.IsLoadingPackages && vm.Packages is null && vm.ToolsFilter != ToolsFilter.UpToDate)
        {
            items.Add(Ds.Card(Ui.H(Tokens.Space3, new ProgressRing { IsActive = true, Width = 18, Height = 18 }, Ui.Secondary("Checking winget, Scoop and Chocolatey…"))));
        }

        if (items.Count == 0)
        {
            body.Children.Add(Ds.Card(vm.AllToolRows.Count == 0
                ? Ds.EmptyState(Icons.Terminal, "No tools found", "DevSweep didn't find Node, Python, Java, .NET, Go or a database on this PC.")
                : Ds.EmptyState(Icons.Filter, vm.ToolsFilter == ToolsFilter.NeedsAttention ? "Everything is up to date" : "Nothing here", vm.ToolsFilter == ToolsFilter.NeedsAttention ? "No tool needs attention right now." : "Every tool has something to look at.")));
        }
        else
        {
            body.Children.Add(Ds.RowsCard(items));
        }

        if (vm.Packages is { } packages)
        {
            string line = string.Join(" · ", packages.Managers.Where(m => m.Problem is not null).Select(m => m.Name + ": " + m.Problem));
            if (line.Length > 0)
            {
                body.Children.Add(Ui.Caption(line));
            }
        }
    }

    private static UIElement Legend()
    {
        var legendItems = new List<UIElement>();
        foreach ((string text, Tone tone) in (ReadOnlySpan<(string, Tone)>)[("Supported", Tone.Good), ("Ending soon", Tone.Caution), ("End of life", Tone.Critical), ("Not running", Tone.Neutral)])
        {
            legendItems.Add(Ui.H(Tokens.Space1, Dot(tone), Ui.Caption(text)));
        }

        FontIcon terminal = Ui.Icon(Icons.RunsHere, 10);
        legendItems.Add(Ui.H(Tokens.Space1, terminal, Ui.Caption("A new terminal runs this one")));
        return FlowButtons.Build(Tokens.Space4, [.. legendItems]);
    }

    private static Border Dot(Tone tone) => Ui.Dot(tone is Tone.Good or Tone.Caution or Tone.Critical ? tone : Tone.Neutral);

    private static ListRow ToolListRow(HealthViewModel vm, ToolRow row)
    {
        var grid = new Grid { ColumnSpacing = Tokens.Space3 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });

        StackPanel name = Ui.V(0, Ui.Text(row.Title, "DsStrong", 1), Ui.Caption(row.Summary));
        name.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(name);

        UIElement pills = Pills(vm, row);
        if (pills is FrameworkElement pillElement)
        {
            pillElement.VerticalAlignment = VerticalAlignment.Center;
        }

        Grid.SetColumn((FrameworkElement)pills, 1);
        grid.Children.Add(pills);

        UIElement status = row.Alerts == 0
            ? StatusLine(Icons.CheckCircle, "All good", "SystemFillColorSuccessBrush", "DsCaption")
            : StatusLine(row.Critical ? Icons.Error : Icons.Warning, $"{row.Alerts} to look at", row.Critical ? "SystemFillColorCriticalBrush" : "SystemFillColorCautionBrush", "DsCaption");
        ((FrameworkElement)status).VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn((FrameworkElement)status, 2);
        grid.Children.Add(status);

        Button info = Ui.IconButton(Icons.Info, $"Show details for {row.Title}", () => vm.SelectTool(row.Id));
        info.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(info, 3);
        grid.Children.Add(info);

        var item = new ListRow(grid, () => vm.SelectTool(row.Id), tabStop: false);
        AutomationProperties.SetName(item.Root, $"{row.Title}, {row.Summary}, {(row.Alerts == 0 ? "all good" : row.Alerts + " to look at")}");
        return item;
    }

    private static UIElement StatusLine(string glyph, string text, string brush, string style)
    {
        FontIcon icon = Ui.Icon(glyph, 14);
        Ui.Tint(icon, brush);
        TextBlock label = Ui.Text(text, style);
        label.TextWrapping = TextWrapping.NoWrap;
        StackPanel line = Ui.H(Tokens.Space1, icon, label);
        AutomationProperties.SetName(line, text);
        return line;
    }

    private static UIElement Pills(HealthViewModel vm, ToolRow row)
    {
        var pills = new List<UIElement>();
        switch (row.Kind)
        {
            case ToolKind.Packages:
                PackagesResult? packages = vm.Packages;
                if (packages is { Deprecated: > 0 })
                {
                    pills.Add(Ds.StatusTag($"{packages.Deprecated} no longer offered", Tone.Critical));
                }

                if (packages is { Outdated: > 0 })
                {
                    pills.Add(Ds.StatusTag($"{packages.Outdated} outdated", Tone.Caution));
                }

                if (packages is { Duplicates.Count: > 0 })
                {
                    pills.Add(Ds.StatusTag($"{packages.Duplicates.Count} installed twice", Tone.Caution));
                }

                break;
            case ToolKind.Projects:
                IReadOnlyList<ProjectRequirement> requirements = vm.Report?.Versions?.Projects ?? [];
                int problems = requirements.Count(p => p.Status != RequirementStatus.Ok);
                if (problems > 0)
                {
                    pills.Add(Ds.StatusTag($"{problems} not met", Tone.Caution));
                }

                pills.Add(Ds.StatusTag($"{requirements.Count - problems} OK", Tone.Good));
                break;
            default:
                foreach (Installation install in row.Report?.Installs ?? [])
                {
                    pills.Add(VersionPill(install, row.Kind == ToolKind.Windows));
                }

                break;
        }

        return pills.Count == 0 ? Ui.Caption(string.Empty) : FlowButtons.Build(6, [.. pills]);
    }

    /// <summary>One coloured pill per install: version and owner, a terminal glyph when a new terminal runs it, a dot when the server runs.</summary>
    internal static UIElement VersionPill(Installation install, bool versionOnly = false)
    {
        bool notRunning = install.IsRunning == false;
        Tone tone = notRunning ? Tone.Neutral : install.Support switch
        {
            SupportStatus.EndOfLife => Tone.Critical,
            SupportStatus.EndingSoon => Tone.Caution,
            SupportStatus.Supported => Tone.Good,
            _ => Tone.Neutral,
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Tokens.Space1 };
        string toneName = tone.ToString();
        if (install.IsDefault)
        {
            FontIcon icon = Ui.Icon(Icons.RunsHere, 10);
            Ui.Tint(icon, tone switch { Tone.Good => "SystemFillColorSuccessBrush", Tone.Caution => "SystemFillColorCautionBrush", Tone.Critical => "SystemFillColorCriticalBrush", _ => "TextFillColorSecondaryBrush" });
            content.Children.Add(icon);
        }

        if (install.IsRunning == true)
        {
            content.Children.Add(Dot(Tone.Good));
        }

        content.Children.Add(new TextBlock { Text = versionOnly ? install.Version : $"{install.Version} · {install.SourceTitle}", Style = Ui.Style("DsBadgeText" + toneName), TextWrapping = TextWrapping.NoWrap });
        var pill = new Border { Style = Ui.Style("DsBadge" + toneName), Child = content, VerticalAlignment = VerticalAlignment.Center };
        string tip = string.Join(" · ", new[] { SupportText(install.Support), install.IsDefault ? "a new terminal runs this one" : null, install.IsRunning is { } r ? (r ? "running" : "not running") : null }.OfType<string>());
        Ui.Tip(pill, tip);
        AutomationProperties.SetName(pill, $"{install.Version} from {install.SourceTitle}, {tip}");
        return pill;
    }

    internal static string SupportText(SupportStatus status) => status switch
    {
        SupportStatus.EndOfLife => "End of life",
        SupportStatus.EndingSoon => "Support ending soon",
        SupportStatus.Supported => "Supported",
        _ => "Not checked",
    };

    internal static Tone SupportTone(SupportStatus status) => status switch
    {
        SupportStatus.EndOfLife => Tone.Critical,
        SupportStatus.EndingSoon => Tone.Caution,
        SupportStatus.Supported => Tone.Good,
        _ => Tone.Neutral,
    };

    // ---- Security -----------------------------------------------------------------------------------------------------------

    private static void SecurityTab(MainViewModel model, HealthViewModel vm, StackPanel body, Dictionary<string, ListRow> rows)
    {
        if (vm.Report?.Security is not { } security)
        {
            body.Children.Add(Ds.Card(Ds.EmptyState(Icons.Shield, "Security is off", "Turn it on in Settings, Modules, to look for files that look like secrets.")));
            return;
        }

        IReadOnlyList<SecurityFinding> needs = vm.NeedsAction;
        IReadOnlyList<SecurityFinding> fine = vm.LooksFine;
        if (needs.Count + fine.Count == 0)
        {
            body.Children.Add(Ds.Card(Ds.EmptyState(Icons.CheckCircle, "No secrets lying around", "DevSweep looked for recovery codes, private keys, cloud keys, password exports and .env files, and found none.")));
        }

        AddSecurity(vm, body, "Needs action", needs, rows);
        AddSecurity(vm, body, "Looks fine", fine, rows);

        var notes = new List<string>
        {
            $"Looked in {string.Join(", ", security.Looked)}. Names and at most the first 64 KB are read; secrets are never shown or stored."
            + (security.CloudFilesSkipped > 0 ? $" {security.CloudFilesSkipped} online-only files were not opened." : string.Empty),
        };
        if (security.GitUnavailable is { } git)
        {
            notes.Add(git);
        }

        foreach (string note in notes)
        {
            body.Children.Add(Ui.Caption(note));
        }

        if (vm.IgnoredFindings > 0)
        {
            body.Children.Add(Ui.H(Tokens.Space2, Ui.Caption($"{vm.IgnoredFindings} ignored."), Ui.Link("Manage in Settings, Ignored items", () => model.Go("settings"))));
        }
    }

    private static void AddSecurity(HealthViewModel vm, StackPanel body, string title, IReadOnlyList<SecurityFinding> findings, Dictionary<string, ListRow> rows)
    {
        if (findings.Count == 0)
        {
            return;
        }

        var items = new List<UIElement>();
        foreach (SecurityFinding finding in findings)
        {
            var grid = new Grid { ColumnSpacing = Tokens.Space3 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            FontIcon icon = Ui.Icon(KindGlyph(finding.Kind), 16);
            Ui.Tint(icon, "TextFillColorSecondaryBrush");
            icon.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(icon);
            TextBlock path = Ui.Caption(finding.Path);
            path.MaxLines = 1;
            path.TextTrimming = TextTrimming.CharacterEllipsis;
            StackPanel text = Ui.V(0, Ui.Text(finding.Title, "DsStrong", 1), path);
            text.VerticalAlignment = VerticalAlignment.Center;
            Ui.Tip(text, finding.Path);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            Tone tone = finding.Level switch { SecurityLevel.Critical => Tone.Critical, SecurityLevel.Warning => Tone.Caution, _ => Tone.Good };
            Border tag = Ds.StatusTag(finding.Status, tone);
            Grid.SetColumn(tag, 2);
            grid.Children.Add(tag);
            string id = finding.Id;
            Button info = Ui.IconButton(Icons.Info, $"Show details for {finding.Title}", () => vm.SelectedSecurity = vm.SelectedSecurity == id ? null : id);
            info.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(info, 3);
            grid.Children.Add(info);
            var row = new ListRow(grid, () => vm.SelectedSecurity = vm.SelectedSecurity == id ? null : id, tabStop: false);
            AutomationProperties.SetName(row.Root, $"{finding.Title}, {finding.Status}");
            rows["sec:" + id] = row;
            items.Add(row.Root);
        }

        body.Children.Add(Ui.V(Tokens.Space2, Ds.SectionTitle(title), Ds.RowsCard(items)));
    }

    internal static string KindGlyph(SecurityKind kind) => kind switch
    {
        SecurityKind.RecoveryCodes => Icons.Key,
        SecurityKind.PrivateKey or SecurityKind.PuttyKey => Icons.Key,
        SecurityKind.ServiceAccountKey or SecurityKind.CloudAccessKeys => Icons.Cloud,
        SecurityKind.PasswordExport => Icons.Contact,
        SecurityKind.Certificate => Icons.Certificate,
        _ => Icons.Page,
    };

    // ---- Ports --------------------------------------------------------------------------------------------------------------

    private static void PortsTab(HealthViewModel vm, StackPanel body, Dictionary<string, ListRow> rows)
    {
        if (vm.Report?.Ports is not { } ports)
        {
            body.Children.Add(Ds.Card(Ds.EmptyState(Icons.Network, "Ports is off", "Turn it on in Settings, Modules, to see what's listening on this PC.")));
            return;
        }

        if (ports.Ports.Count == 0)
        {
            body.Children.Add(Ds.Card(Ds.EmptyState(Icons.Network, "Nothing is listening", "Dev servers and databases you start show up here.")));
        }

        AddPorts(vm, body, "Development", vm.DevelopmentPorts, rows);
        AddPorts(vm, body, "Apps and Windows", vm.OtherPorts, rows);
        if (ports.OthersHidden > 0)
        {
            body.Children.Add(Ui.Caption($"{ports.OthersHidden} listeners of other users and services are hidden."));
        }

        if (ports.FirewallProblem is { } problem)
        {
            body.Children.Add(Ui.Caption(problem));
        }

        body.Children.Add(Ui.Caption("This list updates every few seconds while the tab is open, and pauses when DevSweep's window is hidden."));
    }

    private static void AddPorts(HealthViewModel vm, StackPanel body, string title, IReadOnlyList<ListeningPort> list, Dictionary<string, ListRow> rows)
    {
        if (list.Count == 0)
        {
            return;
        }

        var items = new List<UIElement>();
        foreach (ListeningPort port in list)
        {
            var grid = new Grid { ColumnSpacing = Tokens.Space3 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            string number = port.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TextBlock numberText = Ui.Text(number, "DsMono");
            numberText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            numberText.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(numberText);
            TextBlock secondary = Ui.Caption(port.Folder ?? $"{port.ProcessName} · PID {port.Pid}");
            secondary.MaxLines = 1;
            secondary.TextTrimming = TextTrimming.CharacterEllipsis;
            StackPanel text = Ui.V(0, Ui.Text(port.Label, "DsStrong", 1), secondary);
            text.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            if (port.ReachableFromNetwork)
            {
                Border tag = Ds.StatusTag("On your network", Tone.Caution, "Other devices on your network may be able to connect.");
                Grid.SetColumn(tag, 2);
                grid.Children.Add(tag);
            }

            string id = port.Id;
            Button info = Ui.IconButton(Icons.Info, $"Show details for port {number}", () => vm.SelectedPort = vm.SelectedPort == id ? null : id);
            info.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(info, 3);
            grid.Children.Add(info);
            var row = new ListRow(grid, () => vm.SelectedPort = vm.SelectedPort == id ? null : id, tabStop: false);
            AutomationProperties.SetName(row.Root, $"Port {number}, {port.Label}");
            rows["port:" + id] = row;
            items.Add(row.Root);
        }

        body.Children.Add(Ui.V(Tokens.Space2, Ds.SectionTitle(title), Ds.RowsCard(items)));
    }
}
