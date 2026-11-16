using DevSweep.App.Toolkit;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DevSweep.App.Design;

/// <summary>
/// A row in a list: rounded surface with hover, pressed, selected (a quiet fill plus a thin accent bar) and keyboard focus visuals.
/// Clicking it (or Enter or Space when it has focus) runs <c>activate</c>, unless the click was on a button, check box or combo box inside it.
/// </summary>
internal sealed class ListRow
{
    private readonly ContentControl _root;
    private readonly Border _surface;
    private readonly Border _bar;
    private readonly Action? _activate;
    private bool _hover;
    private bool _pressed;
    private bool _selected;

    public ListRow(UIElement content, Action? activate = null, string? automationName = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        _activate = activate;
        _surface = new Border { Style = Ui.Style("DsRow"), Child = content };
        _bar = new Border { Style = Ui.Style("DsAccentBar"), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        var layers = new Grid();
        layers.Children.Add(_surface);
        layers.Children.Add(_bar);
        _root = new ContentControl
        {
            Content = layers,
            Padding = new Thickness(0),
            IsTabStop = activate is not null,
            UseSystemFocusVisuals = true,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(Tokens.RadiusMedium - 2),
        };
        if (automationName is not null)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_root, automationName);
        }

        _root.PointerEntered += (_, _) =>
        {
            _hover = true;
            Refresh();
        };
        _root.PointerExited += (_, _) =>
        {
            _hover = false;
            _pressed = false;
            Refresh();
        };
        _root.PointerPressed += (_, _) =>
        {
            _pressed = activate is not null;
            Refresh();
        };
        _root.PointerReleased += (_, _) =>
        {
            _pressed = false;
            Refresh();
        };
        _root.PointerCaptureLost += (_, _) =>
        {
            _pressed = false;
            Refresh();
        };
        _root.Tapped += OnTapped;
        _root.KeyDown += OnKeyDown;
    }

    public FrameworkElement Root => _root;

    /// <summary>The right-click menu.</summary>
    public FlyoutBase? ContextFlyout
    {
        get => _root.ContextFlyout;
        set => _root.ContextFlyout = value;
    }

    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            Refresh();
        }
    }

    /// <summary>For tests: which surface style the row shows right now.</summary>
    public string State => _pressed ? "pressed" : _hover ? "hover" : _selected ? "selected" : "rest";

    private void Refresh()
    {
        _surface.Style = Ui.Style(_pressed ? "DsRowPressed" : _hover ? "DsRowHover" : _selected ? "DsSelectedRow" : "DsRow");
        Ui.Show(_bar, _selected);
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_activate is null || e.OriginalSource is not DependencyObject source || IsInteractive(source))
        {
            return;
        }

        _activate();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_activate is not null && ReferenceEquals(e.OriginalSource, _root) && e.Key is VirtualKey.Enter or VirtualKey.Space)
        {
            _activate();
            e.Handled = true;
        }
    }

    private static bool IsInteractive(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase or ComboBox or HyperlinkButton)
            {
                return true;
            }
        }

        return false;
    }
}
