using DevSweep.App.Design;
using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Text;
using DevSweep.Core.Cleaning;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DevSweep.App.Views;

/// <summary>Clean up: filter by kind, grouped by risk, size with its confidence, selection, ⓘ inspector, Review.</summary>
internal static class CleanUpView
{
    public static UIElement Build(MainViewModel model, Subscriptions subs, MainWindow window)
    {
        CleanUpViewModel vm = model.CleanUp;
        TextBlock subtitle = Ui.Secondary(string.Empty);
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Tokens.Space2 };
        var search = new AutoSuggestBox { PlaceholderText = "Search", Width = 240, QueryIcon = new SymbolIcon(Symbol.Find) };
        AutomationProperties.SetName(search, "Search Clean up");
        search.TextChanged += (_, _) => vm.Search = search.Text;
        var list = new StackPanel { Spacing = Tokens.Space6 };
        var reportHost = new Border();
        var chips = new ScrollViewer { Content = filters, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalAlignment = VerticalAlignment.Center };

        StackPanel page = Ui.V(Tokens.Space4,
            Ds.PageHeader("Clean up", subtitle, ScanButtons.Build(model.Scan, subs)),
            Ds.CommandBar(chips, search),
            ScanProgressView.Build(model.Scan, subs, compact: false),
            list,
            reportHost);

        var column = new Grid();
        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        column.Children.Add(Ds.PageScroll(page));
        UIElement bar = SelectionBar(vm);
        Grid.SetRow((FrameworkElement)bar, 1);
        column.Children.Add(bar);

        var panel = new SidePanel(
            column,
            (key, panelSubs) => vm.All.FirstOrDefault(f => f.Id == key) is { } item ? InspectorView.Build(model, item, panelSubs, window) : new Grid(),
            "Details")
        {
            Closed = () => vm.Inspected = null,
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

        subs.On(vm, nameof(CleanUpViewModel.Subtitle), () => subtitle.Text = vm.Subtitle);
        subs.OnCollection(vm.Filters, () => RenderFilters(vm, filters));
        subs.On(vm, nameof(CleanUpViewModel.SelectedKind), () => RenderFilters(vm, filters));
        subs.OnCollection(vm.Groups, () => RenderList(model, vm, list, subs));
        subs.On(model.Scan, nameof(ScanViewModel.Report), () =>
        {
            reportHost.Child = model.Scan.Report is { } report
                ? Ui.V(Tokens.Space2, Ds.SectionTitle("About this scan"), Ds.Card(ScanReportView.Build(report, expanded: false)))
                : null;
            RenderList(model, vm, list, subs);
        });
        subs.On(model.Scan, nameof(ScanViewModel.IsScanning), () => RenderList(model, vm, list, subs));
        subs.On(vm, nameof(CleanUpViewModel.Inspected), () => panel.Select(vm.Inspected?.Id));
        subs.On(vm, nameof(CleanUpViewModel.SelectedCount), () => Ui.Show(bar, model.Scan.HasScanned && vm.All.Count > 0));
        return panel.Root;
    }

    private static void RenderFilters(CleanUpViewModel vm, StackPanel filters)
    {
        filters.Children.Clear();
        if (vm.All.Count == 0)
        {
            return;
        }

        foreach (KindFilter filter in vm.Filters)
        {
            filters.Children.Add(Ds.Chip(filter.Title, filter.Detail, filter.Kind == vm.SelectedKind, $"Show {filter.Title}, {filter.Detail}", () => vm.SelectedKind = filter.Kind));
        }
    }

    private static void RenderList(MainViewModel model, CleanUpViewModel vm, StackPanel list, Subscriptions subs)
    {
        list.Children.Clear();
        if (!model.Scan.HasScanned)
        {
            list.Children.Add(model.Scan.IsScanning
                ? Ds.Card(Ds.Skeleton(5))
                : Ds.Card(Ds.EmptyState(Icons.CleanUp, "Not scanned yet", "Scan to see what's safe to clean on this PC. Nothing changes until you review and confirm.", Ui.Button("Scan now", () => _ = model.Scan.ScanAsync(), accent: true))));
            return;
        }

        if (vm.Groups.Count == 0)
        {
            list.Children.Add(Ds.Card(vm.All.Count == 0
                ? Ds.EmptyState(Icons.CheckCircle, "Nothing to clean", "DevSweep didn't find anything to clean. Every check below ran.")
                : Ds.EmptyState(Icons.Search, "Nothing matches", "Try another filter or search.")));
            return;
        }

        foreach (RiskGroup group in vm.Groups)
        {
            var rows = new List<UIElement>();
            foreach (FindingViewModel item in group.Items)
            {
                rows.Add(Row(vm, item, subs));
            }

            HyperlinkButton? all = group.CanSelectAll
                ? Ui.Link("Select all", () => CleanUpViewModel.ToggleGroup(group), $"Select or deselect all {group.Title} items")
                : null;
            Grid header = Ui.Row(Ui.H(Tokens.Space3, Ds.StatusTag(group.Title, group.Tone, group.Summary), Ui.Secondary(group.Detail)), all);
            header.Margin = new Thickness(Tokens.Space1, 0, Tokens.Space1, 0);
            list.Children.Add(Ui.V(Tokens.Space2, header, Ds.RowsCard(rows)));
        }
    }

