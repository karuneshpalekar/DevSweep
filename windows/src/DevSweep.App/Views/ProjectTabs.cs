using DevSweep.App.Design;
using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Text;
using DevSweep.Core.Projects;
using DevSweep.Core.Projects.Git;
using DevSweep.Core.Projects.GitHub;
using DevSweep.Core.Projects.Storage;
using DevSweep.Core.Scanning;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DevSweep.App.Views;

/// <summary>The Cleanup, Accounts and Activity tabs, in the Mac's structure and wording.</summary>
internal static class ProjectTabs
{
    // ---- Cleanup -----------------------------------------------------------------------------------------------------------------------

    public static UIElement Cleanup(MainViewModel model, ProjectsViewModel vm, Subscriptions subs, MainWindow window)
    {
        vm.SelectedId = null;
        var idle = new ComboBox { Width = 150 };
        foreach ((_, string title) in ProjectsViewModel.IdleChoices)
        {
            idle.Items.Add(title);
        }

        idle.SelectedIndex = Math.Max(0, ProjectsViewModel.IdleChoices.ToList().FindIndex(c => c.Days == vm.IdleDays));
        idle.SelectionChanged += (_, _) =>
        {
            int days = ProjectsViewModel.IdleChoices[Math.Max(0, idle.SelectedIndex)].Days;
            if (days != vm.IdleDays)
            {
                vm.IdleDays = days;
            }
        };
        AutomationProperties.SetName(idle, "Not opened or changed for");

        Button removeAll = Theme.DangerButton();
        removeAll.Click += async (_, _) =>
        {
            if (vm.Idle is not { } result || result.Safe.Count == 0)
            {
                return;
            }

            bool yes = await window.ConfirmAsync(
                $"Remove {Words.Count(result.Safe.Count, "project")} from this PC?",
                $"Each one is fully pushed to GitHub, with no uncommitted changes, stashes or other worktrees. They go to the Recycle Bin (or DevSweep's quarantine) and are recorded in History, and you can download them again. About {SizeLabels.Size(result.SafeBytes)} will be freed.",
                "Remove them");
            if (yes)
            {
                await vm.RemoveAllSafeAsync();
            }
        };
        var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        TextBlock status = Ui.Secondary(string.Empty);
        TextBlock summary = Ui.Secondary(string.Empty);
        var listHost = new Border();
        var noticeHost = new Border();

        var page = new StackPanel { Spacing = Tokens.Space4 };
        page.Children.Add(Ds.CommandBar(Ui.H(Tokens.Space3, Ui.Text("Not opened or changed for"), idle), null, removeAll));
        page.Children.Add(noticeHost);
        page.Children.Add(progress);
        page.Children.Add(status);
        page.Children.Add(summary);
        page.Children.Add(listHost);
        page.Children.Add(Ui.Caption("Sizes are measured exactly. Only fully pushed projects with no uncommitted changes, stashes or other worktrees are safe to remove; they go to the Recycle Bin (or DevSweep's quarantine), are recorded in History, and download again into the same folder."));

        var scroll = Ds.PageScroll(page, Tokens.Space2);
        var panel = new SidePanel(scroll, (key, panelSubs) => vm.All.FirstOrDefault(p => p.Id == key) is { } project ? ProjectPanel.Build(model, vm, project, panelSubs, window) : new Grid(), "Project details")
        {
            Closed = () => vm.SelectedId = null,
        };
        subs.Add(panel.Dispose);
        window.ActivePanel = panel;

        void Render()
        {
            summary.Text = vm.IdleSummary;
            IdleCleanupResult? result = vm.Idle;
            int safe = result?.Safe.Count ?? 0;
            Ui.Show(removeAll, safe > 0);
            removeAll.Content = safe > 0 ? $"Remove {Words.Count(safe, "safe project")} · free {SizeLabels.Size(result!.SafeBytes)}…" : "Remove";
            AutomationProperties.SetName(removeAll, (string)removeAll.Content);
            removeAll.IsEnabled = vm.BusyId is null;
            bool removing = vm.BusyId == "all";
            Ui.Show(progress, removing || vm.IsMeasuringIdle);
            status.Text = vm.RemoveAllStatus;
            Ui.Show(status, vm.RemoveAllStatus.Length > 0);
            if (result is null)
            {
                listHost.Child = vm.IsMeasuringIdle ? Ds.Card(Ds.Skeleton(3)) : null;
                return;
            }

            if (result.Projects.Count == 0)
            {
                listHost.Child = Ds.Card(Ds.EmptyState(Icons.CheckCircle, "Disk is tidy",
                    $"Nothing on this PC has sat untouched for {ProjectsViewModel.IdleChoices.FirstOrDefault(c => c.Days == vm.IdleDays).Title ?? vm.IdleDays + " days"}."));
                return;
            }

            var rows = new List<UIElement>();
            foreach (IdleProject item in result.Projects)
            {
                rows.Add(IdleRow(model, vm, item, window));
            }

            listHost.Child = Ds.RowsCard(rows);
        }

        subs.On(vm, nameof(ProjectsViewModel.Idle), Render);
        subs.On(vm, nameof(ProjectsViewModel.IsMeasuringIdle), Render);
        subs.On(vm, nameof(ProjectsViewModel.BusyId), Render);
        subs.On(vm, nameof(ProjectsViewModel.RemoveAllStatus), Render);
        subs.On(vm, nameof(ProjectsViewModel.Removing), Render);
        subs.On(vm, nameof(ProjectsViewModel.Notice), () => Ui.Fill(noticeHost, vm.Notice is { } n
            ? Ds.Banner(n.Kind switch { NoticeKind.Error => BannerKind.Critical, NoticeKind.Warning => BannerKind.Warning, _ => BannerKind.Info }, n.Text, null, "Dismiss", vm.Dismiss)
            : null));
        subs.On(vm, nameof(ProjectsViewModel.SelectedId), () => panel.Select(vm.SelectedId is { } id && vm.All.Any(p => p.Id == id) ? id : null));
        return panel.Root;
    }

