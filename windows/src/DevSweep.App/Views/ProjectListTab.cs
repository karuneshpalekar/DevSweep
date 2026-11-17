using DevSweep.App.Design;
using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Places;
using DevSweep.App.ViewModels.Text;
using DevSweep.Core.Projects;
using DevSweep.Core.Scanning;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DevSweep.App.Views;

/// <summary>
/// The Projects tab: Show filter with counts, account filter, search (Ctrl+F), Get a project, the "what was searched" line, the GitHub
/// set-up and deep-search cards, a virtualised list of rows and the side panel. Every repo on this PC is listed with or without GitHub.
/// </summary>
internal static class ProjectListTab
{
    private const double RowHeight = 62;

    private sealed record RowTag(ListRow Row, string Id);

    /// <summary>For tests and the screenshot tour: how many projects the list has and how many rows are built right now.</summary>
    public static Func<(int Count, int Realized)>? ListStats { get; private set; }

    private sealed class ReportState
    {
        public bool Open { get; set; }
    }

    public static UIElement Build(MainViewModel model, ProjectsViewModel vm, Subscriptions subs, MainWindow window)
    {
        vm.SelectedId = null;
        IReadOnlyList<Project> visible = vm.Visible;

        // ---- Above the list -----------------------------------------------------------------------------------------------------
        var noticeHost = new Border();
        var setupHost = new Border();
        var consentHost = new Border();
        var deepHost = new Border();

        var segHost = new Border();
        var accountBox = new ComboBox { MinWidth = 150 };
        AutomationProperties.SetName(accountBox, "Account");
        bool fillingAccounts = false;
        accountBox.SelectionChanged += (_, _) =>
        {
            if (!fillingAccounts && accountBox.SelectedIndex >= 0)
            {
                vm.AccountFilter = accountBox.SelectedIndex == 0 ? null : vm.Accounts[accountBox.SelectedIndex - 1];
            }
        };

        var search = new TextBox { PlaceholderText = "Search projects", Width = 230, Padding = new Thickness(30, 5, 8, 6) };
        AutomationProperties.SetName(search, "Search projects");
        Ui.Tip(search, "Search by name, owner or description (Ctrl+F)");
        search.TextChanged += (_, _) => vm.Search = search.Text;
        var focusSearch = new KeyboardAccelerator { Key = VirtualKey.F, Modifiers = VirtualKeyModifiers.Control };
        focusSearch.Invoked += (_, e) =>
        {
            search.Focus(FocusState.Keyboard);
            e.Handled = true;
        };
        search.KeyboardAccelerators.Add(focusSearch);
        FontIcon searchIcon = Ui.Icon(Icons.Search, 13);
        searchIcon.HorizontalAlignment = HorizontalAlignment.Left;
        searchIcon.VerticalAlignment = VerticalAlignment.Center;
        searchIcon.Margin = new Thickness(10, 0, 0, 0);
        searchIcon.IsHitTestVisible = false;
        var searchBox = new Grid { Width = 230 };
        searchBox.Children.Add(search);
        searchBox.Children.Add(searchIcon);

        DropDownButton getProject = GetProjectMenu(vm, window);
        UIElement commandBar = Ds.CommandBar(Ui.H(Tokens.Space3, segHost, accountBox), searchBox, getProject);

        var reportHost = new Border();

        // ---- The list ------------------------------------------------------------------------------------------------------------
        var list = new VirtualList(RowHeight, i => Row(model, vm, visible[i], subs), "Projects");
        ListStats = () => (list.Count, list.RealizedCount);
        var emptyHost = new Border { VerticalAlignment = VerticalAlignment.Center };
        var listArea = new Grid();
        listArea.Children.Add(list.Root);
        listArea.Children.Add(emptyHost);
        Border listCard = Ui.Card(listArea);
        listCard.Padding = Tokens.Pad(Tokens.Space1);
        listCard.Margin = new Thickness(Tokens.PageGutter, Tokens.Space2, Tokens.PageGutter, Tokens.Space6);
        Grid.SetRow(listCard, 1);

        StackPanel above = Ui.V(Tokens.Space2, noticeHost, setupHost, consentHost, deepHost, commandBar, reportHost);
        var above2 = new Border { Padding = new Thickness(Tokens.PageGutter, 0, Tokens.PageGutter, 0), Child = above };
        var column = new Grid();
        column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        column.Children.Add(above2);
        column.Children.Add(listCard);

        var panel = new SidePanel(column, (key, panelSubs) => vm.All.FirstOrDefault(p => p.Id == key) is { } project ? ProjectPanel.Build(model, vm, project, panelSubs, window) : new Grid(), "Project details")
        {
            Closed = () => vm.SelectedId = null,
        };
        subs.Add(panel.Dispose);
        window.ActivePanel = panel;

        // ---- Updates ---------------------------------------------------------------------------------------------------------------
        var reportState = new ReportState();

        void RenderFilters()
        {
            segHost.Child = Ds.Segmented(
                new[] { vm.FilterLabel(ProjectFilter.All), vm.FilterLabel(ProjectFilter.OnThisPc), vm.FilterLabel(ProjectFilter.OnGitHub) },
                (int)vm.Filter,
                index => vm.Filter = index switch { 1 => ProjectFilter.OnThisPc, 2 => ProjectFilter.OnGitHub, _ => ProjectFilter.All },
                "Show");
            Ui.Show(accountBox, vm.ShowAccountFilter);
            fillingAccounts = true;
            accountBox.Items.Clear();
            accountBox.Items.Add("All accounts");
            foreach (string account in vm.Accounts)
            {
                accountBox.Items.Add(account);
            }

            accountBox.SelectedIndex = vm.AccountFilter is { } a ? Math.Max(0, vm.Accounts.ToList().FindIndex(x => x.Equals(a, StringComparison.OrdinalIgnoreCase)) + 1) : 0;
            fillingAccounts = false;
        }

        string lastKey = string.Empty;

        void RenderList()
        {
            visible = vm.Visible;
            string key = $"{vm.Filter}|{vm.AccountFilter}|{vm.Search}";
            list.SetCount(visible.Count, toTop: key != lastKey);
            lastKey = key;
            if (visible.Count == 0)
            {
                emptyHost.Child = Ds.EmptyState(
                    vm.Search.Trim().Length > 0 ? Icons.Search : Icons.Folder, vm.EmptyTitle, vm.EmptyText,
                    vm.Search.Trim().Length > 0 ? Ui.Button("Clear search", () =>
                    {
                        search.Text = string.Empty;
                        vm.Search = string.Empty;
                    }) : null,
                    vm.Search.Trim().Length == 0 && vm.Filter != ProjectFilter.OnGitHub ? Ui.Button("Find all repos on this PC…", () => _ = vm.StartDeepSearchAsync(), accent: true) : null,
                    vm.Search.Trim().Length == 0 && vm.Filter != ProjectFilter.OnGitHub ? Ui.Button("Add a folder…", () => _ = AddFolderAsync(vm, window)) : null);
            }
            else
            {
                emptyHost.Child = null;
            }
        }

        void RenderSelection()
        {
            foreach (UIElement row in list.Realized)
            {
                if (row is FrameworkElement { Tag: RowTag tag })
                {
                    tag.Row.Selected = tag.Id == vm.SelectedId;
                }
            }

            panel.Select(vm.SelectedId is { } id && vm.All.Any(p => p.Id == id) ? id : null);
        }

        void RenderReport() => reportHost.Child = SearchReportView(vm, window, reportState, RenderReport);

        subs.On(vm, nameof(ProjectsViewModel.Visible), RenderList);
        subs.On(vm, nameof(ProjectsViewModel.Report), () =>
        {
            RenderFilters();
            RenderReport();
            Ui.Show(segHost, true);
        });
        subs.On(vm, nameof(ProjectsViewModel.Filter), RenderFilters);
        subs.On(vm, nameof(ProjectsViewModel.AccountFilter), RenderFilters);
        subs.On(vm, nameof(ProjectsViewModel.SelectedId), RenderSelection);
        subs.On(vm, nameof(ProjectsViewModel.Notice), () => noticeHost.Child = vm.Notice is { } n
            ? Ds.Banner(n.Kind switch { NoticeKind.Error => BannerKind.Critical, NoticeKind.Warning => BannerKind.Warning, _ => BannerKind.Info }, n.Text, null, "Dismiss", vm.Dismiss)
            : null);
        subs.On(vm, nameof(ProjectsViewModel.Setup), () => setupHost.Child = GitHubSetupCard(vm));
        subs.On(vm, nameof(ProjectsViewModel.NeedsDeepSearchConsent), () => consentHost.Child = vm.NeedsDeepSearchConsent ? ConsentCard(vm) : null);
        subs.On(vm, nameof(ProjectsViewModel.DeepState), () => deepHost.Child = DeepSearchCard(vm));
        subs.On(vm, nameof(ProjectsViewModel.DeepProgress), () => deepHost.Child = DeepSearchCard(vm));
        subs.On(vm, nameof(ProjectsViewModel.DeepResult), () => deepHost.Child = DeepSearchCard(vm));
        subs.On(vm, nameof(ProjectsViewModel.Removing), () => list.Invalidate());
        subs.On(vm, nameof(ProjectsViewModel.BusyId), () => list.Invalidate());
        return panel.Root;
    }