    private static UIElement Row(CleanUpViewModel vm, FindingViewModel item, Subscriptions subs)
    {
        var grid = new Grid { ColumnSpacing = Tokens.Space3 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });

        FrameworkElement select;
        if (item.CanSelect)
        {
            var box = new CheckBox { MinWidth = 0, IsChecked = item.IsSelected, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(box, $"Select {item.Title}");
            box.Checked += (_, _) => item.IsSelected = true;
            box.Unchecked += (_, _) => item.IsSelected = false;
            subs.On(item, nameof(FindingViewModel.IsSelected), () => box.IsChecked = item.IsSelected);
            select = box;
        }
        else
        {
            FontIcon lockIcon = Ui.Icon(Icons.Lock, 14);
            Ui.Tip(lockIcon, "You run the commands for this yourself; open the details to see them.");
            lockIcon.HorizontalAlignment = HorizontalAlignment.Center;
            lockIcon.VerticalAlignment = VerticalAlignment.Center;
            select = lockIcon;
        }

        grid.Children.Add(select);
        TextBlock subtitle = Ui.Caption(item.Subtitle);
        subtitle.MaxLines = 1;
        subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
        StackPanel text = Ui.V(0, Ui.Text(item.Title, "DsStrong", maxLines: 1), subtitle);
        text.VerticalAlignment = VerticalAlignment.Center;
        Ui.Tip(text, item.Subtitle);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        StackPanel size = Ui.Size(item.SizeText, item.Badges, item.SizeTooltip);
        size.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(size, 2);
        grid.Children.Add(size);

        FrameworkElement action;
        if (item.Actions.Count > 1)
        {
            var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (CleanAction choice in item.Actions)
            {
                combo.Items.Add(choice.DisplayLabel);
            }

            combo.SelectedIndex = Math.Max(0, item.Actions.ToList().IndexOf(item.ChosenAction));
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedIndex >= 0)
                {
                    item.ChosenAction = item.Actions[combo.SelectedIndex];
                }
            };
            AutomationProperties.SetName(combo, $"What to do with {item.Title}");
            action = combo;
        }
        else
        {
            TextBlock label = Ui.Secondary(item.ChosenAction.DisplayLabel);
            label.TextWrapping = TextWrapping.NoWrap;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            Ui.Tip(label, item.ChosenAction.DisplayLabel);
            action = label;
        }

        action.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(action, 3);
        grid.Children.Add(action);
        Button info = Ui.IconButton(Icons.Info, $"Explain {item.Title}: what happens if I clean this?", () => vm.Inspect(item));
        info.HorizontalAlignment = HorizontalAlignment.Center;
        info.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(info, 4);
        grid.Children.Add(info);

        var row = new ListRow(grid, () => vm.Inspect(item), tabStop: false);
        subs.On(vm, nameof(CleanUpViewModel.Inspected), () => row.Selected = vm.Inspected?.Id == item.Id);
        var menu = new MenuFlyout();
        var reveal = new MenuFlyoutItem { Text = "Show in File Explorer", IsEnabled = item.Finding.Paths.Count > 0 };
        reveal.Click += (_, _) => Shell.Reveal(item.Finding.Paths[0]);
        var ignore = new MenuFlyoutItem { Text = "Always ignore" };
        ignore.Click += (_, _) => vm.Ignore(item);
        menu.Items.Add(reveal);
        menu.Items.Add(ignore);
        row.ContextFlyout = menu;
        AutomationProperties.SetName(row.Root, $"{item.Title}, {item.SizeText}");
        return row.Root;
    }

    private static UIElement SelectionBar(CleanUpViewModel vm)
    {
        TextBlock text = Ui.Secondary(vm.SelectionText);
        Button clear = Ui.Button("Clear selection", vm.ClearSelection);
        Button review = Ui.Button("Review", vm.Review, accent: true);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CleanUpViewModel.SelectionText) or nameof(CleanUpViewModel.SelectedCount))
            {
                text.Text = vm.SelectionText;
                review.Content = vm.SelectedCount == 0 ? "Review" : $"Review {Words.Items(vm.SelectedCount)}";
                AutomationProperties.SetName(review, (string)review.Content);
                review.IsEnabled = vm.SelectedCount > 0;
                Ui.Show(clear, vm.SelectedCount > 0);
            }
        };
        review.IsEnabled = vm.SelectedCount > 0;
        Ui.Show(clear, vm.SelectedCount > 0);
        var bar = new Border
        {
            Style = Ui.Style("DsCard"),
            CornerRadius = new CornerRadius(0),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(Tokens.PageGutter, Tokens.Space3, Tokens.PageGutter, Tokens.Space3),
            Child = Ui.Row(text, clear, review),
        };
        return bar;
    }
}