    private static UIElement IdleRow(MainViewModel model, ProjectsViewModel vm, IdleProject item, MainWindow window)
    {
        Project p = item.Project;
        DateTimeOffset now = model.Backend.Now;
        var grid = new Grid { ColumnSpacing = Tokens.Space3 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });

        string when = p.LastUsed is { } last ? "Last opened " + Words.Relative(last, now) : "Last opened a long time ago";
        TextBlock sub = Ui.Caption($"{when} · {Words.Path(p.LocalPath ?? string.Empty, model.Backend.ProfilePath)}");
        sub.TextTrimming = TextTrimming.CharacterEllipsis;
        sub.MaxLines = 1;
        StackPanel text = Ui.V(2, Ui.Text(p.NameWithOwner, "DsStrong", maxLines: 1), sub);
        if (!item.IsSafe)
        {
            foreach (string reason in item.Reasons.Take(2))
            {
                TextBlock line = Ui.Caption(reason);
                Ui.Tint(line, "SystemFillColorCautionBrush");
                line.TextTrimming = TextTrimming.CharacterEllipsis;
                line.MaxLines = 1;
                Ui.Tip(line, string.Join(Environment.NewLine, item.Reasons));
                text.Children.Add(line);
            }
        }

        text.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(text);

        bool removing = vm.Removing.Contains(p.Id);
        bool busy = vm.BusyId == p.Id || vm.BusyId == "all";
        UIElement state;
        if (removing)
        {
            state = Ui.H(Tokens.Space2, new ProgressRing { IsActive = true, Width = 16, Height = 16 }, Ui.Caption("Removing…"));
        }
        else if (busy)
        {
            state = new ProgressRing { IsActive = true, Width = 18, Height = 18 };
        }
        else
        {
            (string statusText, Tone tone, string tooltip) = item.IsSafe ? ("Safe to remove", Tone.Good, "Everything in it is on GitHub.") : ProjectsViewModel.Safety(p);
            state = Ds.StatusTag(statusText, tone, tooltip);
        }

        Grid.SetColumn((FrameworkElement)state, 1);
        ((FrameworkElement)state).VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(state);

        if (!removing && !busy)
        {
            Button remove = Ui.Button(item.IsSafe ? "Remove" : "Remove…", () => _ = RemoveAsync(vm, p, window), $"Remove {p.NameWithOwner} from this PC", danger: true);
            Grid.SetColumn(remove, 2);
            remove.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(remove);
        }