    // ---- Get a project ---------------------------------------------------------------------------------------------------------------

    private static DropDownButton GetProjectMenu(ProjectsViewModel vm, MainWindow window)
    {
        var menu = new MenuFlyout();

        MenuFlyoutItem Item(string text, string glyph, Action action)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            AutomationProperties.SetName(item, text);
            item.Click += (_, _) => action();
            return item;
        }

        bool github = vm.Source?.Features.Accounts == true;
        menu.Items.Add(Item("Add a project by URL…", Icons.Link, () => vm.OpenSheet(ProjectSheet.AddByUrl)));
        menu.Items.Add(Item("Publish a folder to GitHub…", Icons.Upload, () => vm.OpenSheet(ProjectSheet.Publish)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Find all repos on this PC…", Icons.Search, () => _ = vm.StartDeepSearchAsync()));
        menu.Items.Add(Item("Add a folder to look in…", Icons.Folder, () => _ = AddFolderAsync(vm, window)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Refresh", Icons.Refresh, () => _ = vm.LoadAsync()));
        if (!github)
        {
            menu.Items[0].IsEnabled = false;
            menu.Items[1].IsEnabled = false;
        }

        var button = new DropDownButton { Content = Ui.H(Tokens.Space2, Ui.Icon(Icons.Add, 14), Ui.Text("Get a project")), Flyout = menu };
        AutomationProperties.SetName(button, "Get a project");
        return button;
    }

    internal static async Task AddFolderAsync(ProjectsViewModel vm, MainWindow window)
    {
        if (await window.PickFolderAsync() is { } folder)
        {
            vm.AddFolder(folder);
        }
    }

    // ---- Rows --------------------------------------------------------------------------------------------------------------------------

    private static UIElement Row(MainViewModel model, ProjectsViewModel vm, Project project, Subscriptions subs)
    {
        DateTimeOffset now = model.Backend.Now;
        bool removing = vm.Removing.Contains(project.Id);
        bool busy = vm.BusyId == project.Id || vm.BusyId == "all";
        var grid = new Grid { ColumnSpacing = Tokens.Space3 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });

        // Name, lock, and the path (on this PC) or owner and description (on GitHub).
        StackPanel nameLine = Ui.H(Tokens.Space2, Ui.Text(project.Name, "DsStrong", maxLines: 1));
        if (project.IsPrivate == true)
        {
            FontIcon lockIcon = Ui.Icon(Icons.Lock, 11);
            lockIcon.VerticalAlignment = VerticalAlignment.Center;
            lockIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            Ui.Tip(lockIcon, "Private repository");
            AutomationProperties.SetName(lockIcon, "Private");
            nameLine.Children.Add(lockIcon);
        }

        string where = project.OnDisk
            ? Words.Path(project.LocalPath ?? string.Empty, model.Backend.ProfilePath)
            : string.Join(" · ", new[] { project.Owner, project.Description }.Where(t => t.Length > 0));
        TextBlock sub = Ui.Caption(where);
        sub.MaxLines = 1;
        sub.TextTrimming = TextTrimming.CharacterEllipsis;
        Ui.Tip(sub, where);
        StackPanel text = Ui.V(2, nameLine, sub);
        text.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(text);

        // What is happening to it, the Download button, or its status.
        UIElement state;
        if (removing)
        {
            state = Ui.H(Tokens.Space2, new ProgressRing { IsActive = true, Width = 16, Height = 16 }, Ui.Caption("Removing…"));
        }
        else if (busy)
        {
            state = new ProgressRing { IsActive = true, Width = 18, Height = 18 };
        }
        else if (!project.OnDisk)
        {
            state = Ui.Button("Download", () => vm.OpenSheet(ProjectSheet.Clone, project), $"Download {project.NameWithOwner}");
        }
        else
        {
            (string statusText, Tone tone, string tooltip) = ProjectsViewModel.Safety(project);
            Border tag = Ds.StatusTag(statusText, tone, tooltip);
            tag.HorizontalAlignment = HorizontalAlignment.Right;
            state = tag;
        }

        Grid.SetColumn((FrameworkElement)state, 1);
        ((FrameworkElement)state).VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(state);

        // Size and when it was last used.
        string size = project.OnDisk
            ? project.IsWsl ? string.Empty : (project.LocalSizeComplete ? string.Empty : "at least ") + SizeLabels.Size(project.LocalSize)
            : project.RemoteKB > 0 ? SizeText.Format(project.RemoteKB * 1024) : string.Empty;
        TextBlock sizeText = Ui.Secondary(size);
        sizeText.TextAlignment = TextAlignment.Right;
        sizeText.TextWrapping = TextWrapping.NoWrap;
        sizeText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(sizeText, 2);
        grid.Children.Add(sizeText);
        TextBlock used = Ui.Caption(ProjectListing.UsedAgo(project.LastUsed, now));
        used.TextAlignment = TextAlignment.Right;
        used.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(used, 3);
        grid.Children.Add(used);

        Button info = Ui.IconButton(Icons.Info, $"Details for {project.NameWithOwner}", () => vm.Toggle(project));
        info.VerticalAlignment = VerticalAlignment.Center;
        info.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(info, 4);
        grid.Children.Add(info);

        var row = new ListRow(grid, () => vm.Toggle(project), tabStop: false) { Selected = vm.SelectedId == project.Id };
        ProjectMenu(row, vm, project);
        AutomationProperties.SetName(row.Root, $"{project.Name}, {(project.OnDisk ? ProjectsViewModel.Safety(project).Text : "only on GitHub")}");
        row.Root.Tag = new RowTag(row, project.Id);
        return row.Root;
    }

    private static void ProjectMenu(ListRow row, ProjectsViewModel vm, Project project)
    {
        var menu = new MenuFlyout();
        if (project.OnDisk)
        {
            var open = new MenuFlyoutItem { Text = "Open in editor" };
            open.Click += (_, _) => vm.OpenInEditor(project);
            var show = new MenuFlyoutItem { Text = "Show in File Explorer" };
            show.Click += (_, _) => Shell.Reveal(project.LocalPath!);
            menu.Items.Add(open);
            menu.Items.Add(show);
        }
        else
        {
            var download = new MenuFlyoutItem { Text = "Download…" };
            download.Click += (_, _) => vm.OpenSheet(ProjectSheet.Clone, project);
            menu.Items.Add(download);
        }

        if (project.OnGitHub)
        {
            var github = new MenuFlyoutItem { Text = "Open on GitHub" };
            github.Click += (_, _) => Shell.OpenUrl(ProjectsViewModel.GitHubUrl(project));
            menu.Items.Add(github);
        }

        row.ContextFlyout = menu;
    }

    // ---- Cards ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>"Install GitHub CLI" or "Sign in to GitHub…" when GitHub isn't ready; local projects are listed either way.</summary>
    internal static UIElement? GitHubSetupCard(ProjectsViewModel vm)
    {
        switch (vm.Setup)
        {
            case GitHubSetup.NotInstalled:
                return SetupCard(
                    "Connect GitHub to see and download your repositories",
                    "The GitHub CLI isn't installed. Every project on this PC is listed below either way. Installing opens a PowerShell window that runs winget; nothing starts until you press Enter there.",
                    Ui.Button("Install GitHub CLI", vm.InstallGitHubCli, "Install GitHub CLI", accent: true),
                    Ui.Button("Refresh", () => _ = vm.LoadAsync()));
            case GitHubSetup.NotSignedIn:
                return SetupCard(
                    "Sign in to GitHub to see and download your repositories",
                    "The GitHub CLI is installed but no account is signed in. Every project on this PC is listed below either way.",
                    Ui.Button("Sign in to GitHub…", () => vm.OpenSheet(ProjectSheet.AddAccount), accent: true),
                    Ui.Button("Refresh", () => _ = vm.LoadAsync()));
            default:
                return null;
        }
    }

    private static UIElement SetupCard(string title, string text, params UIElement?[] buttons)
    {
        FontIcon icon = Ui.Icon(Icons.Cloud, 20);
        icon.VerticalAlignment = VerticalAlignment.Top;
        TextBlock body = Ui.Secondary(text);
        StackPanel content = Ui.V(Tokens.Space2, Ui.Text(title, "DsStrong"), body, FlowButtons.Build(buttons));
        var grid = new Grid { ColumnSpacing = Tokens.Space3 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(content, 1);
        grid.Children.Add(icon);
        grid.Children.Add(content);
        Border card = Ui.Card(grid, "DsBetter");
        AutomationProperties.SetName(card, title);
        return card;
    }

    private static UIElement ConsentCard(ProjectsViewModel vm)
    {
        FontIcon icon = Ui.Icon(Icons.Search, 20);
        icon.VerticalAlignment = VerticalAlignment.Top;
        StackPanel content = Ui.V(Tokens.Space2,
            Ui.Text("Look for projects everywhere on this PC?", "DsStrong"),
            Ui.Secondary("DevSweep can search every drive for git repositories, not only your usual project folders, so clones in Downloads, on other drives or under C:\\ show up too. It reads folder names only (never what's inside your files), skips system folders and dependency folders such as node_modules, and remembers what it finds so later refreshes are quick. Linux distributions (WSL) that are already running are included; DevSweep never starts one."),
            FlowButtons.Build(
                Ui.Button("Look everywhere", () => _ = vm.AnswerDeepSearchAsync(true), "Look for projects everywhere on this PC", accent: true),
                Ui.Button("Not now", () => _ = vm.AnswerDeepSearchAsync(false))));
        var grid = new Grid { ColumnSpacing = Tokens.Space3 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(content, 1);
        grid.Children.Add(icon);
        grid.Children.Add(content);
        Border card = Ui.Card(grid, "DsBetter");
        AutomationProperties.SetName(card, "Look for projects everywhere on this PC?");
        return card;
    }

    /// <summary>Progress while "Find all repos on this PC" runs (folders looked at, repos found, the folder it is in, Cancel), then its result.</summary>
    internal static UIElement? DeepSearchCard(ProjectsViewModel vm)
    {
        if (vm.DeepState == DeepSearchState.Running)
        {
            DevSweep.Core.Projects.Discovery.DeepSearchProgress p = vm.DeepProgress ?? new(0, 0, string.Empty);
            var bar = new ProgressBar { IsIndeterminate = true };
            AutomationProperties.SetName(bar, "Looking for repos on this PC");
            TextBlock counts = Ui.Text(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{p.Folders:N0} folders looked at · {p.Repos} repos found"), "DsBody");
            TextBlock current = Ui.Caption(p.Current.Length > 0 ? p.Current : "Starting…");
            current.TextTrimming = TextTrimming.CharacterEllipsis;
            current.MaxLines = 1;
            Button cancel = Ui.Button("Cancel", vm.CancelDeepSearch, "Stop looking and keep what was found");
            Border card = Ui.Card(Ui.V(Tokens.Space2,
                Ui.Row(Ui.Text("Looking for repos on this PC", "DsStrong"), cancel),
                bar, counts, current,
                Ui.Caption("Reading folder names only. You can keep using DevSweep; stopping keeps what it found so far.")));
            AutomationProperties.SetName(card, "Looking for repos on this PC");
            return card;
        }

        if (vm.DeepResult is { } result)
        {
            return Ds.Banner(BannerKind.Info, result, "Find all repos on this PC", "OK", () => vm.DeepResult = null);
        }

        return null;
    }

    private static UIElement SearchReportView(ProjectsViewModel vm, MainWindow window, ReportState state, Action rerender)
    {
        if (vm.SearchReport is not { } report)
        {
            return new Grid();
        }

        bool open = state.Open;
        TextBlock line = Ui.Caption(report.Line);
        line.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(line, report.Line);
        HyperlinkButton details = Ui.Link(open ? "Hide details" : "What was searched", () => { }, "Show what was searched and skipped");
        HyperlinkButton add = Ui.Link("Add folder…", () => _ = AddFolderAsync(vm, window), "Add a folder to look in");
        HyperlinkButton all = Ui.Link("Find all repos on this PC…", () => _ = vm.StartDeepSearchAsync(), "Find all repos on this PC");
        line.TextWrapping = TextWrapping.Wrap;
        UIElement bar = Ui.V(Tokens.Space1, line, FlowButtons.Build(Tokens.Space3, details, add, all));
        var host = new StackPanel { Spacing = Tokens.Space2 };
        host.Children.Add(bar);

        UIElement? Details()
        {
            var rows = new StackPanel { Spacing = Tokens.Space2 };
            rows.Children.Add(Ds.SectionTitle("Folders searched"));
            foreach (SearchedFolder folder in report.Folders)
            {
                Button? remove = folder.AddedByYou ? Ui.Button("Remove folder", () => vm.RemoveFolder(folder.Path), $"Stop looking in {folder.Path}") : null;
                rows.Children.Add(Ui.Row(Ui.Text(folder.Path, "DsMono"), remove));
            }

            if (report.Notes.Count > 0)
            {
                rows.Children.Add(Ds.SectionTitle("Not looked at"));
                foreach (string note in report.Notes)
                {
                    rows.Children.Add(Ds.CheckRow(false, note));
                }
            }

            return Ds.Card(rows);
        }

        if (open)
        {
            host.Children.Add(Details()!);
        }

        details.Click += (_, _) =>
        {
            state.Open = !state.Open;
            rerender();
        };
        return host;
    }
}
