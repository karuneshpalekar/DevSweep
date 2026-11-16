using DevSweep.App.Toolkit;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DevSweep.App.Design;

/// <summary>
/// A list with a detail pane that slides in from the right (the Mac's <c>SidePanelLayout</c>). The pane
/// keeps showing the last item while it closes, cross-fades when another item is selected, closes on Esc and puts keyboard
/// focus back where it was when it opened. On a narrow window it overlays the list instead of squeezing it.
/// </summary>
internal sealed partial class SidePanel : IDisposable
{
    private readonly Grid _root = new();
    private readonly Border _host;
    private readonly Grid _inner;
    private readonly Func<string, Subscriptions, UIElement> _build;
    private readonly double _width;
    private Subscriptions _subs = new();
    private double _progress;
    private bool _open;
    private string? _shown;
    private Control? _opener;
    private MotionHandle? _slide;
    private MotionHandle? _fade;

    public SidePanel(UIElement content, Func<string, Subscriptions, UIElement> build, string automationName, double width = Tokens.PanelWidth)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(build);
        _build = build;
        _width = width;
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.Children.Add(content);

        _inner = new Grid { Width = width, HorizontalAlignment = HorizontalAlignment.Left, Opacity = 0 };
        _host = new Border
        {
            Style = Ui.Style("DsInspector"),
            Width = 0,
            Visibility = Visibility.Collapsed,
            Child = _inner,
        };
        AutomationProperties.SetName(_host, automationName);
        Grid.SetColumn(_host, 1);
        _root.Children.Add(_host);
        _root.KeyDown += OnKeyDown;
        _root.SizeChanged += (_, _) =>
        {
            if (!_open)
            {
                ChooseMode();
            }
        };
    }

    /// <summary>The list and the pane; put this in the page.</summary>
    public FrameworkElement Root => _root;

    /// <summary>Called when the panel closes itself (Esc), so the owner can clear what it selected.</summary>
    public Action? Closed { get; set; }

    /// <summary>The key of the item the panel is open on, or null when it is closed (or closing).</summary>
    public string? Selected => _open ? _shown : null;

    /// <summary>The key of the item the panel shows, also while it is closing.</summary>
    public string? Shown => _shown;

    public bool IsOpen => _open;

    /// <summary>How far the pane is open, 0 (closed) to 1 (fully open).</summary>
    public double Progress => _progress;

    /// <summary>The pane's current width in pixels.</summary>
    public double PaneWidth => _host.Width;

    /// <summary>True when the pane lies over the list instead of pushing it.</summary>
    public bool IsOverlay => Grid.GetColumnSpan(_host) == 2;

    /// <summary>Opens the panel on <paramref name="key"/>, switches to it when open, or closes it when null.</summary>
    public void Select(string? key)
    {
        if (key is null)
        {
            Close();
            return;
        }

        if (!_open)
        {
            _opener = FocusManager.GetFocusedElement(_root.XamlRoot) as Control;
            _open = true;
            ChooseMode();
            Show(key);
            _host.Visibility = Visibility.Visible;
            _inner.Opacity = 0;
            Animate(1, Motion.PanelMs);
            return;
        }

        if (key == _shown)
        {
            return;
        }

        _fade?.Cancel();
        _fade = Motion.Run(Motion.SwapMs / 2, Ease.InOut, t => _inner.Opacity = 1 - t, () =>
        {
            Show(key);
            _fade = Motion.Run(Motion.SwapMs / 2, Ease.InOut, t => _inner.Opacity = t);
        });
    }

    /// <summary>Esc closes the panel; returns true when it handled the key.</summary>
    public bool HandleKey(Windows.System.VirtualKey key)
    {
        if (key != Windows.System.VirtualKey.Escape || !_open)
        {
            return false;
        }

        Close();
        Closed?.Invoke();
        return true;
    }

    public void Dispose()
    {
        _slide?.Cancel();
        _fade?.Cancel();
        _subs.Dispose();
    }

    private void Close()
    {
        if (!_open)
        {
            return;
        }

        _open = false;
        Control? opener = _opener;
        _opener = null;
        if (opener is { XamlRoot: not null, IsLoaded: true })
        {
            opener.Focus(FocusState.Programmatic);
        }

        _fade?.Cancel();
        Animate(0, Motion.PanelMs);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (HandleKey(e.Key))
        {
            e.Handled = true;
        }
    }

    private void Show(string key)
    {
        _subs.Dispose();
        _subs = new Subscriptions();
        _shown = key;
        _inner.Children.Clear();
        _inner.Children.Add(_build(key, _subs));
        _inner.Opacity = 1;
    }

    private void Animate(double target, int milliseconds)
    {
        _slide?.Cancel();
        double from = _progress;
        _slide = Motion.Run(milliseconds, Ease.Spring, t => Apply(Motion.Lerp(from, target, t)), () =>
        {
            if (target == 0)
            {
                _host.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void Apply(double progress)
    {
        _progress = progress;
        double width = _width * progress;
        _host.Width = width;
        _host.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, 100000) };
        _inner.Opacity = Math.Clamp(progress * 1.4, 0, 1);
    }

    private void ChooseMode()
    {
        bool overlay = _root.ActualWidth > 0 && _root.ActualWidth < Tokens.PanelOverlayBelow;
        Grid.SetColumn(_host, overlay ? 0 : 1);
        Grid.SetColumnSpan(_host, overlay ? 2 : 1);
        _host.HorizontalAlignment = overlay ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
    }
}
