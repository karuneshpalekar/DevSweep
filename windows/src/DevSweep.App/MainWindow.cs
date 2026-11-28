using DevSweep.App.Design;
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
    private readonly Border _page = new() { RenderTransform = new TranslateTransform() };
    private readonly Grid _sheetLayer = new() { Visibility = Visibility.Collapsed };
    private readonly Border _sheet = new() { MaxWidth = 720, Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Subscriptions _windowSubs = new();
    private Subscriptions _pageSubs = new();
    private Subscriptions _sheetSubs = new();
    private string? _sheetKind;
    private string? _shownPage;
    private MotionHandle? _pageMotion;
    private MotionHandle? _sheetMotion;
    private bool _allowClose;
    private Button _themeButton = new();
    private FontIcon _themeIcon = new();
    private TextBlock _themeText = new();
    private readonly ColumnDefinition _captionInset = new() { Width = new GridLength(138) };
    private BitmapImage? _logo;
    private bool _logoFailed;
    private (int Width, int Height) _minimumSize = (WindowSizing.MinimumWidth, WindowSizing.MinimumHeight);

    private readonly bool _screenshots;

    public MainWindow(MainViewModel model, bool screenshots)
    {
        _model = model;
        _screenshots = screenshots;
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

        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Tokens.TitleBarHeight) });
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
        _windowSubs.On(model.Projects, nameof(ProjectsViewModel.Sheet), UpdateSheet);
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

    /// <summary>True while the window is on screen (not hidden in the tray, not minimised); live lists pause when it is not.</summary>
    public bool IsShowing => AppWindow.IsVisible && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    /// <summary>The page's side panel while it is on screen (Clean up); null otherwise. Tests drive it.</summary>
    public SidePanel? ActivePanel { get; set; }

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
            AppearanceMode.Light => Icons.Sun,
            AppearanceMode.Dark => Icons.Moon,
            _ => Icons.Auto,
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

    /// <summary>The first control of type <typeparamref name="T"/> in the window with this automation name (tests).</summary>
    public T? Find<T>(string automationName)
        where T : DependencyObject
        => Find<T>(_root, automationName);

    private static T? Find<T>(DependencyObject parent, string automationName)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match && AutomationProperties.GetName(child) == automationName)
            {
                return match;
            }

            if (Find<T>(child, automationName) is { } deeper)
            {
                return deeper;
            }
        }

        return default;
    }


    /// <summary>True when the navigation shows this place as selected (the built-in Settings item included).</summary>
    public bool IsSelected(string id)
    {
        NameSettingsItem();
        return _nav.SelectedItem is { } selected && ((selected is NavigationViewItem { Tag: string tag } && tag == id) || (id == "settings" && ReferenceEquals(selected, _nav.SettingsItem)));
    }

    /// <summary>What the navigation has selected, for failure messages.</summary>
    public string DescribeSelection()
        => _nav.SelectedItem is { } selected
            ? $"{selected.GetType().Name}, tag '{(selected as FrameworkElement)?.Tag}', is the settings item: {ReferenceEquals(selected, _nav.SettingsItem)}, content '{(selected as NavigationViewItem)?.Content}'"
            : "nothing";

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
        (double titleBrightness, double contentBrightness) = Brightness(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixels), bitmap.PixelWidth, bitmap.PixelHeight, (int)Math.Round(Tokens.TitleBarHeight * (_root.XamlRoot?.RasterizationScale ?? 1)));
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

    /// <summary>True while a Projects sheet is working (a download, a publish): Esc does not close it then.</summary>
    public bool SheetBusy { get; set; }

    /// <summary>Tells the person something in a dialog with one Close button.</summary>
    public async Task InformAsync(string title, UIElement content)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = _root.XamlRoot,
            RequestedTheme = _root.ActualTheme,
        };
        await dialog.ShowAsync();
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
        var bar = new Grid { Padding = new Thickness(Tokens.Space4, 0, 0, 0), ColumnSpacing = Tokens.Space1 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
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
            logo = Ui.Icon(Icons.Logo, 16);
        }

        AutomationProperties.SetName(logo, "DevSweep logo");
        StackPanel title = Ui.H(Tokens.Space3, logo, Ui.Text("DevSweep", "DsCaption"));
        title.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(title);

        _themeIcon = Ui.Icon(Icons.Auto, 14);
        _themeText = Ui.Caption("System");
        _themeButton = new Button
        {
            Content = Ui.H(Tokens.Space2, _themeIcon, _themeText),
            Height = 32,
            Padding = new Thickness(Tokens.Space3, 0, Tokens.Space3, 0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _themeButton.Click += (_, _) => _model.SettingsPage.CycleAppearance();
        Grid.SetColumn(_themeButton, 1);
        bar.Children.Add(_themeButton);
        if (_screenshots)
        {
            // A capture of the window's content has no system caption buttons; draw stand-ins so the shot shows where the theme button really sits.
            StackPanel caption = Ui.H(0, Ui.Icon("\uE921", 10), Ui.Icon("\uE922", 10), Ui.Icon("\uE8BB", 10));
            foreach (UIElement part in caption.Children)
            {
                ((FrameworkElement)part).Width = 46;
            }

            caption.VerticalAlignment = VerticalAlignment.Center;
            caption.HorizontalAlignment = HorizontalAlignment.Right;
            caption.IsHitTestVisible = false;
            Grid.SetColumn(caption, 2);
            bar.Children.Add(caption);
        }

        return bar;
    }

    /// <summary>Keeps the title bar's buttons clear of the system caption buttons on the right.</summary>
    private void UpdateCaptionInset()
    {
        double scale = _root.XamlRoot?.RasterizationScale ?? 1;
        double inset = AppWindow.TitleBar.RightInset / scale;
        _captionInset.Width = new GridLength(inset > 0 ? inset + Tokens.Space1 : 144);
    }

    private void BuildNavigation()
    {
        _nav.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
        // The built-in Settings item sits in the standard bottom-left spot (there is no second Settings button in the title bar).
        _nav.IsSettingsVisible = true;
        _nav.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
        _nav.IsPaneToggleButtonVisible = true;
        _nav.OpenPaneLength = 256;
        _nav.Content = _page;
        // The free-space card sits above the places; it and the footer hide when the pane is compact.
        _paneHeader = SidebarView.Header(_model.Sidebar, _windowSubs);
        _nav.PaneHeader = _paneHeader;
        // The update card, the attention cards and the status line may never push the places out of view: they scroll inside a box
        // whose height is what is left once the header and the places (and History and Settings) have their room.
        _paneFooter = new ScrollViewer
        {
            Content = FooterContent(),
            MaxHeight = 190,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _nav.PaneFooter = _paneFooter;
        _nav.SizeChanged += (_, _) =>
        {
            _paneFooter.MaxHeight = Math.Max(96, _nav.ActualHeight - PaneReserved);
            UpdateFold();
        };
        _nav.PaneClosing += (_, _) => SetPaneParts(false);
        _nav.PaneOpening += (_, _) => SetPaneParts(true);
        AutomationProperties.SetName(_nav, "DevSweep places");
        foreach (Place place in _model.Places)
        {
            var item = new NavigationViewItem
            {
                Content = SidebarView.ItemContent(place.Title, place.Id, _model.Sidebar, _windowSubs),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Tag = place.Id,
                Icon = new FontIcon { Glyph = Icons.ForPlace(place.Id, place.Glyph) },
            };
            AutomationProperties.SetName(item, place.Title);
            if (SidebarSubRows.Has(place.Id))
            {
                _sections.Add(place.Id);
            }

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

        foreach (string placeId in _sections)
        {
            System.ComponentModel.INotifyPropertyChanged source = placeId switch
            {
                "cleanup" => _model.CleanUp,
                "projects" => _model.Projects,
                _ => _model.Health,
            };
            _windowSubs.On(source, null, RenderSectionRows);
        }

        _windowSubs.OnCollection(_model.CleanUp.Filters, RenderSectionRows);
        _windowSubs.On(_model.Scan, nameof(ScanViewModel.HasScanned), RenderSectionRows);

        _nav.SelectionChanged += (_, e) =>
        {
            if (TraceNavigation)
            {
                Log.Write($"selection changed: settings={e.IsSettingsSelected}, item tag '{(e.SelectedItem as FrameworkElement)?.Tag}', model is on '{_model.Current}'");
            }

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

    /// <summary>Places that have rows (Clean up, Projects, Health), when they are turned on.</summary>
    private readonly HashSet<string> _sections = new(StringComparer.Ordinal);

    /// <summary>The open section's rows, at the top of the pane footer, just under the places (the user's choice: WinUI keeps its list
    /// of places at a fixed height, so rows nested under a place would push other places out of sight on a short window).</summary>
    private readonly StackPanel _sectionRows = new() { Spacing = 2 };

    private readonly TextBlock _sectionTitle = new();

    private readonly Dictionary<string, Action<SubRow>> _rowUpdates = new(StringComparer.Ordinal);

    private UIElement? _sectionPanel;

    private UIElement? _footerCards;

    private string? _shownSection;

    /// <summary>Folds the update card and alerts into one line (rows win on a short window, as the user chose).</summary>
    private Action<bool> _foldFooter = _ => { };

    /// <summary>The person clicked the folded line: show the cards in full on this section.</summary>
    private bool _footerPinned;

    private bool _footerFolded;

    private double _cardsHeight;

    private UIElement FooterContent()
    {
        (UIElement cards, Action<bool> fold) = SidebarView.FoldableFooter(_model, _windowSubs, UpdateCard.Build(_model.Background.Updates, _windowSubs), () =>
        {
            _footerPinned = true;
            UpdateFold();
        });
        _foldFooter = fold;
        _footerCards = cards;
        _sectionTitle.Margin = new Thickness(Tokens.Space3, Tokens.Space1, Tokens.Space3, 0);
        _sectionTitle.Style = Ui.Style("DsCaption");
        _sectionRows.Margin = new Thickness(Tokens.Space1, 0, Tokens.Space1, 0);
        _sectionPanel = Ui.V(Tokens.Space1, _sectionTitle, _sectionRows);
        _sectionPanel.Visibility = Visibility.Collapsed;
        return Ui.V(Tokens.Space2, _sectionPanel, cards);
    }

    /// <summary>Builds or updates the open section's rows (rebuilt only when the section or its rows change, so nothing flickers).</summary>
    private void RenderSectionRows()
    {
        if (_sectionPanel is null)
        {
            return;
        }

        string? section = _sections.Contains(_model.Current) ? _model.Current : null;
        IReadOnlyList<SubRow> list = section is null ? [] : SidebarSubRows.For(_model, section);
        if (section != _shownSection || !list.Select(r => r.Tag).SequenceEqual(_rowUpdates.Keys))
        {
            bool newSection = section != _shownSection;
            _shownSection = section;
            _sectionRows.Children.Clear();
            _rowUpdates.Clear();
            foreach (SubRow row in list)
            {
                (UIElement content, Action<SubRow> update) = SidebarSubRows.Content(row);
                string tag = row.Tag;
                var item = new ListRow(content, () => _model.Go(SidebarSubRows.Open(_model, tag)), row.Title);
                _sectionRows.Children.Add(item.Root);
                _rowUpdates[tag] = update;
            }

            _sectionTitle.Text = section is null ? string.Empty : _model.Places.First(p => p.Id == section).Title.ToUpperInvariant();
            Motion.Reveal(_sectionPanel, list.Count > 0);
            if (newSection && list.Count > 0)
            {
                Motion.FadeIn(_sectionRows);
            }

            UpdateFold();
        }

        foreach (SubRow row in list)
        {
            _rowUpdates[row.Tag](row);
        }
    }

    /// <summary>Rows win on a short window: the cards fold into one line when rows and cards together don't fit the footer.</summary>
    private void UpdateFold()
    {
        if (_shownSection is null || _rowUpdates.Count == 0 || _paneFooter is null)
        {
            FoldFooter(false);
            return;
        }

        if (!_footerFolded && _footerCards is FrameworkElement { ActualHeight: > 0 } cards)
        {
            _cardsHeight = cards.ActualHeight;
        }

        double rowsHeight = 28 + (_rowUpdates.Count * 40);
        FoldFooter(!_footerPinned && rowsHeight + _cardsHeight > _paneFooter.MaxHeight);
    }

    private void FoldFooter(bool folded)
    {
        _footerFolded = folded;
        _foldFooter(folded);
    }

    /// <summary>How the footer's room was shared out, for the tour log.</summary>
    public string DescribeRoom()
        => $"footer {_paneFooter?.ActualHeight ?? 0:0} of at most {_paneFooter?.MaxHeight ?? 0:0} px, cards {_cardsHeight:0} px, {_rowUpdates.Count} rows, folded: {_footerFolded}, pinned: {_footerPinned}";

    /// <summary>The rows shown for a section right now (tests and the screenshot tour).</summary>
    public IReadOnlyList<string> SubRowTags(string placeId)
        => _shownSection == placeId && _sectionPanel is { Visibility: Visibility.Visible } ? [.. _rowUpdates.Keys] : [];

    /// <summary>Room the header, the hamburger, the four places and History and Settings need; the pane footer gets what is left.</summary>
    private const double PaneReserved = 400;

    private UIElement? _paneHeader;
    private ScrollViewer? _paneFooter;

    private void SetPaneParts(bool open)
    {
        if (_paneHeader is not null)
        {
            _paneHeader.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_paneFooter is not null)
        {
            _paneFooter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Opens or collapses the pane (the hamburger does the same); screenshots use it for the compact sidebar and for a wide Home.</summary>
    public void SetPaneOpen(bool open)
    {
        _nav.IsPaneOpen = open;
        SetPaneParts(open);
    }

    public bool PaneOpen => _nav.IsPaneOpen;

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

    /// <summary>Makes the navigation highlight the current place when something other than a click changed it (a toast, the tray).</summary>
    private void SyncSelection(int retries = 3)
    {
        string id = _model.Current;
        if (NavItem(id) is not { } target)
        {
            if (TraceNavigation)
            {
                Log.Write($"sync: no navigation item for '{id}' yet");
            }

            return;
        }

        if (IsSelected(id))
        {
            return;
        }

        _nav.SelectedItem = target;
        if (TraceNavigation)
        {
            Log.Write($"sync: asked for '{id}', navigation now has {DescribeSelection()}");
        }

        // The navigation can ignore a selection made while it is busy with the previous one; ask again once it is idle.
        if (!IsSelected(id) && retries > 0)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_model.Current == id)
                {
                    SyncSelection(retries - 1);
                }
            });
        }
    }

    /// <summary>Logs how the navigation follows the model (smoke test).</summary>
    public bool TraceNavigation { get; set; }

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
        if (TraceNavigation)
        {
            Log.Write($"show page '{id}'; navigation has {DescribeSelection()}");
        }

        SyncSelection();
        _footerPinned = false;
        RenderSectionRows();

        bool changed = _shownPage is not null && _shownPage != id;
        _shownPage = id;
        _pageMotion?.Cancel();
        _page.Child = id switch
        {
            "cleanup" => CleanUpView.Build(_model, _pageSubs, this),
            "projects" => ProjectsView.Build(_model, _pageSubs, this),
            "health" => HealthView.Build(_model, _pageSubs, this),
            "history" => HistoryView.Build(_model, _pageSubs, this),
            "settings" => SettingsView.Build(_model, _pageSubs, this),
            _ => HomeView.Build(_model, _pageSubs, this),
        };
        if (changed)
        {
            // A new screen eases in (220 ms); the very first one and every screenshot appear at once.
            var move = (TranslateTransform)_page.RenderTransform;
            _pageMotion = Motion.Run(Motion.ScreenMs, Ease.InOut, t =>
            {
                _page.Opacity = t;
                move.Y = (1 - t) * Tokens.Space2;
            });
        }
    }

    private void UpdateSheet()
    {
        string? kind = _model.ShowWelcome ? "welcome" : _model.Review.IsOpen ? "review"
            : _model.Projects.Sheet != ProjectSheet.None ? "projects:" + _model.Projects.Sheet : null;
        if (kind == _sheetKind)
        {
            return;
        }

        _sheetKind = kind;
        _sheetSubs.Dispose();
        _sheetSubs = new Subscriptions();
        _sheetMotion?.Cancel();
        if (kind is null)
        {
            // Closing: the sheet fades out with its last content, then goes (as the Mac's sheets do).
            _nav.IsEnabled = true;
            if (_sheetLayer.Visibility == Visibility.Visible && Motion.Enabled)
            {
                _sheetLayer.IsHitTestVisible = false;
                double from = _sheetLayer.Opacity;
                _sheetMotion = Motion.Run(Motion.SwapMs, Ease.InOut, t => _sheetLayer.Opacity = Motion.Lerp(from, 0, t), () =>
                {
                    _sheet.Child = null;
                    Ui.Show(_sheetLayer, false);
                    _sheetLayer.Opacity = 1;
                    _sheetLayer.IsHitTestVisible = true;
                });
                return;
            }

            _sheet.Child = null;
            Ui.Show(_sheetLayer, false);
            return;
        }

        _sheetLayer.IsHitTestVisible = true;
        _sheet.Child = kind switch
        {
            "welcome" => WelcomeSheet.Build(_model, _sheetSubs),
            "review" => ReviewSheet.Build(_model, _sheetSubs, this),
            not null when kind.StartsWith("projects:", StringComparison.Ordinal) => ProjectSheets.Build(_model, _sheetSubs, this),
            _ => null,
        };
        bool open = kind is not null;
        Ui.Show(_sheetLayer, open);
        _nav.IsEnabled = !open;
        if (open)
        {
            _sheetMotion = Motion.Run(Motion.SwapMs, Ease.InOut, t => _sheetLayer.Opacity = t);
        }
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

        if (_sheetKind is not null && _sheetKind.StartsWith("projects:", StringComparison.Ordinal))
        {
            if (!SheetBusy)
            {
                _model.Projects.CloseSheet();
            }

            e.Handled = true;
        }
        else if (_sheetKind == "review" && _model.Review.Stage != ReviewStage.Cleaning)
        {
            _model.Review.Close();
            e.Handled = true;
        }
        else if (_sheetKind is null && ActivePanel is { IsOpen: true } panel && panel.HandleKey(e.Key))
        {
            e.Handled = true;
        }
        else if (_sheetKind is null && _model.CleanUp.Inspected is not null)
        {
            _model.CleanUp.Inspected = null;
            e.Handled = true;
        }
    }
}
