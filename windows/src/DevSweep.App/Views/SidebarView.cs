using DevSweep.App.Design;
using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DevSweep.App.Views;

/// <summary>The parts of the sidebar besides the places themselves: the free-space card, the numbers on the places, the attention cards and the status line.</summary>
internal static class SidebarView
{
    /// <summary>The card at the top of the pane: free space, a bar with the cleanable part, and what can be cleaned.</summary>
    public static UIElement Header(SidebarViewModel side, Subscriptions subs)
    {
        TextBlock free = Ui.Text(string.Empty, "DsSubtitle");
        TextBlock freeLabel = Ui.Caption("free");
        freeLabel.VerticalAlignment = VerticalAlignment.Bottom;
        freeLabel.Margin = new Thickness(0, 0, 0, 3);
        var track = new Grid { Height = 6 };
        track.Children.Add(new Border { Style = Ui.Style("DsBarTrack"), CornerRadius = new CornerRadius(3) });
        var bars = new Grid();
        var used = new ColumnDefinition();
        var clean = new ColumnDefinition();
        var rest = new ColumnDefinition();
        bars.ColumnDefinitions.Add(used);
        bars.ColumnDefinitions.Add(clean);
        bars.ColumnDefinitions.Add(rest);
        var usedBar = new Border { Style = Ui.Style("DsBarUsed"), CornerRadius = new CornerRadius(3, 0, 0, 3) };
        var cleanBar = new Border { Style = Ui.Style("DsBarCleanable") };
        Grid.SetColumn(cleanBar, 1);
        bars.Children.Add(usedBar);
        bars.Children.Add(cleanBar);
        track.Children.Add(bars);
        TextBlock cleanable = Ui.Caption(string.Empty);
        Border card = Ds.Card(Ui.V(Tokens.Space2, Ui.H(Tokens.Space1, free, freeLabel), track, cleanable), Tokens.Space3);
        card.Margin = new Thickness(Tokens.Space3, Tokens.Space1, Tokens.Space3, Tokens.Space2);
        subs.On(side, null, () =>
        {
            Ui.Show(card, side.HasVolume);
            free.Text = side.FreeText;
            double usedFraction = side.Volume?.UsedFraction ?? 0;
            double cleanFraction = side.HasCleanable ? side.Volume?.CleanableFraction ?? 0 : 0;
            used.Width = new GridLength(Math.Max(0.0001, usedFraction - cleanFraction), GridUnitType.Star);
            clean.Width = new GridLength(Math.Max(0, cleanFraction), GridUnitType.Star);
            rest.Width = new GridLength(Math.Max(0.0001, 1 - usedFraction), GridUnitType.Star);
            cleanable.Text = side.CleanableLine;
            AutomationProperties.SetName(card, side.FreeText + " free. " + side.CleanableLine);
        });
        return card;
    }

    /// <summary>A place's row content: its title, and the number or size at the end (Clean up size, Projects needing a push, Health items).</summary>
    public static UIElement ItemContent(string title, string placeId, SidebarViewModel side, Subscriptions subs)
    {
        var grid = new Grid { ColumnSpacing = Tokens.Space2, HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // No Foreground set on purpose: inside a selected NavigationViewItem the text must follow the item's own
        // foreground, or it stays white on the high-contrast selection colour.
        var name = new TextBlock { Text = title, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        var count = new TextBlock { FontSize = 12, Opacity = 0.8, MaxLines = 1 };
        Grid.SetColumn(count, 1);
        count.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(name);
        grid.Children.Add(count);
        subs.On(side, null, () =>
        {
            count.Text = side.CountFor(placeId);
            Ui.Show(count, count.Text.Length > 0);
        });
        return grid;
    }

    /// <summary>The pane footer: the update card, up to two attention cards under "Needs your attention", and the last scan / next scan line.</summary>
    public static StackPanel Footer(MainViewModel model, Subscriptions subs, UIElement updateCard)
    {
        SidebarViewModel side = model.Sidebar;
        TextBlock heading = Ui.Caption("NEEDS YOUR ATTENTION");
        heading.Margin = new Thickness(Tokens.Space3, Tokens.Space1, Tokens.Space3, 0);
        var cards = new StackPanel { Spacing = Tokens.Space1 };
        TextBlock status = Ui.Caption(string.Empty);
        status.TextWrapping = TextWrapping.Wrap;
        status.Margin = new Thickness(Tokens.Space3, Tokens.Space1, Tokens.Space3, Tokens.Space2);
        subs.On(side, null, () =>
        {
            cards.Children.Clear();
            IReadOnlyList<AttentionRow> rows = side.Cards;
            Ui.Show(heading, rows.Count > 0);
            foreach (AttentionRow row in rows)
            {
                cards.Children.Add(Card(model, row));
            }

            status.Text = side.StatusLine;
        });
        return Ui.V(0, updateCard, heading, cards, status);
    }

    private static UIElement Card(MainViewModel model, AttentionRow row)
    {
        var dot = new Border { Style = Ui.Style(row.Tone == Tone.Critical ? "DsBadgeCritical" : "DsBadgeCaution"), Width = 10, Height = 10, Padding = new Thickness(0), BorderThickness = new Thickness(5), CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) };
        TextBlock text = Ui.Caption(row.Text);
        text.TextWrapping = TextWrapping.Wrap;
        text.MaxLines = 2;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        var grid = new Grid { ColumnSpacing = Tokens.Space2 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(text, 1);
        grid.Children.Add(dot);
        grid.Children.Add(text);
        Border card = Ds.Card(new ListRow(grid, () => model.OpenAttention(row), $"{row.Tag}: {row.Text}").Root, 0);
        card.Margin = new Thickness(Tokens.Space3, 0, Tokens.Space3, 0);
        return card;
    }
}
