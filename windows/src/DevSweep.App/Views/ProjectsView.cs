using DevSweep.App.Design;
using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DevSweep.App.Views;

/// <summary>
/// Projects: Projects, Cleanup, Accounts, Activity (the Mac's four tabs). The page keeps its header and tabs; the body is built once per
/// tab and updates itself as the view model changes, so typing in the search box or an action finishing never rebuilds the list.
/// </summary>
internal static class ProjectsView
{
    public static UIElement Build(MainViewModel model, Subscriptions subs, MainWindow window)
    {
        ProjectsViewModel vm = model.Projects;
        TextBlock subtitle = Ui.Secondary(string.Empty);
        Button refresh = Ui.IconButton(Icons.Refresh, "Refresh projects", () => _ = vm.LoadAsync());
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
            if (tabs.SelectedItem?.Tag is ProjectsTab tab && vm.Tab != tab)
            {
                vm.Tab = tab;
            }
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var top = new Border
        {
            Padding = new Thickness(Tokens.PageGutter, Tokens.Space6, Tokens.PageGutter, Tokens.Space2),
            Child = Ui.V(Tokens.Space3, Ds.PageHeader("Projects", subtitle, refresh), tabs),
        };
        var bodyHost = new Border();
        Grid.SetRow(bodyHost, 1);
        root.Children.Add(top);
        root.Children.Add(bodyHost);

        string? shown = null;
        var bodySubs = new Subscriptions();
        subs.Add(() => bodySubs.Dispose());

        void Render()
        {
            foreach (SelectorBarItem item in tabs.Items)
            {
                var tab = (ProjectsTab)item.Tag;
                string count = vm.Count(tab);
                item.Text = ProjectsViewModel.TabTitle(tab) + (count.Length > 0 ? $" ({count})" : string.Empty);
                if (tab == vm.Tab && !item.IsSelected)
                {
                    item.IsSelected = true;
                }
            }

            subtitle.Text = vm.Subtitle;
            Ui.Show(tabs, vm.State != PlaceState.Unavailable);
            Ui.Show(refresh, vm.State is PlaceState.Ready or PlaceState.Failed || (vm.State == PlaceState.Loading && vm.Report is not null));

            string key = vm.State switch
            {
                PlaceState.Unavailable => "unavailable",
                PlaceState.Failed => "failed",
                _ when vm.Report is null => "loading",
                _ => vm.Tab.ToString(),
            };
            if (key == shown)
            {
                return;
            }

            shown = key;
            bodySubs.Dispose();
            bodySubs = new Subscriptions();
            window.ActivePanel = null;
            bodyHost.Child = key switch
            {
                "unavailable" => Padded(Ds.Card(Ui.V(Tokens.Space2, Ui.Text("Projects is off", "DsSubtitle"), Ui.Secondary(ProjectsViewModel.UnavailableText), Ui.Button("Open Settings", () => model.Go("settings"))))),
                "failed" => Padded(Ds.Banner(BannerKind.Critical, "Projects couldn't load: " + vm.Error, "Projects", "Try again", () => _ = vm.LoadAsync())),
                "loading" => Padded(Ds.Card(Ds.Skeleton(5))),
                nameof(ProjectsTab.Cleanup) => ProjectTabs.Cleanup(model, vm, bodySubs, window),
                nameof(ProjectsTab.Accounts) => ProjectTabs.Accounts(model, vm, bodySubs, window),
                nameof(ProjectsTab.Activity) => ProjectTabs.Activity(model, vm, bodySubs),
                _ => ProjectListTab.Build(model, vm, bodySubs, window),
            };
        }

        subs.On(vm, null, Render);
        subs.Add(() => window.ActivePanel = null);
        return root;
    }

    private static UIElement Padded(UIElement content) => new Border { Padding = new Thickness(Tokens.PageGutter, Tokens.Space2, Tokens.PageGutter, Tokens.Space6), Child = content };
}
