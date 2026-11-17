using DevSweep.App.Design;
using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels.Background;
using DevSweep.App.ViewModels.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace DevSweep.App.Background;

/// <summary>
/// The tray's left-click popover, the Windows counterpart of the Mac's menu bar popover: free space with its bar, what can be cleaned,
/// up to two alerts, three recent projects with Open, Scan now and Open DevSweep, and when the last scan was. Nothing here cleans anything.
/// </summary>
internal static class TrayFlyoutView
{
    public const double Width = 320;

    public static FrameworkElement Build(TrayViewModel tray, Action close)
    {
        TrayFlyoutContent c = tray.Flyout();

        void Do(TrayItem item)
        {
            close();
            tray.Execute(item);
        }

        Button settings = Ui.IconButton(Icons.Settings, "Settings", () => Do(new TrayItem("Settings", TrayCommand.OpenSettings)));
        Button quit = Ui.IconButton("", "Quit DevSweep", () => Do(new TrayItem("Quit DevSweep", TrayCommand.Quit)));
        TextBlock free = Ui.Caption(c.FreeText);
        free.VerticalAlignment = VerticalAlignment.Center;
        var header = new Grid { ColumnSpacing = Tokens.Space2 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        FontIcon logo = Ui.Icon(Icons.Logo, 18);
        logo.VerticalAlignment = VerticalAlignment.Center;
        TextBlock name = Ui.Text("DevSweep", "DsStrong");
        name.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(name, 1);
        Grid.SetColumn(free, 2);
        Grid.SetColumn(settings, 3);
        Grid.SetColumn(quit, 4);
        foreach (UIElement part in new UIElement[] { logo, name, free, settings, quit })
        {
            header.Children.Add(part);
        }

        // Headline: what can be cleaned (or why not), the volume bar, and the one button that goes with it.
        TextBlock headline = Ui.Text(c.Headline, c.State == FlyoutState.HasCleanable ? "DsBigAccent" : "DsBig");
        TextBlock sub = Ui.Caption(c.SubLine);
        sub.MaxLines = 1;
        sub.TextTrimming = TextTrimming.CharacterEllipsis;
        Button? primary = c.State switch
        {
            FlyoutState.HasCleanable => Ui.Button("Review", () => Do(new TrayItem("Review", TrayCommand.OpenCleanUp)), "Review what can be cleaned", accent: true),
            FlyoutState.NotScanned => Ui.Button("Scan now", () => Do(new TrayItem("Scan now", TrayCommand.ScanNow)), "Scan now", accent: true),
            _ => null,
        };
        Grid top = Ui.Row(Ui.V(2, headline, sub), primary);
        var track = new Grid { Height = 6 };
        track.Children.Add(new Border { Style = Ui.Style("DsBarTrack"), CornerRadius = new CornerRadius(3) });
        var bars = new Grid();
        bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, c.UsedFraction - c.CleanableFraction), GridUnitType.Star) });
        bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, c.CleanableFraction), GridUnitType.Star) });
        bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 1 - c.UsedFraction), GridUnitType.Star) });
        var used = new Border { Style = Ui.Style("DsBarUsed"), CornerRadius = new CornerRadius(3, 0, 0, 3) };
        var clean = new Border { Style = Ui.Style("DsBarCleanable") };
        Grid.SetColumn(clean, 1);
        bars.Children.Add(used);
        bars.Children.Add(clean);
        track.Children.Add(bars);
        Border card = Ds.Card(Ui.V(Tokens.Space3, top, track), Tokens.Space3);
        AutomationProperties.SetName(card, $"{c.FreeText}. {c.Headline} {c.SubLine}");

        var body = new StackPanel { Spacing = Tokens.Space3 };
        body.Children.Add(header);
        body.Children.Add(card);

        if (c.Alerts.Count > 0)
        {
            body.Children.Add(Ui.V(Tokens.Space1, [Ui.Caption("Needs attention"), .. c.Alerts.Select(a => Alert(a, Do))]));
        }

        if (c.Recent.Count > 0)
        {
            var projects = new List<UIElement> { Ui.Caption("Recent projects") };
            foreach (FlyoutProject p in c.Recent)
            {
                TextBlock title = Ui.Text(p.Name, maxLines: 1);
                StackPanel text = Ui.H(Tokens.Space2, title);
                if (p.Note is not null)
                {
                    text.Children.Add(Ds.StatusTag(p.Note, Tone.Caution));
                }

                projects.Add(Ui.Row(text, Ui.Button("Open", () => Do(new TrayItem(p.Name, TrayCommand.OpenProject, p.Path)), $"Open {p.Name} in your editor")));
            }

            projects.Add(Ui.Link("All projects", () => Do(new TrayItem("Projects", TrayCommand.OpenTarget, nameof(ToastTarget.Projects))), "Open all projects in DevSweep"));
            body.Children.Add(Ui.V(Tokens.Space1, [.. projects]));
        }

        Button scan = Ui.Button(c.CanScan ? "Scan now" : "Scanning…", () => Do(new TrayItem("Scan now", TrayCommand.ScanNow)), "Scan now");
        scan.IsEnabled = c.CanScan;
        Button open = Ui.Button("Open DevSweep", () => Do(new TrayItem("Open DevSweep", TrayCommand.OpenWindow)), "Open DevSweep");
        var buttons = new Grid { ColumnSpacing = Tokens.Space2 };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scan.HorizontalAlignment = HorizontalAlignment.Stretch;
        open.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(open, 1);
        buttons.Children.Add(scan);
        buttons.Children.Add(open);
        body.Children.Add(buttons);
        if (c.LastScanText is { } last)
        {
            TextBlock text = Ui.Caption(last);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            body.Children.Add(text);
        }

        var root = new Grid { Style = Ui.Style("DsRoot"), Width = Width, Padding = new Thickness(Tokens.Space4) };
        root.Children.Add(body);
        AutomationProperties.SetName(root, "DevSweep tray flyout");
        return root;
    }

    private static UIElement Alert(FlyoutAlert alert, Action<TrayItem> act)
    {
        FontIcon icon = Ui.Icon(alert.Critical ? Icons.Error : Icons.Warning, 14);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 3, 0, 0);
        TextBlock text = Ui.Text(alert.Item.Text, maxLines: 2);
        var line = new Grid { ColumnSpacing = Tokens.Space2 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(text, 1);
        line.Children.Add(icon);
        line.Children.Add(text);
        return new ListRow(line, () => act(alert.Item), "Needs attention: " + alert.Item.Text).Root;
    }
}

