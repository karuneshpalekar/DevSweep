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

        AppWindow.Resize(screenshots ? new SizeInt32(1280, 840) : new SizeInt32(1240, 820));
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid titleBar = TitleBar();
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

        _windowSubs.On(model, nameof(MainViewModel.Current), ShowPage);
        _windowSubs.On(model, nameof(MainViewModel.ShowWelcome), UpdateSheet);
        _windowSubs.On(model.Review, nameof(ReviewViewModel.Stage), UpdateSheet);
        _windowSubs.On(model.SettingsPage, nameof(SettingsViewModel.Appearance), ApplyTheme);
        Closed += (_, _) =>
        {
            _windowSubs.Dispose();
            _pageSubs.Dispose();
            _sheetSubs.Dispose();
        };
    }

    public FrameworkElement Root => _root;

    public MainViewModel Model => _model;

    /// <summary>Forces a theme (screenshots); null follows Settings.</summary>
    public void SetTheme(ElementTheme theme) => _root.RequestedTheme = theme;

    public void ApplyTheme() => _root.RequestedTheme = _model.SettingsPage.Appearance switch
    {
        AppearanceMode.Light => ElementTheme.Light,
        AppearanceMode.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

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
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(_root);
        IBuffer pixels = await bitmap.GetPixelsAsync();
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
        return (bitmap.PixelWidth, bitmap.PixelHeight);
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

    private static Grid TitleBar()
    {
        var bar = new Grid { Padding = new Thickness(16, 0, 0, 0) };
        StackPanel title = Ui.H(10, Ui.Icon("", 16), Ui.Text("DevSweep", "DsCaption"));
        title.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(title);
        return bar;
    }

    private void BuildNavigation()
    {
        _nav.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
        _nav.IsSettingsVisible = false;
        _nav.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
        _nav.IsPaneToggleButtonVisible = true;
        _nav.OpenPaneLength = 240;
        _nav.Content = _page;
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
            if (place.Id is "history" or "settings")
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
            if (e.SelectedItem is NavigationViewItem { Tag: string id })
            {
                _model.Go(id);
            }
        };
    }

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
