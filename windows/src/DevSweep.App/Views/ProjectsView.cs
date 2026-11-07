using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Places;
using DevSweep.App.ViewModels.Text;
using DevSweep.Core.Projects;
using DevSweep.Core.Projects.GitHub;
using DevSweep.Core.Projects.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DevSweep.App.Views;

/// <summary>Projects: Projects, Cleanup, Accounts, Activity (the Mac layout), bound to M3c's result types. Tabs follow their flags.</summary>
internal static class ProjectsView
{
    public static UIElement Build(MainViewModel model, Subscriptions subs, MainWindow window)
    {
        ProjectsViewModel vm = model.Projects;
        var tabs = new SelectorBar();
        AutomationProperties.SetName(tabs, "Projects tabs");
        foreach (ProjectsTab tab in vm.Tabs)
        {
            var item = new SelectorBarItem { Text = ProjectsViewModel.TabTitle(tab), Tag = tab, IsSelected = tab == vm.Tab };
            AutomationProperties.SetName(item, ProjectsViewModel.TabTitle(tab));
            tabs.Items.Add(item);
        }

        tabs.SelectionChanged += (_, _) =>
        {
            if (tabs.SelectedItem?.Tag is ProjectsTab tab)
            {
                vm.Tab = tab;
            }
        };
        var body = new StackPanel { Spacing = 16 };
        Button refresh = Ui.Button("Refresh", () => _ = vm.LoadAsync());
        StackPanel page = Ui.V(16, Ui.PageHeader("Projects", Ui.Secondary("Your repos on this PC and on GitHub, across every account you're signed in to."), refresh), tabs, body);

        void Render()
        {
            body.Children.Clear();
            foreach (SelectorBarItem item in tabs.Items)
            {
                var tab = (ProjectsTab)item.Tag;
                item.IsSelected = tab == vm.Tab;
                string count = vm.Count(tab);
                item.Text = ProjectsViewModel.TabTitle(tab) + (count.Length > 0 ? $" ({count})" : string.Empty);
            }

            Ui.Show(tabs, vm.State != PlaceState.Unavailable);
            Ui.Show(refresh, vm.State is PlaceState.Ready or PlaceState.Failed);
            switch (vm.State)
            {
                case PlaceState.Unavailable:
                    body.Children.Add(Ui.Card(Ui.V(8, Ui.Text("Projects is off", "DsSubtitle"), Ui.Secondary(ProjectsViewModel.UnavailableText), Ui.Button("Open Settings", () => model.Go("settings")))));
                    return;
                case PlaceState.NotChecked:
                case PlaceState.Loading:
                    body.Children.Add(Ui.H(10, new ProgressRing { IsActive = true, Width = 20, Height = 20 }, Ui.Secondary("Looking for your projects and checking each one…")));
                    return;
                case PlaceState.Failed:
                    body.Children.Add(Ui.Text("Projects couldn't load: " + vm.Error, "DsCriticalText"));
                    return;
            }

            if (vm.Error is { } error)
            {
                body.Children.Add(Ui.Text(error, "DsWarningText"));
            }

            ProjectsReport report = vm.Report!;
            DateTimeOffset now = model.Backend.Now;
            switch (vm.Tab)
            {
                case ProjectsTab.Projects:
                    ProjectsTabBody(vm, report.Snapshot, body, now);
                    break;
                case ProjectsTab.Cleanup:
                    CleanupTab(vm, body, now);
                    break;
                case ProjectsTab.Accounts:
                    AccountsTab(vm, report.Snapshot, body);
                    break;
                default:
                    ActivityTab(report.Activity, body, now);
                    break;
            }
        }

        subs.On(vm, null, Render);
        return Ui.Scroll(page);
    }

    private static void ProjectsTabBody(ProjectsViewModel vm, ProjectsSnapshot snapshot, StackPanel body, DateTimeOffset now)
    {
        body.Children.Add(Ui.Card(Ui.H(10, Ui.Icon(""),
            Ui.Text("Compared with GitHub as of each clone's last fetch: commits pushed from another PC since then aren't known here. \"Unknown\" and \"not checked\" are never treated as safe.", "DsBody")), "DsBetter"));
        var rows = new StackPanel { Spacing = 10 };
        foreach (Project project in snapshot.Projects.OrderByDescending(p => p.OnDisk).ThenByDescending(p => p.LastUsed))
        {
            if (rows.Children.Count > 0)
            {
                rows.Children.Add(Ui.Divider());
            }

            rows.Children.Add(ProjectRow(vm, project, now, project.LocalSize, project.LocalSizeComplete));
        }

        body.Children.Add(Ui.Card(rows));
        foreach (string message in snapshot.Messages)
        {
            body.Children.Add(Ui.Caption(message));
        }

        foreach (string folder in snapshot.Search.CloudOnlyFolders.Take(5))
        {
            body.Children.Add(Ui.Caption($"In OneDrive, not checked: {folder} (its files are online-only; looking would download them)."));
        }

        foreach (string folder in snapshot.Search.UnreadableRoots.Take(5))
        {
            body.Children.Add(Ui.Caption($"Couldn't read {folder}."));
        }
    }

