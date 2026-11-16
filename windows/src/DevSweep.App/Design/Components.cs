using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace DevSweep.App.Design;

/// <summary>How serious a <see cref="Ds.Banner"/> is.</summary>
internal enum BannerKind
{
    Info,
    Warning,
    Critical,
}

/// <summary>The building blocks every screen is made of (see windows/docs/p0-design-system.md). Built in code, themed by ThemeResource styles.</summary>
internal static class Ds
{
    /// <summary>The page's title, a one-line status under it, and the page's main actions at the end.</summary>
    public static Grid PageHeader(string title, TextBlock subtitle, params UIElement?[] actions)
    {
        TextBlock heading = Ui.Text(title, "DsTitle", 1);
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        Grid row = Ui.Row(Ui.V(Tokens.Space1, heading, subtitle), actions);
        row.ColumnSpacing = Tokens.Space3;
        return row;
    }

    /// <summary>A rounded surface with a hairline border.</summary>
    public static Border Card(UIElement child, double padding = Tokens.Space4)
    {
        Border card = Ui.Card(child);
        card.Padding = Tokens.Pad(padding);
        return card;
    }

    /// <summary>A small coloured label ("Rebuilds itself", "Grew", "Cleaned"), optionally with a tooltip that says what it means.</summary>
    public static Border StatusTag(string text, Tone tone, string? tooltip = null) => Ui.Badge(text, tone, tooltip);

    /// <summary>A message across the width of a page or panel (the system InfoBar: wraps, themed, announced to screen readers).</summary>
    public static InfoBar Banner(BannerKind kind, string text, string? title = null, string? actionLabel = null, Action? action = null)
    {
        var bar = new InfoBar
        {
            Severity = kind switch
            {
                BannerKind.Warning => InfoBarSeverity.Warning,
                BannerKind.Critical => InfoBarSeverity.Error,
                _ => InfoBarSeverity.Informational,
            },
            Title = title ?? string.Empty,
            Message = text,
            IsOpen = true,
            IsClosable = false,
        };
        AutomationProperties.SetName(bar, title is null ? text : title + ". " + text);
        if (actionLabel is not null && action is not null)
        {
            var button = new Button { Content = actionLabel };
            AutomationProperties.SetName(button, actionLabel);
            button.Click += (_, _) => action();
            bar.ActionButton = button;
        }

        return bar;
    }

