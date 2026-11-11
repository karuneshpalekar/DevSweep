using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Settings;
using DevSweep.App.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.UI;
using Rect = Windows.Foundation.Rect;
using Windows.Storage.Streams;

namespace DevSweep.App;

/// <summary>
/// The one window: Mica backdrop, content in the title bar, a NavigationView with the places
/// Core's modules provide, and a sheet layer (Welcome, Review) inside the window so keyboard
/// focus stays in it and screenshots capture it.
/// </summary>
internal sealed partial class MainWindow : Window
{
    private readonly MainViewModel _model;
    private readonly Grid _root = new();
    private readonly NavigationView _nav = new();
    private readonly Border _page = new();
    private readonly Grid _sheetLayer = new() { Visibility = Visibility.Collapsed };
    private readonly Border _sheet = new() { MaxWidth = 720, Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Subscriptions _windowSubs = new();
    private Subscriptions _pageSubs = new();
    private Subscriptions _sheetSubs = new();
    private string? _sheetKind;
    private bool _allowClose;
    private Button _themeButton = new();
    private Button _settingsButton = new();
    private FontIcon _themeIcon = new();
    private TextBlock _themeText = new();
    private readonly ColumnDefinition _captionInset = new() { Width = new GridLength(138) };
    private BitmapImage? _logo;
    private bool _logoFailed;
    private (int Width, int Height) _minimumSize = (WindowSizing.MinimumWidth, WindowSizing.MinimumHeight);

    public MainWindow(MainViewModel model, bool screenshots)
    {
        _model = model;
        Title = "DevSweep";
        ExtendsContentIntoTitleBar = true;
        if (!screenshots)
        {
            SystemBackdrop = new MicaBackdrop();
        }
        else
        {
            // Mica is not part of a RenderTargetBitmap; screenshots use the solid base colour.
            _root.Style = Ui.Style("DsRoot");
        }

        SizeWindow(screenshots);
        ApplyWindowIcon();
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid titleBar = BuildTitleBar();
        _root.Children.Add(titleBar);
        BuildNavigation();
        Grid.SetRow(_nav, 1);
        _root.Children.Add(_nav);

        var scrim = new Grid { Style = Ui.Style("DsScrim") };
        _sheet.Style = Ui.Style("DsSheet");
        _sheetLayer.Children.Add(scrim);
        _sheetLayer.Children.Add(_sheet);
        Grid.SetRowSpan(_sheetLayer, 2);
        _root.Children.Add(_sheetLayer);
        _root.KeyDown += OnKeyDown;
        Content = _root;
        SetTitleBar(titleBar);

        AppWindow.Closing += OnAppWindowClosing;
        _windowSubs.On(model, nameof(MainViewModel.Current), ShowPage);
        _windowSubs.On(model, nameof(MainViewModel.ShowWelcome), UpdateSheet);
        _windowSubs.On(model.Review, nameof(ReviewViewModel.Stage), UpdateSheet);
        _windowSubs.On(model.SettingsPage, nameof(SettingsViewModel.Appearance), ApplyTheme);
        _root.ActualThemeChanged += (_, _) => ApplyCaptionColors();
        _root.Loaded += (_, _) => UpdateCaptionInset();
        _root.SizeChanged += (_, _) => UpdateCaptionInset();
        Closed += (_, _) =>
        {
            _windowSubs.Dispose();
            _pageSubs.Dispose();
            _sheetSubs.Dispose();
        };
    }

    public FrameworkElement Root => _root;

    /// <summary>
    /// When this returns true, closing the window only hides it (DevSweep stays in the tray, so scheduled scans
    /// and alerts keep running). The app sets it from the "open at login" switch; null (screenshots, smoke) closes normally.
    /// </summary>
    public Func<bool>? KeepInTray { get; set; }

    /// <summary>Lets the next close really close the window (Quit from the tray).</summary>
    public void AllowClose() => _allowClose = true;

    /// <summary>Shows the window again (from the tray, a toast, or a second start), restoring it if it was minimised.</summary>
    public void RevealWindow()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        Activate();
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_allowClose && KeepInTray?.Invoke() == true)
        {
            args.Cancel = true;
            sender.Hide();
        }
    }

    public MainViewModel Model => _model;

    /// <summary>Forces a theme (screenshots); null follows Settings.</summary>
    public void SetTheme(ElementTheme theme)
    {
        _root.RequestedTheme = theme;
        ApplyCaptionColors();
    }

    /// <summary>
    /// Follows Settings: the content, the title bar (it is part of the content), the Mica backdrop (it follows the
    /// content's theme) and the system caption buttons (which would otherwise follow Windows, not DevSweep) all switch live.
    /// </summary>
    public void ApplyTheme()
    {
        AppearanceMode mode = _model.SettingsPage.Appearance;
        _root.RequestedTheme = mode switch
        {
            AppearanceMode.Light => ElementTheme.Light,
            AppearanceMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        _themeIcon.Glyph = mode switch
        {
            AppearanceMode.Light => "\uE706",
            AppearanceMode.Dark => "\uE708",
            _ => "\uE7F4",
        };
        _themeText.Text = SettingsViewModel.AppearanceName(mode);
        string label = _model.SettingsPage.ThemeButtonLabel;
        AutomationProperties.SetName(_themeButton, label);
        Ui.Tip(_themeButton, label);
        ApplyCaptionColors();
    }

    /// <summary>The colour the system caption buttons (minimise, maximise, close) were last given; null where the platform cannot recolour them.</summary>
    public Color? CaptionForeground { get; private set; }

    private void ApplyCaptionColors()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        bool dark = _root.ActualTheme == ElementTheme.Dark;
        Color foreground = dark ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xFF, 0x1B, 0x1B, 0x1B);
        Color inactive = Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A);
        Color none = Color.FromArgb(0, 0, 0, 0);
        AppWindowTitleBar bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = none;
        bar.ButtonInactiveBackgroundColor = none;
        bar.ButtonForegroundColor = foreground;
        bar.ButtonInactiveForegroundColor = inactive;
        bar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(0x19, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0F, 0, 0, 0);
        bar.ButtonHoverForegroundColor = foreground;
        bar.ButtonPressedBackgroundColor = dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1E, 0, 0, 0);
        bar.ButtonPressedForegroundColor = foreground;
        CaptionForeground = foreground;
    }

    /// <summary>What the theme is right now, for the smoke log: the asked-for and the actual element theme and the caption button colour.</summary>
    public string DescribeTheme()
        => $"setting={_model.SettingsPage.Appearance}, requested={_root.RequestedTheme}, actual={_root.ActualTheme}, caption buttons={(CaptionForeground is { } c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : "system")}, button \"{_themeText.Text}\" / {AutomationProperties.GetName(_themeButton)}";

    /// <summary>The theme the content is really showing (Light or Dark).</summary>
    public ElementTheme ActualTheme => _root.ActualTheme;

    /// <summary>Clicks the title bar's gear the way UI Automation does (smoke test).</summary>
    public void ClickSettingsButton() => new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(_settingsButton).Invoke();

    /// <summary>True when the navigation shows this place as selected (the built-in Settings item included).</summary>
    public bool IsSelected(string id) => _nav.SelectedItem is NavigationViewItem { Tag: string tag } && tag == id;

    /// <summary>Clicks the title bar's theme button the way UI Automation does (smoke test); returns false when it cannot be invoked.</summary>
    public bool ClickThemeButton()
    {
        if (!_themeButton.IsEnabled)
        {
            return false;
        }

        new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(_themeButton).Invoke();
        return true;
    }

    /// <summary>Waits until layout has run and two frames have been drawn.</summary>
    public async Task WaitForRenderAsync(int extraMilliseconds = 150)
    {
        _root.UpdateLayout();
        for (int i = 0; i < 2; i++)
        {
            var frame = new TaskCompletionSource();
            void OnRendering(object? sender, object e)
            {
                CompositionTarget.Rendering -= OnRendering;
                frame.TrySetResult();
            }

            CompositionTarget.Rendering += OnRendering;
            await Task.WhenAny(frame.Task, Task.Delay(2000));
        }

        await Task.Delay(extraMilliseconds);
    }

    /// <summary>Saves the window's own content as a PNG (RenderTargetBitmap: only this window, never the screen).</summary>
    public async Task<(int Width, int Height)> CaptureAsync(string path)
    {
        (int width, int height, _, _) = await CaptureWithBrightnessAsync(path);
        return (width, height);
    }

    /// <summary>
    /// Like <see cref="CaptureAsync"/>, and also the average brightness (0 to 255) of the title bar strip and of the content
    /// below it, so a test can tell a light shot from a dark one without looking at it.
    /// </summary>
    public async Task<(int Width, int Height, double TitleBar, double Content)> CaptureWithBrightnessAsync(string path)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(_root);
        IBuffer pixels = await bitmap.GetPixelsAsync();
        (double titleBrightness, double contentBrightness) = Brightness(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixels), bitmap.PixelWidth, bitmap.PixelHeight, (int)Math.Round(48 * (_root.XamlRoot?.RasterizationScale ?? 1)));
        using var stream = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        double dpi = 96 * (_root.XamlRoot?.RasterizationScale ?? 1);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, dpi, dpi, System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixels));
        await encoder.FlushAsync();
        stream.Seek(0);
        byte[] bytes = new byte[stream.Size];
        using (DataReader reader = new(stream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
        }

        await File.WriteAllBytesAsync(path, bytes);
        return (bitmap.PixelWidth, bitmap.PixelHeight, titleBrightness, contentBrightness);
    }

    /// <summary>Mean luma of BGRA pixels, separately for the first <paramref name="titleRows"/> rows and the rest (every 3rd pixel and row is enough).</summary>
    private static (double Title, double Content) Brightness(byte[] bgra, int width, int height, int titleRows)
    {
        double[] sum = new double[2];
        long[] count = new long[2];
        for (int y = 0; y < height; y += 3)
        {
            int part = y < titleRows ? 0 : 1;
            for (int x = 0; x < width; x += 3)
            {
                int i = ((y * width) + x) * 4;
                sum[part] += (0.0722 * bgra[i]) + (0.7152 * bgra[i + 1]) + (0.2126 * bgra[i + 2]);
                count[part]++;
            }
        }

        return (count[0] == 0 ? 0 : sum[0] / count[0], count[1] == 0 ? 0 : sum[1] / count[1]);
    }

    /// <summary>Picks a folder (Settings, project folders).</summary>
    public async Task<string?> PickFolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    /// <summary>Asks a yes/no question in a dialog.</summary>
    public async Task<bool> ConfirmAsync(string title, string text, string yes)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = yes,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = _root.XamlRoot,
            RequestedTheme = _root.ActualTheme,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Whether the logo in the title bar decoded (smoke test): "loaded", "failed" or "missing".</summary>
    public string LogoState => _logo is null ? "missing" : _logoFailed ? "failed" : _logo.PixelWidth > 0 ? "loaded" : "pending";

    /// <summary>Whether the window has its own big and small icon (they are not the generic system one).</summary>
    public (bool Small, bool Big) WindowIcons
    {
        get
        {
            (nint small, nint big) = Win32Window.GetIcons(WinRT.Interop.WindowNative.GetWindowHandle(this));
            return (small != 0, big != 0);
        }
    }

    private void ApplyWindowIcon()
    {
        if (!AppAssets.HasIcon)
        {
            Log.Write("icon: Assets\\DevSweep.ico is missing next to the exe");
            return;
        }

        try
        {
            AppWindow.SetIcon(AppAssets.IconPath);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException or IOException)
        {
            Log.Write("icon: AppWindow.SetIcon failed: " + ex.Message);
        }

        // Make sure Alt+Tab, the taskbar and the title bar really have the logo, whatever SetIcon did.
        nint window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        (nint small, nint big) = Win32Window.GetIcons(window);
        if (small == 0 || big == 0)
        {
            Win32Window.SetIcons(window, AppAssets.IconPath);
        }
    }

    /// <summary>
    /// Opens at 1240 x 820 (device-independent) but never bigger than the screen's work area, centred, with a minimum
    /// size (1000 x 680) at which every place in the navigation is still on screen. A window taller than a small screen
    /// pushed the navigation's last item off the bottom.
    /// </summary>
    private void SizeWindow(bool screenshots)
    {
        if (screenshots)
        {
            AppWindow.Resize(new SizeInt32(1280, 840));
        }
        else
        {
            double scale = Win32Window.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
            RectInt32 work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            (PixelRect bounds, int minWidth, int minHeight) = WindowSizing.Fit(new PixelRect(work.X, work.Y, work.Width, work.Height), scale);
            AppWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
            _minimumSize = (minWidth, minHeight);
        }

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = _minimumSize.Width;
            presenter.PreferredMinimumHeight = _minimumSize.Height;
        }
    }

    /// <summary>Resizes to the smallest size the window allows (smoke and screenshot checks of the navigation).</summary>
    public void ResizeToMinimum() => AppWindow.Resize(new SizeInt32(_minimumSize.Width, _minimumSize.Height));

    public void ResizeTo(int width, int height) => AppWindow.Resize(new SizeInt32(width, height));

    public SizeInt32 WindowSize => AppWindow.Size;

    private Grid BuildTitleBar()
    {
        var bar = new Grid { Padding = new Thickness(14, 0, 0, 0), ColumnSpacing = 4 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(_captionInset);

        UIElement logo;
        if (AppAssets.HasLogo)
        {
            _logo = new BitmapImage(new Uri(AppAssets.LogoPath));
            _logo.ImageFailed += (_, _) => _logoFailed = true;
            logo = new Image { Source = _logo, Width = 20, Height = 20, Stretch = Stretch.Uniform };
        }
        else
        {
            logo = Ui.Icon("\uE74D", 16);
        }

        AutomationProperties.SetName(logo, "DevSweep logo");
        StackPanel title = Ui.H(10, logo, Ui.Text("DevSweep", "DsCaption"));
        title.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(title);

        Button settings = Ui.IconButton("\uE713", "Settings", () => _model.Go("settings"));
        _settingsButton = settings;
        settings.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(settings, 1);
        bar.Children.Add(settings);

        _themeIcon = Ui.Icon("\uE7F4", 14);
        _themeText = Ui.Caption("System");
        _themeButton = new Button
        {
            Content = Ui.H(6, _themeIcon, _themeText),
            Padding = new Thickness(10, 5, 10, 5),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _themeButton.Click += (_, _) => _model.SettingsPage.CycleAppearance();
        Grid.SetColumn(_themeButton, 2);
        bar.Children.Add(_themeButton);
        return bar;
    }

    /// <summary>Keeps the title bar's buttons clear of the system caption buttons on the right.</summary>
    private void UpdateCaptionInset()
    {
        double scale = _root.XamlRoot?.RasterizationScale ?? 1;
        double inset = AppWindow.TitleBar.RightInset / scale;
        _captionInset.Width = new GridLength(inset > 0 ? inset + 4 : 142);
    }

    private void BuildNavigation()
    {
        _nav.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
        // The built-in Settings item sits in the standard bottom-left spot (the title bar's gear is a second way in).
        _nav.IsSettingsVisible = true;
        _nav.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
        _nav.IsPaneToggleButtonVisible = true;
        _nav.OpenPaneLength = 240;
        _nav.Content = _page;
        // The update card may never push the places out of view: it scrolls inside its own capped box.
        _nav.PaneFooter = new ScrollViewer
        {
            Content = UpdateCard.Build(_model.Background.Updates, _windowSubs),
            MaxHeight = 190,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        AutomationProperties.SetName(_nav, "DevSweep places");
        foreach (Place place in _model.Places)
        {
            var item = new NavigationViewItem
            {
                Content = place.Title,
                Tag = place.Id,
                Icon = new FontIcon { Glyph = place.Glyph },
            };
            AutomationProperties.SetName(item, place.Title);
            if (place.Id == "settings")
            {
                continue; // the built-in Settings item
            }

            if (place.Id == "history")
            {
                _nav.FooterMenuItems.Add(item);
            }
            else
            {
                _nav.MenuItems.Add(item);
            }
        }

        _nav.SelectionChanged += (_, e) =>
        {
            if (e.IsSettingsSelected)
            {
                _model.Go("settings");
            }
            else if (e.SelectedItem is NavigationViewItem { Tag: string id })
            {
                _model.Go(id);
            }
        };
        _nav.Loaded += (_, _) =>
        {
            NameSettingsItem();
            SyncSelection();
        };
    }

    private void NameSettingsItem()
    {
        if (_nav.SettingsItem is NavigationViewItem settings)
        {
            settings.Tag = "settings";
            AutomationProperties.SetName(settings, "Settings");
        }
    }

    /// <summary>The navigation item for a place (the built-in Settings item included); null when it does not exist (yet).</summary>
    private NavigationViewItem? NavItem(string id)
    {
        if (id == "settings")
        {
            NameSettingsItem();
            return _nav.SettingsItem as NavigationViewItem;
        }

        return _nav.MenuItems.Concat(_nav.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == id);
    }

    private void SyncSelection()
    {
        if (NavItem(_model.Current) is { } selected && !ReferenceEquals(_nav.SelectedItem, selected))
        {
            _nav.SelectedItem = selected;
        }
    }

    /// <summary>
    /// Opens a place by selecting its navigation item: the same path a click takes (SelectionChanged, then the model, then the page).
    /// Throws when the item does not exist or the page did not change.
    /// </summary>
    public async Task SelectPlaceAsync(string id)
    {
        NavigationViewItem item = NavItem(id) ?? throw new InvalidOperationException($"There is no navigation item for '{id}'.");
        if (ReferenceEquals(_nav.SelectedItem, item))
        {
            _nav.SelectedItem = null;
        }

        _nav.SelectedItem = item;
        await WaitForRenderAsync();
        if (_model.Current != id)
        {
            throw new InvalidOperationException($"Selecting '{id}' in the navigation opened '{_model.Current}'.");
        }
    }

    /// <summary>
    /// Every place must have a navigation item that is really on screen: laid out (height and width above zero), inside the
    /// window, and not scrolled or clipped out of its pane; and no item may exist for a place that is not offered.
    /// Returns what is wrong (empty when all is well).
    /// </summary>
    public IReadOnlyList<string> VerifyNavigation(IEnumerable<string> expected)
    {
        var problems = new List<string>();
        _root.UpdateLayout();
        double windowWidth = _root.ActualWidth;
        double windowHeight = _root.ActualHeight;
        string[] ids = [.. expected];
        foreach (string id in ids)
        {
            if (NavItem(id) is not { } item)
            {
                problems.Add($"{id}: no navigation item");
                continue;
            }

            if (item.Visibility != Visibility.Visible || item.ActualHeight <= 0 || item.ActualWidth <= 0)
            {
                problems.Add($"{id}: not laid out ({item.ActualWidth:0} x {item.ActualHeight:0}, {item.Visibility})");
                continue;
            }

            Rect bounds = item.TransformToVisual(_root).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
            if (bounds.Left < -0.5 || bounds.Top < -0.5 || bounds.Right > windowWidth + 0.5 || bounds.Bottom > windowHeight + 0.5)
            {
                problems.Add($"{id}: outside the window (item {bounds.Left:0},{bounds.Top:0} to {bounds.Right:0},{bounds.Bottom:0}; window {windowWidth:0} x {windowHeight:0})");
            }

            for (DependencyObject? parent = VisualTreeHelper.GetParent(item); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is ScrollViewer { ActualHeight: > 0 } viewer)
                {
                    Rect inViewer = item.TransformToVisual(viewer).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
                    if (inViewer.Top < -0.5 || inViewer.Bottom > viewer.ActualHeight + 0.5)
                    {
                        problems.Add($"{id}: scrolled or clipped out of its pane (item {inViewer.Top:0} to {inViewer.Bottom:0} in a viewer {viewer.ActualHeight:0} high)");
                    }
                }
            }
        }

        string[] present = [.. _nav.MenuItems.Concat(_nav.FooterMenuItems).OfType<NavigationViewItem>().Select(i => (string)i.Tag)];
        foreach (string extra in present.Except(ids))
        {
            problems.Add($"{extra}: has a navigation item but is not an offered place");
        }

        return problems;
    }

    /// <summary>One line for the logs: each item's id and where it is.</summary>
    public string DescribeNavigation(IEnumerable<string> ids)
        => string.Join("; ", ids.Select(id =>
        {
            NavigationViewItem? item = NavItem(id);
            if (item is null || item.ActualHeight <= 0)
            {
                return $"{id} MISSING";
            }

            Rect b = item.TransformToVisual(_root).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
            return $"{id} y={b.Top:0}..{b.Bottom:0}";
        }));

    private void ShowPage()
    {
        _pageSubs.Dispose();
        _pageSubs = new Subscriptions();
        string id = _model.Current;
        NavigationViewItem? selected = _nav.MenuItems.Concat(_nav.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == id);
        if (selected is not null && !ReferenceEquals(_nav.SelectedItem, selected))
        {
            _nav.SelectedItem = selected;
        }

        _page.Child = id switch
        {
            "cleanup" => CleanUpView.Build(_model, _pageSubs, this),
            "projects" => ProjectsView.Build(_model, _pageSubs, this),
            "health" => HealthView.Build(_model, _pageSubs, this),
            "history" => HistoryView.Build(_model, _pageSubs, this),
            "settings" => SettingsView.Build(_model, _pageSubs, this),
            _ => HomeView.Build(_model, _pageSubs, this),
        };
    }

    private void UpdateSheet()
    {
        string? kind = _model.ShowWelcome ? "welcome" : _model.Review.IsOpen ? "review" : null;
        if (kind == _sheetKind)
        {
            return;
        }

        _sheetKind = kind;
        _sheetSubs.Dispose();
        _sheetSubs = new Subscriptions();
        _sheet.Child = kind switch
        {
            "welcome" => WelcomeSheet.Build(_model, _sheetSubs),
            "review" => ReviewSheet.Build(_model, _sheetSubs, this),
            _ => null,
        };
        bool open = kind is not null;
        Ui.Show(_sheetLayer, open);
        _nav.IsEnabled = !open;
        if (open && _sheet.Child is FrameworkElement content)
        {
            content.Loaded += (_, _) => FocusFirst(content);
        }
    }

    private static void FocusFirst(DependencyObject root)
    {
        if (FocusManager.FindFirstFocusableElement(root) is Control control)
        {
            control.Focus(FocusState.Programmatic);
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape)
        {
            return;
        }

        if (_sheetKind == "review" && _model.Review.Stage != ReviewStage.Cleaning)
        {
            _model.Review.Close();
            e.Handled = true;
        }
        else if (_sheetKind is null && _model.CleanUp.Inspected is not null)
        {
            _model.CleanUp.Inspected = null;
            e.Handled = true;
        }
    }
}
