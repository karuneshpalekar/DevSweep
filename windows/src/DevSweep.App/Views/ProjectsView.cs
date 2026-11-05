using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Places;
using DevSweep.App.ViewModels.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DevSweep.App.Views;

/// <summary>Projects: Projects, Cleanup, Accounts, Activity (the Mac layout), against <see cref="IProjectsSource"/>.</summary>
internal static class ProjectsView
{
    public static UIElement Build(MainViewModel model, Subscriptions subs, MainWindow window)
    {
        ProjectsViewModel vm = model.Projects;
        var tabs = new SelectorBar();
        AutomationProperties.SetName(tabs, "Projects tabs");
        foreach (ProjectsTab tab in Enum.GetValues<ProjectsTab>())
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
        StackPanel page = Ui.V(16, Ui.PageHeader("Projects", Ui.Secondary("Your repos on this PC and on GitHub, across every account you're signed in to.")), tabs, body);

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
            switch (vm.State)
            {
                case PlaceState.Unavailable:
                    body.Children.Add(Ui.Card(Ui.V(8, Ui.Text("Projects isn't available yet", "DsSubtitle"), Ui.Secondary(ProjectsViewModel.UnavailableText))));
                    return;
                case PlaceState.NotChecked:
                case PlaceState.Loading:
                    body.Children.Add(Ui.H(10, new ProgressRing { IsActive = true, Width = 20, Height = 20 }, Ui.Secondary("Looking for your projects…")));
                    return;
                case PlaceState.Failed:
                    body.Children.Add(Ui.Text("Projects couldn't load: " + vm.Error, "DsCriticalText"));
                    return;
            }

            ProjectsReport report = vm.Report!;
            switch (vm.Tab)
            {
                case ProjectsTab.Projects:
                    body.Children.Add(ProjectList(report.Projects, model.Backend.Now));
                    break;
                case ProjectsTab.Cleanup:
                    body.Children.Add(Cleanup(vm, model.Backend.Now));
                    break;
                case ProjectsTab.Accounts:
                    body.Children.Add(Accounts(report.Accounts));
                    break;
                default:
                    body.Children.Add(Activity(report.Activity, model.Backend.Now));
                    break;
            }
        }

        subs.On(vm, null, Render);
        return Ui.Scroll(page);
    }

    private static UIElement ProjectList(IReadOnlyList<ProjectInfo> projects, DateTimeOffset now)
    {
        var rows = new StackPanel { Spacing = 10 };
        foreach (ProjectInfo project in projects)
        {
            if (rows.Children.Count > 0)
            {
                rows.Children.Add(Ui.Divider());
            }

            Border safety = Ui.Badge(project.Safety, project.SafeToRemove ? Tone.Good : Tone.Caution,
                project.SafeToRemove ? "Everything in it is on GitHub." : "Removing it would lose work that isn't on GitHub.");
            string where = project.Path ?? "On GitHub only · downloads again on demand";
            string when = project.LastOpened is { } last ? "changed " + Words.Relative(last, now) : string.Empty;
            rows.Children.Add(Ui.Row(Ui.V(2, Ui.Text(project.Name, "DsStrong"), Ui.Caption($"{where}{(when.Length > 0 ? " · " + when : string.Empty)}{(project.Account is { } a ? " · " + a : string.Empty)}")),
                Ui.Secondary(project.OnDisk ? SizeLabels.Size(project.Bytes) : string.Empty), safety));
        }

        return Ui.Card(rows);
    }

    private static UIElement Cleanup(ProjectsViewModel vm, DateTimeOffset now)
    {
        var idle = new ComboBox { Width = 160 };
        foreach ((int days, string title) in ProjectsViewModel.IdleChoices)
        {
            idle.Items.Add(title);
        }

        idle.SelectedIndex = Math.Max(0, ProjectsViewModel.IdleChoices.ToList().FindIndex(c => c.Days == vm.IdleDays));
        idle.SelectionChanged += (_, _) => vm.IdleDays = ProjectsViewModel.IdleChoices[Math.Max(0, idle.SelectedIndex)].Days;
        AutomationProperties.SetName(idle, "Not opened or changed for");
        return Ui.V(12,
            Ui.Row(Ui.Text("Not opened or changed for"), idle),
            Ui.Secondary(vm.IdleSummary),
            vm.Idle.Count == 0 ? Ui.Card(Ui.V(4, Ui.Text("Disk is tidy", "DsSubtitle"), Ui.Secondary("Nothing idle for that long."))) : ProjectList(vm.Idle, now),
            Ui.Caption("Fully pushed ones, with no uncommitted changes or stashes, are safe to remove. They go to the Recycle Bin, and you can download them again."));
    }

    private static UIElement Accounts(IReadOnlyList<AccountInfo> accounts)
    {
        var rows = new StackPanel { Spacing = 10 };
        foreach (AccountInfo account in accounts)
        {
            if (rows.Children.Count > 0)
            {
                rows.Children.Add(Ui.Divider());
            }

            rows.Children.Add(Ui.Row(
                Ui.V(2, Ui.Text(account.Login, "DsStrong"), Ui.Caption($"Commits as {account.Name} <{account.Email}>"), Ui.Caption($"{account.Repositories} repositories · {account.OnDisk} on this PC · {SizeLabels.Size(account.Bytes)}")),
                Ui.Badge(account.SignedIn ? "Signed in" : "Not signed in", account.SignedIn ? Tone.Good : Tone.Caution)));
        }

        return Ui.V(8, Ui.Card(rows), Ui.Caption("Each account's name and email are written into every project you download with it, so commits are attributed correctly whatever your global Git identity is."));
    }

    private static UIElement Activity(IReadOnlyList<ActivityInfo> activity, DateTimeOffset now)
    {
        if (activity.Count == 0)
        {
            return Ui.Card(Ui.V(4, Ui.Text("Nothing yet", "DsSubtitle"), Ui.Secondary("Downloads, removals, publishes and account changes are listed here.")));
        }

        var rows = new StackPanel { Spacing = 8 };
        foreach (ActivityInfo item in activity.OrderByDescending(a => a.Date))
        {
            rows.Children.Add(Ui.Row(Ui.Text(item.Text), Ui.Caption(Words.Relative(item.Date, now))));
        }

        return Ui.Card(rows);
    }
}
