using DevSweep.App.Design;
using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DevSweep.App.Views;

/// <summary>Home: the free space and what can be cleaned, a bar per volume, what grew or appeared, and what needs attention.</summary>
internal static class HomeView
{
    public static UIElement Build(MainViewModel model, Subscriptions subs, MainWindow window)
    {
        HomeViewModel home = model.Home;
        ScanViewModel scanModel = model.Scan;
        TextBlock subtitle = Ui.Secondary(string.Empty);
        var volumes = new StackPanel { Spacing = Tokens.Space4 };
        TextBlock free = Ui.Text(string.Empty, "DsDisplay");
        TextBlock freeLabel = Ui.Secondary("free");
        TextBlock cleanable = Ui.Text(string.Empty, "DsDisplayAccent");
        TextBlock cleanableLabel = Ui.Secondary(string.Empty);
        Button review = Ui.Button(string.Empty, () => model.Go("cleanup"), accent: true);
        Button scan = Ui.Button("Scan now", () => _ = scanModel.ScanAsync(), accent: true);
        UIElement progress = ScanProgressView.Build(scanModel, subs, compact: true);
        var changes = new StackPanel();
        TextBlock changesTitle = Ds.SectionTitle(string.Empty);
        var attention = new StackPanel();

        // The headline: free space, then what can be cleaned, then the one button that does something about it.
        var numbers = new Grid { ColumnSpacing = Tokens.Space6 };
        numbers.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        numbers.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        numbers.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        StackPanel freeBlock = Ui.H(Tokens.Space2, free, BaselineLabel(freeLabel));
        StackPanel cleanBlock = Ui.H(Tokens.Space2, cleanable, BaselineLabel(cleanableLabel));
        Grid.SetColumn(cleanBlock, 1);
        StackPanel buttons = Ui.H(Tokens.Space2, review, scan);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(buttons, 2);
        numbers.Children.Add(freeBlock);
        numbers.Children.Add(cleanBlock);
        numbers.Children.Add(buttons);
        numbers.SizeChanged += (_, e) =>
        {
            // On a narrow page the two numbers stack and the button drops under them.
            bool narrow = e.NewSize.Width < 700;
            free.FontSize = narrow ? 32 : 44;
            cleanable.FontSize = narrow ? 32 : 44;
        };

        UIElement lower = Ds.TwoColumns(
            Ui.V(Tokens.Space2, changesTitle, changes),
            Ui.V(Tokens.Space2, Ds.SectionTitle("Needs attention"), attention));

        StackPanel page = Ui.V(Tokens.Space6,
            Ds.PageHeader("Home", subtitle, ScanButtons.Build(scanModel, subs)),
            Ds.Card(Ui.V(Tokens.Space4, numbers, progress, volumes), Tokens.Space6),
            lower);

        void Headline()
        {
            VolumeRow? system = home.Volumes.FirstOrDefault(v => v.Volume.IsSystem) ?? home.Volumes.FirstOrDefault();
            free.Text = system?.FreeText ?? "-";
            Ui.Show(freeBlock, system is not null);
            if (scanModel.HasScanned && scanModel.Findings.Count > 0)
            {
                cleanable.Text = SizeLabels.Size(scanModel.TotalBytes);
                cleanableLabel.Text = "can be cleaned";
                Ui.Show(cleanable, true);
            }
            else
            {
                cleanable.Text = string.Empty;
                cleanableLabel.Text = home.CleanableText;
                Ui.Show(cleanable, false);
            }

            AutomationProperties.SetName(cleanBlock, home.CleanableText);
        }

        subs.On(home, nameof(HomeViewModel.Subtitle), () => subtitle.Text = home.Subtitle);
        subs.On(home, nameof(HomeViewModel.CleanableText), Headline);
        subs.On(home, nameof(HomeViewModel.ReviewLabel), () =>
        {
            review.Content = home.ReviewLabel;
            AutomationProperties.SetName(review, home.ReviewLabel);
            Ui.Show(review, home.ReviewLabel.Length > 0);
        });
        subs.On(scanModel, nameof(ScanViewModel.IsScanning), () => Ui.Show(scan, !scanModel.HasScanned && !scanModel.IsScanning));
        subs.OnCollection(home.Volumes, () =>
        {
            volumes.Children.Clear();
            foreach (VolumeRow volume in home.Volumes)
            {
                volumes.Children.Add(Volume(volume));
            }

            Headline();
        });
        subs.On(home, nameof(HomeViewModel.ChangesTitle), () => changesTitle.Text = home.ChangesTitle);
        subs.OnCollection(home.Changes, () =>
        {
            changes.Children.Clear();
            if (home.Changes.Count > 0)
            {
                changes.Children.Add(Ds.RowsCard(home.Changes.Select(Change)));
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

            attention.Children.Add(Ds.RowsCard(home.Attention.Select(row => Attention(model, row))));
        });
        home.Refresh();
        return Ds.PageScroll(page);
    }

    private static UIElement BaselineLabel(TextBlock label)
    {
        label.VerticalAlignment = VerticalAlignment.Bottom;
        label.Margin = new Thickness(0, 0, 0, Tokens.Space2);
        return label;
    }

    private static UIElement Change(ChangeRow row)
    {
        var grid = new Grid { ColumnSpacing = Tokens.Space3 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        UIElement tag = Ds.StatusTag(row.Tag, row.Tone);
        ((FrameworkElement)tag).HorizontalAlignment = HorizontalAlignment.Left;
        TextBlock title = Ui.Text(row.Title, maxLines: 1);
        TextBlock value = Ui.Secondary(row.Value);
        value.TextWrapping = TextWrapping.NoWrap;
        Grid.SetColumn(title, 1);
        Grid.SetColumn(value, 2);
        foreach (FrameworkElement element in new FrameworkElement[] { (FrameworkElement)tag, title, value })
        {
            element.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(element);
        }

        return new ListRow(grid, automationName: $"{row.Tag}: {row.Title}, {row.Value}").Root;
    }

    private static UIElement Attention(MainViewModel model, AttentionRow row)
    {
        var grid = new Grid { ColumnSpacing = Tokens.Space3 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        FrameworkElement tag = Ds.StatusTag(row.Tag, row.Tone);
        tag.HorizontalAlignment = HorizontalAlignment.Left;
        tag.VerticalAlignment = VerticalAlignment.Top;
        TextBlock text = Ui.Text(row.Text);
        Grid.SetColumn(text, 1);
        grid.Children.Add(tag);
        grid.Children.Add(text);
        return new ListRow(grid, () => model.Go(row.Place), $"{row.Tag}: {row.Text}").Root;
    }

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
        AutomationProperties.SetName(track, $"{volume.Name}: {volume.Detail}");
        string detail = volume.Name + " · " + volume.Detail + (volume.Cleanable > 0 ? " · the blue part can be cleaned" : string.Empty);
        return Ui.V(Tokens.Space2, track, Ui.Caption(detail));
    }
}
