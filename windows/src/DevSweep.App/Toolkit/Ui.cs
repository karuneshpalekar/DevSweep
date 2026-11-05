using System.Collections.Specialized;
using System.ComponentModel;
using DevSweep.App.ViewModels.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DevSweep.App.Toolkit;

/// <summary>
/// Change subscriptions owned by one built view; disposed when the view is replaced, so
/// view models never keep dead views alive.
/// </summary>
internal sealed partial class Subscriptions : IDisposable
{
    private readonly List<Action> _undo = [];

    /// <summary>Runs <paramref name="update"/> now and whenever <paramref name="property"/> (or any, when null) changes.</summary>
    public void On(INotifyPropertyChanged source, string? property, Action update)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(update);
        void Handler(object? sender, PropertyChangedEventArgs e)
        {
            if (property is null || e.PropertyName == property)
            {
                update();
            }
        }

        source.PropertyChanged += Handler;
        _undo.Add(() => source.PropertyChanged -= Handler);
        update();
    }

    /// <summary>Runs <paramref name="update"/> now and whenever the collection changes.</summary>
    public void OnCollection(INotifyCollectionChanged source, Action update)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(update);
        void Handler(object? sender, NotifyCollectionChangedEventArgs e) => update();
        source.CollectionChanged += Handler;
        _undo.Add(() => source.CollectionChanged -= Handler);
        update();
    }

    public void Add(Action undo) => _undo.Add(undo);

    public void Dispose()
    {
        foreach (Action undo in _undo)
        {
            undo();
        }

        _undo.Clear();
    }
}

/// <summary>Small builders for the C#-built views. Every interactive control gets an automation name.</summary>
internal static class Ui
{
    public static Style Style(string key) => Theme.Style(key);

    public static TextBlock Text(string text, string style = "DsBody", int maxLines = 0)
    {
        var block = new TextBlock { Text = text, Style = Style(style) };
        if (maxLines > 0)
        {
            block.MaxLines = maxLines;
            block.TextTrimming = TextTrimming.CharacterEllipsis;
            block.TextWrapping = maxLines == 1 ? TextWrapping.NoWrap : TextWrapping.Wrap;
        }

        return block;
    }

    public static TextBlock Heading(string text) => Text(text, "DsSectionHeading");

    public static TextBlock Caption(string text) => Text(text, "DsCaption");

    public static TextBlock Secondary(string text) => Text(text, "DsSecondary");

    public static StackPanel V(double spacing, params UIElement?[] children)
    {
        var panel = new StackPanel { Spacing = spacing };
        foreach (UIElement? child in children)
        {
            if (child is not null)
            {
                panel.Children.Add(child);
            }
        }

        return panel;
    }

    public static StackPanel H(double spacing, params UIElement?[] children)
    {
        StackPanel panel = V(spacing, children);
        panel.Orientation = Orientation.Horizontal;
        return panel;
    }

    /// <summary>A row: <paramref name="left"/> stretches, <paramref name="right"/> items sit at the end.</summary>
    public static Grid Row(UIElement left, params UIElement?[] right)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(left);
        int column = 1;
        foreach (UIElement? item in right)
        {
            if (item is null)
            {
                continue;
            }

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn((FrameworkElement)item, column++);
            if (item is FrameworkElement element)
            {
                element.VerticalAlignment = VerticalAlignment.Center;
            }

            grid.Children.Add(item);
        }

        return grid;
    }

    public static Border Card(UIElement child, string style = "DsCard") => new() { Style = Style(style), Child = child };

    public static Border Divider() => new() { Style = Style("DsDivider") };

    public static Border Badge(string text, Tone tone, string? tooltip = null)
    {
        string name = tone.ToString();
        var badge = new Border
        {
            Style = Style("DsBadge" + name),
            Child = new TextBlock { Text = text, Style = Style("DsBadgeText" + name) },
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (tooltip is not null)
        {
            Tip(badge, tooltip);
            AutomationProperties.SetName(badge, $"{text}: {tooltip}");
        }
        else
        {
            AutomationProperties.SetName(badge, text);
        }

        return badge;
    }

    public static Button Button(string text, Action click, string? automationName = null, bool accent = false)
    {
        Button button = accent ? Theme.AccentButton() : new Button();
        button.Content = text;

        AutomationProperties.SetName(button, automationName ?? text);
        button.Click += (_, _) => click();
        return button;
    }

    public static Button IconButton(string glyph, string automationName, Action click)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 14 },
            Padding = new Thickness(6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        AutomationProperties.SetName(button, automationName);
        Tip(button, automationName);
        button.Click += (_, _) => click();
        return button;
    }

    public static HyperlinkButton Link(string text, Action click, string? automationName = null)
    {
        var link = new HyperlinkButton { Content = text, Padding = new Thickness(0) };
        AutomationProperties.SetName(link, automationName ?? text);
        link.Click += (_, _) => click();
        return link;
    }

    public static FontIcon Icon(string glyph, double size = 16) => new() { Glyph = glyph, FontSize = size };

    public static void Tip(DependencyObject element, string text) => ToolTipService.SetToolTip(element, text);

    public static ScrollViewer Scroll(UIElement content, double padding = 28)
        => new()
        {
            Content = new Border { Padding = new Thickness(padding), Child = content },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

    /// <summary>A size with its confidence badges; the tooltip says how it was measured.</summary>
    public static StackPanel Size(string size, IReadOnlyList<SizeBadge> badges, string tooltip, string style = "DsBody")
    {
        TextBlock text = Text(size, style);
        text.TextWrapping = TextWrapping.NoWrap;
        StackPanel panel = H(6, text);
        foreach (SizeBadge badge in badges)
        {
            panel.Children.Add(Badge(badge.Text, badge.Tone, badge.Tooltip));
        }

        Tip(panel, tooltip);
        AutomationProperties.SetName(panel, $"{size}. {tooltip}");
        return panel;
    }

    /// <summary>Title, subtitle and actions at the top of a page.</summary>
    public static Grid PageHeader(string title, TextBlock subtitle, params UIElement?[] actions)
        => Row(V(2, Text(title, "DsTitle"), subtitle), actions);

    public static void Show(UIElement element, bool visible) => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
}