    private static Grid ProjectRow(ProjectsViewModel vm, Project project, DateTimeOffset now, long bytes, bool exact)
    {
        (string text, Tone tone, string tooltip) = ProjectsViewModel.Safety(project);
        string where = project.LocalPath ?? "Only on GitHub · downloads again on demand";
        string when = project.LastUsed is { } last ? " · used " + Words.Relative(last, now) : string.Empty;
        var lines = Ui.V(2,
            Ui.Text(project.NameWithOwner, "DsStrong"),
            Ui.Caption($"{where}{when}{(project.Account is { } a ? " · " + a : string.Empty)}"),
            project.Safety is { } safety && project.OnDisk ? Ui.Caption("Compared " + ProjectsViewModel.FetchNote(safety)) : null);
        Button? push = project.Safety is { UnpushedCommits: > 0 }
            ? Ui.Button("Push…", () => vm.Push(project), $"Push {project.NameWithOwner} in a terminal window")
            : null;
        return Ui.Row(lines,
            Ui.Secondary(project.OnDisk ? (exact ? string.Empty : "at least ") + SizeLabels.Size(bytes) : string.Empty),
            Ui.Badge(text, tone, tooltip),
            push);
    }

    private static void CleanupTab(ProjectsViewModel vm, StackPanel body, DateTimeOffset now)
    {
        var idle = new ComboBox { Width = 160 };
        foreach ((int days, string title) in ProjectsViewModel.IdleChoices)
        {
            idle.Items.Add(title);
        }

        idle.SelectedIndex = Math.Max(0, ProjectsViewModel.IdleChoices.ToList().FindIndex(c => c.Days == vm.IdleDays));
        idle.SelectionChanged += (_, _) => vm.IdleDays = ProjectsViewModel.IdleChoices[Math.Max(0, idle.SelectedIndex)].Days;
        AutomationProperties.SetName(idle, "Not opened or changed for");
        body.Children.Add(Ui.Row(Ui.Text("Not opened or changed for"), idle));
        body.Children.Add(Ui.Secondary(vm.IdleSummary));
        if (vm.Idle is { } result)
        {
            if (result.Projects.Count == 0)
            {
                body.Children.Add(Ui.Card(Ui.V(4, Ui.Text("Disk is tidy", "DsSubtitle"), Ui.Secondary("Nothing idle for that long."))));
            }
            else
            {
                var rows = new StackPanel { Spacing = 10 };
                foreach (IdleProject item in result.Projects)
                {
                    if (rows.Children.Count > 0)
                    {
                        rows.Children.Add(Ui.Divider());
                    }

                    rows.Children.Add(ProjectRow(vm, item.Project, now, item.FreeableBytes, item.Exact));
                    foreach (string reason in item.Reasons.Take(3))
                    {
                        rows.Children.Add(Ui.Caption("    " + reason));
                    }
                }

                body.Children.Add(Ui.Card(rows));
            }
        }

        body.Children.Add(Ui.Caption("Sizes are measured exactly. Only fully pushed projects with no uncommitted changes, stashes or other worktrees are safe to remove; they go to the Recycle Bin (or DevSweep's quarantine) and download again into the same folder."));
    }

    private static void AccountsTab(ProjectsViewModel vm, ProjectsSnapshot snapshot, StackPanel body)
    {
        GitHubAccounts? accounts = snapshot.Accounts;
        if (accounts is null || accounts.Status != GitHubStatus.SignedIn)
        {
            string text = accounts?.Status == GitHubStatus.NotInstalled
                ? "The GitHub CLI isn't installed. Install it with: winget install GitHub.cli"
                : accounts?.Message ?? "No GitHub account is signed in.";
            body.Children.Add(Ui.Card(Ui.V(8, Ui.Text(text), Ui.Button("Sign in to GitHub…", vm.SignIn))));
            return;
        }

        var rows = new StackPanel { Spacing = 10 };
        foreach (AccountSummary account in snapshot.AccountSummaries)
        {
            if (rows.Children.Count > 0)
            {
                rows.Children.Add(Ui.Divider());
            }

            string identity = account.IdentityMissing ? "No name and email set for this account's commits" : $"Commits as {account.Identity}";
            rows.Children.Add(Ui.Row(
                Ui.V(2, Ui.Text(account.Login, "DsStrong"), Ui.Caption(identity), Ui.Caption($"{account.Repos} repositories · {account.OnDisk} on this PC · {SizeLabels.Size(account.Bytes)}")),
                account.IsActive ? Ui.Badge("Active in gh", Tone.Accent) : null,
                account.IdentityMissing ? Ui.Badge("Identity missing", Tone.Caution) : Ui.Badge("Signed in", Tone.Good)));
        }

        body.Children.Add(Ui.Card(rows));
        foreach (OutsideOwner owner in snapshot.OutsideOwners)
        {
            body.Children.Add(Ui.Caption($"{owner.Owner}: {Words.Count(owner.Projects, "clone")} ({SizeLabels.Size(owner.Bytes)}) from an owner nobody here is signed in as."));
        }

        body.Children.Add(Ui.Row(
            Ui.Caption("Each account's name and email are written into every project you download with it. DevSweep uses each account's own token, so the account active in your terminal never changes."),
            Ui.Button("Add account…", vm.SignIn, "Sign in to another GitHub account in a terminal window")));
    }

    private static void ActivityTab(IReadOnlyList<ActivityEntry> activity, StackPanel body, DateTimeOffset now)
    {
        if (activity.Count == 0)
        {
            body.Children.Add(Ui.Card(Ui.V(4, Ui.Text("Nothing yet", "DsSubtitle"), Ui.Secondary("Downloads, removals, publishes, pushes and account changes are listed here."))));
            return;
        }

        var rows = new StackPanel { Spacing = 8 };
        foreach (ActivityEntry entry in activity.OrderByDescending(a => a.Date))
        {
            rows.Children.Add(Ui.Row(Ui.V(0, Ui.Text($"{entry.Subject}: {entry.Detail}"), Ui.Caption(entry.Kind.ToString())), Ui.Caption(Words.Relative(entry.Date, now))));
        }

        body.Children.Add(Ui.Card(rows));
    }
}