        TextBlock size = Ui.Secondary((item.Exact ? string.Empty : "≈ ") + SizeLabels.Size(item.FreeableBytes));
        size.TextAlignment = TextAlignment.Right;
        size.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(size, 3);
        grid.Children.Add(size);
        Button info = Ui.IconButton(Icons.Info, $"Details for {p.NameWithOwner}", () => vm.Toggle(p));
        info.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(info, 4);
        grid.Children.Add(info);
        var row = new ListRow(grid, () => vm.Toggle(p), tabStop: false);
        AutomationProperties.SetName(row.Root, $"{p.NameWithOwner}, {SizeLabels.Size(item.FreeableBytes)}, {(item.IsSafe ? "safe to remove" : "not safe to remove")}");
        return row.Root;
    }

    private static async Task RemoveAsync(ProjectsViewModel vm, Project project, MainWindow window)
    {
        RemovalSafety? s = project.Safety;
        if (s?.IsSafeToRemove == true)
        {
            if (await window.ConfirmAsync($"Remove {project.Name} from this PC?",
                $"{project.Name} is fully backed up, so nothing is lost. It goes to the Recycle Bin (or DevSweep's quarantine), is recorded in History, and stays in the Projects list with a Download button.", "Remove from this PC"))
            {
                await vm.RemoveAsync(project);
            }

            return;
        }

        string bullets = string.Join("\n", (s?.Blockers ?? []).DefaultIfEmpty(s?.Headline ?? "Something DevSweep couldn't check").Select(l => "• " + l));
        if (await window.ConfirmAsync(
            $"Remove {project.Name} anyway?",
            $"{project.Name} isn't fully saved anywhere else. Removing it would lose:\n\n{bullets}\n\nDownloading {project.Name} again won't bring this back. The folder goes to the Recycle Bin (or DevSweep's quarantine), so you can still restore it from History.",
            "Remove anyway"))
        {
            await vm.RemoveAsync(project, acknowledgeLoss: true);
        }
    }

    // ---- Accounts ------------------------------------------------------------------------------------------------------------------------

    public static UIElement Accounts(MainViewModel model, ProjectsViewModel vm, Subscriptions subs, MainWindow window)
    {
        var page = new StackPanel { Spacing = Tokens.Space4 };
        var noticeHost = new Border();
        var content = new StackPanel { Spacing = Tokens.Space4 };
        page.Children.Add(Ui.Secondary("Each account's name and email are written into every project you download with it, so commits are attributed correctly whatever your global Git identity is. DevSweep uses each account's own token, so the account active in your terminal never changes."));
        page.Children.Add(noticeHost);
        page.Children.Add(content);

        void Render()
        {
            content.Children.Clear();
            ProjectsSnapshot? snapshot = vm.Report?.Snapshot;
            if (snapshot is null)
            {
                return;
            }

            IReadOnlyList<AccountSummary> summaries = snapshot.AccountSummaries;
            if (summaries.Count == 0)
            {
                content.Children.Add(ProjectListTab.GitHubSetupCard(vm) ?? Ds.Card(Ds.EmptyState(Icons.Person, "No accounts yet", "Sign in to GitHub to list and download your repositories.", Ui.Button("Add account…", () => vm.OpenSheet(ProjectSheet.AddAccount), accent: true))));
            }

            foreach (AccountSummary account in summaries)
            {
                content.Children.Add(AccountCard(vm, account, snapshot.Identities));
            }

            if (snapshot.OutsideOwners.Count > 0)
            {
                var outside = new StackPanel { Spacing = Tokens.Space2 };
                outside.Children.Add(Ds.SectionTitle("Clones from accounts you're not signed in to"));
                var rows = new List<UIElement>();
                foreach (OutsideOwner owner in snapshot.OutsideOwners)
                {
                    TextBlock size = Ui.Secondary($"{Words.Count(owner.Projects, "project")} · {SizeLabels.Size(owner.Bytes)}");
                    rows.Add(new Border { Padding = new Thickness(Tokens.Space3, Tokens.Space2, Tokens.Space3, Tokens.Space2), Child = Ui.Row(Ui.Text(owner.Owner, "DsStrong"), size) });
                }

                outside.Children.Add(Ds.RowsCard(rows));
                outside.Children.Add(Ui.Caption($"They show in Projects and can be removed safely, but to download more from {(snapshot.OutsideOwners.Count == 1 ? "this owner" : "these owners")} you'd need to sign in with an account that can read them."));
                content.Children.Add(outside);
            }

            content.Children.Add(FlowButtons.Build(
                Ui.Button("Add account…", () => vm.OpenSheet(ProjectSheet.AddAccount), "Add a GitHub account", accent: true),
                Ui.Button("Refresh", () => _ = vm.LoadAsync(), "Refresh the accounts"),
                Ui.Button("Downloads and folders in Settings", () => model.Go("settings"))));
        }

        subs.On(vm, nameof(ProjectsViewModel.Report), Render);
        subs.On(vm, nameof(ProjectsViewModel.Notice), () => Ui.Fill(noticeHost, vm.Notice is { } n
            ? Ds.Banner(n.Kind switch { NoticeKind.Error => BannerKind.Critical, NoticeKind.Warning => BannerKind.Warning, _ => BannerKind.Info }, n.Text, null, "Dismiss", vm.Dismiss)
            : null));
        return Ds.PageScroll(page, Tokens.Space2);
    }

    private static UIElement AccountCard(ProjectsViewModel vm, AccountSummary account, IReadOnlyDictionary<string, GitIdentity> identities)
    {
        GitIdentity saved = identities.FirstOrDefault(i => i.Key.Equals(account.Login, StringComparison.OrdinalIgnoreCase)).Value ?? new GitIdentity();
        var name = new TextBox { Header = "Name for commits", Text = saved.Name, MinWidth = 200 };
        var email = new TextBox { Header = "Email for commits", Text = saved.Email, MinWidth = 220 };
        AutomationProperties.SetName(name, $"Name for commits as {account.Login}");
        AutomationProperties.SetName(email, $"Email for commits as {account.Login}");
        Button save = Ui.Button("Save", () => vm.SaveIdentity(account.Login, new GitIdentity(name.Text.Trim(), email.Text.Trim())), $"Save the commit identity for {account.Login}");
        var stateHost = new Border();

        void Check()
        {
            var now = new GitIdentity(name.Text.Trim(), email.Text.Trim());
            bool changed = now != saved;
            save.IsEnabled = changed;
            stateHost.Child = now.IsBlank
                ? Ds.CheckRow(false, "Not set. Projects downloaded with this account use your global Git identity.")
                : !changed ? Ds.CheckRow(true, $"Commits in projects downloaded with this account are made as {now}.") : null;
        }

        name.TextChanged += (_, _) => Check();
        email.TextChanged += (_, _) => Check();
        Check();

        FontIcon avatar = Ui.Icon(Icons.Person, 22);
        avatar.VerticalAlignment = VerticalAlignment.Center;
        StackPanel title = Ui.H(Tokens.Space2, Ui.Text(account.Login, "DsStrong"));
        if (account.IsActive)
        {
            title.Children.Add(Ds.StatusTag("Active in terminal", Tone.Neutral, "The account gh and git use in a terminal. DevSweep never changes it."));
        }

        TextBlock counts = Ui.Caption($"{Words.Count(account.Repos, "repository").Replace("repositorys", "repositories", StringComparison.Ordinal)} · {account.OnDisk} on this PC · {SizeLabels.Size(account.Bytes)}");
        var header = new Grid { ColumnSpacing = Tokens.Space3 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        StackPanel names = Ui.V(2, title, counts);
        Grid.SetColumn(names, 1);
        header.Children.Add(avatar);
        header.Children.Add(names);

        Grid fields = Ui.Row(Ui.H(Tokens.Space3, name, email), save);
        fields.ColumnDefinitions[0].Width = GridLength.Auto;
        fields.VerticalAlignment = VerticalAlignment.Bottom;
        Border card = Ds.Card(Ui.V(Tokens.Space3, header, fields, stateHost));
        AutomationProperties.SetName(card, "GitHub account " + account.Login);
        return card;
    }

    // ---- Activity ------------------------------------------------------------------------------------------------------------------------

    public static UIElement Activity(MainViewModel model, ProjectsViewModel vm, Subscriptions subs)
    {
        var page = new StackPanel { Spacing = Tokens.Space4 };
        var chips = new Border();
        var list = new StackPanel { Spacing = Tokens.Space4 };
        page.Children.Add(chips);
        page.Children.Add(list);

        void Render()
        {
            IReadOnlyList<ActivityEntry> all = vm.Report?.Activity ?? [];
            var chipItems = new List<UIElement>();
            chipItems.Add(Ds.Chip("All", all.Count > 0 ? all.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : null, vm.ActivityFilter is null, "Show all activity", () => vm.ActivityFilter = null));
            foreach (ActivityKind kind in all.Select(a => a.Kind).Distinct().Order())
            {
                ActivityKind k = kind;
                chipItems.Add(Ds.Chip(KindTitle(kind), all.Count(a => a.Kind == kind).ToString(System.Globalization.CultureInfo.InvariantCulture), vm.ActivityFilter == kind, $"Show {KindTitle(kind)}", () => vm.ActivityFilter = k));
            }

            chips.Child = FlowButtons.Build([.. chipItems]);
            list.Children.Clear();
            List<ActivityEntry> shown = [.. all.Where(a => vm.ActivityFilter is null || a.Kind == vm.ActivityFilter).OrderByDescending(a => a.Date)];
            if (shown.Count == 0)
            {
                list.Children.Add(Ds.Card(Ds.EmptyState(Icons.Clock, "Nothing yet", "Downloads, removals, publishes, pushes and account changes are listed here.")));
                return;
            }

            foreach (IGrouping<DateTime, ActivityEntry> day in shown.GroupBy(a => a.Date.ToLocalTime().Date))
            {
                var rows = new List<UIElement>();
                foreach (ActivityEntry entry in day)
                {
                    rows.Add(ActivityRow(entry));
                }

                list.Children.Add(Ui.V(Tokens.Space2, Ds.SectionTitle(Words.Day(new DateTimeOffset(day.Key))), Ds.RowsCard(rows)));
            }
        }

        subs.On(vm, nameof(ProjectsViewModel.Report), Render);
        subs.On(vm, nameof(ProjectsViewModel.ActivityFilter), Render);
        return Ds.PageScroll(page, Tokens.Space2);
    }

    private static UIElement ActivityRow(ActivityEntry entry)
    {
        TextBlock time = Ui.Caption(Words.Time(entry.Date));
        time.Width = 48;
        FontIcon icon = Ui.Icon(KindIcon(entry.Kind), 16);
        Ui.Tint(icon, KindBrush(entry.Kind));
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 0, 0);
        TextBlock what = Ui.Text(Verb(entry.Kind) + " " + entry.Subject, "DsStrong");
        what.TextWrapping = TextWrapping.Wrap;
        StackPanel text = Ui.V(2, what);
        if (entry.Detail.Length > 0)
        {
            TextBlock detail = Ui.Caption(entry.Detail);
            detail.TextWrapping = TextWrapping.Wrap;
            text.Children.Add(detail);
        }

        var grid = new Grid { ColumnSpacing = Tokens.Space3, Padding = new Thickness(Tokens.Space3, Tokens.Space2, Tokens.Space3, Tokens.Space2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        time.VerticalAlignment = VerticalAlignment.Top;
        time.Margin = new Thickness(0, 3, 0, 0);
        Grid.SetColumn(icon, 1);
        Grid.SetColumn(text, 2);
        grid.Children.Add(time);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        AutomationProperties.SetName(grid, $"{Words.Time(entry.Date)}, {Verb(entry.Kind)} {entry.Subject}. {entry.Detail}");
        return grid;
    }

    internal static string Verb(ActivityKind kind) => kind switch
    {
        ActivityKind.Download => "Downloaded",
        ActivityKind.Remove => "Removed",
        ActivityKind.Publish => "Published",
        ActivityKind.Push => "Pushed",
        ActivityKind.AddRepo => "Added",
        ActivityKind.SignIn => "Signed in to",
        ActivityKind.Identity => "Commit identity for",
        ActivityKind.Fetch => "Compared with the remote:",
        _ => "Searched",
    };

    internal static string KindTitle(ActivityKind kind) => kind switch
    {
        ActivityKind.Download => "Downloads",
        ActivityKind.Remove => "Removals",
        ActivityKind.Publish => "Publishes",
        ActivityKind.Push => "Pushes",
        ActivityKind.AddRepo => "Added",
        ActivityKind.SignIn => "Sign-ins",
        ActivityKind.Identity => "Identities",
        ActivityKind.Fetch => "Fetches",
        _ => "Searches",
    };

    private static string KindIcon(ActivityKind kind) => kind switch
    {
        ActivityKind.Download => Icons.Download,
        ActivityKind.Remove => Icons.Delete,
        ActivityKind.Publish or ActivityKind.Push => Icons.Upload,
        ActivityKind.AddRepo => Icons.Add,
        ActivityKind.SignIn => Icons.PersonAdd,
        ActivityKind.Identity => Icons.Person,
        ActivityKind.Fetch => Icons.Refresh,
        _ => Icons.Search,
    };

    private static string KindBrush(ActivityKind kind) => kind switch
    {
        ActivityKind.Download or ActivityKind.AddRepo => "AccentTextFillColorPrimaryBrush",
        ActivityKind.Remove => "SystemFillColorCautionBrush",
        ActivityKind.Publish or ActivityKind.Push => "SystemFillColorSuccessBrush",
        _ => "TextFillColorSecondaryBrush",
    };
}