/// <summary>
/// A small borderless window shown next to the tray icon on a left click (closes when it loses focus). The native right-click menu stays as the
/// fallback. Where the window cannot be created the tray opens DevSweep instead.
/// </summary>
internal sealed class TrayFlyoutWindow : Window
{
    private readonly TrayViewModel _tray;
    private readonly Func<ElementTheme> _theme;
    private readonly Grid _host = new();

    public TrayFlyoutWindow(TrayViewModel tray, Func<ElementTheme> theme)
    {
        _tray = tray;
        _theme = theme;
        Title = "DevSweep";
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
            presenter.SetBorderAndTitleBar(true, false);
        }

        AppWindow.IsShownInSwitchers = false;
        Content = _host;
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                Dismiss();
            }
        };
    }

    public FrameworkElement Root => _host;

    public bool IsOpen => AppWindow.IsVisible;

    /// <summary>Rebuilds the content from what the app knows now.</summary>
    public void Refresh()
    {
        _host.RequestedTheme = _theme();
        _host.Children.Clear();
        _host.Children.Add(TrayFlyoutView.Build(_tray, Dismiss));
    }

    /// <summary>Shows the flyout above (or below) the point where the tray icon was clicked, inside the screen's work area.</summary>
    public void ShowAt(int x, int y)
    {
        Refresh();
        double scale = Win32Window.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        _host.Measure(new Windows.Foundation.Size(TrayFlyoutView.Width, double.PositiveInfinity));
        int width = (int)Math.Ceiling(TrayFlyoutView.Width * scale);
        int height = (int)Math.Ceiling(_host.DesiredSize.Height * scale) + 2;
        RectInt32 work = DisplayArea.GetFromPoint(new PointInt32(x, y), DisplayAreaFallback.Nearest).WorkArea;
        int left = Math.Clamp(x - (width / 2), work.X + 8, Math.Max(work.X + 8, work.X + work.Width - width - 8));
        int top = y - height - 12 >= work.Y ? y - height - 12 : Math.Min(y + 12, work.Y + work.Height - height);
        AppWindow.MoveAndResize(new RectInt32(left, top, width + 16, height + 16));
        AppWindow.ResizeClient(new SizeInt32(width, height));
        AppWindow.Show();
        Activate();
    }

    /// <summary>When it was last hidden (a click on the tray icon deactivates it first, and must not reopen it at once).</summary>
    public long DismissedAt { get; private set; }

    public void Dismiss()
    {
        DismissedAt = Environment.TickCount64;
        AppWindow.Hide();
    }
}
