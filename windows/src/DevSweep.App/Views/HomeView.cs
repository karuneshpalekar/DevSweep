using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevSweep.App.Views;

/// <summary>Home: free space per volume, what can be cleaned, what grew or appeared, and what needs attention.</summary>
internal static class HomeView
{
    public static UIElement Build(MainViewModel model, Subscriptions subs, MainWindow window)
    {
        HomeViewModel home = model.Home;
        TextBlock subtitle = Ui.Secondary(string.Empty);
        var volumes = new StackPanel { Spacing = 14 };
        TextBlock cleanable = Ui.Text(string.Empty, "DsBigAccent");
        Button review = Ui.Button(string.Empty, () => model.Go("cleanup"), accent: true);
        Button scan = Ui.Button("Scan now", () => _ = model.Scan.ScanAsync(), accent: true);
        UIElement progress = ScanProgressView.Build(model.Scan, subs, compact: true);
        var changes = new StackPanel();
        TextBlock changesTitle = Ui.Text(string.Empty, "DsStrong");
        var attention = new StackPanel();

        var top = new Grid { ColumnSpacing = 24 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(Ui.V(6, cleanable, progress));
        StackPanel buttons = Ui.H(8, review, scan);
        buttons.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(buttons, 1);
        top.Children.Add(buttons);

        var lower = new Grid { ColumnSpacing = 28 };
        lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400) });
        lower.Children.Add(Ui.V(8, changesTitle, changes));
        StackPanel attentionColumn = Ui.V(8, Ui.Text("Needs attention", "DsStrong"), attention);
        Grid.SetColumn(attentionColumn, 1);
        lower.Children.Add(attentionColumn);

        StackPanel page = Ui.V(24,
            Ui.PageHeader("Home", subtitle, ScanButtons.Build(model.Scan, subs)),
            Ui.Card(Ui.V(18, top, volumes)),
            lower);

        subs.On(home, nameof(HomeViewModel.Subtitle), () => subtitle.Text = home.Subtitle);
        subs.On(home, nameof(HomeViewModel.CleanableText), () => cleanable.Text = home.CleanableText);
        subs.On(home, nameof(HomeViewModel.ReviewLabel), () =>
        {
            review.Content = home.ReviewLabel;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(review, home.ReviewLabel);
            Ui.Show(review, home.ReviewLabel.Length > 0);
        });
        subs.On(model.Scan, nameof(ScanViewModel.IsScanning), () => Ui.Show(scan, !model.Scan.HasScanned && !model.Scan.IsScanning));
        subs.OnCollection(home.Volumes, () =>
        {
            volumes.Children.Clear();
            foreach (VolumeRow volume in home.Volumes)
            {
                volumes.Children.Add(Volume(volume));
            }
        });
        subs.On(home, nameof(HomeViewModel.ChangesTitle), () => changesTitle.Text = home.ChangesTitle);
        subs.OnCollection(home.Changes, () =>
        {
            changes.Children.Clear();
            foreach (ChangeRow row in home.Changes)
            {
                changes.Children.Add(Ui.V(0, Ui.Row(Ui.H(10, Ui.Badge(row.Tag, row.Tone), Ui.Text(row.Title, maxLines: 1)), Ui.Secondary(row.Value)), Spacer(8), Ui.Divider()));
            }

            if (home.ChangesEmpty is { } empty)
            {
                changes.Children.Add(Ui.Secondary(empty));
            }
        });
        subs.OnCollection(home.Attention, () =>
        {
            attention.Children.Clear();
            if (home.Attention.Count == 0)
            {
                attention.Children.Add(Ui.Secondary(home.AttentionEmpty ?? string.Empty));
                return;
            }

            var list = new StackPanel { Spacing = 0 };
            foreach (AttentionRow row in home.Attention)
            {
                var button = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = null,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(4, 8, 4, 8),
                    Content = Ui.H(10, Ui.Badge(row.Tag, row.Tone), Ui.Text(row.Text)),
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{row.Tag}: {row.Text}");
                button.Click += (_, _) => model.Go(row.Place);
                list.Children.Add(button);
            }

            attention.Children.Add(Ui.Card(list));
        });
        home.Refresh();
        return Ui.Scroll(page);
    }

    private static Border Spacer(double height) => new() { Height = height };

    private static UIElement Volume(VolumeRow volume)
    {
        var track = new Grid { Height = 12 };
        track.Children.Add(new Border { Style = Ui.Style("DsBarTrack") });
        var bars = new Grid();
        bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, volume.UsedFraction - volume.CleanableFraction), GridUnitType.Star) });
        bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(volume.CleanableFraction, GridUnitType.Star) });
        bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, 1 - volume.UsedFraction), GridUnitType.Star) });
        var used = new Border { Style = Ui.Style("DsBarUsed"), CornerRadius = new CornerRadius(6, 0, 0, 6) };
        var clean = new Border { Style = Ui.Style("DsBarCleanable") };
        Grid.SetColumn(clean, 1);
        bars.Children.Add(used);
        bars.Children.Add(clean);
        track.Children.Add(bars);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(track, $"{volume.Name}: {volume.Detail}");
        TextBlock free = Ui.Text(volume.FreeText, volume.Volume.IsSystem ? "DsBig" : "DsSubtitle");
        return Ui.V(6,
            Ui.Row(Ui.H(8, free, Ui.Secondary("free")), Ui.Caption(volume.Name)),
            track,
            Ui.Caption(volume.Detail + (volume.Cleanable > 0 ? " · the blue part can be cleaned" : string.Empty)));
    }
}
