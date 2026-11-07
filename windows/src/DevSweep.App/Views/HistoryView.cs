using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevSweep.App.Views;

/// <summary>History: everything DevSweep changed, with Restore, and the quarantine's size and Empty.</summary>
internal static class HistoryView
{
    public static UIElement Build(MainViewModel model, Subscriptions subs, MainWindow window)
    {
        HistoryViewModel vm = model.History;
        TextBlock subtitle = Ui.Secondary(string.Empty);
        var days = new StackPanel { Spacing = 18 };
        TextBlock quarantineText = Ui.Secondary(string.Empty);
        Button empty = Ui.Button("Empty quarantine…", async () =>
        {
            if (await window.ConfirmAsync("Empty DevSweep's quarantine?", "Everything in it is deleted for good and can't be restored.", "Empty"))
            {
                await vm.EmptyQuarantineAsync();
            }
        });
        TextBlock message = Ui.Secondary(string.Empty);

        StackPanel page = Ui.V(18,
            Ui.PageHeader("History", subtitle, Ui.Button("Refresh", vm.Refresh)),
            Ui.Card(Ui.Row(quarantineText, empty)),
            message,
            days,
            Ui.Caption("Items stay restorable until you empty the Recycle Bin. DevSweep never empties it for you. Items in DevSweep's quarantine take space until you empty it."));

        subs.On(vm, nameof(HistoryViewModel.Subtitle), () => subtitle.Text = vm.Subtitle);
        subs.On(vm, nameof(HistoryViewModel.QuarantineText), () => quarantineText.Text = vm.QuarantineText);
        subs.On(vm, nameof(HistoryViewModel.HasQuarantine), () => Ui.Show(empty, vm.HasQuarantine));
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
                days.Children.Add(Ui.Card(Ui.V(6, Ui.Text("Nothing cleaned yet", "DsSubtitle"), Ui.Secondary("Everything DevSweep changes shows up here, with a way to undo it."))));
            }

            foreach (HistoryDay day in vm.Days)
            {
                var rows = new StackPanel { Spacing = 8 };
                foreach (HistoryRow row in day.Rows)
                {
                    if (rows.Children.Count > 0)
                    {
                        rows.Children.Add(Ui.Divider());
                    }

                    rows.Children.Add(Row(vm, row));
                }

                days.Children.Add(Ui.V(6, Ui.Heading(day.Title), Ui.Card(rows)));
            }
        });
        vm.Refresh();
        return Ui.Scroll(page);
    }

    private static UIElement Row(HistoryViewModel vm, HistoryRow row)
    {
        TextBlock time = Ui.Caption(row.Time);
        time.Width = 48;
        UIElement status = row.Status.Length > 0
            ? Ui.Badge(row.Status, row.Status == "Restored" ? ViewModels.Text.Tone.Good : ViewModels.Text.Tone.Neutral)
            : Ui.Button("Restore", () => _ = vm.RestoreAsync(row), $"Restore {row.Title}");
        StackPanel text = Ui.V(2, Ui.Text(row.Title, "DsStrong"), Ui.Caption(row.Detail));
        var items = new StackPanel { Spacing = 2, Margin = new Thickness(60, 4, 0, 0) };
        foreach (HistoryItemRow item in row.Items)
        {
            items.Children.Add(Ui.Row(Ui.Caption(item.Path), Ui.Caption(item.Where)));
        }

        Grid top = Ui.Row(Ui.H(12, time, text), Ui.Secondary(row.SizeText), status);
        return row.Items.Count > 0 ? Ui.V(2, top, items) : top;
    }
}
