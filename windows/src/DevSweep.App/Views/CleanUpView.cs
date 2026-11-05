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
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        TextBlock subtitle = Ui.Secondary(string.Empty);
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var search = new AutoSuggestBox { PlaceholderText = "Search", Width = 220, QueryIcon = new SymbolIcon(Symbol.Find) };
        AutomationProperties.SetName(search, "Search Clean up");
        search.TextChanged += (_, _) => vm.Search = search.Text;
        var list = new StackPanel { Spacing = 18 };
        var reportHost = new Border();

        var filterRow = new Grid();
        filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filterRow.Children.Add(new ScrollViewer { Content = filters, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Grid.SetColumn(search, 1);
        filterRow.Children.Add(search);

        StackPanel page = Ui.V(16,
            Ui.PageHeader("Clean up", subtitle, ScanButtons.Build(model.Scan, subs)),
            filterRow,
            ScanProgressView.Build(model.Scan, subs, compact: false),
            list,
            reportHost);
        layout.Children.Add(Ui.Scroll(page));

        var inspector = new Border { Style = Ui.Style("DsInspector"), Width = 380 };
        Grid.SetColumn(inspector, 1);
        Grid.SetRowSpan(inspector, 2);
        layout.Children.Add(inspector);

        UIElement bar = SelectionBar(vm);
        Grid.SetRow((FrameworkElement)bar, 1);
        layout.Children.Add(bar);

        subs.On(vm, nameof(CleanUpViewModel.Subtitle), () => subtitle.Text = vm.Subtitle);
        subs.OnCollection(vm.Filters, () => RenderFilters(vm, filters));
        subs.On(vm, nameof(CleanUpViewModel.SelectedKind), () => RenderFilters(vm, filters));
        subs.OnCollection(vm.Groups, () => RenderList(model, vm, list, subs));
        subs.On(model.Scan, nameof(ScanViewModel.Report), () =>
        {
            reportHost.Child = model.Scan.Report is { } report
                ? Ui.V(8, Ui.Text("About this scan", "DsStrong"), Ui.Card(ScanReportView.Build(report, expanded: false)))
                : null;
            RenderList(model, vm, list, subs);
        });
        subs.On(model.Scan, nameof(ScanViewModel.IsScanning), () => RenderList(model, vm, list, subs));
        subs.On(vm, nameof(CleanUpViewModel.Inspected), () =>
        {
            Ui.Show(inspector, vm.Inspected is not null);
            inspector.Child = vm.Inspected is { } item ? InspectorView.Build(model, item, subs, window) : null;
        });
        subs.On(vm, nameof(CleanUpViewModel.SelectedCount), () => Ui.Show(bar, model.Scan.HasScanned && vm.All.Count > 0));
        return layout;
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
            var chip = new ToggleButton
            {
                Content = Ui.H(6, Ui.Text(filter.Title), Ui.Caption(filter.Detail)),
                IsChecked = filter.Kind == vm.SelectedKind,
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12, 4, 12, 4),
            };
            AutomationProperties.SetName(chip, $"Show {filter.Title}, {filter.Detail}");
            chip.Click += (_, _) => vm.SelectedKind = filter.Kind;
            filters.Children.Add(chip);
        }
    }

    private static void RenderList(MainViewModel model, CleanUpViewModel vm, StackPanel list, Subscriptions subs)
    {
        list.Children.Clear();
        if (!model.Scan.HasScanned)
        {
            if (!model.Scan.IsScanning)
            {
                list.Children.Add(Ui.Card(Ui.V(10,
                    Ui.Text("Not scanned yet", "DsSubtitle"),
                    Ui.Secondary("Scan to see what's safe to clean on this PC. Nothing changes until you review and confirm."),
                    Ui.Button("Scan now", () => _ = model.Scan.ScanAsync(), accent: true))));
            }

            return;
        }

        if (vm.Groups.Count == 0)
        {
            list.Children.Add(Ui.Card(Ui.V(6,
                Ui.Text(vm.All.Count == 0 ? "Nothing to clean" : "Nothing matches", "DsSubtitle"),
                Ui.Secondary(vm.All.Count == 0 ? "DevSweep didn't find anything to clean. Every check below ran." : "Try another filter or search."))));
            return;
        }

        foreach (RiskGroup group in vm.Groups)
        {
            var rows = new StackPanel { Spacing = 2 };
            foreach (FindingViewModel item in group.Items)
            {
                rows.Children.Add(Row(vm, item, subs));
            }

            Button? all = group.CanSelectAll
                ? Ui.Button("Select all", () => CleanUpViewModel.ToggleGroup(group), $"Select or deselect all {group.Title} items")
                : null;
            if (all is not null)
            {
                all.Style = Ui.Style("DefaultButtonStyle");
                all.Padding = new Thickness(10, 2, 10, 2);
            }

            list.Children.Add(Ui.V(6,
                Ui.Row(Ui.H(10, Ui.Badge(group.Title, group.Tone, group.Summary), Ui.Secondary(group.Detail)), all),
                Ui.Card(rows, "DsCard")));
        }
    }

    private static UIElement Row(CleanUpViewModel vm, FindingViewModel item, Subscriptions subs)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

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
            FontIcon lockIcon = Ui.Icon("", 14);
            Ui.Tip(lockIcon, "You run the commands for this yourself; open ⓘ to see them.");
            lockIcon.VerticalAlignment = VerticalAlignment.Center;
            select = lockIcon;
        }

        grid.Children.Add(select);
        StackPanel text = Ui.V(0, Ui.Text(item.Title, "DsStrong", maxLines: 1), Ui.Caption(item.Subtitle));
        ((TextBlock)text.Children[1]).MaxLines = 1;
        ((TextBlock)text.Children[1]).TextTrimming = TextTrimming.CharacterEllipsis;
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
            action = Ui.Secondary(item.ChosenAction.DisplayLabel);
        }

        action.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(action, 3);
        grid.Children.Add(action);
        Button info = Ui.IconButton("", $"Explain {item.Title}: what happens if I clean this?", () => vm.Inspect(item));
        Grid.SetColumn(info, 4);
        grid.Children.Add(info);

        var row = new Border { Child = grid };
        subs.On(vm, nameof(CleanUpViewModel.Inspected), () => row.Style = Ui.Style(vm.Inspected?.Id == item.Id ? "DsSelectedRow" : "DsRow"));
        row.Tapped += (_, e) =>
        {
            if (e.OriginalSource is FrameworkElement source && IsInside<ButtonBase>(source) || e.OriginalSource is FrameworkElement s2 && IsInside<ComboBox>(s2))
            {
                return;
            }

            vm.Inspect(item);
        };
        var menu = new MenuFlyout();
        var reveal = new MenuFlyoutItem { Text = "Show in File Explorer", IsEnabled = item.Finding.Paths.Count > 0 };
        reveal.Click += (_, _) => Shell.Reveal(item.Finding.Paths[0]);
        var ignore = new MenuFlyoutItem { Text = "Always ignore" };
        ignore.Click += (_, _) => vm.Ignore(item);
        menu.Items.Add(reveal);
        menu.Items.Add(ignore);
        row.ContextFlyout = menu;
        return row;
    }

    private static bool IsInside<T>(DependencyObject element)
        where T : DependencyObject
    {
        for (DependencyObject? current = element; current is not null; current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current))
        {
            if (current is T)
            {
                return true;
            }
        }

        return false;
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
            Padding = new Thickness(28, 12, 28, 12),
            Child = Ui.Row(text, clear, review),
        };
        return bar;
    }
}
