using DevSweep.App.Toolkit;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DevSweep.App.Design;

/// <summary>
/// The pinned footer of a side panel: the primary actions and the verdict stay on screen while the details above scroll
/// (P1 showed Push, Publish and Remove below the fold at about 1030 px). Collapses when it has nothing to show.
/// </summary>
internal sealed class PanelFooter
{
    /// <summary>Marks the footer's scroll viewer so the screenshot tour can tell it from the panel body's.</summary>
    public const string FooterTag = "panel-footer";

    private readonly Border _frame;
    private readonly ScrollViewer _scroll;

    public PanelFooter(string automationName = "Actions")
    {
        _scroll = new ScrollViewer
        {
            MaxHeight = 280,
            Tag = FooterTag,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _frame = new Border
        {
            Style = Ui.Style("DsFooterBar"),
            Padding = new Thickness(Tokens.Space6, Tokens.Space3, Tokens.Space6, Tokens.Space3),
            Child = _scroll,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetName(_frame, automationName);
    }

    public FrameworkElement Root => _frame;

    /// <summary>True while the footer has content.</summary>
    public bool HasContent => _scroll.Content is not null;

    public void Set(UIElement? content)
    {
        _scroll.Content = content;
        _frame.Visibility = content is null ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// <summary>A panel body that scrolls above a footer that doesn't.</summary>
internal static class PanelFrame
{
    public static Grid Build(UIElement body, PanelFooter footer)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(footer);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(body);
        Grid.SetRow(footer.Root, 1);
        grid.Children.Add(footer.Root);
        return grid;
    }
}
