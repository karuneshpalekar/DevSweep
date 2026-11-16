using DevSweep.App.Design;
using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DevSweep.App.Views;

/// <summary>History: everything DevSweep changed, with Restore, and the quarantine's size and Empty.</summary>
internal static class HistoryView
{
    public static UIElement Build(MainViewModel model, Subscriptions subs, MainWindow window)
    {
        HistoryViewModel vm = model.History;
        TextBlock subtitle = Ui.Secondary(string.Empty);
        var days = new StackPanel { Spacing = Tokens.Space6 };
        var quarantine = new Border();
        TextBlock message = Ui.Secondary(string.Empty);

        StackPanel page = Ui.V(Tokens.Space4,
            Ds.PageHeader("History", subtitle, Ui.Button("Refresh", vm.Refresh)),
            quarantine,
            message,
            days,
            Ui.Caption("Items stay restorable until you empty the Recycle Bin. DevSweep never empties it for you. Items in DevSweep's quarantine take space until you empty it."));

        void RenderQuarantine()
        {
            quarantine.Child = vm.HasQuarantine
                ? Ds.Banner(BannerKind.Info, vm.QuarantineText, null, "Empty quarantine…", async () =>
                {
                    if (await window.ConfirmAsync("Empty DevSweep's quarantine?", "Everything in it is deleted for good and can't be restored.", "Empty"))
                    {
                        await vm.EmptyQuarantineAsync();
                    }
                })
                : Ui.Secondary(vm.QuarantineText);
        }

        subs.On(vm, nameof(HistoryViewModel.Subtitle), () => subtitle.Text = vm.Subtitle);
        subs.On(vm, nameof(HistoryViewModel.QuarantineText), RenderQuarantine);
        subs.On(vm, nameof(HistoryViewModel.HasQuarantine), RenderQuarantine);
        subs.On(vm, nameof(HistoryViewModel.Message), () =>
        {
            message.Text = vm.Message ?? string.Empty;
            Ui.Show(message, vm.Message is not null);
        });
        subs.OnCollection(vm.Days, () =>
        {
            days.Children.Clear();
            if (vm.Days.Count == 0)
            {
                days.Children.Add(Ds.Card(Ds.EmptyState(Icons.History, "Nothing cleaned yet", "Everything DevSweep changes shows up here, with a way to undo it.")));
            }

            foreach (HistoryDay day in vm.Days)
            {
                TextBlock title = Ds.SectionTitle(day.Title);
                title.Margin = new Thickness(Tokens.Space1, 0, 0, 0);
                days.Children.Add(Ui.V(Tokens.Space2, title, Ds.RowsCard(day.Rows.Select(row => Row(vm, row)))));
            }
        });
        vm.Refresh();
        return Ds.PageScroll(page);
    }

    private static UIElement Row(HistoryViewModel vm, HistoryRow row)
    {
        var grid = new Grid { ColumnSpacing = Tokens.Space3, RowSpacing = Tokens.Space1 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        TextBlock time = Ui.Caption(row.Time);
        time.VerticalAlignment = VerticalAlignment.Top;
        time.Margin = new Thickness(0, Tokens.Space1, 0, 0);
        grid.Children.Add(time);

        StackPanel text = Ui.V(2, Ui.Text(row.Title, "DsStrong"), Ui.Caption(row.Detail));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        TextBlock size = Ui.Secondary(row.SizeText);
        size.TextWrapping = TextWrapping.NoWrap;
        size.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(size, 2);
        grid.Children.Add(size);

        FrameworkElement status = row.Status.Length > 0
            ? Ds.StatusTag(row.Status, row.Status == "Restored" ? ViewModels.Text.Tone.Good : ViewModels.Text.Tone.Neutral)
            : Ui.Button("Restore", () => _ = vm.RestoreAsync(row), $"Restore {row.Title}");
        status.HorizontalAlignment = HorizontalAlignment.Right;
        status.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(status, 3);
        grid.Children.Add(status);

        if (row.Items.Count > 0)
        {
            var items = new StackPanel { Spacing = 2 };
            foreach (HistoryItemRow item in row.Items)
            {
                items.Children.Add(Ui.Row(Ui.Caption(item.Path), Ui.Caption(item.Where)));
            }

            Grid.SetRow(items, 1);
            Grid.SetColumn(items, 1);
            Grid.SetColumnSpan(items, 3);
            grid.Children.Add(items);
        }

        AutomationProperties.SetName(grid, $"{row.Title}, {row.SizeText}");
        return new ListRow(grid).Root;
    }
}