    /// <summary>The "nothing here yet" state: an icon, a title, a sentence and optional buttons.</summary>
    public static UIElement EmptyState(string glyph, string title, string text, params UIElement?[] actions)
    {
        FontIcon icon = Ui.Icon(glyph, 32);
        icon.Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        TextBlock heading = Ui.Text(title, "DsSubtitle");
        heading.TextAlignment = TextAlignment.Center;
        TextBlock body = Ui.Secondary(text);
        body.TextAlignment = TextAlignment.Center;
        body.MaxWidth = 420;
        StackPanel panel = Ui.V(Tokens.Space2, icon, heading, body);
        panel.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Padding = Tokens.Pad(Tokens.Space6);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        if (actions.Any(a => a is not null))
        {
            panel.Children.Add(new Border { Margin = new Thickness(0, Tokens.Space2, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, Child = FlowButtons.Build(actions) });
        }

        return panel;
    }

    /// <summary>
    /// A loading placeholder shaped like <paramref name="rows"/> list rows; pulses gently while motion is on, and is still when it is off.
    /// Hidden from screen readers (the real content announces itself).
    /// </summary>
    public static UIElement Skeleton(int rows = 4)
    {
        var panel = new StackPanel { Spacing = Tokens.Space4, Padding = Tokens.Pad(Tokens.Space3) };
        AutomationProperties.SetAccessibilityView(panel, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        for (int i = 0; i < rows; i++)
        {
            var title = new Border { Style = Ui.Style("DsSkeleton"), Width = 160 + ((i * 37) % 80), HorizontalAlignment = HorizontalAlignment.Left };
            var line = new Border { Style = Ui.Style("DsSkeleton"), Height = 8, Width = 260 + ((i * 53) % 120), HorizontalAlignment = HorizontalAlignment.Left };
            panel.Children.Add(Ui.V(Tokens.Space2, title, line));
        }

        bool alive = false;
        panel.Loaded += (_, _) =>
        {
            alive = true;
            Pulse(panel, () => alive, false);
        };
        panel.Unloaded += (_, _) => alive = false;
        return panel;
    }

    private static void Pulse(UIElement element, Func<bool> alive, bool up)
    {
        if (!Motion.Enabled || !alive())
        {
            return;
        }

        Motion.Run(900, Ease.InOut, t => element.Opacity = up ? 0.55 + (0.45 * t) : 1 - (0.45 * t), () => Pulse(element, alive, !up));
    }

    /// <summary>One line of a checklist: a green tick for something that was checked, an amber warning for something to know.</summary>
    public static UIElement CheckRow(bool ok, string text)
    {
        FontIcon icon = Ui.Icon(ok ? Icons.CheckCircle : Icons.Warning, 14);
        icon.Foreground = (Brush)Application.Current.Resources[ok ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush"];
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 3, 0, 0);
        TextBlock label = Ui.Text(text, ok ? "DsBody" : "DsWarningText");
        var row = new Grid { ColumnSpacing = Tokens.Space2 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(label, 1);
        row.Children.Add(icon);
        row.Children.Add(label);
        AutomationProperties.SetName(row, (ok ? "Checked: " : "Warning: ") + text);
        return row;
    }

    /// <summary>The bar above a list: filter chips (or tabs) at the start, a search box and actions at the end. Wraps onto two lines when narrow.</summary>
    public static UIElement CommandBar(UIElement? filters, UIElement? search, params UIElement?[] actions)
    {
        var bar = new Grid { ColumnSpacing = Tokens.Space3, RowSpacing = Tokens.Space2 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        bar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        StackPanel end = Ui.H(Tokens.Space2);
        end.VerticalAlignment = VerticalAlignment.Center;
        if (search is not null)
        {
            end.Children.Add(search);
        }

        foreach (UIElement? action in actions)
        {
            if (action is not null)
            {
                end.Children.Add(action);
            }
        }

        if (filters is not null)
        {
            filters.SetValue(Grid.ColumnProperty, 0);
            bar.Children.Add(filters);
        }

        Grid.SetColumn(end, 1);
        bar.Children.Add(end);
        bar.SizeChanged += (_, e) =>
        {
            bool narrow = e.NewSize.Width < 820 && filters is not null;
            Grid.SetRow(end, narrow ? 1 : 0);
            Grid.SetColumn(end, narrow ? 0 : 1);
            Grid.SetColumnSpan(end, narrow ? 2 : 1);
        };
        return bar;
    }

    /// <summary>A filter chip row (a toggle per option); only one is on. Used for Clean up's kinds.</summary>
    public static ToggleButton Chip(string label, string? detail, bool selected, string automationName, Action click)
    {
        UIElement content = detail is { Length: > 0 } ? Ui.H(Tokens.Space2, Ui.Text(label), Ui.Caption(detail)) : Ui.Text(label);
        var chip = new ToggleButton
        {
            Content = content,
            IsChecked = selected,
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(Tokens.Space3, Tokens.Space1, Tokens.Space3, Tokens.Space1),
        };
        AutomationProperties.SetName(chip, automationName);
        chip.Click += (_, _) => click();
        return chip;
    }

    /// <summary>A segmented control: a few exclusive options in one rounded track (Mac pickers, Windows 11 look).</summary>
    public static FrameworkElement Segmented(IReadOnlyList<string> options, int selected, Action<int> changed, string automationName)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var buttons = new List<ToggleButton>();
        for (int i = 0; i < options.Count; i++)
        {
            int index = i;
            var button = new ToggleButton
            {
                Content = options[i],
                IsChecked = i == selected,
                CornerRadius = new CornerRadius(Tokens.RadiusSmall + 2),
                Padding = new Thickness(Tokens.Space3, Tokens.Space1, Tokens.Space3, Tokens.Space1),
                MinWidth = 64,
            };
            AutomationProperties.SetName(button, options[i]);
            button.Click += (_, _) =>
            {
                for (int k = 0; k < buttons.Count; k++)
                {
                    buttons[k].IsChecked = k == index;
                }

                changed(index);
            };
            buttons.Add(button);
            strip.Children.Add(button);
        }

        var track = new Border { Style = Ui.Style("DsSegmentTrack"), Child = strip, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(track, automationName);
        return track;
    }

    /// <summary>Tabs: the system SelectorBar with one item per label.</summary>
    public static SelectorBar Tabs(IReadOnlyList<string> labels, int selected, Action<int> changed, string automationName)
    {
        var bar = new SelectorBar();
        for (int i = 0; i < labels.Count; i++)
        {
            var item = new SelectorBarItem { Text = labels[i], IsSelected = i == selected };
            AutomationProperties.SetName(item, labels[i]);
            bar.Items.Add(item);
        }

        AutomationProperties.SetName(bar, automationName);
        bar.SelectionChanged += (s, _) => changed(s.Items.IndexOf(s.SelectedItem));
        return bar;
    }

    /// <summary>A section heading above a group of rows (the Mac's small bold grey title).</summary>
    public static TextBlock SectionTitle(string text)
    {
        TextBlock block = Ui.Text(text, "DsStrong");
        AutomationProperties.SetHeadingLevel(block, AutomationHeadingLevel.Level2);
        return block;
    }

    /// <summary>A thin accent-free divider line between rows of a card.</summary>
    public static Border Divider() => Ui.Divider();

    /// <summary>A scrolling page: the 32 px gutter at the sides, 24 above and below.</summary>
    public static ScrollViewer PageScroll(UIElement content)
        => new()
        {
            Content = new Border { Padding = new Thickness(Tokens.PageGutter, Tokens.Space6, Tokens.PageGutter, Tokens.Space6), Child = content },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

    /// <summary>A card of rows: the rows sit close together with hairlines between them.</summary>
    public static Border RowsCard(IEnumerable<UIElement> rows)
    {
        var stack = new StackPanel { Spacing = 0 };
        foreach (UIElement row in rows)
        {
            if (stack.Children.Count > 0)
            {
                stack.Children.Add(new Border { Height = 1, Margin = new Thickness(Tokens.Space3, 0, Tokens.Space3, 0), Style = Ui.Style("DsDivider") });
            }

            stack.Children.Add(row);
        }

        Border card = Ui.Card(stack);
        card.Padding = Tokens.Pad(Tokens.Space1);
        return card;
    }

    /// <summary>Two columns side by side, stacked when the page is narrower than <paramref name="breakpoint"/>.</summary>
    public static Grid TwoColumns(UIElement left, UIElement right, double breakpoint = 860, double rightWidth = 400)
    {
        var grid = new Grid { ColumnSpacing = Tokens.Space6, RowSpacing = Tokens.Space6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(rightWidth) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(left);
        Grid.SetColumn((FrameworkElement)right, 1);
        grid.Children.Add(right);
        grid.SizeChanged += (_, e) =>
        {
            bool stacked = e.NewSize.Width < breakpoint;
            grid.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(rightWidth);
            Grid.SetColumn((FrameworkElement)right, stacked ? 0 : 1);
            Grid.SetRow((FrameworkElement)right, stacked ? 1 : 0);
        };
        return grid;
    }
}

/// <summary>A row of buttons that wraps onto further lines when it does not fit.</summary>
internal static class FlowButtons
{
    public static UIElement Build(params UIElement?[] items) => Build(Tokens.Space2, items);

    public static UIElement Build(double spacing, params UIElement?[] items)
    {
        UIElement[] children = [.. items.OfType<UIElement>()];
        var host = new StackPanel { Spacing = spacing };
        double lastWidth = -1;
        void Flow(double width)
        {
            lastWidth = width;
            foreach (UIElement child in children)
            {
                if (child is FrameworkElement { Parent: StackPanel row })
                {
                    row.Children.Remove(child);
                }
            }

            host.Children.Clear();
            StackPanel? current = null;
            double used = 0;
            foreach (UIElement child in children)
            {
                child.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                double need = child.DesiredSize.Width;
                if (current is null || (used > 0 && used + spacing + need > width))
                {
                    current = Ui.H(spacing);
                    host.Children.Add(current);
                    used = 0;
                }

                current.Children.Add(child);
                used += (used > 0 ? spacing : 0) + need;
            }
        }

        Flow(double.PositiveInfinity);
        host.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - lastWidth) > 1 && e.NewSize.Width > 0)
            {
                Flow(e.NewSize.Width);
            }
        };
        return host;
    }
}

/// <summary>The frame of a dialog sheet: title and subtitle, a scrolling body, and a footer with a message and buttons.</summary>
internal sealed class SheetFrame
{
    public SheetFrame(double width)
    {
        Heading = Ui.Text(string.Empty, "DsSubtitle");
        AutomationProperties.SetHeadingLevel(Heading, AutomationHeadingLevel.Level1);
        Subheading = Ui.Secondary(string.Empty);
        Body = new StackPanel { Spacing = Tokens.Space4 };
        Footer = Ui.Secondary(string.Empty);
        Buttons = Ui.H(Tokens.Space2);
        Root = new Grid { Width = width };
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Border { Padding = new Thickness(Tokens.Space6, Tokens.Space6, Tokens.Space6, Tokens.Space3), Child = Ui.V(Tokens.Space1, Heading, Subheading) };
        Root.Children.Add(header);
        var scroll = new ScrollViewer
        {
            Content = new Border { Padding = new Thickness(Tokens.Space6, Tokens.Space1, Tokens.Space6, Tokens.Space4), Child = Body },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(scroll, 1);
        Root.Children.Add(scroll);
        var bar = new Border { Style = Ui.Style("DsFooterBar"), Child = Ui.Row(Footer, Buttons) };
        Grid.SetRow(bar, 2);
        Root.Children.Add(bar);
    }

    public Grid Root { get; }

    public TextBlock Heading { get; }

    public TextBlock Subheading { get; }

    public StackPanel Body { get; }

    public TextBlock Footer { get; }

    public StackPanel Buttons { get; }
}
